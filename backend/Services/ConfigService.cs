using System.Data;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ClinicalRag.Api.Services;

public interface IConfigService
{
    Task<ConfigVersion> GetPublishedAsync(Guid workspaceId);
    Task<ConfigVersion> CreateDraftAsync(Guid workspaceId, string configJson, Guid? userId, Guid? publishedConfigId = null);
    Task<IReadOnlyList<ConfigVersionDetails>> GetVersionsAsync(Guid workspaceId);
    Task<ConfigVersion?> PublishAsync(Guid workspaceId, Guid configId);
    Task<ConfigVersion?> DiscardAsync(Guid workspaceId, Guid configId);
    Task<ConfigVersion?> RollbackAsync(Guid workspaceId, int version, Guid? userId);
}

public class ConfigService : IConfigService
{
    private readonly AppDbContext _db;
    private readonly IConfigTestRunProvider _testRuns;
    public ConfigService(AppDbContext db, IConfigTestRunProvider? testRuns = null)
    {
        _db = db;
        _testRuns = testRuns ?? new NoConfigTestRunProvider();
    }

    public async Task<ConfigVersion> GetPublishedAsync(Guid workspaceId)
    {
        await using var transaction = await BeginLifecycleAsync();
        var c = await _db.ConfigVersions
            .Where(x => x.WorkspaceId == workspaceId && x.Status == "published")
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync();
        if (c == null)
        {
            // bootstrap a default published config
            var next = await NextVersionAsync(workspaceId);
            c = new ConfigVersion { WorkspaceId = workspaceId, Version = next, Status = "published", ConfigJson = DefaultConfig(), PublishedAt = DateTime.UtcNow };
            _db.ConfigVersions.Add(c);
            await _db.SaveChangesAsync();
        }
        if (transaction != null) await transaction.CommitAsync();
        return c;
    }

