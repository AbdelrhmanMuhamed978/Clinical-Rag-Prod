using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClinicalRag.Api.Services;

/// <summary>Text-free, ephemeral test-run metadata. Never comparison or publication evidence.</summary>
public static class PipelineAnswerDiagnostics
{
    private static readonly string[] Reasons = ["empty_draft", "json_parse_error", "quote_not_found", "formatting_mismatch", "number_missing",
        "stronger_than_source", "negation_conflict", "scope_mismatch", "topic_mismatch", "truncated", "deduplicated", "below_threshold"];

    private static bool Counter(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt32(out number) && number is >= 0 and <= 100000;
    }

    public static JsonElement? ForTestRun(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("diagnostics", out var diagnostics)
            || diagnostics.ValueKind != JsonValueKind.Array || diagnostics.GetArrayLength() > 100) return null;
        var result = new JsonArray();
        var seen = new HashSet<int>();
        foreach (var item in diagnostics.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Counter(item, "problemIndex", out var index) || index is < 1 or > 100
                || !Counter(item, "drafts", out var drafts) || !Counter(item, "candidates", out var candidates)
                || !Counter(item, "verified", out var verified) || verified > candidates || !seen.Add(index)) continue;
            var reasons = new JsonObject();
            if (item.TryGetProperty("reasons", out var counts) && counts.ValueKind == JsonValueKind.Object)
                foreach (var name in Reasons)
                    if (Counter(counts, name, out var count)) reasons[name] = count;
            result.Add(new JsonObject { ["problemIndex"] = index, ["drafts"] = drafts, ["candidates"] = candidates,
                ["verified"] = verified, ["reasons"] = reasons });
        }
        return result.Count == 0 ? null : JsonSerializer.SerializeToElement(result);
    }
}
