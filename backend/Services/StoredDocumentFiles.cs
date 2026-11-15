using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

/// <summary>Cleanup is limited to regular files inside the configured upload root.</summary>
public static class StoredDocumentFiles
{
    public static List<PendingUploadDeletion> Queue(AppDbContext db, IEnumerable<string?> paths)
    {
        var queued = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Select(p => new PendingUploadDeletion { StoragePath = p! }).ToList();
        db.PendingUploadDeletions.AddRange(queued);
        return queued;
    }

    public static async Task<int> CompleteAsync(AppDbContext db, IConfiguration cfg, List<PendingUploadDeletion> queued)
    {
        var failed = 0;
        foreach (var item in queued)
        {
            if (TryDelete(cfg, item.StoragePath)) db.PendingUploadDeletions.Remove(item);
            else { item.LastAttemptAt = DateTime.UtcNow; failed++; }
        }
        // The tombstone remains durable if completing its cleanup record fails.
        try { if (queued.Count > 0) await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return queued.Count; }
        return failed;
    }
    public static string DirectoryPath(IConfiguration configuration) => Path.GetFullPath(configuration["Documents:UploadsDirectory"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads"));

    public static bool TryDelete(IConfiguration configuration, string? path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        try
        {
            var root = DirectoryPath(configuration);
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)) return false;
            // A lexical prefix must not allow deletion through a directory symlink/junction.
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(full)!); parent != null; parent = parent.Parent)
            {
                if (parent.LinkTarget != null || (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)) return false;
            }
            if (new FileInfo(full).LinkTarget != null || Directory.Exists(full)
                || (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)) return false;
            File.Delete(full);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}

public sealed class UploadCleanupWorker(IServiceScopeFactory scopes, ILogger<UploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var queued = await db.PendingUploadDeletions.OrderBy(p => p.LastAttemptAt).ThenBy(p => p.CreatedAt).Take(20).ToListAsync(stoppingToken);
                await StoredDocumentFiles.CompleteAsync(db, cfg, queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Upload cleanup deferred; pending cleanup records are retained."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
