using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

/// <summary>
/// Executes one claimed chunk job in batches. The job record is reloaded between
/// batches so pause/cancel requests are observed without aborting an in-flight batch.
/// </summary>
public sealed class ChunkJobProcessor(
    AppDbContext db, IColabClient colab, ILogger<ChunkJobProcessor> logger)
{
    public async Task ProcessAsync(Guid jobId, string leaseOwner, CancellationToken stoppingToken)
    {
        var job = await db.ChunkJobs.FirstOrDefaultAsync(item => item.Id == jobId, stoppingToken);
        // Pausing/stopping can be requested between the worker's claim and this
        // first load. The lease owner must still observe them and finalize the
        // request instead of returning and leaving the job stuck.
        if (job == null || job.Status is not ("running" or "pausing" or "stopping")
            || job.LeaseOwner != leaseOwner) return;

        var doc = await db.Documents.FirstOrDefaultAsync(item => item.Id == job.DocumentId
            && item.WorkspaceId == job.WorkspaceId, stoppingToken);
        if (doc == null)
        {
            await FinishAsync(job, null, "failed", "worker_error", stoppingToken);
            return;
        }

        var pageMap = ChunkJobSupport.ParsePages(doc.ParsedText ?? "")
            .GroupBy(page => page.Page).ToDictionary(group => group.Key, group => group.First());
        var requested = ChunkJobSupport.ReadPages(job.PagesJson);
        if (requested.Length == 0 || requested.Any(page => !pageMap.ContainsKey(page)))
        {
            await FinishAsync(job, doc, "failed", "invalid_batch_result", stoppingToken);
            return;
        }

        job.TotalBatches = Math.Max(1, (int)Math.Ceiling(requested.Length / (double)job.BatchSize));
        try
        {
            while (true)
            {
                await db.Entry(job).ReloadAsync(stoppingToken);
                if (job.CancelRequested || job.Status == "stopping")
                {
                    await FinishCancelledAsync(job, doc, stoppingToken);
                    return;
                }
                if (job.PauseRequested || job.Status == "pausing")
                {
                    await PauseAsync(job, stoppingToken);
                    return;
                }

                var completed = ChunkJobSupport.ReadPages(job.CompletedPagesJson);
                var failed = ChunkJobSupport.ReadPages(job.FailedPagesJson);
                var skipped = ChunkJobSupport.ReadPages(job.SkippedPagesJson);
                var remaining = requested
                    .Where(page => !completed.Contains(page) && !failed.Contains(page) && !skipped.Contains(page))
                    .ToList();
                if (remaining.Count == 0)
                {
                    await FinishCompletedAsync(job, doc, stoppingToken);
                    return;
                }

                var batchPages = remaining.Take(job.BatchSize).ToList();
                var batch = batchPages.Select(page => pageMap[page]).ToList();
                job.CurrentBatchStart = batchPages[0];
                job.CurrentBatchEnd = batchPages[^1];
                job.LastHeartbeatAt = DateTime.UtcNow;
                // Retries each get their own timeout, so the lease must cover all attempts.
                job.LeaseUntil = DateTime.UtcNow.AddSeconds(
                    (job.MaxRetries + 1L) * job.BatchTimeoutSeconds + 120);
                job.Revision = checked(job.Revision + 1);
                await db.SaveChangesAsync(stoppingToken);

                ChunkBatchOutcome? outcome;
                string? failureCode;
                try
                {
                    (outcome, failureCode) = await RunBatchWithRetriesAsync(job, doc, batch, stoppingToken);
                }
                catch (DependencyUnavailableException unavailable)
                {
                    await FinishBlockedAsync(job, doc,
                        unavailable.FailureCode == "timeout" ? "batch_timeout" : "colab_unavailable",
                        stoppingToken);
                    return;
                }

                job.CompletedBatches = Math.Min(job.TotalBatches, job.CompletedBatches + 1);
                job.CurrentBatchStart = null;
                job.CurrentBatchEnd = null;
                job.LastHeartbeatAt = DateTime.UtcNow;
                job.Revision = checked(job.Revision + 1);
                if (outcome == null)
                {
                    var existingFailed = ChunkJobSupport.ReadPages(job.FailedPagesJson);
                    job.FailedPagesJson = ChunkJobSupport.WritePages(existingFailed.Concat(batchPages));
                    job.ErrorCode = failureCode ?? "batch_failed";
                    await db.SaveChangesAsync(stoppingToken);
                    if (job.FailureMode == "stop")
                    {
                        await FinishWithFailuresAsync(job, doc, stoppingToken);
                        return;
                    }
                    continue;
                }

                // A page that is neither chunked nor explicitly skipped must not stay
                // "remaining" forever. Record it as failed so the job can terminate and
                // a retry can target it.
                var accounted = outcome.CompletedPages.Concat(outcome.SkippedPages).ToHashSet();
                var unaccounted = batchPages.Where(page => !accounted.Contains(page)).ToList();
                if (unaccounted.Count > 0)
                {
                    var existingFailed = ChunkJobSupport.ReadPages(job.FailedPagesJson);
                    job.FailedPagesJson = ChunkJobSupport.WritePages(existingFailed.Concat(unaccounted));
                    job.ErrorCode = "empty_batch_result";
                    await db.SaveChangesAsync(stoppingToken);
                    if (job.FailureMode == "stop")
                    {
                        await FinishWithFailuresAsync(job, doc, stoppingToken);
                        return;
                    }
                    continue;
                }

                // Keep an earlier failed-batch code: a later success must not make a
                // partial job look like it timed out or failed for no recorded reason.
                await db.SaveChangesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never persist an upstream body or exception text in the job.
            logger.LogWarning("Chunk job {JobId} failed with {ExceptionType}", job.Id, exception.GetType().Name);
            await FinishAsync(job, doc, "failed", "worker_error", CancellationToken.None);
        }
    }

    private async Task<(ChunkBatchOutcome? Outcome, string? FailureCode)> RunBatchWithRetriesAsync(
        ChunkJob job, Document doc, IReadOnlyList<ChunkPageText> batch, CancellationToken stoppingToken)
    {
        string? failureCode = null;
        for (var attempt = 0; attempt <= job.MaxRetries; attempt++)
        {
            try
            {
                var outcome = await ChunkBatchProcessor.ProcessAsync(db, colab, doc, job, batch, stoppingToken);
                return (outcome, null);
            }
            catch (DependencyUnavailableException exception) when (exception.FailureCode == "timeout")
            {
                failureCode = "batch_timeout";
            }
            catch (ChunkBatchException exception)
            {
                failureCode = exception.Code;
            }

            if (attempt < job.MaxRetries)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt * 2)), stoppingToken);
        }
        return (null, failureCode);
    }

    private async Task PauseAsync(ChunkJob job, CancellationToken cancellationToken)
    {
        job.Status = "paused";
        job.PauseRequested = false;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.LastHeartbeatAt = DateTime.UtcNow;
        job.Revision = checked(job.Revision + 1);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishCancelledAsync(ChunkJob job, Document doc, CancellationToken cancellationToken)
    {
        var completed = ChunkJobSupport.ReadPages(job.CompletedPagesJson);
        job.Status = "cancelled";
        job.CompletedAt = DateTime.UtcNow;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.PauseRequested = false;
        job.CancelRequested = false;
        job.CurrentBatchStart = null;
        job.CurrentBatchEnd = null;
        job.Revision = checked(job.Revision + 1);
        doc.ProcessingOperation = null;
        doc.ChunkingStatus = completed.Length > 0 ? "partial" : "not_started";
        doc.Status = "parsed";
        DocumentLifecycle.Touch(doc);
        await DocumentLifecycle.ReconcileAsync(db, doc);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishCompletedAsync(ChunkJob job, Document doc, CancellationToken cancellationToken)
    {
        var failedPages = ChunkJobSupport.ReadPages(job.FailedPagesJson);
        if (failedPages.Length > 0)
        {
            await FinishWithFailuresAsync(job, doc, cancellationToken);
            return;
        }
        var completed = ChunkJobSupport.ReadPages(job.CompletedPagesJson);
        var skipped = ChunkJobSupport.ReadPages(job.SkippedPagesJson);
        var allPages = ChunkJobSupport.ParsePages(doc.ParsedText ?? "").Select(page => page.Page).ToHashSet();
        // A job covers only its selected pages. Preserve pages that were already
        // processed before it started; never replace the document checkpoint.
        var checkpoint = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers)
            .Concat(completed).Concat(skipped).Distinct().Order().ToArray();
        doc.ChunkedPagesCount = checkpoint.Length;
        doc.ChunkedPageNumbers = ChunkJobSupport.WritePages(checkpoint);
        doc.ChunkingStatus = checkpoint.Length >= allPages.Count ? "completed"
            : checkpoint.Length > 0 ? "partial" : "not_started";
        job.Status = "completed";
        job.CompletedAt = DateTime.UtcNow;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.PauseRequested = false;
        job.CancelRequested = false;
        job.CurrentBatchStart = null;
        job.CurrentBatchEnd = null;
        job.Revision = checked(job.Revision + 1);
        doc.ProcessingOperation = null;
        doc.Status = "parsed";
        DocumentLifecycle.Touch(doc);
        await DocumentLifecycle.ReconcileAsync(db, doc);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishWithFailuresAsync(ChunkJob job, Document doc, CancellationToken cancellationToken)
    {
        var completed = ChunkJobSupport.ReadPages(job.CompletedPagesJson);
        var failed = ChunkJobSupport.ReadPages(job.FailedPagesJson);
        var skipped = ChunkJobSupport.ReadPages(job.SkippedPagesJson);
        var checkpoint = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers)
            .Concat(completed).Concat(skipped).Distinct().Order().ToArray();
        job.Status = completed.Length > 0 ? "partial" : "failed";
        job.ErrorCode ??= "batch_failed";
        job.CompletedAt = DateTime.UtcNow;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.PauseRequested = false;
        job.CancelRequested = false;
        job.CurrentBatchStart = null;
        job.CurrentBatchEnd = null;
        job.Revision = checked(job.Revision + 1);
        doc.ChunkedPagesCount = checkpoint.Length;
        doc.ChunkedPageNumbers = ChunkJobSupport.WritePages(checkpoint);
        doc.ChunkingStatus = "failed";
        var pageLabel = string.Join(", ", failed.Distinct().Order().Take(20));
        var reason = $"Could not chunk pages {pageLabel}{(failed.Length > 20 ? ", …" : "")}. Completed pages were kept. Retry the failed pages or stop after the current batch.";
        DocumentLifecycle.Fail(doc, "chunking", reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishBlockedAsync(ChunkJob job, Document doc, string code, CancellationToken cancellationToken)
    {
        job.Status = "blocked";
        job.ErrorCode = code;
        job.CompletedAt = null;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.CurrentBatchStart = null;
        job.CurrentBatchEnd = null;
        job.Revision = checked(job.Revision + 1);
        DocumentLifecycle.Fail(doc, "chunking", ChunkJobSupport.SafeFailureReason(code));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishAsync(ChunkJob job, Document? doc, string status, string code,
        CancellationToken cancellationToken)
    {
        job.Status = status;
        job.ErrorCode = code;
        job.CompletedAt = DateTime.UtcNow;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.Revision = checked(job.Revision + 1);
        if (doc != null) DocumentLifecycle.Fail(doc, "chunking", ChunkJobSupport.SafeFailureReason(code));
        await db.SaveChangesAsync(cancellationToken);
    }
}
