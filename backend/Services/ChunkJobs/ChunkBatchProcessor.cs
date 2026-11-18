using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed record ChunkBatchOutcome(int ChunksCreated, IReadOnlyList<int> CompletedPages, IReadOnlyList<int> SkippedPages);

public sealed class ChunkBatchException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The batch seam shared by the background job worker. It copies the existing
/// controller's validation/attribution rules but writes only one committed batch.
/// </summary>
public static class ChunkBatchProcessor
{
    public static async Task<ChunkBatchOutcome> ProcessAsync(
        AppDbContext db, IColabClient colab, Document doc, ChunkJob job,
        IReadOnlyList<ChunkPageText> batch, CancellationToken cancellationToken)
    {
        var batchPageNumbers = batch.Select(page => page.Page).ToList();
        var batchText = string.Join("\n\n", batch.Select(page => $"--- Page {page.Page} ---\n{page.Text}"));
        var body = JsonSerializer.SerializeToElement(new
        {
            text = batchText,
            guidance = job.Guidance,
            max_tokens = job.MaxTokens,
        });

        var jobResult = await colab.RunChunkJobAsync(body, job.BatchTimeoutSeconds, cancellationToken)
            ?? throw new ChunkBatchException("invalid_batch_result");
        var chunkRes = jobResult;

        var batchSkippedPages = new List<int>();
        var skippedPages = new List<object>();
        if (chunkRes.TryGetProperty("skipped_pages", out var skippedArray) && skippedArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in skippedArray.EnumerateArray())
            {
                var page = item.TryGetProperty("page", out var pageValue) && pageValue.ValueKind == JsonValueKind.Number
                    ? pageValue.GetInt32() : -1;
                var reason = item.TryGetProperty("reason", out var reasonValue) && reasonValue.ValueKind == JsonValueKind.String
                    ? reasonValue.GetString() ?? "skipped" : "skipped";
                if (!batchPageNumbers.Contains(page)) continue;
                batchSkippedPages.Add(page);
                skippedPages.Add(new { page, reason });
            }
        }

