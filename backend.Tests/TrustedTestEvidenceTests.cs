using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class TrustedTestEvidenceTests : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    internal const string VerifiedResult = """
        {"plan_source":"rules","version":"synthetic","disclaimer":"Synthetic test only.",
         "warnings":[],"not_covered":[],"problems":[{"title":"Synthetic question","status":"ok",
         "queries":[],"sources":[],"unverified":[],"items":[{"recommendation":"Synthetic source statement.",
         "quote":"Synthetic source statement.","citation":{"id":"synthetic-source","label":"Synthetic guideline",
         "section":"Test","page":"1","strength":"conditional","draft":false}}]}]}
        """;

    private sealed class Pipeline : IPipelineClient
    {
        public string Result = VerifiedResult;
        public string? Authorization;
        public PipelineAnalyzeRequest? Request;
        public Func<Task>? BeforeResult;
        public Exception? Failure { get; set; }
        public bool WaitForCancellation;
        public async Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request, string? authorization, CancellationToken cancellationToken = default)
        {
            Authorization = authorization; Request = request;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (BeforeResult != null) await BeforeResult();
            if (Failure != null) throw Failure;
            return JsonDocument.Parse(Result).RootElement.Clone();
        }
        public IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(PipelineAnalyzeRequest request, string? authorization, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Pipeline _pipeline = new();
    private readonly HttpClient _client;
    private readonly Guid _workspace;
    private readonly Guid _draft;
    private readonly ClaimsPrincipal _owner;

    public TrustedTestEvidenceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPipelineClient>(); services.AddSingleton<IPipelineClient>(_pipeline);
        }));
        _client = _factory.CreateClient();
        // A deliberately non-secret test credential; never a live token.
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-test-credential");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.Add(new User { FirebaseUid = $"trusted-{Guid.NewGuid():N}", Email = "synthetic@example.org" }).Entity;
        _workspace = db.Workspaces.Add(new Workspace { Name = "Synthetic evidence", OwnerId = user.Id }).Entity.Id;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspace, UserId = user.Id, Role = "owner" });
        db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = _workspace, Version = 1, Status = "published", ConfigJson = ConfigService.DefaultConfig() });
        _draft = db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = _workspace, Version = 2, ConfigJson = ConfigService.DefaultConfig() }).Entity.Id;
        db.SaveChanges();
        _owner = Principal(user); TestPrincipal.Current = _owner;
    }

    private static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, user.FirebaseUid), new Claim(ClaimTypes.Email, user.Email), new Claim("email_verified", "true") }, "Test"));
    private string Root => $"/api/workspaces/{_workspace}";
    private async Task<Guid> Create(string expected = "{\"mustHaveProblems\":[\"Synthetic question\"],\"minVerifiedItemsPerProblem\":1}")
    {
        var response = await _client.PostAsJsonAsync(Root + "/tests", new { name = "Synthetic case", inputText = "SYNTHETIC_INPUT_MARKER", expectedJson = expected });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private Task<HttpResponseMessage> Run(params Guid[] ids) => _client.PostAsJsonAsync(Root + "/tests/run", new { caseIds = ids.Length == 0 ? null : ids, configId = _draft });
    private async Task AssertBlocked(string code)
    {
        var response = await _client.PostAsJsonAsync($"{Root}/config/{_draft}/publish", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    private void EditRun(Action<TestRun> edit)
    {
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        edit(db.TestRuns.Single(run => run.WorkspaceId == _workspace)); db.SaveChanges();
    }
    public void Dispose() { TestPrincipal.Current = null; _client.Dispose(); _factory.Dispose(); }

    [Fact]
    public async Task Governance_audit_exports_csv_and_pdf_for_config_publications()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConfigVersions.Add(new ConfigVersion
            {
                WorkspaceId = _workspace, Version = 9, Status = "archived", ConfigJson = ConfigService.DefaultConfig(),
                PublishedAt = DateTime.UtcNow.AddDays(-1)
            });
            db.Rules.Add(new Rule
            {
                WorkspaceId = _workspace, RuleId = "audit-rule", Title = "Synthetic approved rule",
                Status = "approved", ReviewedAt = DateTime.UtcNow.AddDays(-2),
                ReviewerName = "Dr Synthetic Reviewer", ApprovedSourceUrl = "https://example.org/synthetic-source"
            });
            await db.SaveChangesAsync();
        }
        var csv = await _client.GetAsync($"{Root}/governance/audit?format=csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var csvBody = await csv.Content.ReadAsStringAsync();
        Assert.Contains("v9", csvBody);
        Assert.Contains("audit-rule", csvBody);
        Assert.Contains("Dr Synthetic Reviewer", csvBody);
        Assert.Contains("https://example.org/synthetic-source", csvBody);
        var pdf = await _client.GetAsync($"{Root}/governance/audit?format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Flag_stores_run_config_and_reason_but_not_case_text()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync();
        var run = db.AnalysisRuns.Add(new AnalysisRun
        {
            WorkspaceId = _workspace, UserId = user.Id, InputHash = "PRIVATE_CASE_TEXT",
            ConfigVersion = 7, ResultJson = "PRIVATE_CASE_TEXT"
        }).Entity;
        await db.SaveChangesAsync();

        var response = await _client.PostAsJsonAsync($"{Root}/analyses/{run.Id}/flag",
            new { reason = "unsafe", note = "Synthetic clinician note", caseText = "PRIVATE_CASE_TEXT" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        db.ChangeTracker.Clear();
        var flag = await db.AnswerFlags.SingleAsync(item => item.AnalysisRunId == run.Id);
        Assert.Equal(7, flag.ConfigVersion);
        Assert.Equal("unsafe", flag.Reason);
        Assert.Equal("Synthetic clinician note", flag.Note);
        Assert.DoesNotContain("PRIVATE_CASE_TEXT", JsonSerializer.Serialize(flag));
        var ownerView = await _client.GetStringAsync($"{Root}/flags");
        Assert.DoesNotContain("PRIVATE_CASE_TEXT", ownerView);
        Assert.Contains("unsafe", ownerView);
    }

    [Fact]
    public async Task Caller_authorization_reaches_pipeline_and_all_pass_publishes_exact_draft()
    {
        await Create(); var response = await Run();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer synthetic-test-credential", _pipeline.Authorization);
        Assert.True(_pipeline.Request!.TestRunDiagnostics);
        Assert.Equal("passed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("status").GetString());
        var published = await _client.PostAsJsonAsync($"{Root}/config/{_draft}/publish", new { });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("editor")]
    public async Task Only_authorized_test_execution_returns_text_free_diagnostics(string role)
    {
        var id = await Create();
        var raw = System.Text.Json.Nodes.JsonNode.Parse(VerifiedResult)!;
        raw["diagnostics"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"problemIndex\":1,\"drafts\":1,\"candidates\":1,\"verified\":1,\"reasons\":{\"empty_draft\":0},\"quote\":\"PRIVATE_DIAGNOSTIC\"}]");
        _pipeline.Result = raw.ToJsonString();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Add(new User { FirebaseUid = $"diagnostics-{Guid.NewGuid():N}", Email = "synthetic@example.org" }).Entity;
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspace, UserId = user.Id, Role = role }); db.SaveChanges();
            TestPrincipal.Current = Principal(user);
        }
        var response = await Run(id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("results")[0].GetProperty("diagnostics")[0].GetProperty("verified").GetInt32());
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC", body.GetRawText());
        var history = await _client.GetStringAsync($"{Root}/tests/runs/{id}");
        Assert.DoesNotContain("diagnostics", history);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var viewer = db.Users.Add(new User { FirebaseUid = $"viewer-{Guid.NewGuid():N}", Email = "viewer@example.org" }).Entity;
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspace, UserId = viewer.Id, Role = "viewer" }); db.SaveChanges();
            TestPrincipal.Current = Principal(viewer);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Run(id)).StatusCode);
        Assert.DoesNotContain("diagnostics", await _client.GetStringAsync($"{Root}/tests/runs/{id}"));
    }

    [Fact]
    public async Task Public_analysis_cannot_opt_in_to_test_diagnostics_with_forged_json()
    {
        var raw = System.Text.Json.Nodes.JsonNode.Parse(VerifiedResult)!;
        raw["diagnostics"] = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"problemIndex":1,"drafts":1,"candidates":1,"verified":1,
              "reasons":{"empty_draft":0},"quote":"PRIVATE_DIAGNOSTIC"}]
            """);
        _pipeline.Result = raw.ToJsonString();
        var response = await _client.PostAsJsonAsync(Root + "/analyze", new
        {
            message = "Synthetic source question?", testRunDiagnostics = true,
            diagnostics = new[] { new { verified = 999, quote = "FORGED_DIAGNOSTIC" } }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(_pipeline.Request!.TestRunDiagnostics);
        var returned = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("diagnostics", returned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC", returned);
        Assert.DoesNotContain("FORGED_DIAGNOSTIC", returned);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = db.AnalysisRuns.Single(run => run.WorkspaceId == _workspace).ResultJson;
        Assert.DoesNotContain("diagnostics", stored ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Case_edit_invalidates_evidence()
    {
        var id = await Create(); await Run();
        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync($"{Root}/tests/{id}", new { inputText = "EDITED_SYNTHETIC_INPUT" })).StatusCode);
        await AssertBlocked("case_edited");
    }

    [Fact]
    public async Task Case_edit_while_pipeline_is_in_flight_cannot_be_overwritten_or_pass_gate()
    {
        var id = await Create();
        _pipeline.BeforeResult = async () => Assert.Equal(HttpStatusCode.OK,
            (await _client.PatchAsJsonAsync($"{Root}/tests/{id}", new { inputText = "EDITED_SYNTHETIC_INPUT" })).StatusCode);
        await Run(); await AssertBlocked("case_edited");
        var cases = await _client.GetFromJsonAsync<JsonElement>(Root + "/tests");
        Assert.Equal("EDITED_SYNTHETIC_INPUT", cases[0].GetProperty("inputText").GetString());
    }

    [Theory]
    [InlineData("hash", "config_hash_mismatch")]
    [InlineData("id", "config_mismatch")]
    [InlineData("failed", "case_failed")]
    [InlineData("running", "case_running")]
    public async Task Latest_evidence_must_match_and_pass(string change, string code)
    {
        await Create(); await Run();
        EditRun(run => { if (change == "hash") run.ConfigHash = "different"; else if (change == "id") run.ConfigId = Guid.NewGuid(); else run.Status = change; });
        await AssertBlocked(code);
    }

    [Fact]
    public async Task Missing_case_and_empty_workspace_block()
    {
        await AssertBlocked("no_cases"); await Create(); await AssertBlocked("case_missing_run");
        await Run(); await Create(); await AssertBlocked("case_missing_run");
    }

    [Fact]
    public async Task New_case_requires_fresh_evidence_for_the_whole_suite()
    {
        await Create(); await Run(); var later = await Create(); await Run(later);
        await AssertBlocked("suite_changed");
    }

    [Fact]
    public async Task Forged_server_fields_are_ignored_and_no_result_or_input_is_stored_in_evidence()
    {
        var forgedId = Guid.NewGuid();
        var created = await _client.PostAsJsonAsync(Root + "/tests", new { name = "Synthetic case", inputText = "SYNTHETIC_INPUT_MARKER",
            id = forgedId, lastRunJson = "FORGED_RESULT", lastRunAt = DateTime.UtcNow, createdAt = DateTime.UnixEpoch, revision = 900 });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(forgedId, body.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastRunJson").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastRunAt").ValueKind);
        Assert.True(body.GetProperty("createdAt").GetDateTime() > DateTime.UnixEpoch);
        await Run();
        var cases = await _client.GetFromJsonAsync<JsonElement>(Root + "/tests");
        Assert.Equal(JsonValueKind.Null, cases[0].GetProperty("lastRunJson").ValueKind);
        var history = await _client.GetStringAsync($"{Root}/tests/runs/{body.GetProperty("id").GetGuid()}");
        Assert.DoesNotContain("SYNTHETIC_INPUT_MARKER", history); Assert.DoesNotContain("Synthetic source statement", history);
    }

    [Theory]
    [InlineData("{\"unknownExpectation\":true}")]
    [InlineData("{\"mustHaveProblems\":42}")]
    [InlineData("{\"mustHaveProblems\":[7]}")]
    [InlineData("{\"minVerifiedItemsPerProblem\":-1}")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task Invalid_expectations_are_rejected_on_create_and_update(string expectation)
    {
        var response = await _client.PostAsJsonAsync(Root + "/tests", new { name = "Synthetic", inputText = "Synthetic", expectedJson = expectation });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var id = await Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PatchAsJsonAsync($"{Root}/tests/{id}", new { expectedJson = expectation })).StatusCode);
    }

    [Fact]
    public async Task Cap_rejects_without_starting_any_pipeline_work()
    {
        var response = await Run(Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Null(_pipeline.Request);
    }

    [Fact]
    public async Task Invalid_pipeline_result_is_not_passing_evidence()
    {
        await Create("{}"); _pipeline.Result = "{\"problems\":[],\"private\":\"UPSTREAM_MARKER\"}";
        var response = await Run(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", body.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("invalid_result", body.GetProperty("results")[0].GetProperty("failureCode").GetString());
        Assert.DoesNotContain("UPSTREAM_MARKER", body.GetRawText()); await AssertBlocked("case_failed");
    }

    [Fact]
    public async Task Deliberate_failure_has_readable_but_text_free_diff_and_blocks_publish()
    {
        await Create("{\"mustHaveProblems\":[\"PRIVATE_EXPECTATION_MARKER\"]}");
        var response = await Run(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = body.GetProperty("results")[0];
        Assert.Equal("comparison", result.GetProperty("failureCode").GetString());
        Assert.Contains("Missing expected problem", result.GetProperty("diff").GetString());
        Assert.DoesNotContain("PRIVATE_EXPECTATION_MARKER", body.GetRawText()); await AssertBlocked("case_failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flag_and_rule_expectations_require_projected_verified_patient_notes(bool linked)
    {
        await Create("{\"mustFlag\":[\"synthetic-note\"],\"mustFireRules\":[\"synthetic-note\"]}");
        var raw = System.Text.Json.Nodes.JsonNode.Parse(VerifiedResult)!;
        // Raw facts/diagnostics are never accepted as comparison evidence.
        raw["facts"] = System.Text.Json.Nodes.JsonNode.Parse("{\"flags\":[{\"id\":\"synthetic-note\"}],\"fired_rules\":[\"synthetic-note\"]}");
        if (linked) raw["problems"]![0]!["items"]![0]!["patient_notes"] = System.Text.Json.Nodes.JsonNode.Parse(
            "[{\"id\":\"synthetic-note\",\"title\":\"Synthetic note\",\"level\":\"info\",\"reasons\":[\"Synthetic only\"]}]");
        _pipeline.Result = raw.ToJsonString();
        var response = await Run(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(linked ? "passed" : "failed", body.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.DoesNotContain("synthetic-note", body.GetRawText());
        if (!linked) await AssertBlocked("case_failed");
    }

    [Fact]
    public async Task Uncovered_results_remain_uncovered_not_an_invalid_pipeline_failure()
    {
        await Create("{\"mustHaveProblems\":[\"Synthetic question\"]}");
        var raw = System.Text.Json.Nodes.JsonNode.Parse(VerifiedResult)!;
        raw["problems"]![0]!["status"] = "not_found";
        raw["problems"]![0]!["items"] = new System.Text.Json.Nodes.JsonArray();
        raw["not_covered"] = System.Text.Json.Nodes.JsonNode.Parse("[\"Synthetic question\"]");
        _pipeline.Result = raw.ToJsonString();
        var body = await (await Run()).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("passed", body.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("results")[0].GetProperty("failureCode").ValueKind);
        // A passing comparison is not a claim of source coverage or clinical validity.
    }

    [Theory]
    [InlineData(401, "auth")]
    [InlineData(403, "auth")]
    [InlineData(503, "pipeline_unavailable")]
    [InlineData(408, "timeout")]
    public async Task Failures_persist_only_safe_classification(int status, string code)
    {
        await Create(); _pipeline.Failure = new HttpRequestException("UPSTREAM_PRIVATE_MARKER", null, (HttpStatusCode)status);
        var response = await Run(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("results")[0].GetProperty("failureCode").GetString());
        Assert.DoesNotContain("UPSTREAM_PRIVATE_MARKER", body.GetRawText()); await AssertBlocked("case_failed");
    }

    [Fact]
    public async Task Explicit_foreign_config_does_not_fall_back_to_published()
    {
        await Create();
        var response = await _client.PostAsJsonAsync(Root + "/tests/run", new { configId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Null(_pipeline.Request);
    }

    [Fact]
    public async Task Near_expiry_session_is_rejected_without_starting_work()
    {
        await Create();
        ((ClaimsIdentity)_owner.Identity!).AddClaim(new Claim("exp", DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds().ToString()));
        var response = await Run(); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Null(_pipeline.Request);
    }

    [Fact]
    public async Task Cancelled_request_does_not_bootstrap_an_untested_published_config()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ConfigVersions.RemoveRange(db.ConfigVersions.Where(c => c.WorkspaceId == _workspace));
        await db.SaveChangesAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<TestHarnessService>()
            .RunAsync(_workspace, null, null, Guid.NewGuid(), "Bearer synthetic-test", cancelled.Token));
        Assert.False(db.ConfigVersions.Any(c => c.WorkspaceId == _workspace));
        Assert.Null(_pipeline.Request);
    }

    [Fact]
    public async Task Cancellation_finalizes_all_reserved_runs_as_failed_and_blocks_gate()
    {
        await Create(); await Create();
        _pipeline.Failure = new OperationCanceledException();
        var response = await Run(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(body.GetProperty("results").EnumerateArray(), result => Assert.Equal("timeout", result.GetProperty("failureCode").GetString()));
        await AssertBlocked("case_failed");
    }

    [Fact]
    public async Task Total_deadline_cancels_pipeline_and_finalizes_queued_cases()
    {
        await Create(); await Create();
        var configuration = _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        configuration["Tests:MaxDurationSeconds"] = "2";
        _pipeline.WaitForCancellation = true;
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var response = await Run();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.All(body.GetProperty("results").EnumerateArray(), result => Assert.Equal("timeout", result.GetProperty("failureCode").GetString()));
            await AssertBlocked("case_failed");
        }
        finally { configuration["Tests:MaxDurationSeconds"] = null; }
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    public async Task Real_passing_runs_do_not_allow_nonowner_publication(string role)
    {
        await Create(); await Run();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Add(new User { FirebaseUid = $"member-{Guid.NewGuid():N}", Email = "member@example.org" }).Entity;
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspace, UserId = user.Id, Role = role }); db.SaveChanges();
            TestPrincipal.Current = Principal(user);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync($"{Root}/config/{_draft}/publish", new { })).StatusCode);
    }
}
