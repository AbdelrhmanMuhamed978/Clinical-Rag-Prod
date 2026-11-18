using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

public class TestHarnessTests
{
    private static DbContextOptions<AppDbContext> Options(string? name = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? $"Harness_{Guid.NewGuid():N}");
        return builder.Options;
    }

    private static async Task<(AppDbContext Db, Guid WorkspaceId, Guid UserId)> SeedAsync()
    {
        await using var db = new AppDbContext(Options());
        var user = db.Users.Add(new User { FirebaseUid = $"harness-{Guid.NewGuid():N}", Email = "harness@example.org" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Harness workspace", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "owner" });
        await db.SaveChangesAsync();
        return (db, workspace.Id, user.Id);
    }

    private sealed class FakePipelineClient : IPipelineClient
    {
        public string? ResponseJson { get; set; }
        public bool ShouldThrow { get; set; }
        public string? Authorization { get; private set; }
        public Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request, string? authorization, CancellationToken cancellationToken = default)
        {
            Authorization = authorization;
            if (ShouldThrow) throw new HttpRequestException("Pipeline is down");
            return Task.FromResult(ResponseJson != null
                ? JsonDocument.Parse(ResponseJson).RootElement.Clone()
                : JsonDocument.Parse(TrustedTestEvidenceTests.VerifiedResult).RootElement.Clone());
        }

        public IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(PipelineAnalyzeRequest request, string? authorization, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> ReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static string ProblemJson(string title, string status = "ok", int itemCount = 1)
    {
        var items = string.Join(",", Enumerable.Range(0, itemCount).Select(_ => "{\"recommendation\":\"rec\",\"quote\":\"quote\",\"citation\":{\"id\":\"synthetic\",\"label\":\"Synthetic\",\"section\":\"\",\"page\":\"1\",\"strength\":\"\",\"draft\":true}}"));
        return $"{{\"title\":\"{title}\",\"status\":\"{status}\",\"items\":[{items}],\"unverified\":[],\"sources\":[],\"queries\":[]}}";
    }

    private static string ResultJson(string problem, string warnings = "[]") =>
        $"{{\"plan_source\":\"rules\",\"version\":\"synthetic\",\"disclaimer\":\"Synthetic test only\",\"not_covered\":[],\"problems\":[{problem}],\"warnings\":{warnings}}}";

    [Fact]
    public void Forbidden_phrases_are_compared_as_decoded_text_not_json_escape_sequences()
    {
        var result = JsonNode.Parse(TrustedTestEvidenceTests.VerifiedResult)!;
        result["problems"]![0]!["items"]![0]!["recommendation"] = "Synthetic \"forbidden\" phrase.";
        var expectation = TestExpectation.Parse(JsonSerializer.Serialize(new { forbiddenPhrases = new[] { "\"forbidden\"" } }));
        var comparison = TestExpectationComparer.Compare(PipelineResultPolicy.ForResponse(JsonSerializer.SerializeToElement(result)), expectation);
        Assert.False(comparison.Passed);
        Assert.Contains("Forbidden phrase #1 appears in the result.", comparison.Diffs);
    }

    [Fact]
    public void Comparer_detects_missing_and_unexpected_problems()
    {
        var result = JsonDocument.Parse($"{{\"problems\":[{ProblemJson("Unexpected problem", "ok", 1)}],\"warnings\":[]}}").RootElement.Clone();
        var expectation = TestExpectation.Parse("{\"mustHaveProblems\":[\"Absent expected problem\"],\"mustNotHaveProblems\":[\"Unexpected\"],\"requireDraftWarning\":true}");
        var comparison = TestExpectationComparer.Compare(result, expectation);
        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Diffs, diff => diff.Contains("Missing expected problem"));
        Assert.Contains(comparison.Diffs, diff => diff.Contains("Unexpected problem matching"));
        Assert.Contains(comparison.Diffs, diff => diff.Contains("DRAFT guideline warning"));
    }

    [Fact]
    public async Task Passing_expectation_makes_run_pass()
    {
        await using var db = new AppDbContext(Options());
        var (_, workspaceId, _) = await SeedAsync();
        var responseJson = ResultJson(ProblemJson("Synthetic shock and hypotension", "ok", 2), "[\"Some citations come from DRAFT guidelines\"]");
        var fake = new FakePipelineClient { ResponseJson = responseJson };
        var harness = new TestHarnessService(db, fake, new ConfigService(db), new RuleService(db), new ConfigurationBuilder().Build());

        var caseId = Guid.NewGuid();
        db.TestCases.Add(new TestCase
        {
            Id = caseId, WorkspaceId = workspaceId, Name = "Passing case",
            InputText = "Synthetic shock case",
            ExpectedJson = "{\"mustHaveProblems\":[\"Synthetic shock\"],\"minVerifiedItemsPerProblem\":1,\"requireDraftWarning\":true}",
        });
        await db.SaveChangesAsync();

        var results = await harness.RunAsync(workspaceId, new List<Guid> { caseId }, null, Guid.NewGuid(), "Bearer synthetic-test");
        Assert.Single(results);
        Assert.Equal("passed", results[0].Status);
    }

    [Fact]
    public async Task Failing_expectation_makes_run_fail_with_diff()
    {
        await using var db = new AppDbContext(Options());
        var (_, workspaceId, _) = await SeedAsync();
        var responseJson = ResultJson(ProblemJson("Unexpected problem", "ok", 1));
        var fake = new FakePipelineClient { ResponseJson = responseJson };
        var harness = new TestHarnessService(db, fake, new ConfigService(db), new RuleService(db), new ConfigurationBuilder().Build());

        var caseId = Guid.NewGuid();
        db.TestCases.Add(new TestCase
        {
            Id = caseId, WorkspaceId = workspaceId, Name = "Failing case",
            InputText = "some case",
            ExpectedJson = "{\"mustHaveProblems\":[\"Absent expected problem\"],\"mustNotHaveProblems\":[\"Unexpected\"],\"requireDraftWarning\":true}",
        });
        await db.SaveChangesAsync();

        var results = await harness.RunAsync(workspaceId, new List<Guid> { caseId }, null, Guid.NewGuid(), "Bearer synthetic-test");
        Assert.Single(results);
        Assert.Equal("failed", results[0].Status);
        Assert.NotNull(results[0].Diff);
        Assert.Contains("Missing expected problem", results[0].Diff!);
        Assert.Contains("Unexpected problem matching", results[0].Diff!);
        Assert.Contains("DRAFT guideline warning", results[0].Diff!);
        Assert.Equal("Bearer synthetic-test", fake.Authorization);
    }

    [Fact]
    public async Task Missing_authorization_fails_closed_before_comparison()
    {
        await using var db = new AppDbContext(Options());
        var (_, workspaceId, _) = await SeedAsync();
        var fake = new FakePipelineClient { ResponseJson = ResultJson(ProblemJson("Unexpected problem", "ok", 1)) };
        var harness = new TestHarnessService(db, fake, new ConfigService(db), new RuleService(db), new ConfigurationBuilder().Build());

        var caseId = Guid.NewGuid();
        db.TestCases.Add(new TestCase
        {
            Id = caseId, WorkspaceId = workspaceId, Name = "Missing auth case", InputText = "some case",
            ExpectedJson = "{\"mustHaveProblems\":[\"Absent expected problem\"]}"
        });
        await db.SaveChangesAsync();

        var results = await harness.RunAsync(workspaceId, new List<Guid> { caseId }, null, Guid.NewGuid());
        Assert.Single(results);
        Assert.Equal("failed", results[0].Status);
        Assert.Equal("auth", results[0].FailureCode);
        Assert.Contains("authentication was rejected", results[0].Diff!);
        Assert.Null(fake.Authorization);
    }

    [Fact]
    public async Task Run_updates_last_run_on_case()
    {
        await using var db = new AppDbContext(Options());
        var (_, workspaceId, _) = await SeedAsync();
        var responseJson = ResultJson(ProblemJson("Any problem", "ok", 1), "[\"DRAFT warning\"]");
        var fake = new FakePipelineClient { ResponseJson = responseJson };
        var harness = new TestHarnessService(db, fake, new ConfigService(db), new RuleService(db), new ConfigurationBuilder().Build());

        var caseId = Guid.NewGuid();
        db.TestCases.Add(new TestCase { Id = caseId, WorkspaceId = workspaceId, Name = "Last run case", InputText = "x" });
        await db.SaveChangesAsync();

        var results = await harness.RunAsync(workspaceId, new List<Guid> { caseId }, null, Guid.NewGuid(), "Bearer synthetic-test");
        var savedCase = db.TestCases.Find(caseId);
        Assert.NotNull(savedCase!.LastRunAt);
        Assert.Null(savedCase.LastRunJson);
    }

    [Fact]
    public async Task Pipeline_failure_records_failed_run()
    {
        await using var db = new AppDbContext(Options());
        var (_, workspaceId, _) = await SeedAsync();
        var fake = new FakePipelineClient { ShouldThrow = true };
        var harness = new TestHarnessService(db, fake, new ConfigService(db), new RuleService(db), new ConfigurationBuilder().Build());

        var caseId = Guid.NewGuid();
        db.TestCases.Add(new TestCase { Id = caseId, WorkspaceId = workspaceId, Name = "Pipeline fail case", InputText = "x" });
        await db.SaveChangesAsync();

        var results = await harness.RunAsync(workspaceId, new List<Guid> { caseId }, null, Guid.NewGuid(), "Bearer synthetic-test");
        Assert.Single(results);
        Assert.Equal("failed", results[0].Status);
        Assert.Contains("Run failed", results[0].Diff!);
    }
}