    public async Task<ConfigVersion> CreateDraftAsync(Guid workspaceId, string configJson, Guid? userId, Guid? publishedConfigId = null)
    {
        await using var transaction = await BeginLifecycleAsync();
        var published = await _db.ConfigVersions.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.Status == "published")
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync();
        if (publishedConfigId != null && published?.Id != publishedConfigId)
            throw new ConfigLifecycleException("Published configuration changed. Reload it before saving a draft.");
        // Validate before creating any row, including the bootstrap published version.
        var normalized = ConfigValidator.Normalize(configJson, published?.ConfigJson);
        if (published == null) await GetPublishedAsync(workspaceId);
        var draft = await AddDraftAsync(workspaceId, normalized, userId);
        if (transaction != null) await transaction.CommitAsync();
        return draft;
    }

    public async Task<IReadOnlyList<ConfigVersionDetails>> GetVersionsAsync(Guid workspaceId)
    {
        var versions = await _db.ConfigVersions.AsNoTracking().Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.Version).ToListAsync();
        var result = new List<ConfigVersionDetails>();
        foreach (var version in versions)
        {
            var run = await LatestBoundRunAsync(version);
            result.Add(new ConfigVersionDetails(version.Id, version.WorkspaceId, version.Version,
                version.Status, version.ConfigJson, version.CreatedBy, version.CreatedAt, version.PublishedAt,
                run, PublishBlockingReason(version, run)));
        }
        return result;
    }

    public async Task<ConfigVersion?> PublishAsync(Guid workspaceId, Guid configId)
    {
        await using var transaction = await BeginLifecycleAsync();
        var c = await _db.ConfigVersions.FirstOrDefaultAsync(x => x.Id == configId && x.WorkspaceId == workspaceId);
        if (c == null) return null;
        var run = await LatestBoundRunAsync(c);
        var blockingReason = PublishBlockingReason(c, run);
        if (blockingReason != null) throw new ConfigLifecycleException(blockingReason);
        // Never normalize/change the tested artifact during publication.
        var normalized = ConfigValidator.Normalize(c.ConfigJson);
        if (ConfigValidator.Fingerprint(normalized) != ConfigValidator.Fingerprint(c.ConfigJson))
            throw new ConfigLifecycleException("Save a normalized draft and run tests before publishing.");
        var prev = await _db.ConfigVersions.Where(x => x.WorkspaceId == workspaceId && x.Status == "published").ToListAsync();
        // Check again after validation/reads, before changing tracked objects or persisting anything.
        // Prompt 11's provider must share this transaction/lock with run creation (see its contract).
        await EnsureUnchangedAsync(c);
        var latest = await LatestBoundRunAsync(c);
        blockingReason = PublishBlockingReason(c, latest);
        if (blockingReason != null) throw new ConfigLifecycleException(blockingReason);
        foreach (var p in prev) p.Status = "archived";
        c.Status = "published";
        c.PublishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();
        return c;
    }

    public async Task<ConfigVersion?> DiscardAsync(Guid workspaceId, Guid configId)
    {
        await using var transaction = await BeginLifecycleAsync();
        var draft = await _db.ConfigVersions.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == configId);
        if (draft == null) return null;
        await EnsureUnchangedAsync(draft);
        if (draft.Status == "discarded") return draft;
        if (draft.Status != "draft") throw new ConfigLifecycleException("Only drafts can be discarded.");
        draft.Status = "discarded";
        await _db.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();
        return draft;
    }

    public async Task<ConfigVersion?> RollbackAsync(Guid workspaceId, int version, Guid? userId)
    {
        await using var transaction = await BeginLifecycleAsync();
        var source = await _db.ConfigVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Version == version);
        if (source == null) return null;
        if (source.Status is not ("published" or "archived"))
            throw new ConfigLifecycleException("Only published or archived versions can be restored.");
        // Restore this historical snapshot, not a merge with newer published settings.
        var normalized = ConfigValidator.Normalize(source.ConfigJson);
        await GetPublishedAsync(workspaceId);
        var draft = await AddDraftAsync(workspaceId, normalized, userId);
        if (transaction != null) await transaction.CommitAsync();
        return draft;
    }

    // Serializable reads guard published-id preconditions, version allocation, and publication as one unit.
    // Nested bootstrap calls join the existing transaction; InMemory is a test-only nontransactional provider.
    private async Task<IDbContextTransaction?> BeginLifecycleAsync() =>
        _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;

    private async Task EnsureUnchangedAsync(ConfigVersion version)
    {
        var persisted = await _db.ConfigVersions.AsNoTracking().Where(item => item.Id == version.Id)
            .Select(item => new { item.WorkspaceId, item.Version, item.Status, item.ConfigJson, item.CreatedAt }).SingleOrDefaultAsync();
        // SQL Server commonly uses case-insensitive/trailing-space-insensitive equality. Compare the
        // exact artifact in .NET as well; the serializable read lock protects it through commit.
        if (persisted == null || persisted.WorkspaceId != version.WorkspaceId || persisted.Version != version.Version
            || persisted.CreatedAt != version.CreatedAt || !string.Equals(persisted.Status, version.Status, StringComparison.Ordinal)
            || !string.Equals(persisted.ConfigJson, version.ConfigJson, StringComparison.Ordinal))
            throw new DbUpdateConcurrencyException("Configuration changed. Refresh versions and try again.");
    }

    private async Task<int> NextVersionAsync(Guid workspaceId) =>
        (await _db.ConfigVersions.Where(x => x.WorkspaceId == workspaceId).MaxAsync(x => (int?)x.Version) ?? 0) + 1;

    private async Task<ConfigVersion> AddDraftAsync(Guid workspaceId, string configJson, Guid? userId)
    {
        var draft = new ConfigVersion { WorkspaceId = workspaceId, Version = await NextVersionAsync(workspaceId),
            Status = "draft", ConfigJson = configJson, CreatedBy = userId };
        _db.ConfigVersions.Add(draft);
        await _db.SaveChangesAsync();
        return draft;
    }

    private async Task<ConfigTestRun?> LatestBoundRunAsync(ConfigVersion version)
    {
        var run = await _testRuns.GetLatestAsync(version.WorkspaceId, version.Id);
        if (run == null || run.Id == Guid.Empty || run.WorkspaceId != version.WorkspaceId || run.ConfigId != version.Id
            || run.ConfigVersion != version.Version || run.ConfigHash != ConfigValidator.Fingerprint(version.ConfigJson)
            || run.StartedAt < version.CreatedAt || run.CompletedSteps == null
            || run.CompletedSteps.Any(step => step is < 1 or > 6)
            || run.CompletedSteps.Distinct().Count() != run.CompletedSteps.Count)
            return null;
        return run;
    }

    private static string? PublishBlockingReason(ConfigVersion version, ConfigTestRun? run)
    {
        if (version.Status != "draft") return "only drafts can be published";
        if (run == null) return "no test run";
        if (run.Status == "running") return "latest test run is still running";
        if (run.Status != "passed") return "latest test run did not pass";
        if (run.CompletedAt == null || run.CompletedAt < run.StartedAt) return "latest test run is not complete";
        return null;
    }

    public static string DefaultConfig() => ConfigSchema.Defaults().ToJsonString();
}
