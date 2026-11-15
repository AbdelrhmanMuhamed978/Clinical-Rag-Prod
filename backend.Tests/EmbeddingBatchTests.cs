using System.Net;
using System.Text.Json;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class EmbeddingBatchTests
{
    private static async Task AddAccepted(DocumentTestStore store, Document document, int count)
    {
        await using var db = store.CreateContext();
        db.Chunks.AddRange(Enumerable.Range(0, count).Select(index => new Chunk
        { WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Text = $"Current source {index}", Page = 1, Status = "accepted" }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Default_embedding_batches_are_thirty_two_and_every_chunk_records_the_model()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 65);
        var batches = new List<int>(); var client = new FakeColabClient
        { EmbedHandler = texts => { batches.Add(texts.Count); return Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 3, 4)); } };
        await using var db = store.CreateContext();
        var response = Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(new[] { 32, 32, 1 }, batches); Assert.Equal(65, DocumentTestStore.Json(response.Value).GetProperty("embedded").GetInt32());
        db.ChangeTracker.Clear(); Assert.All(await db.Chunks.ToListAsync(), chunk => { Assert.Equal("embedded", chunk.Status); Assert.Equal(EmbeddingTestData.Model, chunk.EmbeddingModel); });
        Assert.All(await db.Chunks.ToListAsync(), chunk => Assert.Equal(new[] { .6f, .8f }, EmbeddingVectors.ReadNormalized(chunk.Embedding!)));
        var completed = await db.Documents.SingleAsync(); Assert.Equal(65, completed.EmbeddingTotal); Assert.Equal(65, completed.EmbeddingCompleted); Assert.Equal(32, completed.EmbeddingBatchSize);
        var workspace = await db.Workspaces.SingleAsync(); Assert.Equal(EmbeddingTestData.Model, workspace.EmbeddingModel); Assert.Equal(2, workspace.EmbeddingDimensions);
    }

    [Fact]
    public async Task A_transient_embedding_failure_is_retried_without_duplicating_a_batch()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 2); var calls = 0;
        var client = new FakeColabClient { EmbedHandler = texts =>
        {
            calls++; if (calls == 1) throw new HttpRequestException("PRIVATE_UPSTREAM", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0));
        } };
        await using var db = store.CreateContext(); Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(2, calls); db.ChangeTracker.Clear(); Assert.Equal(2, await db.Chunks.CountAsync(c => c.Status == "embedded"));
    }

    [Fact]
    public async Task Committed_batches_release_chunk_tracking_before_the_next_batch_is_loaded()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 65);
        await using var db = store.CreateContext(); var trackedCounts = new List<int>();
        var client = new FakeColabClient { EmbedHandler = texts =>
        {
            trackedCounts.Add(db.ChangeTracker.Entries<Chunk>().Count());
            return Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0));
        } };
        Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(new[] { 32, 32, 1 }, trackedCounts); Assert.Empty(db.ChangeTracker.Entries<Chunk>());
        Assert.Equal(65, await db.Chunks.CountAsync(c => c.Status == "embedded"));
    }

    [Fact]
    public async Task A_failed_later_batch_retains_only_committed_progress_and_retry_embeds_the_remainder()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 65); var calls = 0;
        var client = new FakeColabClient { EmbedHandler = texts =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0)
                : EmbeddingTestData.Result(0, EmbeddingTestData.Model, 1, 0));
        } };
        await using var db = store.CreateContext();
        var failure = Assert.IsType<ObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(502, failure.StatusCode); Assert.Equal(32, DocumentTestStore.Json(failure.Value).GetProperty("embedded").GetInt32());
        Assert.Equal(2, calls); // Invalid responses are not transient retry candidates.
        db.ChangeTracker.Clear(); Assert.Equal(32, await db.Chunks.CountAsync(c => c.Status == "embedded")); Assert.Equal(33, await db.Chunks.CountAsync(c => c.Status == "accepted"));
        var retrySizes = new List<int>(); client.EmbedHandler = texts => { retrySizes.Add(texts.Count); return Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0)); };
        Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(new[] { 32, 1 }, retrySizes); db.ChangeTracker.Clear(); Assert.Equal(65, await db.Chunks.CountAsync(c => c.Status == "embedded"));
    }

    [Fact]
    public async Task New_embeddings_cannot_mix_models_with_other_documents_in_the_workspace()
    {
        using var store = new DocumentTestStore(); var existingDocument = await store.AddDocumentAsync(); var document = await store.AddDocumentAsync();
        await store.AddChunkAsync(existingDocument, "embedded", "[1,0]"); await AddAccepted(store, document, 1);
        var client = new FakeColabClient { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, "different-model", 1, 0)) };
        await using var db = store.CreateContext(); var response = Assert.IsType<ObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(409, response.StatusCode); db.ChangeTracker.Clear(); Assert.Equal(1, await db.Chunks.CountAsync(c => c.Status == "embedded"));
        Assert.Null((await db.Chunks.SingleAsync(c => c.DocumentId == document.Id)).Embedding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progress_is_pollable_after_each_committed_batch_and_cancellation_or_failure_keeps_it(bool cancel)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 65);
        using var cancellation = new CancellationTokenSource(); var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var client = new FakeColabClient { EmbedHandler = texts =>
        {
            if (++calls == 1) return Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0));
            reached.TrySetResult(true); return release.Task;
        } };
        await using var owner = store.CreateContext();
        var work = store.Controller(owner, client, cancellationToken: cancellation.Token).GenerateEmbeddings(store.Workspace.Id, document.Id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); await using var observe = store.CreateContext();
        var progress = DocumentTestStore.Json(Assert.IsType<OkObjectResult>(await store.Controller(observe).EmbeddingProgress(store.Workspace.Id, document.Id)).Value);
        Assert.Equal(65, progress.GetProperty("totalChunks").GetInt32()); Assert.Equal(32, progress.GetProperty("completedChunks").GetInt32());
        Assert.Equal(49.2, progress.GetProperty("progressPercent").GetDouble()); Assert.Equal("in_progress", progress.GetProperty("status").GetString());
        var list = Assert.IsAssignableFrom<IEnumerable<DocumentView>>(Assert.IsType<OkObjectResult>(await store.Controller(observe).List(store.Workspace.Id)).Value);
        Assert.Equal(32, Assert.Single(list).EmbeddingCompleted);
        if (cancel) cancellation.Cancel(); else release.SetResult(EmbeddingTestData.Result(0, EmbeddingTestData.Model, 1, 0));
        var failed = Assert.IsType<ObjectResult>(await work.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(502, failed.StatusCode);
        Assert.Equal(32, DocumentTestStore.Json(failed.Value).GetProperty("embedded").GetInt32());
        if (cancel) release.SetResult(EmbeddingTestData.Result(32, EmbeddingTestData.Model, 0, 1)); // Late results cannot commit.
        observe.ChangeTracker.Clear(); var retained = await observe.Documents.SingleAsync(); Assert.Equal(32, retained.EmbeddingCompleted); Assert.Null(retained.ProcessingOperation);
        Assert.Equal(32, await observe.Chunks.CountAsync(c => c.Status == "embedded")); Assert.Equal(33, await observe.Chunks.CountAsync(c => c.Status == "accepted"));
        progress = DocumentTestStore.Json(Assert.IsType<OkObjectResult>(await store.Controller(observe).EmbeddingProgress(store.Workspace.Id, document.Id)).Value);
        Assert.Equal("failed", progress.GetProperty("status").GetString());
        Assert.IsType<NotFoundResult>(await store.Controller(observe).EmbeddingProgress(Guid.NewGuid(), document.Id));
    }

    [Fact]
    public async Task A_model_change_between_batches_cannot_commit_mixed_vectors()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 33); var calls = 0;
        var client = new FakeColabClient { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count,
            ++calls == 1 ? EmbeddingTestData.Model : "changed-model", 1, 0)) };
        await using var db = store.CreateContext(); var response = Assert.IsType<ObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(409, response.StatusCode); Assert.Equal(32, DocumentTestStore.Json(response.Value).GetProperty("embedded").GetInt32());
        db.ChangeTracker.Clear(); Assert.Equal(32, await db.Chunks.CountAsync(c => c.Status == "embedded"));
        Assert.All(await db.Chunks.Where(c => c.Embedding != null).ToListAsync(), c => Assert.Equal(EmbeddingTestData.Model, c.EmbeddingModel));
    }

    [Fact]
    public async Task A_dimension_mismatch_with_other_active_vectors_is_refused()
    {
        using var store = new DocumentTestStore(); var old = await store.AddDocumentAsync(); var document = await store.AddDocumentAsync();
        await store.AddChunkAsync(old, "embedded", "[1,0]"); await AddAccepted(store, document, 1);
        await using var db = store.CreateContext(); var response = Assert.IsType<ObjectResult>(await store.Controller(db, new FakeColabClient()).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal(409, response.StatusCode); db.ChangeTracker.Clear(); Assert.Null((await db.Chunks.SingleAsync(c => c.DocumentId == document.Id)).Embedding);
    }

    [Fact]
    public async Task Concurrent_empty_workspace_writers_cannot_bind_different_models()
    {
        using var store = new DocumentTestStore(); var first = await store.AddDocumentAsync(); var second = await store.AddDocumentAsync();
        await AddAccepted(store, first, 1); await AddAccepted(store, second, 1);
        var firstResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstDb = store.CreateContext(); await using var secondDb = store.CreateContext();
        // A stale preloaded binding must not override the model committed by the other writer.
        await secondDb.Workspaces.SingleAsync();
        var firstWork = store.Controller(firstDb, new FakeColabClient { EmbedHandler = _ => { firstReady.SetResult(true); return firstResponse.Task; } })
            .GenerateEmbeddings(store.Workspace.Id, first.Id);
        var secondWork = store.Controller(secondDb, new FakeColabClient { EmbedHandler = _ => { secondReady.SetResult(true); return secondResponse.Task; } })
            .GenerateEmbeddings(store.Workspace.Id, second.Id);
        await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
        firstResponse.SetResult(EmbeddingTestData.Result(1, "model-a", 1, 0)); Assert.IsType<OkObjectResult>(await firstWork);
        secondResponse.SetResult(EmbeddingTestData.Result(1, "model-b", 1, 0));
        Assert.Equal(409, Assert.IsType<ObjectResult>(await secondWork).StatusCode);
        await using var observe = store.CreateContext(); Assert.Equal("model-a", (await observe.Workspaces.SingleAsync()).EmbeddingModel);
        Assert.Equal("model-a", (await observe.Chunks.SingleAsync(c => c.Status == "embedded")).EmbeddingModel);
        Assert.Null((await observe.Chunks.SingleAsync(c => c.DocumentId == second.Id)).Embedding);
        Assert.Null((await observe.Documents.SingleAsync(d => d.Id == second.Id)).ProcessingOperation);
    }

    [Fact]
    public async Task The_model_can_change_only_after_every_active_vector_is_invalidated()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await AddAccepted(store, document, 1);
        await using var db = store.CreateContext(); var client = new FakeColabClient
            { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, "old-model", 1, 0)) };
        Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        var id = (await db.Chunks.SingleAsync()).Id;
        Assert.IsType<OkObjectResult>(await new ClinicalRag.Api.Controllers.ChunkReviewController(db, new FakeCurrentUser(store.User)).Review(store.Workspace.Id, id,
            new ClinicalRag.Api.Controllers.ReviewDto(null, "Explicitly edited source", null)));
        client.EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, "new-model", 0, 1));
        Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        db.ChangeTracker.Clear(); Assert.Equal("new-model", (await db.Workspaces.SingleAsync()).EmbeddingModel); Assert.Equal("new-model", (await db.Chunks.SingleAsync()).EmbeddingModel);
    }

    [Fact]
    public async Task An_interrupted_finalization_can_finish_without_reembedding_committed_vectors()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("failed"); await store.AddChunkAsync(document, "embedded", "[1,0]");
        await using var db = store.CreateContext(); var failed = await db.Documents.SingleAsync();
        failed.FailureStage = "embedding"; failed.FailureReason = "Interrupted after commit."; failed.EmbeddingTotal = 1; failed.EmbeddingCompleted = 1; await db.SaveChangesAsync();
        var client = new FakeColabClient { EmbedHandler = _ => throw new InvalidOperationException("must not duplicate committed work") };
        var result = Assert.IsType<OkObjectResult>(await store.Controller(db, client).GenerateEmbeddings(store.Workspace.Id, document.Id));
        Assert.Equal("indexed", DocumentTestStore.Json(result.Value).GetProperty("documentStatus").GetString());
        db.ChangeTracker.Clear(); Assert.Null((await db.Documents.SingleAsync()).FailureReason);
    }
}
