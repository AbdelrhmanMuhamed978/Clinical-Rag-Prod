using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using ClinicalRag.Api.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/chunks")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
public class ChunkReviewController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    public ChunkReviewController(AppDbContext db, ICurrentUser currentUser) { _db = db; _currentUser = currentUser; }
    private IActionResult Busy() => Conflict(new { error = "This document is being processed or changed. Refresh before reviewing again." });
    private static bool IsReviewStatus(string? status) => status == null || (Enum.TryParse<ChunkReviewStatus>(status, out var parsed)
        && Enum.IsDefined(parsed) && parsed.ToString() == status);
    private static object View(Chunk c) => new
    {
        c.Id, c.WorkspaceId, c.DocumentId, c.Page, c.PageStart, c.PageEnd,
        c.Section, c.SectionHierarchy, c.Year, c.Strength, c.Certainty,
        c.Text, c.Status, c.Confidence, c.Metadata, c.CreatedAt, c.ReviewedBy, c.ReviewedAt,
        c.Verbatim, c.Type, c.ValidationError
    };

    private async Task<IActionResult> SaveReviewAsync(Document document, object response)
    {
        await DocumentLifecycle.ReconcileAsync(_db, document);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Busy(); }
        return Ok(response);
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId, [FromQuery] Guid? documentId, [FromQuery] string? status,
        [FromQuery] bool includeEmbedded = false)
    {
        var q = _db.Chunks.AsNoTracking().Where(c => c.WorkspaceId == workspaceId);
        if (!includeEmbedded) q = q.Where(c => c.Status != "embedded");
        if (documentId.HasValue) q = q.Where(c => c.DocumentId == documentId.Value);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        return Ok((await q.OrderBy(c => c.CreatedAt).ToListAsync()).Select(View));
    }

    [HttpPatch("{id}")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Review(Guid workspaceId, Guid id, [FromBody] ReviewDto dto)
    {
        var c = await _db.Chunks.FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (c == null) return NotFound();
        if (!IsReviewStatus(dto.Status) || dto.Status == "embedded")
            return BadRequest(new { error = "Review status must be pending, accepted or rejected. Embedding is a server operation." });
        if ((dto.Text != null && string.IsNullOrWhiteSpace(dto.Text)) || (dto.Section?.Length ?? 0) > 256)
            return BadRequest(new { error = "Chunk text cannot be empty; section must be at most 256 characters." });
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == c.DocumentId && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        var textChanged = dto.Text != null && dto.Text != c.Text;
        var sectionChanged = dto.Section != null && dto.Section != c.Section;
        var statusChanged = dto.Status != null && dto.Status != c.Status;
        if (!textChanged && !sectionChanged && !statusChanged) return Ok(View(c));
        var actor = await _currentUser.GetUserIdAsync();
        if (actor == Guid.Empty) return Unauthorized();
        var wasReviewed = c.Status is "accepted" or "embedded";
        // Editing or changing review invalidates the old vector. The next Embed
        // must operate on these exact words, not the previous chunk text.
        c.Embedding = null;
        c.EmbeddingModel = null;
        c.LegacyEmbeddingJson = null;
        if (c.Status == "embedded") c.Status = "accepted";
        if (dto.Status != null) c.Status = dto.Status;
        if (wasReviewed && (textChanged || sectionChanged)) c.Status = "accepted";
        if (dto.Text != null) c.Text = dto.Text;
        if (dto.Section != null) c.Section = dto.Section;
        if (textChanged)
        {
            c.Verbatim = false;
            c.ValidationError = "Text was edited. Review it against the source PDF.";
        }
        c.ReviewedAt = DateTime.UtcNow;
        c.ReviewedBy = actor;
        return await SaveReviewAsync(doc, View(c));
    }

    [HttpPost("bulk-accept")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> BulkAccept(Guid workspaceId, [FromBody] BulkAcceptDto dto)
    {
        if (!dto.DocumentId.HasValue) return BadRequest("documentId is required.");
        if (dto.MinConfidence.HasValue && (!double.IsFinite(dto.MinConfidence.Value) || dto.MinConfidence.Value is < 0 or > 1))
            return BadRequest(new { error = "Confidence must be between 0 and 1." });
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == dto.DocumentId.Value && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        var q = _db.Chunks.Where(c => c.WorkspaceId == workspaceId && c.DocumentId == dto.DocumentId.Value && c.Status == "pending");
        if (dto.MinConfidence.HasValue) q = q.Where(c => (c.Confidence ?? 0) >= dto.MinConfidence.Value);
        var list = await q.ToListAsync();
        var actor = await _currentUser.GetUserIdAsync();
        if (actor == Guid.Empty) return Unauthorized();
        var now = DateTime.UtcNow;
        foreach (var c in list) { c.Status = "accepted"; c.Embedding = null; c.EmbeddingModel = null; c.LegacyEmbeddingJson = null; c.ReviewedAt = now; c.ReviewedBy = actor; }
        if (list.Count == 0) return Ok(new { accepted = 0 });
        return await SaveReviewAsync(doc, new { accepted = list.Count });
    }

    [HttpPost("bulk")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> BulkCreate(Guid workspaceId, [FromBody] List<ImportChunkDto> chunks)
    {
        if (chunks.Count > 1000 || chunks.Any(c => c == null || string.IsNullOrWhiteSpace(c.Text) || c.Page < 1
            || (c.PageStart ?? c.Page) < 1 || (c.PageEnd ?? c.Page) < (c.PageStart ?? c.Page)
            || c.Page < (c.PageStart ?? c.Page) || c.Page > (c.PageEnd ?? c.Page) || (c.Section?.Length ?? 0) > 256
            || (c.SectionHierarchy?.Length ?? 0) > 256 || (c.Strength?.Length ?? 0) > 32 || (c.Certainty?.Length ?? 0) > 32
            || c.Type is not ("clinical" or "front_matter")
            || (c.Confidence.HasValue && (!double.IsFinite(c.Confidence.Value) || c.Confidence.Value is < 0 or > 1))))
            return BadRequest(new { error = "Invalid chunk content or page ranges. Imports always begin pending review." });
        var documentIds = chunks.Select(c => c.DocumentId).Distinct().ToList();
        var docs = await _db.Documents.Where(d => d.WorkspaceId == workspaceId && documentIds.Contains(d.Id)).ToListAsync();
        if (docs.Count != documentIds.Count) return NotFound();
        if (docs.Any(DocumentLifecycle.IsBusy)) return Busy();
        if (chunks.Any(c => docs.Single(d => d.Id == c.DocumentId).PageCount is int count && count > 0 && (c.PageEnd ?? c.Page) > count))
            return BadRequest(new { error = "Imported page ranges must fit the source document." });
        // Copy only importable content. Never attach client-supplied navigations,
        // identities, vectors, or embedding/review provenance to this workspace.
        _db.Chunks.AddRange(chunks.Select(c => new Chunk
        {
            WorkspaceId = workspaceId, DocumentId = c.DocumentId, Page = c.Page,
            PageStart = c.PageStart, PageEnd = c.PageEnd, Section = c.Section,
            SectionHierarchy = c.SectionHierarchy, Year = c.Year, Strength = c.Strength,
            Certainty = c.Certainty, Text = c.Text, Status = "pending",
            Type = c.Type, Confidence = c.Confidence, Metadata = c.Metadata,
            Verbatim = false, ValidationError = "Imported text. Review it against the source PDF."
        }));
        foreach (var doc in docs) await DocumentLifecycle.ReconcileAsync(_db, doc);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Busy(); }
        return Ok(new { created = chunks.Count });
    }

    [HttpPost("merge")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Merge(Guid workspaceId, [FromBody] MergeDto dto)
    {
        if (dto.Ids == null || dto.Ids.Distinct().Count() < 2 || dto.Ids.Count > 1000) return BadRequest("need 2–1000 distinct chunks");
        var chunks = await _db.Chunks.Where(c => dto.Ids.Contains(c.Id) && c.WorkspaceId == workspaceId).ToListAsync();
        chunks = chunks.OrderBy(c => c.PageStart ?? c.Page).ThenBy(c => c.Page).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        if (chunks.Count != dto.Ids.Distinct().Count()) return NotFound();
        if (chunks.Count < 2) return BadRequest("need at least 2 chunks");
        var distinctDocs = chunks.Select(c => c.DocumentId).Distinct().ToList();
        if (distinctDocs.Count != 1) return BadRequest("all chunks must belong to the same document and workspace");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == distinctDocs[0] && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        var end = chunks[0].PageEnd ?? chunks[0].Page;
        foreach (var chunk in chunks)
        {
            var start = chunk.PageStart ?? chunk.Page;
            var chunkEnd = chunk.PageEnd ?? chunk.Page;
            if (start < 1 || chunkEnd < start || chunk.Page < start || chunk.Page > chunkEnd || (long)start > (long)end + 1
                || (doc.PageCount is > 0 && chunkEnd > doc.PageCount))
                return BadRequest(new { error = "Merge requires valid overlapping or adjacent source pages with no gaps." });
            end = Math.Max(end, chunkEnd);
        }
        var years = chunks.Where(c => c.Year.HasValue).Select(c => c.Year!.Value).Distinct().ToList();
        if (years.Count > 1) return BadRequest(new { error = "Chunks with different source years cannot be merged." });
        string? Combine(Func<Chunk, string?> field) => string.Join(" | ", chunks.Select(field).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()) is { Length: > 0 } value ? value : null;
        var section = Combine(c => c.Section); var hierarchy = Combine(c => c.SectionHierarchy);
        if ((section?.Length ?? 0) > 256 || (hierarchy?.Length ?? 0) > 256) return BadRequest(new { error = "Combined section metadata exceeds 256 characters; choose fewer chunks." });
        var actor = await _currentUser.GetUserIdAsync();
        if (actor == Guid.Empty) return Unauthorized();
        var now = DateTime.UtcNow;
        var merged = new Chunk
        {
            WorkspaceId = workspaceId,
            DocumentId = chunks[0].DocumentId,
            Page = chunks.Min(c => c.PageStart ?? c.Page),
            PageStart = chunks.Min(c => c.PageStart ?? c.Page), PageEnd = chunks.Max(c => c.PageEnd ?? c.Page),
            Section = section, SectionHierarchy = hierarchy, Year = years.Count == 1 ? years[0] : doc.Year,
            Strength = Strongest(chunks.Select(c => c.Strength), ["none", "weak", "conditional", "strong"]),
            Certainty = Strongest(chunks.Select(c => c.Certainty), ["none", "very_low", "low", "moderate", "high"]),
            Metadata = System.Text.Json.JsonSerializer.Serialize(new { mergedFrom = chunks.Select(c => new { c.Id, c.Page, c.PageStart, c.PageEnd, c.Section, c.SectionHierarchy, c.Year, c.Strength, c.Certainty, c.Metadata }) }),
            Text = string.Join("\n\n", chunks.Select(c => c.Text)),
            Status = "accepted",
            Confidence = chunks.Average(c => c.Confidence ?? 0), ReviewedBy = actor, ReviewedAt = now,
            Verbatim = false, ValidationError = "Merged text. Review it against the source PDF."
        };
        _db.Chunks.Add(merged);
        foreach (var c in chunks) { c.Status = "rejected"; c.Embedding = null; c.EmbeddingModel = null; c.LegacyEmbeddingJson = null; c.ReviewedBy = actor; c.ReviewedAt = now; }
        return await SaveReviewAsync(doc, View(merged));
    }

    private static string? Strongest(IEnumerable<string?> values, string[] ranking) => values.Where(v => !string.IsNullOrWhiteSpace(v))
        .OrderByDescending(v => Array.IndexOf(ranking, v!.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'))).FirstOrDefault();
}

public record ReviewDto(string? Status, string? Text, string? Section);
public record BulkAcceptDto(Guid? DocumentId, double? MinConfidence);
public record MergeDto(List<Guid> Ids);
public enum ChunkReviewStatus { pending, accepted, rejected, embedded }
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class ImportChunkDto
{
    public Guid DocumentId { get; set; }
    public int Page { get; set; }
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public string? Section { get; set; }
    public string? SectionHierarchy { get; set; }
    public int? Year { get; set; }
    public string? Strength { get; set; }
    public string? Certainty { get; set; }
    public string Text { get; set; } = "";
    public string Type { get; set; } = "clinical";
    public double? Confidence { get; set; }
    public string? Metadata { get; set; }
}
