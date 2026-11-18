using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using ClinicalRag.Api.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/documents")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
public class DocumentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IColabClient _colab;
    private readonly IConfiguration _cfg;
    private readonly ICurrentUser _currentUser;
    private readonly DocumentParser _parser;
    private readonly DocumentEmbeddingService _embeddings;
    public DocumentsController(AppDbContext db, IColabClient colab, IConfiguration cfg, ICurrentUser currentUser, DocumentParser parser,
        DocumentEmbeddingService? embeddings = null)
    {
        _db = db; _colab = colab; _cfg = cfg; _currentUser = currentUser; _parser = parser;
        _embeddings = embeddings ?? new DocumentEmbeddingService(db, new EmbeddingBatchClient(colab, cfg));
    }

    private string UploadsDirectory => StoredDocumentFiles.DirectoryPath(_cfg);
    private const long MaxPdfBytes = 50 * 1024 * 1024;
    private IActionResult Busy() => Conflict(new { error = "This document is being processed or changed. Refresh before trying again." });

    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId)
    {
        // Polling must not repeatedly download PDFs' extracted text or storage paths.
        var docs = await _db.Documents.AsNoTracking().Where(d => d.WorkspaceId == workspaceId)
            .OrderByDescending(d => d.CreatedAt).Select(d => new
            {
                d.Id, d.WorkspaceId, d.FileName, d.Status, d.PageCount,
                ParsedTextLength = d.ParsedText == null ? 0 : d.ParsedText.Length,
                d.ChunkedPagesCount, d.ChunkingStatus, d.Society, d.Year, d.GuidelineStatus,
                d.ProcessingOperation, d.FailureStage, d.FailureReason, d.Revision,
                d.CreatedAt, d.UpdatedAt, d.EmbeddingTotal, d.EmbeddingCompleted, d.EmbeddingBatchSize, d.NeedsOcrPages,
                d.Title, d.Version, d.EffectiveDate, d.ReviewByDate, d.SourceUrl, d.PermissionNote
            }).ToListAsync();
        var counts = await ReadCountsAsync(workspaceId);
        // Build the DTO after materialization; EF must project only scalar columns,
        // not capture the shared counts object in its relational query plan.
        return Ok(docs.Select(d => WithCounts(new DocumentView(
            d.Id, d.WorkspaceId, d.FileName, d.Status, d.PageCount, d.ParsedTextLength, null,
            d.ChunkedPagesCount, d.ChunkingStatus, d.Society, d.Year, d.GuidelineStatus,
            d.ProcessingOperation, d.FailureStage, d.FailureReason, d.Revision,
            d.CreatedAt, d.UpdatedAt, DocumentChunkCounts.Empty, d.EmbeddingTotal, d.EmbeddingCompleted, d.EmbeddingBatchSize,
            DocumentLifecycle.ParseNeedsOcr(d.NeedsOcrPages), d.Title, d.Version, d.EffectiveDate, d.ReviewByDate,
            d.SourceUrl, d.PermissionNote),
            counts.GetValueOrDefault(d.Id, DocumentChunkCounts.Empty))));
    }

    private static DocumentView WithCounts(DocumentView doc, DocumentChunkCounts counts) => doc with
    {
        ChunkCounts = counts,
        Status = DocumentLifecycle.Status(doc.Status, doc.ProcessingOperation, doc.FailureReason, doc.ParsedTextLength, counts,
            doc.NeedsOcrPages)
    };

    private async Task<Dictionary<Guid, DocumentChunkCounts>> ReadCountsAsync(Guid workspaceId, Guid? documentId = null)
    {
        var query = _db.Chunks.AsNoTracking().Where(c => c.WorkspaceId == workspaceId);
        if (documentId.HasValue) query = query.Where(c => c.DocumentId == documentId.Value);
        var states = await query.Select(c => new
        {
            c.DocumentId, c.Status, HasEmbedding = c.Embedding != null && c.Embedding.Length >= sizeof(float) && c.EmbeddingModel != null && c.EmbeddingModel != ""
        }).ToListAsync();
        return states.GroupBy(c => c.DocumentId).ToDictionary(g => g.Key,
            g => DocumentLifecycle.Counts(g.Select(c => (c.Status, c.HasEmbedding))));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid workspaceId, Guid id)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        var counts = await ReadCountsAsync(workspaceId, id);
        return Ok(DocumentLifecycle.View(doc, counts.GetValueOrDefault(id, DocumentChunkCounts.Empty), includeText: true));
    }

    [HttpPost]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    [RequestSizeLimit(MaxPdfBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid workspaceId, [FromForm] IFormFile? file,
        [FromForm] string? society, [FromForm] string? title, [FromForm] string? version,
        [FromForm] int? year, [FromForm] string? guidelineStatus,
        [FromForm] DateTime? effectiveDate, [FromForm] DateTime? reviewByDate,
        [FromForm] string? sourceUrl, [FromForm] string? permissionNote)
    {
        var fileName = Path.GetFileName(file?.FileName ?? "");
        society = society?.Trim();
        title = title?.Trim();
        version = version?.Trim();
        guidelineStatus = guidelineStatus?.Trim().ToLowerInvariant();
        sourceUrl = sourceUrl?.Trim();
        permissionNote = permissionNote?.Trim();
        if (file == null || file.Length < 5 || file.Length > MaxPdfBytes
            || fileName.Length > 256 || fileName.Any(char.IsControl)
            || !Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Choose a non-empty PDF of at most 50 MB." });
        if (string.IsNullOrWhiteSpace(society) || society.Length > 256 || society.Any(char.IsControl)
            || year is not (>= 1900 and <= 2100) || guidelineStatus is not ("final" or "draft" or "superseded")
            || (title?.Length ?? 0) > 512 || (version?.Length ?? 0) > 64
            || (permissionNote?.Length ?? 0) > 1024
            || (!string.IsNullOrEmpty(sourceUrl) && !ClinicalText.HttpSourceUrl(sourceUrl)))
            return BadRequest(new { error = "Enter a publishing society, year 1900–2100, FINAL/DRAFT/SUPERSEDED status, optional title/version, a valid HTTP(S) source URL, and a bounded permission note." });

        using (var source = file.OpenReadStream())
        {
            var header = new byte[5];
            try { await source.ReadExactlyAsync(header, HttpContext.RequestAborted); }
            catch (EndOfStreamException) { return BadRequest(new { error = "The selected file is not a PDF." }); }
            if (!header.AsSpan().SequenceEqual("%PDF-"u8))
                return BadRequest(new { error = "The selected file is not a PDF." });
        }

        var filePath = Path.Combine(UploadsDirectory, $"{Guid.NewGuid():N}.pdf");
        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (workspace == null) return NotFound();
        try
        {
            Directory.CreateDirectory(UploadsDirectory);
            await using (var stream = new FileStream(filePath, FileMode.CreateNew))
                await file.CopyToAsync(stream, HttpContext.RequestAborted);
            var doc = new Document
            {
                WorkspaceId = workspaceId, FileName = fileName, StoragePath = filePath, Status = "uploaded",
                Society = society, Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title,
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                Year = year, GuidelineStatus = guidelineStatus,
                EffectiveDate = effectiveDate, ReviewByDate = reviewByDate,
                SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl,
                PermissionNote = string.IsNullOrWhiteSpace(permissionNote) ? null : permissionNote,
                UploadedBy = await _currentUser.GetUserIdAsync()
            };
            _db.Documents.Add(doc);
            workspace.MembershipRevision = checked(workspace.MembershipRevision + 1); // Serialize uploads with workspace deletion.
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            return Ok(DocumentLifecycle.View(doc, DocumentChunkCounts.Empty));
        }
        catch (DbUpdateConcurrencyException) { TryDeleteStoredFile(filePath); return Conflict(new { error = "Workspace changed. Refresh before uploading again." }); }
        catch (Exception)
        {
            TryDeleteStoredFile(filePath);
            return StatusCode(500, new { error = "The PDF could not be uploaded. Try again." });
        }
    }

    [HttpDelete("{id}")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        var chunks = await _db.Chunks.Where(c => c.DocumentId == id && c.WorkspaceId == workspaceId).ToListAsync();
        _db.Chunks.RemoveRange(chunks);
        _db.ChunkJobs.RemoveRange(await _db.ChunkJobs.Where(j => j.DocumentId == id && j.WorkspaceId == workspaceId).ToListAsync());
        _db.ChunkerGuidances.RemoveRange(await _db.ChunkerGuidances.Where(g => g.DocumentId == id).ToListAsync());
        _db.Documents.Remove(doc);
        var queued = StoredDocumentFiles.Queue(_db, [doc.StoragePath]);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Busy(); }
        var failed = await StoredDocumentFiles.CompleteAsync(_db, _cfg, queued);
        return Ok(new { deleted = true, warning = failed == 0 ? null : "Document, chunks and guidance deleted. Stored file cleanup could not be completed; a durable cleanup record is retained for safe retry or operator cleanup." });
    }

    private bool TryDeleteStoredFile(string? path)
        => StoredDocumentFiles.TryDelete(_cfg, path);

    [HttpPost("{id}/reset-pages")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> ResetPages(Guid workspaceId, Guid id, [FromBody] ResetPagesDto dto)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        var valid = ParsePages(doc.ParsedText ?? "").Select(p => p.Page).ToHashSet();
        if (string.IsNullOrWhiteSpace(doc.ParsedText) || dto.Pages == null || dto.Pages.Length is < 1 or > 1000 || dto.Pages.Any(p => !valid.Contains(p)))
            return BadRequest(new { error = "Choose 1–1000 valid parsed pages to reset." });
        var selected = dto.Pages.ToHashSet();
        var chunks = await _db.Chunks.Where(c => c.WorkspaceId == workspaceId && c.DocumentId == id).ToListAsync();
        var removed = chunks.Where(c => selected.Any(p => p >= (c.PageStart ?? c.Page) && p <= (c.PageEnd ?? c.Page))).ToList();
        // Deleting a merged range also invalidates its other pages, so none remain
        // falsely flagged as chunked. Disjoint selected pages do not delete gaps.
        var reset = selected.Concat(removed.SelectMany(c => valid.Where(p => p >= (c.PageStart ?? c.Page) && p <= (c.PageEnd ?? c.Page)))).Distinct().Order().ToList();
        var kept = ReadChunkedPages(doc).Except(reset).Where(valid.Contains).Order().ToList();
        _db.Chunks.RemoveRange(removed);
        doc.ChunkedPageNumbers = JsonSerializer.Serialize(kept); doc.ChunkedPagesCount = kept.Count;
        doc.ChunkingStatus = kept.Count == 0 ? "not_started" : kept.Count == valid.Count ? "completed" : "partial";
        ClearEmbeddingProgress(doc); doc.Status = "parsed"; doc.FailureReason = null; doc.FailureStage = null;
        doc.UpdatedBy = await _currentUser.GetUserIdAsync();
        await DocumentLifecycle.ReconcileAsync(_db, doc);
        try { await _db.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { return Busy(); }
        return Ok(new { chunksDeleted = removed.Count, resetPages = reset });
    }

    [HttpPost("{id}/reset-embeddings")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> ResetEmbeddings(Guid workspaceId, Guid id)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        var chunks = await _db.Chunks.Where(c => c.WorkspaceId == workspaceId && c.DocumentId == id
            && (c.Status == "embedded" || c.Embedding != null || c.EmbeddingModel != null || c.LegacyEmbeddingJson != null)).ToListAsync();
        foreach (var chunk in chunks) { chunk.Embedding = null; chunk.EmbeddingModel = null; chunk.LegacyEmbeddingJson = null; if (chunk.Status == "embedded") chunk.Status = "accepted"; }
        ClearEmbeddingProgress(doc);
        if (doc.FailureStage == "embedding") { doc.FailureReason = null; doc.FailureStage = null; doc.Status = "parsed"; }
        doc.UpdatedBy = await _currentUser.GetUserIdAsync();
        await DocumentLifecycle.ReconcileAsync(_db, doc);
        try { await _db.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { return Busy(); }
        return Ok(new { embeddingsReset = chunks.Count });
    }

    private static void ClearEmbeddingProgress(Document doc) { doc.EmbeddingTotal = null; doc.EmbeddingCompleted = 0; doc.EmbeddingBatchSize = null; }
    private static HashSet<int> ReadChunkedPages(Document doc)
    {
        try { return JsonSerializer.Deserialize<HashSet<int>>(doc.ChunkedPageNumbers ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    [HttpGet("{id}/chunker-guidance")]
    public async Task<IActionResult> Guidance(Guid workspaceId, Guid id)
    {
        if (!await _db.Documents.AnyAsync(d => d.WorkspaceId == workspaceId && d.Id == id)) return NotFound();
        return Ok(await _db.ChunkerGuidances.AsNoTracking().Where(g => g.DocumentId == id).OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id)
            .Select(g => new { g.Guidance, g.CreatedAt }).FirstOrDefaultAsync() ?? new { Guidance = DefaultGuidance, CreatedAt = default(DateTime) });
    }
    private const string DefaultGuidance = "keep each recommendation with its strength grade; never split tables";

    [HttpPost("{id}/parse")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Parse(Guid workspaceId, Guid id)
    {
        var result = await _parser.ParseAsync(workspaceId, id, HttpContext.RequestAborted);
        if (result.RetryAfterSeconds.HasValue) Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result.Outcome switch
        {
            "not_found" => NotFound(),
            "conflict" => Conflict(new { error = result.Error }),
            "failed" => StatusCode(result.StatusCode, new { error = result.Error, dependency = result.StatusCode == 503 ? "colab" : null }),
            _ => Ok(new { parsedTextLength = result.ParsedTextLength, pageCount = result.PageCount,
                needsOcrPages = result.NeedsOcrPages ?? [], chunksCreated = 0,
                note = (result.NeedsOcrPages?.Count ?? 0) == 0
                    ? "Parsed. Choose pages and review chunks before embedding."
                    : "Parsed. Image-only pages were detected and are marked needs OCR; they were not indexed." })
        };
    }

    [HttpPost("{id}/reparse")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Reparse(Guid workspaceId, Guid id)
    {
        var result = await _parser.ReparseAsync(workspaceId, id, HttpContext.RequestAborted);
        if (result.RetryAfterSeconds.HasValue) Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var markerCount = 0;
        if (!string.IsNullOrWhiteSpace(result.FootnoteMarkersJson))
        {
            try { markerCount = JsonSerializer.Deserialize<JsonElement>(result.FootnoteMarkersJson).GetArrayLength(); }
            catch (JsonException) { markerCount = 0; }
        }
        return result.Outcome switch
        {
            "not_found" => NotFound(),
            "bad_request" => BadRequest(new { error = result.Error }),
            "conflict" => Conflict(new { error = result.Error }),
            "failed" => StatusCode(result.StatusCode, new { error = result.Error, dependency = result.StatusCode == 503 ? "colab" : null }),
            _ => Ok(new
            {
                parsedTextLength = result.ParsedTextLength,
                pageCount = result.PageCount,
                needsOcrPages = result.NeedsOcrPages ?? [],
                changedPages = result.ChangedPages,
                chunksMarked = result.ChunksMarked,
                footnoteMarkerCount = markerCount,
                note = result.ChunksMarked == 0
                    ? "Source re-parsed. No chunk text changed; existing review is unchanged."
                    : "Source re-parsed. Chunks on changed pages are marked for re-review and their old vectors were cleared."
            })
        };
    }

    [HttpGet("{id}/chunking-progress")]
    public async Task<IActionResult> ChunkingProgress(Guid workspaceId, Guid id)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();

        var chunked = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers);
        var total = doc.PageCount ?? 0;
        var pct = total > 0 ? (double)chunked.Length / total * 100 : 0;

        return Ok(new
        {
            totalPages = total,
            chunkedPages = chunked.Length,
            chunkedPageNumbers = chunked,
            chunkingStatus = doc.ChunkingStatus,
            progressPercent = Math.Round(pct, 1)
        });
    }

    /// <summary>
    /// Lightweight polling projection for Chunk Review. It returns only scalar
    /// status, page/count evidence and the latest job view; no parsed text,
    /// chunk text or vectors.
    /// </summary>
    [HttpGet("{id}/processing-state")]
    public async Task<IActionResult> ProcessingState(Guid workspaceId, Guid id)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == id && d.WorkspaceId == workspaceId)
            .Select(d => new
            {
                d.Id, d.WorkspaceId, d.Status, d.PageCount, d.ChunkedPagesCount, d.ChunkedPageNumbers,
                d.ChunkingStatus, d.ProcessingOperation, d.FailureStage, d.FailureReason, d.Revision, d.UpdatedAt,
                d.NeedsOcrPages,
                ParsedTextLength = d.ParsedText == null ? 0 : d.ParsedText.Length
            }).FirstOrDefaultAsync();
        if (doc == null) return NotFound();

        // Counts only: no chunk text, vectors or row materialization per poll.
        var chunkQuery = _db.Chunks.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.DocumentId == id);
        var total = await chunkQuery.CountAsync();
        var pending = await chunkQuery.CountAsync(c => c.Status == "pending");
        var acceptedOnly = await chunkQuery.CountAsync(c => c.Status == "accepted");
        var rejected = await chunkQuery.CountAsync(c => c.Status == "rejected");
        var embeddedTotal = await chunkQuery.CountAsync(c => c.Status == "embedded");
        var embeddedWithVector = await chunkQuery.CountAsync(c => c.Status == "embedded"
            && c.Embedding != null && c.Embedding.Length >= sizeof(float)
            && c.EmbeddingModel != null && c.EmbeddingModel != "");
        var accepted = acceptedOnly + (embeddedTotal - embeddedWithVector);
        var other = total - pending - accepted - rejected - embeddedWithVector;
        var counts = new DocumentChunkCounts(total, pending, accepted, rejected, embeddedWithVector, other);

        var job = await _db.ChunkJobs.AsNoTracking()
            .Where(j => j.WorkspaceId == workspaceId && j.DocumentId == id)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        var chunked = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers);
        var status = DocumentLifecycle.Status(doc.Status, doc.ProcessingOperation, doc.FailureReason,
            doc.ParsedTextLength, counts, DocumentLifecycle.ParseNeedsOcr(doc.NeedsOcrPages));

        return Ok(new
        {
            documentId = doc.Id,
            workspaceId = doc.WorkspaceId,
            status,
            processingOperation = doc.ProcessingOperation,
            failureStage = doc.FailureStage,
            failureReason = doc.FailureReason,
            revision = doc.Revision,
            updatedAt = doc.UpdatedAt,
            pageCount = doc.PageCount,
            chunkedPagesCount = chunked.Length,
            chunkedPageNumbers = chunked,
            chunkingStatus = doc.ChunkingStatus,
            chunkCounts = counts,
            job = job == null ? null : ChunkJobSupport.View(job),
            serverTime = DateTime.UtcNow
        });
    }

    [HttpPost("{id}/chunk-portions")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> ChunkPortions(Guid workspaceId, Guid id, [FromBody] ChunkPortionsDto dto)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId, cancellationToken);
        if (doc == null) return NotFound();
        if (string.IsNullOrWhiteSpace(doc.ParsedText)) return BadRequest("Document not parsed yet.");
        if (DocumentLifecycle.IsBusy(doc)) return Busy();
        if (dto.Pages == null || dto.Pages.Length == 0 || dto.Pages.Length > 1000
            || dto.BatchSize < 1 || dto.BatchSize > 20 || dto.MaxTokens < 100 || dto.MaxTokens > 2000
            || (dto.Guidance?.Length ?? 0) > 4000)
            return BadRequest(new { error = "Choose 1–1000 pages, a batch size of 1–20, and 100–2000 tokens. Guidance must be at most 4000 characters." });

        var pages = ParsePages(doc.ParsedText);
        var totalPages = pages.Count;
        var pageMap = pages.ToDictionary(p => p.Page);

        // Validate requested page numbers
        var invalidPages = dto.Pages.Where(p => !pageMap.ContainsKey(p)).Distinct().ToList();
        if (invalidPages.Count > 0)
            return BadRequest(new { error = $"Invalid page numbers: {string.Join(", ", invalidPages)}" });

        var alreadyChunked = string.IsNullOrWhiteSpace(doc.ChunkedPageNumbers)
            ? new HashSet<int>()
            : new HashSet<int>(JsonSerializer.Deserialize<List<int>>(doc.ChunkedPageNumbers) ?? new List<int>());

        var pagesToChunk = dto.Pages
            .Distinct()
            .Where(p => !alreadyChunked.Contains(p) && pageMap.TryGetValue(p, out _))
            .Select(p => pageMap[p])
            .ToList();

        var guidance = dto.Guidance ?? DefaultGuidance;
        _db.ChunkerGuidances.Add(new ChunkerGuidance { DocumentId = id, Guidance = guidance });

        if (pagesToChunk.Count == 0)
        {
            DocumentLifecycle.Touch(doc);
            try { await _db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { return Busy(); }
            return Ok(new
            {
                chunksCreated = 0,
                message = "All requested pages already chunked.",
                pagesChunked = 0,
                pagesSkipped = 0,
                failedPages = Array.Empty<int>(),
                skippedPages = Array.Empty<object>(),
                totalChunkedPages = alreadyChunked.Count,
                totalPages,
                progressPercent = Math.Round(totalPages > 0 ? (double)alreadyChunked.Count / totalPages * 100 : 0, 1),
                chunkingStatus = doc.ChunkingStatus
            });
        }

        var requestedPages = pagesToChunk.Select(p => p.Page).ToList();
        var reviewedRanges = await _db.Chunks.Where(c => c.WorkspaceId == workspaceId && c.DocumentId == id
                && (c.ReReviewRequired || c.Status == "accepted" || c.Status == "embedded"))
            .Select(c => new { Start = c.PageStart ?? c.Page, End = c.PageEnd ?? c.Page }).ToListAsync(cancellationToken);
        if (reviewedRanges.Any(c => requestedPages.Any(p => p >= c.Start && p <= c.End)))
        {
            foreach (var entry in _db.ChangeTracker.Entries<ChunkerGuidance>().Where(e => e.State == EntityState.Added && e.Entity.DocumentId == id).ToList()) entry.State = EntityState.Detached;
            return Conflict(new { error = "Some selected pages overlap reviewed chunks. Use Reset selected pages before replacing them." });
        }

        DocumentLifecycle.Begin(doc, "chunking");
        doc.ChunkingStatus = "in_progress";
        try { await _db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Busy(); }
        var operationRevision = doc.Revision;
        using var timing = OperationTelemetry.Start("documents.chunk");

        var totalChunksCreated = 0;
        var totalPagesChunked = 0;
        var totalPagesSkipped = 0;
        var failedPages = new List<int>();
        var skippedPages = new List<object>();
        var allChunked = alreadyChunked.ToList();

        var batchSize = dto.BatchSize > 0 ? dto.BatchSize : 2;
        var maxTokens = dto.MaxTokens > 0 ? dto.MaxTokens : 400;

        try
        {
            // Process pages in batches of BatchSize, one Colab call per batch.
            for (int i = 0; i < pagesToChunk.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = pagesToChunk.Skip(i).Take(batchSize).ToList();
                var batchPageNumbers = batch.Select(p => p.Page).ToList();

                // Build text for just this batch's pages (with page markers so Colab can attribute chunks).
                var batchText = string.Join("\n\n", batch.Select(p => $"--- Page {p.Page} ---\n{p.Text}"));

                var chunkBody = JsonSerializer.SerializeToElement(new
                {
                    text = batchText,
                    guidance,
                    max_tokens = maxTokens,
                });

                var jobResult = await _colab.RunChunkJobAsync(chunkBody, cancellationToken: cancellationToken);
                if (!jobResult.HasValue)
                {
                    // Failed/timed-out jobs never advance page progress.
                    failedPages.AddRange(batchPageNumbers);
                    continue;
                }
                var chunkRes = jobResult.Value;

                // Collect skipped pages reported by Colab (front matter, etc.).
                var batchSkippedPages = new List<int>();
                if (chunkRes.TryGetProperty("skipped_pages", out var spArr) && spArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var spItem in spArr.EnumerateArray())
                    {
                        var pageNum = spItem.TryGetProperty("page", out var pn) && pn.ValueKind == JsonValueKind.Number
                            ? pn.GetInt32() : -1;
                        var reason = spItem.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String
                            ? rs.GetString() ?? "skipped" : "skipped";
                        if (batchPageNumbers.Contains(pageNum))
                        {
                            batchSkippedPages.Add(pageNum);
                            skippedPages.Add(new { page = pageNum, reason });
                        }
                    }
                }

                // Collect all returned chunks: accepted (verbatim) + verbatim-failed (pending with warning).
                var allReturnedChunks = new List<JsonElement>();
                if (chunkRes.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
                    allReturnedChunks.AddRange(chunks.EnumerateArray());
                if (chunkRes.TryGetProperty("failed_chunks", out var failedChunks) && failedChunks.ValueKind == JsonValueKind.Array)
                    allReturnedChunks.AddRange(failedChunks.EnumerateArray());

                // A malformed/out-of-batch result must never overwrite another page.
                if (allReturnedChunks.Any(c => !ValidChunkRange(c, batchPageNumbers)
                    || !c.TryGetProperty("text", out var ct) || ct.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(ct.GetString())))
                {
                    failedPages.AddRange(batchPageNumbers);
                    continue;
                }

                var batchChunkedPages = new HashSet<int>();
                var replacementPages = allReturnedChunks.Select(c => c.GetProperty("page").GetInt32())
                    .Concat(batchSkippedPages).Distinct().ToList();

                // Save this batch's chunks in a transaction (skipped for InMemory provider, which
                // does not support transactions; SaveChanges is atomic within a single context).
                bool isInMemory = string.Equals(
                    _db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory",
                    StringComparison.Ordinal);
                using var transaction = isInMemory ? null : await _db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    // Replace only pages the service actually returned or explicitly
                    // skipped. Missing/empty results must retain their pending content.
                    var existingChunks = await _db.Chunks
                        .Where(c => c.WorkspaceId == workspaceId && c.DocumentId == doc.Id && replacementPages.Contains(c.Page))
                        .ToListAsync(cancellationToken);
                    if (existingChunks.Count > 0)
                    {
                        _db.Chunks.RemoveRange(existingChunks);
                    }

                    var chunkEntities = new List<Chunk>();
                    foreach (var c in allReturnedChunks)
                    {
                        var pageNum = c.GetProperty("page").GetInt32();
                        var chunkText = ChunkMetadata.NormalizeText(c.TryGetProperty("text", out var ct)
                            && ct.ValueKind == JsonValueKind.String ? ct.GetString() : "");

                        JsonElement meta = c.TryGetProperty("metadata", out var m) ? m : default;
                        string section = "";
                        string sectionHierarchy = "";
                        int? year = null;
                        string? strength = null;
                        string? certainty = null;
                        double? confidence = null;

                        if (meta.ValueKind == JsonValueKind.Object)
                        {
                            if (meta.TryGetProperty("section_path", out var sp) && sp.ValueKind == JsonValueKind.String)
                                section = sp.GetString() ?? "";

                            if (meta.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number)
                            {
                                confidence = cf.GetDouble();
                            }
                            if (meta.TryGetProperty("strength", out var sf) && sf.ValueKind == JsonValueKind.String)
                            {
                                var strengthLabel = sf.GetString()?.ToLowerInvariant() ?? "";
                                confidence ??= strengthLabel switch
                                {
                                    "strong" => 0.90,
                                    "conditional" => 0.70,
                                    "weak" => 0.50,
                                    "none" => 0.20,
                                    _ => 0.50
                                };
                                strength = strengthLabel;
                            }

                            if (meta.TryGetProperty("section_path", out var sh) && sh.ValueKind == JsonValueKind.String)
                            {
                                var rawHierarchy = sh.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(rawHierarchy) && rawHierarchy != "...")
                                    sectionHierarchy = rawHierarchy.Substring(0, Math.Min(rawHierarchy.Length, 256));
                            }

                            if (meta.TryGetProperty("year", out var yf) && yf.ValueKind == JsonValueKind.Number)
                            {
                                year = yf.GetInt32();
                            }
                        }

                        // Deterministic cleanup for section hierarchy, year, strength, certainty.
                        section = ChunkMetadata.CleanSection(section);
                        if (string.IsNullOrWhiteSpace(sectionHierarchy))
                            sectionHierarchy = ExtractSectionHierarchy(chunkText);
                        sectionHierarchy = ChunkMetadata.CleanSection(sectionHierarchy);
                        if (year == null)
                            year = ExtractYear(chunkText, pageNum);
                        var providedStrength = strength;
                        strength = ChunkMetadata.Strength(chunkText, strength);
                        certainty = ChunkMetadata.Certainty(chunkText, certainty);
                        if (string.IsNullOrWhiteSpace(providedStrength) || providedStrength == "none")
                        {
                            confidence = strength switch
                            {
                                "strong" => 0.90,
                                "conditional" => 0.70,
                                "weak" => 0.50,
                                _ => confidence
                            };
                        }

                        // Per-chunk type / verbatim / validation error from Colab.
                        string type = "clinical";
                        if (c.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                            type = t.GetString() ?? "clinical";

                        bool verbatim = true;
                        if (c.TryGetProperty("verbatim", out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                            verbatim = v.ValueKind == JsonValueKind.True;

                        string? validationError = null;
                        if (c.TryGetProperty("validation_error", out var ve) && ve.ValueKind == JsonValueKind.String)
                            validationError = ve.GetString();

                        chunkEntities.Add(new Chunk
                        {
                            WorkspaceId = doc.WorkspaceId,
                            DocumentId = doc.Id,
                            Page = pageNum,
                            PageStart = c.TryGetProperty("page_start", out var ps) && ps.ValueKind == JsonValueKind.Number
                                ? ps.GetInt32() : null,
                            PageEnd = c.TryGetProperty("page_end", out var pe) && pe.ValueKind == JsonValueKind.Number
                                ? pe.GetInt32() : null,
                            Section = string.IsNullOrEmpty(section) ? null : section.Substring(0, Math.Min(section.Length, 255)),
                            SectionHierarchy = string.IsNullOrEmpty(sectionHierarchy) ? null : sectionHierarchy.Substring(0, Math.Min(sectionHierarchy.Length, 256)),
                            Year = year,
                            Strength = strength,
                            Certainty = certainty,
                            Text = chunkText,
                            Status = "pending",
                            Type = type,
                            Verbatim = verbatim,
                            ValidationError = validationError,
                            Confidence = confidence,
                            Metadata = meta.ValueKind == JsonValueKind.Undefined ? null : meta.GetRawText()
                        });
                        batchChunkedPages.Add(pageNum);
                    }

                    _db.Chunks.AddRange(chunkEntities);
                    var checkpoint = allChunked.Concat(batchChunkedPages).Concat(batchSkippedPages).Distinct().Order().ToList();
                    // Save the document checkpoint in the same transaction as its chunks.
                    // Pollers can see each committed batch, not just the final response.
                    doc.ChunkedPagesCount = checkpoint.Count;
                    doc.ChunkedPageNumbers = JsonSerializer.Serialize(checkpoint);
                    DocumentLifecycle.Touch(doc);
                    await _db.SaveChangesAsync(cancellationToken);
                    if (!isInMemory) await transaction!.CommitAsync(cancellationToken);
                    operationRevision = doc.Revision;
                    totalChunksCreated += chunkEntities.Count;
                    totalPagesChunked += batchChunkedPages.Count(p => !allChunked.Contains(p));
                    totalPagesSkipped += batchSkippedPages.Distinct().Count(p => !allChunked.Contains(p) && !batchChunkedPages.Contains(p));
                    allChunked = checkpoint;
                    // Empty/unreported pages are failures, not silently "processed".
                    failedPages.AddRange(batchPageNumbers.Where(p => !checkpoint.Contains(p)));
                }
                catch
                {
                    if (!isInMemory) await transaction!.RollbackAsync();
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            // Discard only this request's rolled-back tracked batch, then retain the
            // last committed checkpoint. Never re-save added chunks after a rollback.
            _db.ChangeTracker.Clear();
            doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
            if (doc == null || doc.Revision != operationRevision || doc.ProcessingOperation != "chunking") return Busy();
            allChunked = JsonSerializer.Deserialize<List<int>>(doc.ChunkedPageNumbers ?? "[]") ?? [];
            failedPages = failedPages.Concat(requestedPages.Where(p => !allChunked.Contains(p))).Distinct().Order().ToList();
            doc.ChunkingStatus = "failed";
            var reason = exception is DependencyUnavailableException unavailable
                ? unavailable.PublicMessage + " Completed pages were kept. Choose the remaining pages to retry."
                : "Chunking was interrupted or the service failed. Completed pages were kept. Choose the remaining pages to retry.";
            DocumentLifecycle.Fail(doc, "chunking", reason);
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return Busy(); }

            if (exception is DependencyUnavailableException offline) Response.Headers.RetryAfter = offline.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) timing.Cancelled();
            return StatusCode(exception is DependencyUnavailableException or OperationCanceledException ? 503 : 500, new
            {
                error = reason,
                dependency = exception is DependencyUnavailableException ? "colab" : null,
                chunksCreated = totalChunksCreated,
                pagesChunked = totalPagesChunked,
                pagesSkipped = totalPagesSkipped,
                failedPages,
                skippedPages,
                totalChunkedPages = allChunked.Count,
                totalPages,
                progressPercent = Math.Round(totalPages > 0 ? (double)allChunked.Count / totalPages * 100 : 0, 1),
                chunkingStatus = doc.ChunkingStatus
            });
        }

        doc.ChunkedPagesCount = allChunked.Count;
        doc.ChunkedPageNumbers = JsonSerializer.Serialize(allChunked.OrderBy(x => x).ToList());
        doc.ProcessingOperation = null;
        if (failedPages.Count > 0)
        {
            doc.ChunkingStatus = "failed";
            var pageLabel = string.Join(", ", failedPages.Distinct().Order().Take(20));
            DocumentLifecycle.Fail(doc, "chunking", $"Could not chunk pages {pageLabel}{(failedPages.Count > 20 ? ", …" : "")}. Completed pages were kept. Choose the failed pages to retry.");
        }
        else
        {
            doc.ChunkingStatus = allChunked.Count >= totalPages ? "completed" : allChunked.Count > 0 ? "partial" : "not_started";
            doc.Status = "parsed";
            await DocumentLifecycle.ReconcileAsync(_db, doc);
        }
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Busy(); }
        if (failedPages.Count == 0) timing.Success();

        return Ok(new
        {
            chunksCreated = totalChunksCreated,
            pagesChunked = totalPagesChunked,
            pagesSkipped = totalPagesSkipped,
            failedPages,
            skippedPages,
            totalChunkedPages = allChunked.Count,
            totalPages,
            progressPercent = Math.Round(totalPages > 0 ? (double)allChunked.Count / totalPages * 100 : 0, 1),
            chunkingStatus = doc.ChunkingStatus
        });
    }

    [HttpPatch("{id}/status")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> UpdateStatus(Guid workspaceId, Guid id, [FromBody] StatusDto dto)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == workspaceId);
        if (doc == null) return NotFound();
        var counts = await ReadCountsAsync(workspaceId, id);
        var view = DocumentLifecycle.View(doc, counts.GetValueOrDefault(id, DocumentChunkCounts.Empty));
        if (dto.Status != view.Status)
            return Conflict(new { error = "Document status is set by parsing, chunk review and embedding, not by the client.", status = view.Status });
        return Ok(view); // Legacy callers can confirm a state, never manufacture it.
    }

    [HttpPost("{id}/generate-embeddings")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> GenerateEmbeddings(Guid workspaceId, Guid id)
    {
        var result = await _embeddings.EmbedAsync(workspaceId, id, HttpContext.RequestAborted);
        if (result.RetryAfterSeconds.HasValue) Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.StatusCode == 404) return NotFound();
        if (result.StatusCode == 409 && result.DocumentStatus == null) return Conflict(new { error = result.Error });
        var response = new { embedded = result.Embedded, documentStatus = result.DocumentStatus, error = result.Error, message = result.Message,
            dependency = result.StatusCode == 503 ? "colab" : null };
        return result.StatusCode == 200 ? Ok(response) : StatusCode(result.StatusCode, response);
    }

    [HttpGet("{id}/embedding-progress")]
    public async Task<IActionResult> EmbeddingProgress(Guid workspaceId, Guid id)
    {
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == id && d.WorkspaceId == workspaceId)
            .Select(d => new { d.EmbeddingTotal, d.EmbeddingCompleted, d.EmbeddingBatchSize, d.ProcessingOperation, d.FailureStage, d.FailureReason })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (doc == null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            totalChunks = doc.EmbeddingTotal, completedChunks = doc.EmbeddingCompleted, batchSize = doc.EmbeddingBatchSize,
            status = doc.ProcessingOperation == "embedding" ? "in_progress" : doc.FailureStage == "embedding" ? "failed"
                : doc.EmbeddingTotal.HasValue ? "completed" : "not_started",
            progressPercent = doc.EmbeddingTotal is > 0 ? Math.Round(100.0 * doc.EmbeddingCompleted / doc.EmbeddingTotal.Value, 1) : 0,
            error = doc.FailureStage == "embedding" ? doc.FailureReason : null
        });
    }

    // Helper: parse pages from --- Page N --- markers
    private static bool ValidChunkRange(JsonElement chunk, List<int> batch)
    {
        if (chunk.ValueKind != JsonValueKind.Object || !chunk.TryGetProperty("page", out var source)
            || source.ValueKind != JsonValueKind.Number || !source.TryGetInt32(out var page) || !batch.Contains(page)) return false;
        var start = page; var end = page;
        if (chunk.TryGetProperty("page_start", out var first) && first.ValueKind != JsonValueKind.Null
            && (first.ValueKind != JsonValueKind.Number || !first.TryGetInt32(out start))) return false;
        if (chunk.TryGetProperty("page_end", out var last) && last.ValueKind != JsonValueKind.Null
            && (last.ValueKind != JsonValueKind.Number || !last.TryGetInt32(out end))) return false;
        if (start < 1 || end < start || page < start || page > end || (long)end - start >= batch.Count) return false;
        for (long number = start; number <= end; number++) if (!batch.Contains((int)number)) return false;
        return true;
    }

    private List<PageInfo> ParsePages(string text)
    {
        var pages = new List<PageInfo>();
        var marker = new System.Text.RegularExpressions.Regex(@"(?m)^\s*---\s*Page\s+(\d+)\s*---\s*$");
        var parts = marker.Split(text);
        if (parts.Length == 1)
        {
            pages.Add(new PageInfo { Page = 1, Text = text.Trim() });
            return pages;
        }
        if (parts[0].Trim().Length > 0)
        {
            pages.Add(new PageInfo { Page = 1, Text = parts[0].Trim() });
        }
        for (int i = 1; i < parts.Length; i += 2)
        {
            var num = int.Parse(parts[i]);
            var body = i + 1 < parts.Length ? parts[i + 1] : "";
            pages.Add(new PageInfo { Page = num, Text = body.Trim() });
        }
        return pages;
    }

    private class PageInfo
    {
        public int Page { get; set; }
        public string Text { get; set; } = "";
    }

    // ---------- Deterministic regex extraction (no LLM) ----------

    private static string ExtractSectionHierarchy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Look for heading-like patterns at the start of the chunk
        // Pattern 1: Numbered headings like "1. Clinical management" or "2.1 Induction therapy"
        var headingMatch = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(\d+(?:\.\d+)*)\s+([A-Z][^\n]{3,80})", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (headingMatch.Success)
        {
            var num = headingMatch.Groups[1].Value.Trim();
            var title = headingMatch.Groups[2].Value.Trim();
            return $"{num} {title}".Substring(0, Math.Min(("" + num + " " + title).Length, 256));
        }

        // Pattern 2: ALL CAPS headings (common in guidelines)
        var capsMatch = System.Text.RegularExpressions.Regex.Match(text, @"^\s*([A-Z][A-Z\s,&\-]{5,60})", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (capsMatch.Success)
        {
            var title = capsMatch.Groups[1].Value.Trim();
            return title.Substring(0, Math.Min(title.Length, 256));
        }

        // Pattern 3: Title case headings (first non-empty line that looks like a title)
        var lines = text.Split('\n');
        foreach (var line in lines.Take(5))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 5 && trimmed.Length < 80 &&
                System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Z][a-z]+(\s+[A-Za-z]+)*\s*$") &&
                !System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\s")) // skip numbered lists
            {
                return trimmed.Substring(0, Math.Min(trimmed.Length, 256));
            }
        }

        return "";
    }

    private static int? ExtractYear(string text, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lower = text.ToLowerInvariant();

        // Cue words that establish a year attribution.
        var cues = new[] { "©", "copyright", "published", "updated", "version" };

        // Find every 4-digit candidate in the text.
        var yearMatches = System.Text.RegularExpressions.Regex.Matches(text, @"\d{4}");
        foreach (System.Text.RegularExpressions.Match match in yearMatches)
        {
            var yearStr = match.Value;
            if (!int.TryParse(yearStr, out var y)) continue;
            // Sanity check: year should be reasonable (1900-2100).
            if (y < 1900 || y > 2100) continue;

            var endIndex = match.Index + yearStr.Length;

            // Never accept a year immediately followed by a unit (e.g. "2000 mL", "1500 IU").
            if (endIndex < text.Length)
            {
                var after = text.Substring(endIndex).TrimStart();
                if (System.Text.RegularExpressions.Regex.IsMatch(after, @"^(mL|IU|mg|g|kg|mcg|ml|mmHg|cm|mm|%|dL|L)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;
            }

            // On pages 1-2, any non-unit year is acceptable.
            bool isEarlyPage = pageNumber >= 1 && pageNumber <= 2;

            // Near a cue word: look back up to 40 chars for a cue.
            bool nearCue = false;
            var startIndex = match.Index;
            var lookbackStart = Math.Max(0, startIndex - 40);
            var lookback = text.Substring(lookbackStart, startIndex - lookbackStart).ToLowerInvariant();
            if (cues.Any(c => lookback.Contains(c.ToLowerInvariant())))
                nearCue = true;

            // Parenthesised year "(2023)" is its own cue.
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\(\s*" + System.Text.RegularExpressions.Regex.Escape(yearStr) + @"\s*\)"))
                nearCue = true;

            if (isEarlyPage || nearCue)
                return y;
        }

        return null;
    }

    private static string? ExtractRecommendationStrength(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lower = text.ToLowerInvariant();

        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"strong\s+recommendation")) return "strong";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"weak\s+recommendation")) return "weak";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"conditional\s+recommendation")) return "conditional";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"recommend\s+against")) return "weak";

        return null;
    }

    private static string? ExtractCertainty(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lower = text.ToLowerInvariant();

        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"high[-\s]certainty\s+evidence")) return "high";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"moderate[-\s]certainty\s+evidence")) return "moderate";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"very\s+low[-\s]certainty\s+evidence")) return "very_low";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"low[-\s]certainty\s+evidence")) return "low";

        return null;
    }
}

public record StatusDto(string Status);
public record ChunkPortionsDto(int[] Pages, string? Guidance, int MaxTokens = 400, int BatchSize = 2);
public record ResetPagesDto(int[] Pages);
