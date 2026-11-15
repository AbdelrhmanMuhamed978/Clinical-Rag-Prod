using System.Text.Json.Nodes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

internal sealed class FakeConfigTestRuns : IConfigTestRunProvider
{
    public List<ConfigTestRun> Runs { get; } = new();
    public List<(Guid WorkspaceId, Guid ConfigId)> Calls { get; } = new();
    public Func<int, ConfigTestRun?>? Handler { get; set; }
    public Task<ConfigTestRun?> GetLatestAsync(Guid workspaceId, Guid configId, CancellationToken cancellationToken = default)
    {
        Calls.Add((workspaceId, configId));
        return Task.FromResult(Handler != null ? Handler(Calls.Count)
            : Runs.OrderByDescending(run => run.StartedAt).FirstOrDefault());
    }
    public static ConfigTestRun Passed(ConfigVersion version) => new(Guid.NewGuid(), version.WorkspaceId,
        version.Id, version.Version, ConfigValidator.Fingerprint(version.ConfigJson), "passed",
        version.CreatedAt.AddSeconds(1), version.CreatedAt.AddSeconds(2), new[] { 1, 2, 3, 4, 5, 6 });
}

public class ConfigTestRunProviderTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"ConfigRuns_{Guid.NewGuid():N}").Options);
    private static async Task<(ConfigVersion Published, ConfigVersion Draft)> Seed(AppDbContext db)
    {
        var workspaceId = Guid.NewGuid();
        var service = new ConfigService(db);
        var published = await service.GetPublishedAsync(workspaceId);
        var draft = await service.CreateDraftAsync(workspaceId, "{}", null, published.Id);
        return (published, draft);
    }

    [Fact]
    public async Task Passing_bound_run_publishes_the_exact_tested_artifact_and_archives_only_this_workspace()
    {
        using var db = Database();
        var (published, draft) = await Seed(db);
        var other = await new ConfigService(db).GetPublishedAsync(Guid.NewGuid());
        var json = draft.ConfigJson;
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(FakeConfigTestRuns.Passed(draft));

        var result = await new ConfigService(db, provider).PublishAsync(draft.WorkspaceId, draft.Id);

        Assert.NotNull(result);
        Assert.Equal("published", draft.Status); Assert.Equal("archived", published.Status);
        Assert.Equal("published", other.Status); Assert.Equal(json, result.ConfigJson); Assert.NotNull(result.PublishedAt);
        Assert.All(provider.Calls, call => Assert.Equal((draft.WorkspaceId, draft.Id), call));
    }

    [Theory]
    [InlineData("failed", "latest test run did not pass")]
    [InlineData("running", "latest test run is still running")]
    [InlineData("canceled", "latest test run did not pass")]
    [InlineData("not_run", "latest test run did not pass")]
    public async Task A_newer_nonpassing_run_blocks_an_older_success(string status, string reason)
    {
        using var db = Database(); var (published, draft) = await Seed(db);
        var passed = FakeConfigTestRuns.Passed(draft);
        var latest = passed with { Id = Guid.NewGuid(), Status = status, StartedAt = passed.StartedAt.AddMinutes(1),
            CompletedAt = status == "running" ? null : passed.CompletedAt!.Value.AddMinutes(1), CompletedSteps = new[] { 1, 2 } };
        var provider = new FakeConfigTestRuns(); provider.Runs.AddRange(new[] { passed, latest });
        var service = new ConfigService(db, provider);

        var details = (await service.GetVersionsAsync(draft.WorkspaceId)).Single(version => version.Id == draft.Id);
        Assert.Equal(latest.Id, details.LatestTestRun!.Id); Assert.Equal(new[] { 1, 2 }, details.LatestTestRun.CompletedSteps);
        Assert.Equal(reason, details.PublishBlockingReason);
        var error = await Assert.ThrowsAsync<ConfigLifecycleException>(() => service.PublishAsync(draft.WorkspaceId, draft.Id));
        Assert.Equal(reason, error.Message); Assert.Equal("published", published.Status); Assert.Equal("draft", draft.Status);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("configuration")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("empty id")]
    [InlineData("old start")]
    [InlineData("invalid step")]
    [InlineData("duplicate step")]
    [InlineData("missing steps")]
    public async Task Mismatched_or_malformed_evidence_is_not_exposed_or_authorized(string mismatch)
    {
        using var db = Database(); var (published, draft) = await Seed(db);
        var run = FakeConfigTestRuns.Passed(draft);
        run = mismatch switch
        {
            "workspace" => run with { WorkspaceId = Guid.NewGuid() },
            "configuration" => run with { ConfigId = Guid.NewGuid() },
            "version" => run with { ConfigVersion = draft.Version + 1 },
            "hash" => run with { ConfigHash = ConfigValidator.Fingerprint(draft.ConfigJson + " ") },
            "empty id" => run with { Id = Guid.Empty },
            "old start" => run with { StartedAt = draft.CreatedAt.AddSeconds(-1) },
            "invalid step" => run with { CompletedSteps = new[] { 7 } },
            "duplicate step" => run with { CompletedSteps = new[] { 1, 1 } },
            _ => run with { CompletedSteps = null! }
        };
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(run); var service = new ConfigService(db, provider);

        var details = (await service.GetVersionsAsync(draft.WorkspaceId)).Single(version => version.Id == draft.Id);
        Assert.Null(details.LatestTestRun); Assert.Equal("no test run", details.PublishBlockingReason);
        await Assert.ThrowsAsync<ConfigLifecycleException>(() => service.PublishAsync(draft.WorkspaceId, draft.Id));
        Assert.Equal("published", published.Status); Assert.Equal("draft", draft.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_claimed_pass_without_valid_completion_cannot_publish(bool backwards)
    {
        using var db = Database(); var (_, draft) = await Seed(db);
        var run = FakeConfigTestRuns.Passed(draft);
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(run with { CompletedAt = backwards ? run.StartedAt.AddSeconds(-1) : null });
        var error = await Assert.ThrowsAsync<ConfigLifecycleException>(() => new ConfigService(db, provider).PublishAsync(draft.WorkspaceId, draft.Id));
        Assert.Equal("latest test run is not complete", error.Message);
    }

    [Fact]
    public async Task A_newer_failure_arriving_during_publication_cannot_reuse_the_older_pass()
    {
        using var db = Database(); var (published, draft) = await Seed(db);
        var passed = FakeConfigTestRuns.Passed(draft);
        var failed = passed with { Id = Guid.NewGuid(), Status = "failed", StartedAt = passed.StartedAt.AddSeconds(3), CompletedAt = passed.CompletedAt!.Value.AddSeconds(3) };
        var provider = new FakeConfigTestRuns { Handler = call => call == 1 ? passed : failed };

        await Assert.ThrowsAsync<ConfigLifecycleException>(() => new ConfigService(db, provider).PublishAsync(draft.WorkspaceId, draft.Id));
        Assert.Equal("published", published.Status); Assert.Equal("draft", draft.Status);
    }

    [Fact]
    public async Task Publication_refuses_to_normalize_or_unlock_a_legacy_tested_artifact()
    {
        using var db = Database(); var (published, draft) = await Seed(db);
        draft.ConfigJson = "{\"safety\":{\"verifierLocked\":false}}"; await db.SaveChangesAsync();
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(FakeConfigTestRuns.Passed(draft));

        var error = await Assert.ThrowsAsync<ConfigLifecycleException>(() => new ConfigService(db, provider).PublishAsync(draft.WorkspaceId, draft.Id));
        Assert.Contains("normalized draft", error.Message); Assert.Equal("published", published.Status);
        Assert.Equal("{\"safety\":{\"verifierLocked\":false}}", draft.ConfigJson);
    }

    [Fact]
    public async Task Stale_published_preconditions_reject_saves_without_creating_rows()
    {
        using var db = Database(); var (published, draft) = await Seed(db); var count = db.ConfigVersions.Count();
        await Assert.ThrowsAsync<ConfigLifecycleException>(() => new ConfigService(db)
            .CreateDraftAsync(draft.WorkspaceId, "{\"retrieval\":{\"topK\":10}}", null, Guid.NewGuid()));
        Assert.Equal(count, db.ConfigVersions.Count()); Assert.Equal("published", published.Status);
    }

    [Fact]
    public async Task Rollback_restores_the_historical_snapshot_without_newer_keys_or_inherited_test_permission()
    {
        using var db = Database(); var (published, draft) = await Seed(db);
        var current = JsonNode.Parse(published.ConfigJson)!; current["future"] = new JsonObject { ["newerOnly"] = true };
        published.ConfigJson = current.ToJsonString();
        var historical = JsonNode.Parse(draft.ConfigJson)!; historical["retrieval"]!["candidates"] = 60;
        historical["future"] = new JsonObject { ["historicalOnly"] = true };
        draft.ConfigJson = historical.ToJsonString(); draft.Status = "archived"; await db.SaveChangesAsync();
        var provider = new FakeConfigTestRuns(); provider.Runs.Add(FakeConfigTestRuns.Passed(draft));
        var service = new ConfigService(db, provider); var restored = await service.RollbackAsync(draft.WorkspaceId, draft.Version, null);

        Assert.NotNull(restored); Assert.NotEqual(draft.Id, restored.Id); Assert.Equal(3, restored.Version);
        var saved = JsonNode.Parse(restored.ConfigJson)!;
        Assert.Equal(60, saved["retrieval"]!["candidates"]!.GetValue<int>());
        Assert.Null(saved["future"]!["newerOnly"]); Assert.True(saved["future"]!["historicalOnly"]!.GetValue<bool>());
        Assert.Equal("published", published.Status); Assert.Equal("archived", draft.Status);
        var details = (await service.GetVersionsAsync(draft.WorkspaceId)).Single(version => version.Id == restored.Id);
        Assert.Null(details.LatestTestRun); Assert.Equal("no test run", details.PublishBlockingReason);
    }
}
