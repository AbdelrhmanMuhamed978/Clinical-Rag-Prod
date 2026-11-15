using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClinicalRag.Api.Services;

/// <summary>
/// A single fail-closed boundary for live and saved pipeline results. This checks
/// the contract, not clinical truth: quote/scope verification stays in Python.
/// Unknown fields (including facts, prompts and patient summaries) never pass it.
/// </summary>
public static class PipelineResultPolicy
{
    private static InvalidDataException InvalidContract() => new("Invalid verified pipeline contract.");

    private static JsonElement Field(JsonElement value, string name, JsonValueKind kind)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field)
            || field.ValueKind != kind)
            throw InvalidContract();
        return field;
    }

    private static string Text(JsonElement value, string name, bool nonempty = false)
    {
        var text = Field(value, name, JsonValueKind.String).GetString()!;
        if (nonempty && string.IsNullOrWhiteSpace(text)) throw InvalidContract();
        return text;
    }

    public static string Mode(JsonElement value) => Text(value, "plan_source", true) switch
    {
        "single_question" => "question",
        "rules" or "llm+rules" => "case",
        "chat" => "chat",
        _ => throw InvalidContract()
    };

    private static JsonArray Strings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw InvalidContract();
        var array = new JsonArray();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw InvalidContract();
            array.Add(item.GetString());
        }
        return array;
    }

    private static JsonObject Citation(JsonElement value)
    {
        var result = new JsonObject
        {
            ["id"] = Text(value, "id", true), ["label"] = Text(value, "label", true),
            ["section"] = Text(value, "section"), ["page"] = Text(value, "page"),
            ["strength"] = Text(value, "strength")
        };
        if (!value.TryGetProperty("draft", out var draft)
            || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidContract();
        result["draft"] = draft.GetBoolean();
        if (value.TryGetProperty("score", out var score) && score.ValueKind != JsonValueKind.Null)
        {
            if (score.ValueKind != JsonValueKind.Number || !double.IsFinite(score.GetDouble())) throw InvalidContract();
            result["score"] = score.GetDouble();
        }
        return result;
    }

    private static JsonObject Item(JsonElement value, bool verified)
    {
        var result = new JsonObject
        {
            ["recommendation"] = Text(value, "recommendation", verified),
            ["quote"] = Text(value, "quote", verified)
        };
        if (value.TryGetProperty("citation", out var citation)) result["citation"] = Citation(citation);
        else if (verified) throw InvalidContract();
        if (!verified)
        {
            var issues = Strings(Field(value, "issues", JsonValueKind.Array));
            if (issues.Count == 0) throw InvalidContract();
            result["issues"] = issues;
        }
        else if (value.TryGetProperty("issues", out var issues)
                 && (issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() > 0))
            throw InvalidContract();
        if (value.TryGetProperty("repaired", out _)) result["repaired"] = Text(value, "repaired");

        if (verified && value.TryGetProperty("patient_notes", out var notes))
        {
            if (notes.ValueKind != JsonValueKind.Array) throw InvalidContract();
            var projectedNotes = new JsonArray();
            foreach (var note in notes.EnumerateArray())
            {
                projectedNotes.Add(new JsonObject
                {
                    ["id"] = Text(note, "id"), ["title"] = Text(note, "title"),
                    ["level"] = note.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String
                        ? level.GetString() : null,
                    ["reasons"] = Strings(Field(note, "reasons", JsonValueKind.Array))
                });
            }
            result["patient_notes"] = projectedNotes;
        }
        return result;
    }

    public static JsonElement ForResponse(JsonElement value)
    {
        _ = Mode(value); // A result must carry a recognized, authoritative pipeline mode.
        var result = new JsonObject
        {
            ["plan_source"] = Text(value, "plan_source", true), ["version"] = Text(value, "version", true),
            ["disclaimer"] = Text(value, "disclaimer", true),
            ["warnings"] = Strings(Field(value, "warnings", JsonValueKind.Array)),
            ["not_covered"] = Strings(Field(value, "not_covered", JsonValueKind.Array))
        };
        var problems = new JsonArray();
        foreach (var problem in Field(value, "problems", JsonValueKind.Array).EnumerateArray())
        {
            var status = Text(problem, "status");
            if (status is not ("ok" or "not_found" or "unverified" or "error")) throw InvalidContract();
            var items = new JsonArray();
            var unverified = new JsonArray();
            var sources = new JsonArray();
            foreach (var item in Field(problem, "items", JsonValueKind.Array).EnumerateArray()) items.Add(Item(item, true));
            if ((status == "ok") != (items.Count > 0)) throw InvalidContract();
            foreach (var item in Field(problem, "unverified", JsonValueKind.Array).EnumerateArray()) unverified.Add(Item(item, false));
            foreach (var source in Field(problem, "sources", JsonValueKind.Array).EnumerateArray())
            {
                var projected = Citation(source);
                projected["excerpt"] = Text(source, "excerpt");
                sources.Add(projected);
            }
            var projectedProblem = new JsonObject
            {
                ["title"] = Text(problem, "title", true), ["status"] = status,
                ["queries"] = Strings(Field(problem, "queries", JsonValueKind.Array)),
                ["items"] = items, ["unverified"] = unverified, ["sources"] = sources
            };
            if (problem.TryGetProperty("detail", out _)) projectedProblem["detail"] = Text(problem, "detail");
            problems.Add(projectedProblem);
        }
        result["problems"] = problems;
        if (Text(value, "plan_source") == "chat" && problems.Count > 0) throw InvalidContract();
        // Legacy results grouped processing failures with absent evidence. A failed
        // retrieval/generation cannot establish that a guideline lacks coverage.
        var failedTitles = problems.Where(problem => problem!["status"]!.GetValue<string>() == "error")
            .Select(problem => problem!["title"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        result["not_covered"] = new JsonArray(result["not_covered"]!.AsArray()
            .Where(title => !failedTitles.Contains(title!.GetValue<string>())).Select(title => title!.DeepClone()).ToArray());
        // This is a deterministic greeting, not an alternative clinical answer.
        if (Text(value, "plan_source") == "chat" && problems.Count == 0 && value.TryGetProperty("chat", out _))
            result["chat"] = Text(value, "chat");
        return JsonSerializer.SerializeToElement(result);
    }

    public static JsonElement ForStorage(JsonElement value, bool caseMode = false)
    {
        var result = JsonNode.Parse(ForResponse(value).GetRawText())!.AsObject();
        var problems = result["problems"]!.AsArray();
        var uncovered = new JsonArray();
        for (var index = 0; index < problems.Count; index++)
        {
            var problem = problems[index]!.AsObject();
            problem["queries"] = new JsonArray(); // Planning queries can echo case text.
            problem.Remove("detail");
            if (caseMode) problem["title"] = $"Guideline question {index + 1}";
            foreach (var item in problem["items"]!.AsArray())
            {
                item!.AsObject().Remove("patient_notes");
                // Store source words, not a generated patient-specific paraphrase.
                if (caseMode) item["recommendation"] = item["quote"]!.DeepClone();
            }
            if (caseMode)
            {
                foreach (var item in problem["unverified"]!.AsArray())
                {
                    item!["recommendation"] = "Excluded by verifier.";
                    item["quote"] = "";
                }
                if (problem["status"]!.GetValue<string>() is "not_found" or "unverified") uncovered.Add(problem["title"]!.DeepClone());
            }
        }
        if (caseMode)
        {
            result["not_covered"] = uncovered;
            // Live warnings can quote values or words from the case. Rebuild only
            // guideline-level cautions rather than persisting patient-derived text.
            var warnings = new JsonArray();
            if (problems.Any(problem => problem!["items"]!.AsArray()
                .Any(item => item!["citation"]!["draft"]!.GetValue<bool>())))
                warnings.Add("Some citations come from DRAFT guidelines (marked DRAFT). Do not treat them as final.");
            if (uncovered.Count > 0)
                warnings.Add("“Not covered” means the loaded guidelines gave no verifiable answer. It does not mean the topic is unimportant or safe.");
            if (problems.Any(problem => problem!["status"]!.GetValue<string>() == "error"))
                warnings.Add("Some problems could not be analyzed. Retry them; their guideline coverage is unknown.");
            const string privacyWarning = "Patient-specific checks and planning context are not stored. Rerun the case before applying a saved statement.";
            warnings.Add(privacyWarning);
            result["warnings"] = warnings;
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
