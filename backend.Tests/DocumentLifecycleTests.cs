using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class DocumentLifecycleTests
{
    [Fact]
    public void Counts_separate_missing_vectors_and_unknown_review_from_embedded_evidence()
    {
        var counts = DocumentLifecycle.Counts(new[]
        {
            ("pending", true), ("accepted", true), ("rejected", true), ("embedded", false), ("embedded", true), ("legacy_unknown", true)
        });
        Assert.Equal(new DocumentChunkCounts(6, 1, 2, 1, 1, 1), counts);
        Assert.Equal("review", DocumentLifecycle.Status("indexed", null, null, 100, counts));
    }

    [Theory]
    [InlineData("indexed", null, null, 0, 0, 0, 0, 0, "uploaded")]
    [InlineData("indexed", null, null, 100, 0, 0, 0, 0, "parsed")]
    [InlineData("review", null, null, 100, 2, 0, 0, 1, "indexed")]
    [InlineData("indexed", null, null, 100, 2, 1, 0, 1, "review")]
    [InlineData("indexed", null, null, 100, 2, 0, 1, 1, "review")]
    [InlineData("indexed", null, null, 100, 2, 0, 0, 0, "review")]
    [InlineData("failed", null, "Safe failure", 100, 1, 0, 0, 1, "failed")]
    [InlineData("indexed", "parsing", "Safe failure", 100, 1, 0, 0, 1, "parsing")]
    [InlineData("review", "chunking", null, 100, 1, 0, 0, 1, "chunking")]
    [InlineData("indexed", "embedding", null, 100, 1, 0, 0, 1, "review")]
    public void State_is_derived_from_server_work_and_complete_review(string status, string? operation, string? failure,
        int textLength, int total, int pending, int accepted, int embedded, string expected)
    {
        Assert.Equal(expected, DocumentLifecycle.Status(status, operation, failure, textLength,
            new DocumentChunkCounts(total, pending, accepted, total - pending - accepted - embedded, embedded, 0)));
    }

    [Fact]
    public async Task Reconciliation_includes_unsaved_edits_imports_and_deleted_rows()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        var pending = await store.AddChunkAsync(document);
        await store.AddChunkAsync(document, "embedded", "[1,0]");
        await using var db = store.CreateContext();
        var tracked = await db.Documents.SingleAsync();
        var deleted = await db.Chunks.SingleAsync(c => c.Id == pending.Id);
        db.Chunks.Remove(deleted);
        db.Chunks.Add(new() { WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Page = 2, Text = "Imported words.", Status = "accepted" });
        await DocumentLifecycle.ReconcileAsync(db, tracked);
        Assert.Equal("review", tracked.Status); Assert.Equal(2, tracked.Revision);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Chunks.CountAsync());
        Assert.Equal("review", (await db.Documents.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("chunking", "chunking", true)]
    [InlineData("embedding", "embedding", true)]
    [InlineData("chunking", "embedding", false)]
    [InlineData("embedding", "chunking", false)]
    [InlineData("chunking", "parsing", true)]
    public void A_retry_clears_only_its_failure_not_an_unfinished_different_step(string failedStage, string operation, bool clears)
    {
        var document = new ClinicalRag.Api.Models.Document();
        DocumentLifecycle.Fail(document, failedStage, "Retained failure");
        DocumentLifecycle.Begin(document, operation);
        Assert.Equal(3, document.Revision); Assert.Equal(operation, document.ProcessingOperation);
        Assert.Equal(clears ? null : "Retained failure", document.FailureReason);
    }
}
