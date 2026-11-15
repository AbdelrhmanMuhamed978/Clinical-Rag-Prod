using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed record DocumentParseResult(string Outcome, int ParsedTextLength = 0, int PageCount = 0, string? Error = null,
    int StatusCode = 502, int? RetryAfterSeconds = null);

/// <summary>
/// Uploaded rows are the durable automatic-parse queue. A revision-bound claim
/// lets a worker and a legacy/retry request compete without parsing twice.
/// </summary>
public sealed class DocumentParser(AppDbContext db, IColabClient colab, IConfiguration configuration)
{
    private const string BusyMessage = "This document is being processed or changed. Refresh before trying again.";
    private int TimeoutSeconds => Math.Clamp(configuration.GetValue<int>("Colab:TimeoutSeconds", 300), 30, 3600);

    public async Task<DocumentParseResult> ParseAsync(Guid workspaceId, Guid id, CancellationToken cancellationToken = default)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, cancellationToken);
        if (document == null) return new("not_found");
        if (DocumentLifecycle.IsBusy(document)) return new("conflict", Error: BusyMessage);
        if (document.Status is not ("uploaded" or "failed") || !string.IsNullOrWhiteSpace(document.ParsedText)
            || await db.Chunks.AnyAsync(c => c.DocumentId == id && c.WorkspaceId == workspaceId, cancellationToken))
            return new("conflict", Error: "This document is already parsed. Choose pages and review chunks instead of parsing again.");

        DocumentLifecycle.Begin(document, "parsing");
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return new("conflict", Error: BusyMessage); }
        var claimedRevision = document.Revision;
        using var timing = OperationTelemetry.Start("documents.parse");

        try
        {
            var bytes = await File.ReadAllBytesAsync(document.StoragePath!, cancellationToken);
            var result = await colab.ParsePdfAsync(bytes, document.FileName, cancellationToken);
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String
                || !result.TryGetProperty("pageCount", out var pageValue) || pageValue.ValueKind != JsonValueKind.Number
                || !pageValue.TryGetInt32(out var pageCount) || pageCount <= 0)
                throw new InvalidDataException();
            var text = textValue.GetString()!;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException();
            cancellationToken.ThrowIfCancellationRequested();

            document.ParsedText = text;
            document.PageCount = pageCount;
            document.Status = "parsed";
            document.ProcessingOperation = null;
            document.ChunkingStatus = "not_started";
            document.ChunkedPagesCount = 0;
            document.ChunkedPageNumbers = "[]";
            DocumentLifecycle.Touch(document);
            await db.SaveChangesAsync(cancellationToken);
            timing.Success();
            return new("parsed", text.Length, pageCount);
        }
        catch (DbUpdateConcurrencyException) { return new("conflict", Error: BusyMessage); }
        catch (Exception exception)
        {
            // Reload before saving a failure: rolled-back/unsaved extracted text
            // must not accidentally make a failed document appear parsed.
            db.ChangeTracker.Clear();
            document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
            if (document == null || document.Revision != claimedRevision || document.ProcessingOperation != "parsing")
                return new("conflict", Error: BusyMessage);
            var reason = exception switch
            {
                DependencyUnavailableException unavailable => unavailable.PublicMessage + " Retry Parse when the service is available.",
                FileNotFoundException or DirectoryNotFoundException => "The uploaded file is unavailable. Delete this document and upload the PDF again.",
                InvalidDataException => "No extractable text was found or the parsing response was invalid. Check the PDF; scanned pages may need OCR before upload. Retry Parse.",
                OperationCanceledException => "Parsing was interrupted or timed out. Retry Parse.",
                HttpRequestException => "The PDF parsing service is unavailable. Retry Parse when the service is available.",
                _ => "The PDF could not be parsed. Check the PDF and retry Parse."
            };
            DocumentLifecycle.Fail(document, "parsing", reason);
            try { await db.SaveChangesAsync(); } // Persist even when the requester/host cancelled.
            catch (DbUpdateConcurrencyException) { return new("conflict", Error: BusyMessage); }
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) timing.Cancelled();
            return new("failed", Error: reason, StatusCode: exception is DependencyUnavailableException ? 503 : 502,
                RetryAfterSeconds: (exception as DependencyUnavailableException)?.RetryAfterSeconds);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        // A crashed process cannot leave an eternal spinner. Active steps have a
        // bounded service timeout and checkpoint UpdatedAt. Late writes from an
        // expired owner fail the revision check, rather than reviving stale work.
        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(660, TimeoutSeconds * 2 + 60));
        var stale = await db.Documents.Where(d => d.UpdatedAt < cutoff
            && (d.ProcessingOperation != null || d.Status == "parsing" || d.Status == "chunking"))
            .OrderBy(d => d.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        if (stale != null)
        {
            var stage = stale.ProcessingOperation ?? stale.Status;
            if (stage == "chunking") stale.ChunkingStatus = "failed";
            DocumentLifecycle.Fail(stale, stage,
                stage == "parsing" ? "Parsing was interrupted or timed out. Retry Parse."
                    : stage == "embedding" ? "Embedding was interrupted or timed out. Retry Embed."
                    : "Chunking was interrupted or timed out. Completed pages were kept. Choose the remaining pages to retry.");
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { /* Another owner checkpointed/recovered it first. */ }
            db.ChangeTracker.Clear();
        }

        var queued = await db.Documents.AsNoTracking().Where(d => d.Status == "uploaded" && d.ProcessingOperation == null
            && (d.ParsedText == null || d.ParsedText.Trim() == "")
            && !db.Chunks.Any(c => c.DocumentId == d.Id && c.WorkspaceId == d.WorkspaceId))
            .OrderBy(d => d.CreatedAt).Select(d => new { d.Id, d.WorkspaceId }).FirstOrDefaultAsync(cancellationToken);
        if (queued == null) return false;
        await ParseAsync(queued.WorkspaceId, queued.Id, cancellationToken);
        return true;
    }
}

public sealed class DocumentParsingWorker(IServiceScopeFactory scopes, ILogger<DocumentParsingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<DocumentParser>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Do not log response bodies, file paths or PDF contents.
                logger.LogWarning("Document parsing worker could not process its durable queue. It will retry.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
