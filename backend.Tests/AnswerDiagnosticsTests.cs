using System.Text.Json;
using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class AnswerDiagnosticsTests
{
    [Fact]
    public void Test_projection_retains_only_closed_bounded_numeric_fields()
    {
        using var raw = JsonDocument.Parse("""
            {"diagnostics":[{"problemIndex":1,"drafts":1,"candidates":2,"verified":1,
            "quote":"PRIVATE","reasons":{"quote_not_found":1,"formatting_mismatch":2,"PRIVATE":"secret","number_missing":-1,
            "scope_mismatch":true,"below_threshold":100001}}]}
            """);
        var result = PipelineAnswerDiagnostics.ForTestRun(raw.RootElement)!.Value;
        Assert.Equal(1, result[0].GetProperty("problemIndex").GetInt32());
        Assert.Equal("{\"quote_not_found\":1,\"formatting_mismatch\":2}", result[0].GetProperty("reasons").GetRawText());
        Assert.DoesNotContain("PRIVATE", result.GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"problemIndex\":\"PRIVATE\",\"drafts\":1}]")]
    public void Malformed_diagnostics_do_not_become_result_or_comparison_evidence(string diagnostics)
    {
        using var raw = JsonDocument.Parse("{\"diagnostics\":" + diagnostics + "}");
        Assert.Null(PipelineAnswerDiagnostics.ForTestRun(raw.RootElement));
    }

    [Theory]
    [InlineData("nothing_retrieved")]
    [InlineData("retrieved_unverified")]
    [InlineData("only_draft_sources")]
    public void Not_covered_reason_is_a_closed_safe_category(string reason)
    {
        using var raw = JsonDocument.Parse($$"""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":["Synthetic problem"],"problems":[{"title":"Synthetic problem","status":"not_found",
             "queries":[],"items":[],"unverified":[],"sources":[],"reason":"{{reason}}"}]}
            """);
        var projected = PipelineResultPolicy.ForResponse(raw.RootElement);
        Assert.Equal(reason, projected.GetProperty("problems")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain("PRIVATE", projected.GetRawText());
    }

    [Theory]
    [InlineData("not_found", "PRIVATE_MODEL_TEXT")]
    [InlineData("ok", "nothing_retrieved")]
    public void Not_covered_reason_rejects_unknown_labels_and_non_covered_statuses(string status, string reason)
    {
        using var raw = JsonDocument.Parse($$$"""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"problems":[{"title":"Synthetic problem","status":"{{{status}}}",
             "queries":[],"items":[{"recommendation":"Synthetic","quote":"Synthetic","citation":{"id":"S1","label":"Synthetic","section":"","page":"1","strength":"","draft":false}}],
             "unverified":[],"sources":[],"reason":"{{{reason}}}"}]}
            """);
        Assert.ThrowsAny<Exception>(() => PipelineResultPolicy.ForResponse(raw.RootElement));
    }

    [Fact]
    public void Table_display_metadata_is_validated_and_projected()
    {
        using var raw = JsonDocument.Parse("""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"problems":[{"title":"Synthetic table problem","status":"ok","queries":[],
             "items":[{"recommendation":"Synthetic table recommendation","quote":"Synthetic row: Synthetic cell",
             "citation":{"id":"S1","label":"Synthetic","section":"Table","page":"1","strength":"conditional","draft":false},
             "table":{"headers":["Intervention","CD4 cell count"],"row":["Synthetic row","Synthetic cell"]}}],
             "unverified":[],"sources":[]}]}
            """);
        var projected = PipelineResultPolicy.ForResponse(raw.RootElement);
        var table = projected.GetProperty("problems")[0].GetProperty("items")[0].GetProperty("table");
        Assert.Equal("Intervention", table.GetProperty("headers")[0].GetString());
        Assert.Equal("Synthetic cell", table.GetProperty("row")[1].GetString());
    }

    [Fact]
    public void Malformed_table_display_metadata_is_rejected()
    {
        using var raw = JsonDocument.Parse("""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"problems":[{"title":"Synthetic table problem","status":"ok","queries":[],
             "items":[{"recommendation":"Synthetic table recommendation","quote":"Synthetic row: Synthetic cell",
             "citation":{"id":"S1","label":"Synthetic","section":"Table","page":"1","strength":"conditional","draft":false},
             "table":{"headers":["Intervention","CD4 cell count"],"row":["Synthetic row"]}}],
             "unverified":[],"sources":[]}]}
            """);
        Assert.ThrowsAny<Exception>(() => PipelineResultPolicy.ForResponse(raw.RootElement));
    }

    [Fact]
    public void Related_items_and_question_coverage_are_projected_but_never_become_lead_items()
    {
        using var raw = JsonDocument.Parse("""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"coverage":[{"part":"When is screening recommended?","status":"related_only"}],
             "problems":[{"title":"Screening question","status":"ok","covers":"When is screening recommended?","queries":[],
             "items":[],
             "related":[{"recommendation":"Related synthetic statement","quote":"Related synthetic statement",
             "citation":{"id":"S2","label":"Synthetic","section":"Table","page":"2","strength":"conditional","draft":false}}],
             "unverified":[],"sources":[]}]}
            """);
        var projected = PipelineResultPolicy.ForResponse(raw.RootElement);
        var problem = projected.GetProperty("problems")[0];
        Assert.Empty(problem.GetProperty("items").EnumerateArray());
        Assert.Single(problem.GetProperty("related").EnumerateArray());
        Assert.Equal("related_only", projected.GetProperty("coverage")[0].GetProperty("status").GetString());
        var stored = PipelineResultPolicy.ForStorage(projected, caseMode: true);
        Assert.False(stored.TryGetProperty("coverage", out _));
        Assert.False(stored.GetProperty("problems")[0].TryGetProperty("covers", out _));
        Assert.Equal("Related synthetic statement",
            stored.GetProperty("problems")[0].GetProperty("related")[0].GetProperty("recommendation").GetString());
    }

    [Theory]
    [InlineData("ok", "[]")]
    [InlineData("not_found", "[{\"recommendation\":\"Synthetic\",\"quote\":\"Synthetic\",\"citation\":{\"id\":\"S1\",\"label\":\"Synthetic\",\"section\":\"\",\"page\":\"1\",\"strength\":\"\",\"draft\":false}}]")]
    public void Related_items_and_status_must_not_contradict(string status, string related)
    {
        using var raw = JsonDocument.Parse($$"""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"problems":[{"title":"Synthetic problem","status":"{{status}}","queries":[],
             "items":[],"related":{{related}},"unverified":[],"sources":[]}]}
            """);
        Assert.ThrowsAny<Exception>(() => PipelineResultPolicy.ForResponse(raw.RootElement));
    }

    [Fact]
    public void Citation_projects_derived_strength_and_certainty()
    {
        using var raw = JsonDocument.Parse("""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"problems":[{"title":"Synthetic","status":"ok","queries":[],
             "items":[{"recommendation":"Synthetic","quote":"Synthetic",
             "citation":{"id":"S1","label":"WHO, Synthetic (2026, FINAL)","section":"Treatment > Induction therapy",
             "page":"4","strength":"strong","certainty":"high","draft":false}}],
             "unverified":[],"sources":[]}]}
            """);
        var citation = PipelineResultPolicy.ForResponse(raw.RootElement)
            .GetProperty("problems")[0].GetProperty("items")[0].GetProperty("citation");
        Assert.Equal("strong", citation.GetProperty("strength").GetString());
        Assert.Equal("high", citation.GetProperty("certainty").GetString());
        Assert.Equal("Treatment > Induction therapy", citation.GetProperty("section").GetString());
    }

    [Fact]
    public void Question_storage_keeps_problem_topic_labels_in_not_covered()
    {
        using var raw = JsonDocument.Parse("""
            {"plan_source":"single_question","version":"test","disclaimer":"Decision support only.","warnings":[],
             "not_covered":[],"problems":[{"title":"Screening unavailable","status":"not_found","queries":[],
             "items":[],"unverified":[],"sources":[]}]}
            """);
        var stored = PipelineResultPolicy.ForStorage(raw.RootElement, caseMode: false);
        Assert.Equal("Screening unavailable",
            Assert.Single(stored.GetProperty("not_covered").EnumerateArray()).GetString());
        var caseStored = PipelineResultPolicy.ForStorage(raw.RootElement, caseMode: true);
        Assert.Equal("Guideline question 1",
            Assert.Single(caseStored.GetProperty("not_covered").EnumerateArray()).GetString());
    }

    [Fact]
    public void Ordinary_and_saved_results_never_expose_diagnostics()
    {
        var raw = System.Text.Json.Nodes.JsonNode.Parse(TrustedTestEvidenceTests.VerifiedResult)!;
        raw["diagnostics"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"problemIndex\":1,\"drafts\":1,\"candidates\":1,\"verified\":1,\"reasons\":{}}]");
        using var doc = JsonDocument.Parse(raw.ToJsonString());
        Assert.False(PipelineResultPolicy.ForResponse(doc.RootElement).TryGetProperty("diagnostics", out _));
        Assert.False(PipelineResultPolicy.ForStorage(doc.RootElement).TryGetProperty("diagnostics", out _));
    }
}
