using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

/// <summary>
/// Trusted server-side Prompt 11 contract. The provider must return the latest started run, including
/// failures/in-flight runs, not the last successful run. Never derive this from client-written TestCase.LastRunJson.
/// ConfigHash is SHA-256 of the exact persisted ConfigJson. CompletedSteps contains only real step evidence (1–6).
/// </summary>
public sealed record ConfigTestRun(
    Guid Id, Guid WorkspaceId, Guid ConfigId, int ConfigVersion, string ConfigHash,
    string Status, DateTime StartedAt, DateTime? CompletedAt, IReadOnlyList<int> CompletedSteps);

public interface IConfigTestRunProvider
{
    /// <summary>
    /// Publication rechecks immediately before updating versions. A persisted provider must use the
    /// publication DbContext/serializable transaction (or an equivalent lock also taken when starting runs)
    /// so a newer run cannot appear between that final read and commit. Reads must not return cached evidence.
    /// </summary>
    Task<ConfigTestRun?> GetLatestAsync(Guid workspaceId, Guid configId, CancellationToken cancellationToken = default);

    async Task<ConfigTestGate> GetGateAsync(Guid workspaceId, Guid configId, CancellationToken cancellationToken = default) =>
        new(await GetLatestAsync(workspaceId, configId, cancellationToken), null, null);
}

public sealed record ConfigTestGate(ConfigTestRun? LatestRun, string? Code, string? Reason);

/// <summary>Uncached per-case reads using the publication DbContext and its serializable locks.</summary>
public sealed class PersistedConfigTestRunProvider(AppDbContext db) : IConfigTestRunProvider
{
    public async Task<ConfigTestRun?> GetLatestAsync(Guid workspaceId, Guid configId, CancellationToken cancellationToken = default) =>
        (await GetGateAsync(workspaceId, configId, cancellationToken)).LatestRun;

    public async Task<ConfigTestGate> GetGateAsync(Guid workspaceId, Guid configId, CancellationToken cancellationToken = default)
    {
        var config = await db.ConfigVersions.AsNoTracking().SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == configId, cancellationToken);
        // The workspace range index also locks out case additions through publication commit.
        var cases = await db.TestCases.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.Id).ToListAsync(cancellationToken);
        if (cases.Count == 0) return new(null, "no_cases", "Workspace has no test cases; add a case and run tests.");
        if (config == null) return new(null, "config_mismatch", "Test configuration is unavailable.");
        var hash = ConfigValidator.Fingerprint(config.ConfigJson);
        var latest = new List<TestRun>();
        foreach (var c in cases)
        {
            // Latest overall, NOT latest success or latest filtered to this draft.
            var run = await db.TestRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.CaseId == c.Id)
                .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);
            if (run == null) return new(null, "case_missing_run", $"Test case {c.Id} has no run; run every case on this draft.");
            latest.Add(run);
        }
        var representative = latest.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).First();
        // Aggregate real persisted case runs. Analyze supplies no per-step telemetry;
        // do not invent six completed steps from a passing comparison.
        var evidence = new ConfigTestRun(representative.Id, workspaceId, configId, config.Version, hash,
            latest.All(r => r.Status == "passed") ? "passed" : latest.Any(r => r.Status == "running") ? "running" : "failed",
            latest.Min(r => r.StartedAt), latest.All(r => r.CompletedAt != null) ? latest.Max(r => r.CompletedAt) : null, []);
        ConfigTestGate Block(string code, string reason) => new(null, code, reason);
        for (var index = 0; index < cases.Count; index++)
        {
            var c = cases[index]; var run = latest[index];
            if (run.ConfigId != configId || run.ConfigVersion != config.Version)
                return Block("config_mismatch", $"Latest run for test case {c.Id} belongs to a different configuration.");
            if (!string.Equals(run.ConfigHash, hash, StringComparison.Ordinal))
                return Block("config_hash_mismatch", $"Latest run for test case {c.Id} does not match the current draft hash.");
            if (run.CaseRevision <= 0 || run.CaseRevision != c.Revision || run.StartedAt <= c.UpdatedAt || run.StartedAt <= c.CreatedAt)
                return Block("case_edited", $"Test case {c.Id} changed after its tested snapshot; rerun every case.");
            if (run.StartedAt < config.CreatedAt)
                return Block("config_mismatch", $"Latest run for test case {c.Id} predates this draft.");
            if (run.Status == "running") return new(evidence, "case_running", $"Latest run for test case {c.Id} is still running.");
            if (run.Status != "passed" || run.FailureCode != null)
                return new(evidence with { Status = "failed" }, "case_failed", $"Latest run for test case {c.Id} did not pass.");
            if (run.CompletedAt == null || run.CompletedAt < run.StartedAt)
                return new(evidence, "case_incomplete", $"Latest run for test case {c.Id} is not complete.");
        }
        if (cases.Max(c => c.UpdatedAt > c.CreatedAt ? c.UpdatedAt : c.CreatedAt) >= latest.Min(r => r.StartedAt))
            return Block("suite_changed", "A test case was added or edited after suite testing began; rerun every case on this draft.");
        return new(evidence, null, null);
    }
}

public sealed class ConfigLifecycleException : InvalidOperationException
{
    public string Code { get; }
    public ConfigLifecycleException(string message, string code = "config_conflict") : base(message) { Code = code; }
}

public sealed record ConfigVersionDetails(
    Guid Id, Guid WorkspaceId, int Version, string Status, string ConfigJson,
    Guid? CreatedBy, DateTime CreatedAt, DateTime? PublishedAt,
    ConfigTestRun? LatestTestRun, string? PublishBlockingReason, string? PublishBlockingCode = null);
