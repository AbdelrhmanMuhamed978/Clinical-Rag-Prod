using System.Text.Json;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class ReparseTests
{
    private sealed class ParseOnlyClient(string text, bool fail = false) : IColabClient
    {
        public Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default) =>
            fail ? throw new DependencyUnavailableException("colab") : Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                text,
                pageCount = 2,
                needsOcrPages = Array.Empty<int>(),
                footnoteMarkers = new[]
                {
                    new { page = 1, offset = 3, marker = "c", source = "body", context = "page 1" }
                }
            }));

        public Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HealthAsync() => Task.FromResult(true);
        public Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ColabReadiness(true));
        public Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement?> RunChunkJobAsync(JsonElement body, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Failed_reparse_keeps_existing_text_chunks_and_reviews()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 2);
        var accepted = await store.AddChunkAsync(document, "accepted", page: 1);
        await using var db = store.CreateContext();
        var parser = new DocumentParser(db, new ParseOnlyClient("ignored", fail: true), store.Configuration);

        var result = await parser.ReparseAsync(document.WorkspaceId, document.Id);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(503, result.StatusCode);
        db.ChangeTracker.Clear();
        var savedDocument = await db.Documents.SingleAsync(item => item.Id == document.Id);
        Assert.Equal("parsed", savedDocument.Status);
        Assert.Contains("--- Page 1 ---", savedDocument.ParsedText);
        Assert.Null(savedDocument.ProcessingOperation);
        var savedChunk = await db.Chunks.SingleAsync(chunk => chunk.Id == accepted.Id);
        Assert.Equal("accepted", savedChunk.Status);
        Assert.False(savedChunk.ReReviewRequired);
    }

    [Fact]
    public async Task Reparse_keeps_chunks_and_marks_only_changed_pages_for_review()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 2);
        var changed = await store.AddChunkAsync(document, "accepted", page: 1);
        var unchanged = await store.AddChunkAsync(document, "accepted", page: 2);
        await using var db = store.CreateContext();
        var parser = new DocumentParser(db, new ParseOnlyClient(
            "--- Page 1 ---\nChanged source words for page 1.\n\n--- Page 2 ---\nSource words for page 2."),
            store.Configuration);

        var result = await parser.ReparseAsync(document.WorkspaceId, document.Id);

        Assert.Equal("parsed", result.Outcome);
        Assert.Equal(1, result.ChangedPages);
        Assert.Equal(1, result.ChunksMarked);
        db.ChangeTracker.Clear();
        var savedChanged = await db.Chunks.SingleAsync(chunk => chunk.Id == changed.Id);
        var savedUnchanged = await db.Chunks.SingleAsync(chunk => chunk.Id == unchanged.Id);
        Assert.True(savedChanged.ReReviewRequired);
        Assert.Equal("pending", savedChanged.Status);
        Assert.Null(savedChanged.Embedding);
        Assert.Contains("Source page text changed", savedChanged.ValidationError);
        Assert.False(savedUnchanged.ReReviewRequired);
        Assert.Equal("accepted", savedUnchanged.Status);
        var savedDoc = await db.Documents.SingleAsync(item => item.Id == document.Id);
        Assert.Contains("Changed source words", savedDoc.ParsedText);
        Assert.Contains("\"marker\":\"c\"", savedDoc.FootnoteMarkers);
        Assert.Equal("review", savedDoc.Status);
    }
}