        var returnedChunks = new List<JsonElement>();
        if (chunkRes.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            returnedChunks.AddRange(chunks.EnumerateArray());
        if (chunkRes.TryGetProperty("failed_chunks", out var failedChunks) && failedChunks.ValueKind == JsonValueKind.Array)
            returnedChunks.AddRange(failedChunks.EnumerateArray());

        if (returnedChunks.Any(chunk => !ValidChunkRange(chunk, batchPageNumbers)
            || !chunk.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(text.GetString())))
            throw new ChunkBatchException("invalid_batch_result");

        var replacementPages = returnedChunks.Select(chunk => chunk.GetProperty("page").GetInt32())
            .Concat(batchSkippedPages).Distinct().ToList();
        var batchChunkedPages = new HashSet<int>();

        var isInMemory = string.Equals(db.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);
        await using var transaction = isInMemory ? null : await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var existingChunks = await db.Chunks
                .Where(chunk => chunk.WorkspaceId == doc.WorkspaceId && chunk.DocumentId == doc.Id
                    && replacementPages.Contains(chunk.Page))
                .ToListAsync(cancellationToken);
            if (existingChunks.Count > 0) db.Chunks.RemoveRange(existingChunks);

            var entities = new List<Chunk>();
            foreach (var chunk in returnedChunks)
            {
                var pageNumber = chunk.GetProperty("page").GetInt32();
                var chunkText = ChunkMetadata.NormalizeText(chunk.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String ? text.GetString() : "");

                var metadata = chunk.TryGetProperty("metadata", out var meta) ? meta : default;
                var section = "";
                var sectionHierarchy = "";
                int? year = null;
                string? strength = null;
                string? certainty = null;
                double? confidence = null;

                if (metadata.ValueKind == JsonValueKind.Object)
                {
                    if (metadata.TryGetProperty("section_path", out var sectionPath) && sectionPath.ValueKind == JsonValueKind.String)
                        section = sectionPath.GetString() ?? "";
                    if (metadata.TryGetProperty("confidence", out var confidenceValue) && confidenceValue.ValueKind == JsonValueKind.Number)
                        confidence = confidenceValue.GetDouble();
                    if (metadata.TryGetProperty("strength", out var strengthValue) && strengthValue.ValueKind == JsonValueKind.String)
                    {
                        var strengthLabel = strengthValue.GetString()?.ToLowerInvariant() ?? "";
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
                    if (metadata.TryGetProperty("section_path", out var hierarchyValue) && hierarchyValue.ValueKind == JsonValueKind.String)
                    {
                        var rawHierarchy = hierarchyValue.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(rawHierarchy) && rawHierarchy != "...")
                            sectionHierarchy = rawHierarchy[..Math.Min(rawHierarchy.Length, 256)];
                    }
                    if (metadata.TryGetProperty("year", out var yearValue) && yearValue.ValueKind == JsonValueKind.Number)
                        year = yearValue.GetInt32();
                }

                section = ChunkMetadata.CleanSection(section);
                if (string.IsNullOrWhiteSpace(sectionHierarchy)) sectionHierarchy = ExtractSectionHierarchy(chunkText);
                sectionHierarchy = ChunkMetadata.CleanSection(sectionHierarchy);
                year ??= ExtractYear(chunkText, pageNumber);
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

                var type = chunk.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                    ? typeValue.GetString() ?? "clinical" : "clinical";
                var verbatim = !chunk.TryGetProperty("verbatim", out var verbatimValue)
                    || verbatimValue.ValueKind != JsonValueKind.False;
                var validationError = chunk.TryGetProperty("validation_error", out var validationValue)
                    && validationValue.ValueKind == JsonValueKind.String ? validationValue.GetString() : null;

                entities.Add(new Chunk
                {
                    WorkspaceId = doc.WorkspaceId,
                    DocumentId = doc.Id,
                    Page = pageNumber,
                    PageStart = chunk.TryGetProperty("page_start", out var pageStart) && pageStart.ValueKind == JsonValueKind.Number
                        ? pageStart.GetInt32() : null,
                    PageEnd = chunk.TryGetProperty("page_end", out var pageEnd) && pageEnd.ValueKind == JsonValueKind.Number
                        ? pageEnd.GetInt32() : null,
                    Section = string.IsNullOrEmpty(section) ? null : section[..Math.Min(section.Length, 255)],
                    SectionHierarchy = string.IsNullOrEmpty(sectionHierarchy) ? null : sectionHierarchy[..Math.Min(sectionHierarchy.Length, 256)],
                    Year = year,
                    Strength = strength,
                    Certainty = certainty,
                    Text = chunkText,
                    Status = "pending",
                    Type = type,
                    Verbatim = verbatim,
                    ValidationError = validationError,
                    Confidence = confidence,
                    Metadata = metadata.ValueKind == JsonValueKind.Undefined ? null : metadata.GetRawText()
                });
                batchChunkedPages.Add(pageNumber);
            }

            db.Chunks.AddRange(entities);
            var completed = ChunkJobSupport.ReadPages(job.CompletedPagesJson).Concat(batchChunkedPages).Distinct().Order().ToArray();
            var skipped = ChunkJobSupport.ReadPages(job.SkippedPagesJson).Concat(batchSkippedPages).Distinct().Order().ToArray();
            var checkpoint = ChunkJobSupport.ReadPages(doc.ChunkedPageNumbers).Concat(completed).Concat(skipped).Distinct().Order().ToArray();
            job.CompletedPagesJson = ChunkJobSupport.WritePages(completed);
            job.SkippedPagesJson = ChunkJobSupport.WritePages(skipped);
            job.CurrentBatchStart = null;
            job.CurrentBatchEnd = null;
            job.LastHeartbeatAt = DateTime.UtcNow;
            job.LeaseUntil = DateTime.UtcNow.AddSeconds(
                (job.MaxRetries + 1L) * job.BatchTimeoutSeconds + 120);
            job.Revision = checked(job.Revision + 1);
            doc.ChunkedPagesCount = checkpoint.Length;
            doc.ChunkedPageNumbers = ChunkJobSupport.WritePages(checkpoint);
            DocumentLifecycle.Touch(doc);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new(entities.Count, batchChunkedPages.Order().ToArray(), batchSkippedPages.Distinct().Order().ToArray());
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool ValidChunkRange(JsonElement chunk, IReadOnlyList<int> batch)
    {
        if (chunk.ValueKind != JsonValueKind.Object || !chunk.TryGetProperty("page", out var source)
            || source.ValueKind != JsonValueKind.Number || !source.TryGetInt32(out var page) || !batch.Contains(page))
            return false;
        var start = page; var end = page;
        if (chunk.TryGetProperty("page_start", out var first) && first.ValueKind != JsonValueKind.Null
            && (first.ValueKind != JsonValueKind.Number || !first.TryGetInt32(out start))) return false;
        if (chunk.TryGetProperty("page_end", out var last) && last.ValueKind != JsonValueKind.Null
            && (last.ValueKind != JsonValueKind.Number || !last.TryGetInt32(out end))) return false;
        if (start < 1 || end < start || page < start || page > end || (long)end - start >= batch.Count) return false;
        for (long number = start; number <= end; number++) if (!batch.Contains((int)number)) return false;
        return true;
    }

    private static string ExtractSectionHierarchy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var heading = Regex.Match(text, @"^\s*(\d+(?:\.\d+)*)\s+([A-Z][^\n]{3,80})", RegexOptions.Multiline);
        if (heading.Success) return $"{heading.Groups[1].Value.Trim()} {heading.Groups[2].Value.Trim()}";
        var caps = Regex.Match(text, @"^\s*([A-Z][A-Z\s,&\-]{5,60})", RegexOptions.Multiline);
        if (caps.Success) return caps.Groups[1].Value.Trim()[..Math.Min(caps.Groups[1].Value.Trim().Length, 256)];
        foreach (var line in text.Split('\n').Take(5))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 5 && trimmed.Length < 80
                && Regex.IsMatch(trimmed, @"^[A-Z][a-z]+(\s+[A-Za-z]+)*\s*$")
                && !Regex.IsMatch(trimmed, @"^\d+\s"))
                return trimmed[..Math.Min(trimmed.Length, 256)];
        }
        return "";
    }

    private static int? ExtractYear(string text, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cues = new[] { "©", "copyright", "published", "updated", "version" };
        foreach (Match match in Regex.Matches(text, @"\d{4}"))
        {
            if (!int.TryParse(match.Value, out var year) || year is < 1900 or > 2100) continue;
            var end = match.Index + match.Value.Length;
            if (end < text.Length && Regex.IsMatch(text[end..].TrimStart(),
                @"^(mL|IU|mg|g|kg|mcg|ml|mmHg|cm|mm|%|dL|L)\b", RegexOptions.IgnoreCase)) continue;
            var lookback = text[Math.Max(0, match.Index - 40)..match.Index].ToLowerInvariant();
            var nearCue = cues.Any(cue => lookback.Contains(cue, StringComparison.Ordinal))
                || Regex.IsMatch(text, @"\(\s*" + Regex.Escape(match.Value) + @"\s*\)");
            if (pageNumber is 1 or 2 || nearCue) return year;
        }
        return null;
    }

    private static string? ExtractRecommendationStrength(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"strong\s+recommendation")) return "strong";
        if (Regex.IsMatch(lower, @"weak\s+recommendation")) return "weak";
        if (Regex.IsMatch(lower, @"conditional\s+recommendation")) return "conditional";
        if (Regex.IsMatch(lower, @"recommend\s+against")) return "weak";
        return null;
    }

    private static string? ExtractCertainty(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"high[-\s]certainty\s+evidence")) return "high";
        if (Regex.IsMatch(lower, @"moderate[-\s]certainty\s+evidence")) return "moderate";
        if (Regex.IsMatch(lower, @"very\s+low[-\s]certainty\s+evidence")) return "very_low";
        if (Regex.IsMatch(lower, @"low[-\s]certainty\s+evidence")) return "low";
        return null;
    }
}
