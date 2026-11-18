using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed record DocumentChunkCounts(int Total, int Pending, int Accepted, int Rejected, int Embedded, int Other)
{
    public static readonly DocumentChunkCounts Empty = new(0, 0, 0, 0, 0, 0);
}

public sealed record DocumentView(
    Guid Id, Guid WorkspaceId, string FileName, string Status, int? PageCount,
    int ParsedTextLength, string? ParsedText, int? ChunkedPagesCount, string? ChunkingStatus,
    string? Society, int? Year, string GuidelineStatus, string? ProcessingOperation,
    string? FailureStage, string? FailureReason, int Revision,
    DateTime CreatedAt, DateTime UpdatedAt, DocumentChunkCounts ChunkCounts,
    int? EmbeddingTotal = null, int EmbeddingCompleted = 0, int? EmbeddingBatchSize = null,
    IReadOnlyList<int>? NeedsOcrPages = null, string? Title = null, string? Version = null,
    DateTime? EffectiveDate = null, DateTime? ReviewByDate = null, string? SourceUrl = null,
    string? PermissionNote = null);

/// <summary>
/// Document state is evidence of server work, never a client assertion. The revision
/// serializes operation claims with review/delete; all writers touch the same row.
/// </summary>
public static class DocumentLifecycle
{
    public static bool IsBusy(Document document) => !string.IsNullOrEmpty(document.ProcessingOperation)
        || document.Status is "parsing" or "chunking";

    public static IReadOnlyList<int> ParseNeedsOcr(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<int[]>(json)
                ?.Where(page => page > 0).Distinct().Order().ToArray() ?? [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public static string Status(string status, string? operation, string? failureReason,
        int parsedTextLength, DocumentChunkCounts counts, IReadOnlyList<int>? needsOcrPages = null)
    {
        if (operation is "parsing" or "chunking") return operation;
        if (operation == "embedding") return "review";
        if (status is "parsing" or "chunking") return status;
        if (status == "failed" || !string.IsNullOrEmpty(failureReason)) return "failed";
        // Accepted counts include legacy "embedded" rows whose vector is missing.
        // Pending/unknown rows mean review is unfinished, even if other chunks are indexed.
        if (counts.Embedded > 0 && counts.Accepted == 0 && counts.Pending == 0 && counts.Other == 0)
            return "indexed";
        if (counts.Total > 0) return "review";
        return parsedTextLength > 0 || needsOcrPages is { Count: > 0 } ? "parsed" : "uploaded";
    }

    public static DocumentChunkCounts Counts(IEnumerable<(string Status, bool HasEmbedding)> chunks)
    {
        var list = chunks.ToList();
        return new(list.Count,
            list.Count(c => c.Status == "pending"),
            list.Count(c => c.Status == "accepted" || (c.Status == "embedded" && !c.HasEmbedding)),
            list.Count(c => c.Status == "rejected"),
            list.Count(c => c.Status == "embedded" && c.HasEmbedding),
            list.Count(c => c.Status is not ("pending" or "accepted" or "rejected" or "embedded")));
    }

    public static void Touch(Document document)
    {
        document.Revision = checked(document.Revision + 1);
        document.UpdatedAt = DateTime.UtcNow;
    }

    public static void Begin(Document document, string operation)
    {
        document.ProcessingOperation = operation;
        document.Status = operation == "embedding" ? "review" : operation;
        if (document.FailureStage == operation || operation == "parsing" || document.FailureStage == null)
        {
            document.FailureReason = null;
            document.FailureStage = null;
        }
        Touch(document);
    }

    public static void Fail(Document document, string stage, string reason)
    {
        document.ProcessingOperation = null;
        document.Status = "failed";
        document.FailureStage = stage;
        document.FailureReason = reason;
        Touch(document);
    }

    public static async Task ReconcileAsync(AppDbContext db, Document document)
    {
        // Aggregate scalar evidence, not text/legacy JSON/vector columns, even for
        // a very large document. Overlay only this context's unsaved review edits.
        db.ChangeTracker.DetectChanges();
        var groups = await db.Chunks.AsNoTracking().Where(c => c.WorkspaceId == document.WorkspaceId && c.DocumentId == document.Id)
            .GroupBy(c => new { c.Status, HasEmbedding = c.Embedding != null && c.Embedding.Length >= sizeof(float) && c.EmbeddingModel != null && c.EmbeddingModel != "" })
            .Select(g => new { g.Key.Status, g.Key.HasEmbedding, Count = g.Count() }).ToListAsync();
        var states = groups.ToDictionary(g => (g.Status, g.HasEmbedding), g => g.Count);
        foreach (var entry in db.ChangeTracker.Entries<Chunk>().Where(e => e.Entity.WorkspaceId == document.WorkspaceId
            && e.Entity.DocumentId == document.Id && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.State != EntityState.Added)
            {
                var original = (entry.OriginalValues.GetValue<string>(nameof(Chunk.Status)),
                    entry.OriginalValues.GetValue<byte[]?>(nameof(Chunk.Embedding)) is { Length: >= sizeof(float) }
                    && EmbeddingVectors.ValidModel(entry.OriginalValues.GetValue<string?>(nameof(Chunk.EmbeddingModel))));
                states[original] = states.GetValueOrDefault(original) - 1;
            }
            if (entry.State != EntityState.Deleted)
            {
                var current = (entry.Entity.Status, entry.Entity.Embedding is { Length: >= sizeof(float) } && EmbeddingVectors.ValidModel(entry.Entity.EmbeddingModel));
                states[current] = states.GetValueOrDefault(current) + 1;
            }
        }
        int Sum(Func<(string Status, bool HasEmbedding), bool> predicate) => states.Where(pair => predicate(pair.Key)).Sum(pair => pair.Value);
        var counts = new DocumentChunkCounts(Sum(_ => true), Sum(s => s.Status == "pending"),
            Sum(s => s.Status == "accepted" || (s.Status == "embedded" && !s.HasEmbedding)), Sum(s => s.Status == "rejected"),
            Sum(s => s.Status == "embedded" && s.HasEmbedding), Sum(s => s.Status is not ("pending" or "accepted" or "rejected" or "embedded")));
        document.Status = Status(document.Status, document.ProcessingOperation, document.FailureReason,
            document.ParsedText?.Length ?? 0, counts, ParseNeedsOcr(document.NeedsOcrPages));
        Touch(document);
    }

    public static DocumentView View(Document document, DocumentChunkCounts counts, bool includeText = false)
    {
        var length = document.ParsedText?.Length ?? 0;
        var needsOcr = ParseNeedsOcr(document.NeedsOcrPages);
        return new(document.Id, document.WorkspaceId, document.FileName,
            Status(document.Status, document.ProcessingOperation, document.FailureReason, length, counts, needsOcr),
            document.PageCount, length, includeText ? document.ParsedText : null,
            document.ChunkedPagesCount, document.ChunkingStatus, document.Society, document.Year,
            document.GuidelineStatus, document.ProcessingOperation, document.FailureStage,
            document.FailureReason, document.Revision, document.CreatedAt, document.UpdatedAt, counts,
            document.EmbeddingTotal, document.EmbeddingCompleted, document.EmbeddingBatchSize, needsOcr,
            document.Title, document.Version, document.EffectiveDate, document.ReviewByDate,
            document.SourceUrl, document.PermissionNote);
    }
}
