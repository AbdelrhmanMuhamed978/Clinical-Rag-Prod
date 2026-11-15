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
}

/// <summary>Fail closed until Prompt 11 supplies a real, persisted, draft-bound test runner.</summary>
public sealed class NoConfigTestRunProvider : IConfigTestRunProvider
{
    public Task<ConfigTestRun?> GetLatestAsync(Guid workspaceId, Guid configId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ConfigTestRun?>(null);
}

public sealed class ConfigLifecycleException : InvalidOperationException
{
    public ConfigLifecycleException(string message) : base(message) { }
}

public sealed record ConfigVersionDetails(
    Guid Id, Guid WorkspaceId, int Version, string Status, string ConfigJson,
    Guid? CreatedBy, DateTime CreatedAt, DateTime? PublishedAt,
    ConfigTestRun? LatestTestRun, string? PublishBlockingReason);
