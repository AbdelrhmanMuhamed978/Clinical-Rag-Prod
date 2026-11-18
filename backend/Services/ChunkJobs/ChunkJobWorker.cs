using ClinicalRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

/// <summary>
/// Durable worker for chunk jobs. It never keeps an HTTP request open and it
/// leaves a running job claimable for recovery after an API restart.
/// </summary>
public sealed class ChunkJobWorker(
    IServiceScopeFactory scopes, ILogger<ChunkJobWorker> logger) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;

                // A process that disappeared leaves a running lease behind. Make it
                // resumable instead of letting a second worker process it blindly.
                var stale = await db.ChunkJobs
                    .Where(job => (job.Status == "running" || job.Status == "pausing" || job.Status == "stopping")
                        && job.LeaseUntil != null && job.LeaseUntil < now)
                    .Take(20).ToListAsync(stoppingToken);
                foreach (var item in stale)
                {
                    item.Status = "interrupted";
                    item.LeaseOwner = null;
                    item.LeaseUntil = null;
                    item.LastHeartbeatAt = now;
                    item.Revision = checked(item.Revision + 1);
                }
                if (stale.Count > 0) await db.SaveChangesAsync(stoppingToken);

                var job = await db.ChunkJobs
                    .Where(item => item.Status == "queued")
                    .OrderBy(item => item.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);
                if (job != null)
                {
                    job.Status = "running";
                    job.LeaseOwner = _owner;
                    job.LeaseUntil = now.AddSeconds(job.BatchTimeoutSeconds + 120);
                    job.StartedAt ??= now;
                    job.LastHeartbeatAt = now;
                    job.Revision = checked(job.Revision + 1);
                    await db.SaveChangesAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ChunkJobProcessor>()
                        .ProcessAsync(job.Id, _owner, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Log the type only. Job errors must never contain upstream text.
                logger.LogWarning("Chunk job worker could not claim or run its durable queue ({ExceptionType}).",
                    exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
