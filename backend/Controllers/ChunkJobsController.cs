using System.Text.Json;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId:guid}")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
public sealed class ChunkJobsController(AppDbContext db, ICurrentUser currentUser) : ControllerBase
{
    private static IActionResult Busy(string message) => new ConflictObjectResult(new { error = message });
    private static bool IsTerminal(string status) => ChunkJobSupport.TerminalStatuses.Contains(status);

    [HttpGet("documents/{id:guid}/chunk-jobs/active")]
    public async Task<IActionResult> Active(Guid workspaceId, Guid id)
    {
        if (!await DocumentExistsAsync(workspaceId, id)) return NotFound();
        var job = await db.ChunkJobs.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.DocumentId == id
                && ChunkJobSupport.ActiveStatuses.Contains(item.Status))
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync();
        return Ok(new { job = job == null ? null : ChunkJobSupport.View(job) });
    }

    [HttpGet("documents/{id:guid}/chunk-jobs")]
    public async Task<IActionResult> List(Guid workspaceId, Guid id)
    {
        if (!await DocumentExistsAsync(workspaceId, id)) return NotFound();
        var jobs = await db.ChunkJobs.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.DocumentId == id)
            .OrderByDescending(item => item.CreatedAt).Take(20).ToListAsync();
        return Ok(new { jobs = jobs.Select(ChunkJobSupport.View).ToArray() });
    }

    [HttpGet("chunk-jobs/{jobId:guid}")]
    public async Task<IActionResult> Get(Guid workspaceId, Guid jobId)
    {
        var job = await db.ChunkJobs.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == jobId && item.WorkspaceId == workspaceId);
        return job == null ? NotFound() : Ok(ChunkJobSupport.View(job));
    }

    [HttpPost("documents/{id:guid}/chunk-jobs")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Start(Guid workspaceId, Guid id, [FromBody] StartChunkJobDto dto)
    {
        if (!ValidSettings(dto.BatchSize, dto.MaxTokens, dto.BatchTimeoutSeconds, dto.MaxRetries, dto.FailureMode,
            out var error)) return BadRequest(new { error });
        if (dto.Pages is null || dto.Pages.Length is < 1 or > 1000)
            return BadRequest(new { error = "Choose 1–1000 pages." });
        if ((dto.Guidance?.Length ?? 0) > 4000)
            return BadRequest(new { error = "Guidance must be at most 4000 characters." });

        var doc = await db.Documents.FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (string.IsNullOrWhiteSpace(doc.ParsedText)) return BadRequest(new { error = "Document not parsed yet." });
        if (DocumentLifecycle.IsBusy(doc)) return Busy("This document is being processed or changed. Refresh before trying again.");

        var active = await db.ChunkJobs.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id != Guid.Empty && item.DocumentId == id
                && item.WorkspaceId == workspaceId && ChunkJobSupport.ActiveStatuses.Contains(item.Status));
        if (active != null) return Busy("This document already has an active chunk job.");

        var pages = ChunkJobSupport.ParsePages(doc.ParsedText)
            .GroupBy(page => page.Page).ToDictionary(group => group.Key, group => group.First());
        var alreadyChunked = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers).ToHashSet();
        var requested = dto.Pages.Distinct().Order().ToArray();
        var invalid = requested.Where(page => !pages.ContainsKey(page)).ToArray();
        if (invalid.Length > 0) return BadRequest(new { error = $"Invalid page numbers: {string.Join(", ", invalid)}" });
        var pagesToChunk = requested.Where(page => !alreadyChunked.Contains(page)).ToArray();
        if (pagesToChunk.Length == 0) return Conflict(new { error = "All requested pages are already chunked." });

        var reviewedRanges = await db.Chunks.AsNoTracking()
            .Where(chunk => chunk.WorkspaceId == workspaceId && chunk.DocumentId == id
                && (chunk.ReReviewRequired || chunk.Status == "accepted" || chunk.Status == "embedded"))
            .Select(chunk => new { Start = chunk.PageStart ?? chunk.Page, End = chunk.PageEnd ?? chunk.Page })
            .ToListAsync();
        if (reviewedRanges.Any(range => pagesToChunk.Any(page => page >= range.Start && page <= range.End)))
            return Conflict(new { error = "Some selected pages overlap reviewed chunks. Use Reset selected pages before replacing them." });

        var userId = await currentUser.GetUserIdAsync();
        var guidance = string.IsNullOrWhiteSpace(dto.Guidance) ? ChunkJobSupport.DefaultGuidance : dto.Guidance.Trim();
        var job = new ChunkJob
        {
            WorkspaceId = workspaceId, DocumentId = id, Status = "queued",
            PagesJson = ChunkJobSupport.WritePages(pagesToChunk),
            Guidance = guidance,
            BatchSize = dto.BatchSize, MaxTokens = dto.MaxTokens,
            BatchTimeoutSeconds = dto.BatchTimeoutSeconds, MaxRetries = dto.MaxRetries,
            FailureMode = dto.FailureMode, TotalBatches = (int)Math.Ceiling(pagesToChunk.Length / (double)dto.BatchSize),
            CreatedBy = userId
        };
        db.ChunkJobs.Add(job);
        db.ChunkerGuidances.Add(new ChunkerGuidance { DocumentId = id, Guidance = guidance });
        doc.ChunkingStatus = "in_progress";
        DocumentLifecycle.Begin(doc, "chunking");
        if (!await SaveOrConflictAsync())
            return Busy("This document changed while the job was being created. Refresh and try again.");
        return Accepted(new { jobId = job.Id, job.Status, pollAfterMs = 2000 });
    }

    [HttpPost("chunk-jobs/{jobId:guid}/pause")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Pause(Guid workspaceId, Guid jobId)
    {
        var job = await LoadJob(workspaceId, jobId);
        if (job == null) return NotFound();
        if (job.Status == "running")
        {
            job.PauseRequested = true;
            job.Status = "pausing";
        }
        else if (job.Status == "queued")
        {
            job.Status = "paused";
        }
        else if (job.Status != "paused")
            return Busy("Only a queued or running chunk job can be paused.");
        if (!await SaveOrConflictAsync())
            return Busy("The job changed while pausing. Refresh and try again.");
        return Ok(ChunkJobSupport.View(job));
    }

    [HttpPost("chunk-jobs/{jobId:guid}/resume")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Resume(Guid workspaceId, Guid jobId)
    {
        var job = await LoadJob(workspaceId, jobId);
        if (job == null) return NotFound();
        if (job.Status is not ("paused" or "interrupted" or "blocked"))
            return Busy("Only a paused, interrupted or blocked chunk job can be resumed.");
        var doc = await db.Documents.FirstOrDefaultAsync(item => item.Id == job.DocumentId
            && item.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        job.Status = "queued";
        job.PauseRequested = false;
        job.CancelRequested = false;
        job.ErrorCode = null;
        job.CompletedAt = null;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.Revision = checked(job.Revision + 1);
        doc.ChunkingStatus = "in_progress";
        DocumentLifecycle.Begin(doc, "chunking");
        if (!await SaveOrConflictAsync())
            return Busy("The job changed while it was being resumed. Refresh and try again.");
        return Accepted(new { jobId = job.Id, status = job.Status, pollAfterMs = 2000 });
    }

    [HttpPost("chunk-jobs/{jobId:guid}/cancel")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Cancel(Guid workspaceId, Guid jobId)
    {
        var job = await LoadJob(workspaceId, jobId);
        if (job == null) return NotFound();
        var doc = await db.Documents.FirstOrDefaultAsync(item => item.Id == job.DocumentId
            && item.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (job.Status is "queued" or "paused" or "interrupted" or "blocked")
        {
            job.Status = "cancelled";
            job.CompletedAt = DateTime.UtcNow;
            job.CancelRequested = false;
            job.PauseRequested = false;
            job.LeaseOwner = null;
            job.LeaseUntil = null;
            job.Revision = checked(job.Revision + 1);
            var completed = ChunkJobSupport.ReadPages(job.CompletedPagesJson);
            doc.ProcessingOperation = null;
            doc.ChunkingStatus = completed.Length > 0 ? "partial" : "not_started";
            doc.Status = "parsed";
            DocumentLifecycle.Touch(doc);
            await DocumentLifecycle.ReconcileAsync(db, doc);
        }
        else if (job.Status is "running" or "pausing" or "stopping")
        {
            job.CancelRequested = true;
            job.Status = "stopping";
        }
        else return Busy("Only an active chunk job can be cancelled.");
        if (!await SaveOrConflictAsync())
            return Busy("The job changed while cancellation was requested. Refresh and try again.");
        return Ok(ChunkJobSupport.View(job));
    }

    [HttpPost("chunk-jobs/{jobId:guid}/retry")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Retry(Guid workspaceId, Guid jobId, [FromBody] RetryChunkJobDto dto)
    {
        var old = await db.ChunkJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId
            && item.WorkspaceId == workspaceId);
        if (old == null) return NotFound();
        if (!IsTerminal(old.Status)) return Busy("Retry is available after the job has stopped.");
        var active = await db.ChunkJobs.AsNoTracking().FirstOrDefaultAsync(item => item.DocumentId == old.DocumentId
            && item.WorkspaceId == workspaceId && ChunkJobSupport.ActiveStatuses.Contains(item.Status));
        if (active != null) return Busy("This document already has an active chunk job.");
        var doc = await db.Documents.FirstOrDefaultAsync(item => item.Id == old.DocumentId && item.WorkspaceId == workspaceId);
        if (doc == null || string.IsNullOrWhiteSpace(doc.ParsedText)) return BadRequest(new { error = "Document is not parsed." });
        if (DocumentLifecycle.IsBusy(doc)) return Busy("This document is being processed or changed. Refresh before trying again.");

        var completed = ChunkJobSupport.ReadPages(old.CompletedPagesJson).ToHashSet();
        var skipped = ChunkJobSupport.ReadPages(old.SkippedPagesJson).ToHashSet();
        var failed = ChunkJobSupport.ReadPages(old.FailedPagesJson).ToHashSet();
        var requested = ChunkJobSupport.ReadPages(old.PagesJson);
        var pages = dto.Scope switch
        {
            "all" => requested,
            "remaining" => requested.Where(page => !completed.Contains(page) && !skipped.Contains(page)).ToArray(),
            _ => failed.ToArray()
        };
        if (pages.Length == 0) return Conflict(new { error = "There are no pages to retry for that scope." });

        var chunked = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers).ToHashSet();
        pages = pages.Where(page => !chunked.Contains(page)).ToArray();
        if (pages.Length == 0) return Conflict(new { error = "The selected retry pages are already chunked." });

        var reviewedRanges = await db.Chunks.AsNoTracking()
            .Where(chunk => chunk.WorkspaceId == workspaceId && chunk.DocumentId == old.DocumentId
                && (chunk.ReReviewRequired || chunk.Status == "accepted" || chunk.Status == "embedded"))
            .Select(chunk => new { Start = chunk.PageStart ?? chunk.Page, End = chunk.PageEnd ?? chunk.Page })
            .ToListAsync();
        if (reviewedRanges.Any(range => pages.Any(page => page >= range.Start && page <= range.End)))
            return Conflict(new { error = "Some retry pages overlap reviewed chunks. Use Reset selected pages before replacing them." });

        var userId = await currentUser.GetUserIdAsync();
        var job = new ChunkJob
        {
            WorkspaceId = workspaceId, DocumentId = old.DocumentId, Status = "queued",
            PagesJson = ChunkJobSupport.WritePages(pages), Guidance = old.Guidance,
            BatchSize = old.BatchSize, MaxTokens = old.MaxTokens,
            BatchTimeoutSeconds = old.BatchTimeoutSeconds, MaxRetries = old.MaxRetries,
            FailureMode = old.FailureMode, TotalBatches = (int)Math.Ceiling(pages.Length / (double)old.BatchSize),
            CreatedBy = userId, RetryOfJobId = old.Id
        };
        db.ChunkJobs.Add(job);
        doc.ChunkingStatus = "in_progress";
        DocumentLifecycle.Begin(doc, "chunking");
        if (!await SaveOrConflictAsync())
            return Busy("This document changed while the retry was being created. Refresh and try again.");
        return Accepted(new { jobId = job.Id, status = job.Status, pollAfterMs = 2000 });
    }

    private async Task<bool> SaveOrConflictAsync()
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private async Task<ChunkJob?> LoadJob(Guid workspaceId, Guid jobId) =>
        await db.ChunkJobs.FirstOrDefaultAsync(item => item.Id == jobId && item.WorkspaceId == workspaceId);

    private Task<bool> DocumentExistsAsync(Guid workspaceId, Guid documentId) =>
        db.Documents.AsNoTracking().AnyAsync(item => item.Id == documentId && item.WorkspaceId == workspaceId);

    private static bool ValidSettings(int batchSize, int maxTokens, int timeoutSeconds, int maxRetries,
        string failureMode, out string error)
    {
        if (batchSize is < 1 or > 10) { error = "Batch size must be between 1 and 10."; return false; }
        if (maxTokens is < 100 or > 2000) { error = "Max tokens must be between 100 and 2000."; return false; }
        if (timeoutSeconds is < 60 or > 600) { error = "Batch timeout must be between 60 and 600 seconds."; return false; }
        if (maxRetries is < 0 or > 3) { error = "Retries must be between 0 and 3."; return false; }
        if (failureMode is not ("continue" or "stop")) { error = "Failure mode must be continue or stop."; return false; }
        error = "";
        return true;
    }
}

public record StartChunkJobDto(int[] Pages, string? Guidance, int BatchSize = 2, int MaxTokens = 400,
    int BatchTimeoutSeconds = 300, int MaxRetries = 1, string FailureMode = "continue");

public record RetryChunkJobDto(string Scope = "failed");
