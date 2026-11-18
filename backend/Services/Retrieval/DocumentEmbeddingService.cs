using System.Data;
using ClinicalRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed record DocumentEmbeddingResult(int StatusCode, int Embedded = 0, string? DocumentStatus = null, string? Error = null, string? Message = null,
    int? RetryAfterSeconds = null);

/// <summary>Claimed document operation with bounded reads and durable per-batch checkpoints.</summary>
public sealed class DocumentEmbeddingService(AppDbContext db, EmbeddingBatchClient embeddings)
{
    private const string BusyReason = "This document is being processed or changed. Refresh before trying again.";

    public async Task<DocumentEmbeddingResult> EmbedAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.WorkspaceId == workspaceId, cancellationToken);
        if (document == null) return new(404);
        if (DocumentLifecycle.IsBusy(document)) return new(409, Error: BusyReason);
        var pending = db.Chunks.Where(c => c.WorkspaceId == workspaceId && c.DocumentId == documentId
            && !c.ReReviewRequired
            && (c.Status == "accepted" || (c.Status == "embedded" && (c.Embedding == null || c.Embedding.Length < sizeof(float) || c.EmbeddingModel == null || c.EmbeddingModel == ""))));
        var total = await pending.CountAsync(cancellationToken);
        if (total == 0)
        {
            if (document.FailureStage == "embedding")
            {
                document.FailureStage = null; document.FailureReason = null; document.Status = "review";
            }
            await DocumentLifecycle.ReconcileAsync(db, document);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { return new(409, Error: BusyReason); }
            return new(200, DocumentStatus: document.Status, Message: "No accepted chunks need embedding.");
        }

        DocumentLifecycle.Begin(document, "embedding");
        document.EmbeddingTotal = total;
        document.EmbeddingCompleted = 0;
        document.EmbeddingBatchSize = embeddings.BatchSize;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return new(409, Error: BusyReason); }
        var operationRevision = document.Revision;
        var committed = 0;
        using var timing = OperationTelemetry.Start("documents.embed");
        string? model = null;
        int? dimensions = null;
        try
        {
            while (committed < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Persisted successes no longer match pending. Do not Skip over a
                // shrinking result set, load all chunks, or send unbounded texts.
                var chunks = await pending.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(embeddings.BatchSize).ToListAsync(cancellationToken);
                if (chunks.Count == 0) throw new InvalidDataException("Pending embedding set changed.");
                var batch = await embeddings.EmbedAsync(chunks.Select(c => c.Text).ToArray(), cancellationToken);
                if (model != null && (!StringComparer.Ordinal.Equals(model, batch.Model) || dimensions != batch.Dimensions))
                    throw new EmbeddingModelMismatchException();
                model = batch.Model; dimensions = batch.Dimensions;
                cancellationToken.ThrowIfCancellationRequested();
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
                try
                {
                    await EmbeddingModels.RequireCompatibleAsync(db, workspaceId, batch, cancellationToken);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        chunks[i].Embedding = batch.Vectors[i];
                        chunks[i].EmbeddingModel = batch.Model;
                        chunks[i].LegacyEmbeddingJson = null; // An explicit re-embed supersedes the archived vector.
                        chunks[i].Status = "embedded";
                    }
                    document.EmbeddingCompleted = committed + chunks.Count;
                    DocumentLifecycle.Touch(document);
                    await db.SaveChangesAsync(cancellationToken);
                    if (transaction != null) await transaction.CommitAsync(cancellationToken);
                    committed += chunks.Count;
                    operationRevision = document.Revision;
                    // Keep application memory/change detection bounded too, not
                    // just the GPU request. Committed rows are durable and no
                    // longer need tracking across every later batch.
                    foreach (var chunk in chunks) db.Entry(chunk).State = EntityState.Detached;
                }
                catch
                {
                    if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            document.ProcessingOperation = null;
            document.Status = "review";
            await DocumentLifecycle.ReconcileAsync(db, document);
            await db.SaveChangesAsync(cancellationToken);
            timing.Success();
            return new(200, committed, document.Status);
        }
        catch (Exception exception)
        {
            // Never save rolled-back/uncommitted vectors in the failure update,
            // or replace a recovered operation/new owner's checkpoint.
            db.ChangeTracker.Clear();
            document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.WorkspaceId == workspaceId);
            if (document == null || document.Revision != operationRevision || document.ProcessingOperation != "embedding")
                return new(409, committed, Error: BusyReason);
            var reason = exception switch
            {
                DependencyUnavailableException unavailable => unavailable.PublicMessage + " Completed batches were kept; retry Embed for remaining chunks.",
                EmbeddingModelMismatchException => "Embedding model or dimensions changed. Completed batches were kept. Re-embed the workspace with a single model.",
                DbUpdateConcurrencyException => "The workspace embedding model changed concurrently. Completed batches were kept; refresh and retry Embed.",
                InvalidDataException => "The embedding service returned invalid or mismatched vectors. Completed batches were kept; retry Embed for remaining chunks.",
                OperationCanceledException => "Embedding was interrupted or timed out. Completed batches were kept; retry Embed for remaining chunks.",
                _ => "Embedding failed. Completed batches were kept; retry Embed for remaining chunks when the service is available."
            };
            DocumentLifecycle.Fail(document, "embedding", reason);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return new(409, committed, Error: BusyReason); }
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) timing.Cancelled();
            return new(exception is DependencyUnavailableException ? 503 : exception is EmbeddingModelMismatchException or DbUpdateConcurrencyException ? 409 : 502,
                committed, document.Status, reason, RetryAfterSeconds: (exception as DependencyUnavailableException)?.RetryAfterSeconds);
        }
    }
}
