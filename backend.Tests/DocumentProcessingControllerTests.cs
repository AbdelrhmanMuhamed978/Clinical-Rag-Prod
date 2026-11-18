using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class DocumentProcessingControllerTests
{
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FormFile Pdf(Stream stream, string name = "synthetic.pdf", long? length = null) => new(stream, 0, length ?? stream.Length, "file", name)
    { Headers = new HeaderDictionary(), ContentType = "application/pdf" };

    [Fact]
    public async Task Upload_persists_valid_required_metadata_as_an_uploaded_queue_row_and_returns_no_storage_path()
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext();
        using var content = new MemoryStream("%PDF-1.7 synthetic"u8.ToArray());
        var result = Assert.IsType<OkObjectResult>(await store.Controller(db).Upload(store.Workspace.Id,
            Pdf(content, Path.Combine("..", "untrusted", "Guideline.PDF")), "  Synthetic Society  ",
            null, null, 2024, " FINAL ", null, null, null, null));
        var view = Assert.IsType<DocumentView>(result.Value);
        Assert.Equal("uploaded", view.Status); Assert.Equal("Synthetic Society", view.Society); Assert.Equal(2024, view.Year); Assert.Equal("final", view.GuidelineStatus);
        Assert.Equal("Guideline.PDF", view.FileName); Assert.Equal(DocumentChunkCounts.Empty, view.ChunkCounts);
        Assert.DoesNotContain("storagePath", DocumentTestStore.Json(view).GetRawText(), StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear(); var document = await db.Documents.SingleAsync();
        Assert.Equal(store.User.Id, document.UploadedBy); Assert.Null(document.ParsedText); Assert.Null(document.ProcessingOperation);
        Assert.True(File.Exists(document.StoragePath)); Assert.StartsWith(store.Uploads, document.StoragePath); Assert.Empty(await db.Chunks.ToListAsync());
    }

    [Theory]
    [InlineData(null, 2024, "draft")]
    [InlineData(" ", 2024, "draft")]
    [InlineData("Synthetic\nSociety", 2024, "draft")]
    [InlineData("Synthetic Society", null, "draft")]
    [InlineData("Synthetic Society", 1899, "draft")]
    [InlineData("Synthetic Society", 2101, "draft")]
    [InlineData("Synthetic Society", 2024, null)]
    [InlineData("Synthetic Society", 2024, "unknown")]
    [InlineData("Synthetic Society", 2024, "final or draft")]
    public async Task Invalid_or_missing_upload_metadata_creates_no_file_or_document(string? society, int? year, string? status)
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext();
        using var content = new MemoryStream("%PDF-1.7 synthetic"u8.ToArray());
        Assert.IsType<BadRequestObjectResult>(await store.Controller(db).Upload(store.Workspace.Id, Pdf(content),
            society, null, null, year, status, null, null, null, null));
        Assert.Empty(await db.Documents.ToListAsync()); Assert.Empty(Directory.GetFiles(store.Uploads));
    }

    [Theory]
    [InlineData("not-pdf", "synthetic.pdf", 20)]
    [InlineData("%PDF-", "synthetic.txt", 5)]
    [InlineData("%PDF-", "synthetic.pdf", 52428801)]
    [InlineData("x", "synthetic.pdf", 1)]
    public async Task Invalid_file_content_extension_or_size_creates_no_upload(string body, string fileName, long length)
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext(); using var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Assert.IsType<BadRequestObjectResult>(await store.Controller(db).Upload(store.Workspace.Id,
            Pdf(content, fileName, length), "Synthetic Society", null, null, 2024, "draft", null, null, null, null));
        Assert.Empty(await db.Documents.ToListAsync()); Assert.Empty(Directory.GetFiles(store.Uploads));
    }

    [Fact]
    public async Task Polling_lists_are_lightweight_scoped_and_count_each_review_status_without_trusting_indexed()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("indexed");
        foreach (var state in new[] { "pending", "accepted", "rejected", "embedded", "legacy_unknown" })
            await store.AddChunkAsync(document, state, state == "embedded" ? "[1,0]" : null);
        await store.AddChunkAsync(document, "embedded"); // Missing vector is accepted work, not indexed evidence.
        await using var db = store.CreateContext();
        var rows = Assert.IsAssignableFrom<IEnumerable<DocumentView>>(Assert.IsType<OkObjectResult>(await store.Controller(db).List(document.WorkspaceId)).Value).ToList();
        var view = Assert.Single(rows); Assert.Equal("review", view.Status); Assert.Equal(new DocumentChunkCounts(6, 1, 2, 1, 1, 1), view.ChunkCounts);
        Assert.Null(view.ParsedText); Assert.Equal(document.ParsedText!.Length, view.ParsedTextLength);
        Assert.DoesNotContain("storagePath", DocumentTestStore.Json(view).GetRawText(), StringComparison.OrdinalIgnoreCase);
        var detail = Assert.IsType<DocumentView>(Assert.IsType<OkObjectResult>(await store.Controller(db).Get(document.WorkspaceId, document.Id)).Value);
        Assert.Equal(document.ParsedText, detail.ParsedText); Assert.Equal(view.ChunkCounts, detail.ChunkCounts);
        Assert.IsType<NotFoundResult>(await store.Controller(db).Get(Guid.NewGuid(), document.Id));
    }

    [Theory]
    [InlineData("indexed")]
    [InlineData("embedded")]
    [InlineData("failed")]
    public async Task Client_status_changes_cannot_manufacture_server_work(string status)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await using var db = store.CreateContext();
        Assert.IsType<ConflictObjectResult>(await store.Controller(db).UpdateStatus(document.WorkspaceId, document.Id, new StatusDto(status)));
        db.ChangeTracker.Clear(); Assert.Equal("parsed", (await db.Documents.SingleAsync()).Status); Assert.Equal(1, (await db.Documents.SingleAsync()).Revision);
        Assert.IsType<OkObjectResult>(await store.Controller(db).UpdateStatus(document.WorkspaceId, document.Id, new StatusDto("parsed")));
        Assert.Equal(1, (await db.Documents.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Deleting_a_document_removes_all_its_chunk_states_and_only_its_upload()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); var other = await store.AddDocumentAsync();
        foreach (var state in new[] { "pending", "accepted", "rejected", "embedded", "legacy_unknown" }) await store.AddChunkAsync(document, state, "[1,0]");
        var retained = await store.AddChunkAsync(other, "embedded", "[0,1]");
        await using var db = store.CreateContext();
        Assert.IsType<OkObjectResult>(await store.Controller(db).Delete(document.WorkspaceId, document.Id));
        db.ChangeTracker.Clear(); Assert.Equal(other.Id, (await db.Documents.SingleAsync()).Id); Assert.Equal(retained.Id, (await db.Chunks.SingleAsync()).Id);
        Assert.False(File.Exists(document.StoragePath)); Assert.True(File.Exists(other.StoragePath));
    }

    [Fact]
    public async Task Delete_does_not_remove_files_outside_the_application_upload_directory()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); var outside = store.CreateFile(insideUploads: false);
        await using var db = store.CreateContext(); (await db.Documents.SingleAsync()).StoragePath = outside; await db.SaveChangesAsync();
        var response = Assert.IsType<OkObjectResult>(await store.Controller(db).Delete(document.WorkspaceId, document.Id));
        Assert.True(File.Exists(outside)); Assert.Contains("cleanup could not", DocumentTestStore.Json(response.Value).GetProperty("warning").GetString());
        Assert.Empty(await db.Documents.ToListAsync());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("review")]
    [InlineData("import")]
    public async Task A_stale_mutation_racing_an_operation_claim_rolls_back_without_losing_or_adding_chunks(string mutation)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review"); var chunk = await store.AddChunkAsync(document);
        await using var staleDb = store.CreateContext(); await staleDb.Documents.SingleAsync(); await staleDb.Chunks.SingleAsync();
        await using (var ownerDb = store.CreateContext())
        {
            DocumentLifecycle.Begin(await ownerDb.Documents.SingleAsync(), "chunking"); await ownerDb.SaveChangesAsync();
        }
        IActionResult response = mutation switch
        {
            "delete" => await store.Controller(staleDb).Delete(document.WorkspaceId, document.Id),
            "review" => await new ChunkReviewController(staleDb, new FakeCurrentUser(store.User)).Review(document.WorkspaceId, chunk.Id, new ReviewDto("accepted", "Changed words.", null)),
            _ => await new ChunkReviewController(staleDb, new FakeCurrentUser(store.User)).BulkCreate(document.WorkspaceId,
                [new ImportChunkDto { DocumentId = document.Id, Page = 2, Text = "Imported words." }])
        };
        Assert.IsType<ConflictObjectResult>(response);
        await using var observe = store.CreateContext(); var retained = await observe.Chunks.SingleAsync();
        Assert.Equal(chunk.Id, retained.Id); Assert.Equal(chunk.Text, retained.Text); Assert.Equal("pending", retained.Status);
        Assert.Equal("chunking", (await observe.Documents.SingleAsync()).ProcessingOperation); Assert.True(File.Exists(document.StoragePath));
    }

    [Theory]
    [InlineData("parsing")]
    [InlineData("chunking")]
    [InlineData("embedding")]
    public async Task Active_operations_block_delete_review_merge_import_and_duplicate_processing(string operation)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review");
        var first = await store.AddChunkAsync(document); var second = await store.AddChunkAsync(document, page: 2);
        await using var db = store.CreateContext(); DocumentLifecycle.Begin(await db.Documents.SingleAsync(), operation); await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await store.Controller(db).Delete(document.WorkspaceId, document.Id));
        Assert.IsType<ConflictObjectResult>(await store.Controller(db).ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1], null)));
        Assert.IsType<ConflictObjectResult>(await store.Controller(db).GenerateEmbeddings(document.WorkspaceId, document.Id));
        var review = new ChunkReviewController(db, new FakeCurrentUser(store.User));
        Assert.IsType<ConflictObjectResult>(await review.Review(document.WorkspaceId, first.Id, new ReviewDto("accepted", null, null)));
        Assert.IsType<ConflictObjectResult>(await review.Merge(document.WorkspaceId, new MergeDto([first.Id, second.Id])));
        Assert.IsType<ConflictObjectResult>(await review.BulkAccept(document.WorkspaceId, new BulkAcceptDto(document.Id, null)));
        Assert.IsType<ConflictObjectResult>(await review.BulkCreate(document.WorkspaceId, [new ImportChunkDto { DocumentId = document.Id, Page = 3, Text = "Imported source." }]));
        Assert.Equal(2, await db.Chunks.CountAsync());
    }

    [Fact]
    public async Task Editing_embedded_text_invalidates_the_vector_while_exact_no_ops_preserve_it()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("indexed"); var chunk = await store.AddChunkAsync(document, "embedded", "[1,0]");
        await using var db = store.CreateContext(); var review = new ChunkReviewController(db, new FakeCurrentUser(store.User));
        Assert.IsType<OkObjectResult>(await review.Review(document.WorkspaceId, chunk.Id, new ReviewDto(null, chunk.Text, null)));
        Assert.Equal(1, (await db.Documents.SingleAsync()).Revision); Assert.Equal(chunk.Embedding, (await db.Chunks.SingleAsync()).Embedding);
        Assert.IsType<BadRequestObjectResult>(await review.Review(document.WorkspaceId, chunk.Id, new ReviewDto("embedded", null, null)));
        Assert.IsType<OkObjectResult>(await review.Review(document.WorkspaceId, chunk.Id, new ReviewDto(null, "Edited source words.", null)));
        db.ChangeTracker.Clear(); var edited = await db.Chunks.SingleAsync();
        Assert.Null(edited.Embedding); Assert.Null(edited.EmbeddingModel); Assert.False(edited.Verbatim); Assert.Equal("accepted", edited.Status);
        Assert.Contains("against the source PDF", edited.ValidationError); Assert.Equal("review", (await db.Documents.SingleAsync()).Status);
        Assert.Equal(2, (await db.Documents.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Import_contract_rejects_client_identities_vectors_status_and_provenance_and_creates_pending_content()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await using var db = store.CreateContext();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var forbidden in new[] { "id", "workspaceId", "status", "embedding", "embeddingModel", "reviewedBy", "reviewedAt", "document", "verbatim" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ImportChunkDto>($"{{\"{forbidden}\":\"forged\"}}", options));
        var input = new ImportChunkDto { DocumentId = document.Id, Page = 1, Text = "Imported exact words." };
        var review = new ChunkReviewController(db, new FakeCurrentUser(store.User));
        Assert.IsType<OkObjectResult>(await review.BulkCreate(document.WorkspaceId, [input]));
        db.ChangeTracker.Clear(); var imported = await db.Chunks.SingleAsync();
        Assert.Equal("pending", imported.Status); Assert.Equal(document.WorkspaceId, imported.WorkspaceId); Assert.Equal(document.Id, imported.DocumentId);
        Assert.Null(imported.Embedding); Assert.Null(imported.LegacyEmbeddingJson); Assert.Null(imported.EmbeddingModel); Assert.Null(imported.ReviewedBy); Assert.Null(imported.ReviewedAt); Assert.False(imported.Verbatim);
        Assert.Single(await db.Documents.ToListAsync());
        input.DocumentId = Guid.NewGuid(); Assert.IsType<NotFoundResult>(await review.BulkCreate(document.WorkspaceId, [input]));
        Assert.Single(await db.Chunks.ToListAsync());
    }

    public static IEnumerable<object[]> InvalidVectors()
    {
        foreach (var json in new[] { "null", "[]", "{}", "{\"embeddings\":[]}", "{\"embeddings\":[[1,0]]}",
            "{\"embeddings\":[[1,0],[0,1],[1,1]]}", "{\"embeddings\":[[1,0],[]]}", "{\"embeddings\":[[1,0],[1]]}",
            "{\"embeddings\":[[1,0],[0,0]]}", "{\"embeddings\":[[1,0],[\"1\",0]]}", "{\"embeddings\":[[1,0],[null,1]]}",
            "{\"embeddings\":[[1,0],[1e400,0]]}", "{\"embeddings\":[[1,0],[1e308,0]]}", "{\"embeddings\":[[1,0],[1e-300,0]]}" }) yield return [json];
        yield return [JsonSerializer.Serialize(new { embeddings = new[] { Enumerable.Repeat(1.0, 16385).ToArray(), Enumerable.Repeat(1.0, 16385).ToArray() } })];
    }

    [Theory]
    [MemberData(nameof(InvalidVectors))]
    public async Task Invalid_vector_batches_mark_no_new_chunks_embedded_and_preserve_existing_evidence(string json)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review");
        await store.AddChunkAsync(document, "accepted"); await store.AddChunkAsync(document, "accepted", page: 2);
        var existing = await store.AddChunkAsync(document, "embedded", "[0,1]", page: 3);
        var malformed = JsonDocument.Parse(json).RootElement.Clone();
        if (malformed.ValueKind == JsonValueKind.Object)
        {
            var labelled = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            labelled["model"] = JsonSerializer.SerializeToElement(EmbeddingTestData.Model);
            labelled["dimensions"] = JsonSerializer.SerializeToElement(2);
            malformed = JsonSerializer.SerializeToElement(labelled);
        }
        var colab = new FakeColabClient { EmbedHandler = _ => Task.FromResult(malformed) };
        await using var db = store.CreateContext();
        var response = Assert.IsType<ObjectResult>(await store.Controller(db, colab).GenerateEmbeddings(document.WorkspaceId, document.Id));
        Assert.Equal(502, response.StatusCode); Assert.Equal(0, DocumentTestStore.Json(response.Value).GetProperty("embedded").GetInt32());
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Equal("embedding", failed.FailureStage); Assert.Null(failed.ProcessingOperation);
        Assert.Equal(2, await db.Chunks.CountAsync(c => c.Status == "accepted" && c.Embedding == null));
        Assert.Equal(existing.Embedding, (await db.Chunks.SingleAsync(c => c.Id == existing.Id)).Embedding);
    }

    [Fact]
    public async Task Embedding_uses_current_accepted_text_and_indexes_only_after_all_review_is_finished_even_for_a_selected_subset()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review", pageCount: 10);
        var accepted = await store.AddChunkAsync(document, "accepted"); var missing = await store.AddChunkAsync(document, "embedded", page: 3);
        var pending = await store.AddChunkAsync(document, page: 3);
        await using var db = store.CreateContext(); var tracked = await db.Documents.SingleAsync();
        tracked.ChunkingStatus = "partial"; tracked.ChunkedPageNumbers = "[1,3]"; tracked.ChunkedPagesCount = 2; await db.SaveChangesAsync();
        var colab = new FakeColabClient(); var controller = store.Controller(db, colab);
        var embedded = Assert.IsType<OkObjectResult>(await controller.GenerateEmbeddings(document.WorkspaceId, document.Id));
        Assert.Equal(2, DocumentTestStore.Json(embedded.Value).GetProperty("embedded").GetInt32());
        Assert.Equal(new[] { accepted.Text, missing.Text }.Order(), colab.LastSubQueries.Order());
        db.ChangeTracker.Clear(); Assert.Equal("review", (await db.Documents.SingleAsync()).Status);
        Assert.IsType<OkObjectResult>(await new ChunkReviewController(db, new FakeCurrentUser(store.User)).Review(document.WorkspaceId, pending.Id, new ReviewDto("rejected", null, null)));
        db.ChangeTracker.Clear(); var indexed = await db.Documents.SingleAsync();
        Assert.Equal("indexed", indexed.Status); Assert.Equal("partial", indexed.ChunkingStatus); Assert.Equal(2, indexed.ChunkedPagesCount); Assert.Equal(10, indexed.PageCount);
        Assert.Equal(2, await db.Chunks.CountAsync(c => c.Status == "embedded" && c.Embedding != null));
        Assert.IsType<OkObjectResult>(await controller.GenerateEmbeddings(document.WorkspaceId, document.Id));
        Assert.Equal("indexed", (await db.Documents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancelled_embedding_releases_ownership_and_never_commits_the_late_vectors()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review"); await store.AddChunkAsync(document, "accepted");
        using var cancellation = new CancellationTokenSource(); var reached = Signal<bool>(); var release = Signal<JsonElement>();
        var colab = new FakeColabClient { EmbedHandler = _ => { reached.TrySetResult(true); return release.Task; } };
        await using var db = store.CreateContext(); var work = store.Controller(db, colab, cancellationToken: cancellation.Token).GenerateEmbeddings(document.WorkspaceId, document.Id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.True(colab.LastEmbeddingCancellationToken.IsCancellationRequested);
        var response = Assert.IsType<ObjectResult>(await work.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(502, response.StatusCode);
        // Ownership must be released before an uncooperative upstream returns.
        release.SetResult(JsonSerializer.SerializeToElement(new { embeddings = new[] { new[] { 1.0, 0.0 } } }));
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Null(failed.ProcessingOperation); Assert.Equal("embedding", failed.FailureStage);
        Assert.Equal("accepted", (await db.Chunks.SingleAsync()).Status); Assert.Null((await db.Chunks.SingleAsync()).Embedding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_embedding_owner_cannot_commit_vectors_or_replace_a_newer_failure(bool fails)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review"); await store.AddChunkAsync(document, "accepted");
        var reached = Signal<bool>(); var release = Signal<JsonElement>();
        var colab = new FakeColabClient { EmbedHandler = _ => { reached.TrySetResult(true); return release.Task; } };
        await using var ownerDb = store.CreateContext(); var work = store.Controller(ownerDb, colab).GenerateEmbeddings(document.WorkspaceId, document.Id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var recoveryDb = store.CreateContext(); (await recoveryDb.Documents.SingleAsync()).UpdatedAt = DateTime.UtcNow.AddHours(-1); await recoveryDb.SaveChangesAsync();
        Assert.False(await new DocumentParser(recoveryDb, new FakeColabClient(), store.Configuration).ProcessNextAsync());
        recoveryDb.ChangeTracker.Clear(); var recovered = await recoveryDb.Documents.SingleAsync(); var reason = recovered.FailureReason; var revision = recovered.Revision;
        if (fails) release.SetException(new HttpRequestException("PRIVATE_LATE_ERROR"));
        else release.SetResult(EmbeddingTestData.Result(1, EmbeddingTestData.Model, 1, 0));
        Assert.IsType<ConflictObjectResult>(await work);
        recoveryDb.ChangeTracker.Clear(); var retained = await recoveryDb.Documents.SingleAsync();
        Assert.Equal(reason, retained.FailureReason); Assert.Equal(revision, retained.Revision); Assert.Null((await recoveryDb.Chunks.SingleAsync()).Embedding);
        Assert.Equal("accepted", (await recoveryDb.Chunks.SingleAsync()).Status);
    }

    [Fact]
    public async Task Chunking_deduplicates_pages_checkpoints_a_selected_subset_and_preserves_document_metadata()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); var calls = 0;
        var mock = new MockColabHandler { RespondAsync = _ => { calls++; return Task.FromResult<HttpResponseMessage?>(MockColabHandler.SuccessResponse(1, 3)); } };
        await using var db = store.CreateContext();
        var response = Assert.IsType<OkObjectResult>(await store.Controller(db, http: new FakeHttpClientFactory(mock)).ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1, 1, 3, 3], null)));
        Assert.Equal(1, calls); Assert.Equal(2, DocumentTestStore.Json(response.Value).GetProperty("pagesChunked").GetInt32());
        db.ChangeTracker.Clear(); var completed = await db.Documents.SingleAsync();
        Assert.Equal("review", completed.Status); Assert.Null(completed.ProcessingOperation); Assert.Equal("partial", completed.ChunkingStatus);
        Assert.Equal("[1,3]", completed.ChunkedPageNumbers); Assert.Equal("draft", completed.GuidelineStatus); Assert.Equal(document.Society, completed.Society); Assert.Equal(document.Year, completed.Year);
        Assert.Equal(2, await db.Chunks.CountAsync(c => c.Status == "pending" && c.Embedding == null));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("out-of-batch")]
    [InlineData("partial")]
    public async Task Failed_or_unreported_chunk_pages_never_delete_their_retained_pending_content(string scenario)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var first = await store.AddChunkAsync(document); var second = await store.AddChunkAsync(document, page: 2);
        var mock = new MockColabHandler { RespondAsync = _ => Task.FromResult<HttpResponseMessage?>(scenario switch
        {
            "empty" => MockColabHandler.SuccessResponse(), "out-of-batch" => MockColabHandler.SuccessResponse(3), _ => MockColabHandler.SuccessResponse(1)
        }) };
        await using var db = store.CreateContext();
        Assert.IsType<OkObjectResult>(await store.Controller(db, http: new FakeHttpClientFactory(mock)).ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1, 2], null)));
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Equal("chunking", failed.FailureStage); Assert.Null(failed.ProcessingOperation);
        var retained = await db.Chunks.SingleAsync(c => c.Page == 2); Assert.Equal(second.Id, retained.Id); Assert.Equal(second.Text, retained.Text);
        if (scenario != "partial") Assert.Equal(first.Id, (await db.Chunks.SingleAsync(c => c.Page == 1)).Id);
        Assert.Equal(scenario == "partial" ? "[1]" : "[]", failed.ChunkedPageNumbers);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("embedded")]
    public async Task Chunking_cannot_replace_already_reviewed_source_pages(string status)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); var chunk = await store.AddChunkAsync(document, status, status == "embedded" ? "[1,0]" : null);
        await using var db = store.CreateContext();
        Assert.IsType<ConflictObjectResult>(await store.Controller(db).ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1], null)));
        Assert.Equal(chunk.Id, (await db.Chunks.SingleAsync()).Id); Assert.Equal(1, (await db.Documents.SingleAsync()).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chunking_polls_committed_checkpoints_and_retains_them_on_failure_or_cancellation(bool cancelled)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        using var cancellation = new CancellationTokenSource(); var secondBatch = Signal<bool>(); var release = Signal<HttpResponseMessage?>(); var calls = 0;
        var mock = new MockColabHandler { RespondAsync = _ => { calls++; if (calls == 1) return Task.FromResult<HttpResponseMessage?>(MockColabHandler.SuccessResponse(1)); secondBatch.TrySetResult(true); return release.Task; } };
        await using var ownerDb = store.CreateContext(); var work = store.Controller(ownerDb, http: new FakeHttpClientFactory(mock), cancellationToken: cancellation.Token)
            .ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1, 2, 3], null, BatchSize: 1));
        await secondBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var observe = store.CreateContext(); var checkpoint = await observe.Documents.SingleAsync();
        Assert.Equal("chunking", checkpoint.Status); Assert.Equal("chunking", checkpoint.ProcessingOperation); Assert.Equal("[1]", checkpoint.ChunkedPageNumbers); Assert.Single(await observe.Chunks.ToListAsync());
        var progress = DocumentTestStore.Json(Assert.IsType<OkObjectResult>(await store.Controller(observe).ChunkingProgress(document.WorkspaceId, document.Id)).Value);
        Assert.Equal(1, progress.GetProperty("chunkedPages").GetInt32()); Assert.Equal(33.3, progress.GetProperty("progressPercent").GetDouble());
        if (cancelled) cancellation.Cancel();
        release.SetException(cancelled ? new OperationCanceledException(cancellation.Token) : new HttpRequestException("PRIVATE_UPSTREAM_ERROR"));
        var response = Assert.IsType<ObjectResult>(await work); Assert.Equal(503, response.StatusCode);
        Assert.DoesNotContain("PRIVATE", DocumentTestStore.Json(response.Value).GetRawText());
        observe.ChangeTracker.Clear(); var failed = await observe.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Null(failed.ProcessingOperation); Assert.Equal("[1]", failed.ChunkedPageNumbers); Assert.Equal(1, failed.ChunkedPagesCount);
        Assert.Equal(new[] { 2, 3 }, DocumentTestStore.Json(response.Value).GetProperty("failedPages").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Single(await observe.Chunks.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_chunk_owner_cannot_replace_pending_content_or_overwrite_a_newer_failure(bool upstreamFails)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var retainedChunk = await store.AddChunkAsync(document);
        var reached = Signal<bool>(); var release = Signal<HttpResponseMessage?>();
        var mock = new MockColabHandler { RespondAsync = _ => { reached.TrySetResult(true); return release.Task; } };
        var http = new FakeHttpClientFactory(mock);
        await using var ownerDb = store.CreateContext();
        var work = store.Controller(ownerDb, http: http).ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1], null));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var recoveryDb = store.CreateContext();
        (await recoveryDb.Documents.SingleAsync()).UpdatedAt = DateTime.UtcNow.AddHours(-1); await recoveryDb.SaveChangesAsync();
        Assert.False(await new DocumentParser(recoveryDb, TestColabClients.Create(store.Configuration, http), store.Configuration).ProcessNextAsync());
        recoveryDb.ChangeTracker.Clear(); var recovered = await recoveryDb.Documents.SingleAsync();
        var revision = recovered.Revision; var reason = recovered.FailureReason;
        if (upstreamFails) release.SetException(new HttpRequestException("PRIVATE_LATE_ERROR"));
        else release.SetResult(MockColabHandler.SuccessResponse(1));
        Assert.IsType<ConflictObjectResult>(await work);
        recoveryDb.ChangeTracker.Clear(); var retained = await recoveryDb.Documents.SingleAsync();
        Assert.Equal(revision, retained.Revision); Assert.Equal(reason, retained.FailureReason); Assert.Equal("[]", retained.ChunkedPageNumbers);
        var chunk = await recoveryDb.Chunks.SingleAsync(); Assert.Equal(retainedChunk.Id, chunk.Id); Assert.Equal(retainedChunk.Text, chunk.Text);
        Assert.Equal("pending", chunk.Status); Assert.Null(chunk.Embedding);
    }

    [Theory]
    [InlineData("chunking", "chunking")]
    [InlineData("embedding", "embedding")]
    [InlineData("chunking", "embedding")]
    [InlineData("embedding", "chunking")]
    public async Task Stale_duplicate_or_cross_operation_claims_cannot_start_upstream_work(string ownerOperation, string staleOperation)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review");
        var accepted = await store.AddChunkAsync(document, "accepted"); var pending = await store.AddChunkAsync(document, page: 2);
        await using var staleDb = store.CreateContext(); await staleDb.Documents.SingleAsync();
        await using (var ownerDb = store.CreateContext())
        {
            DocumentLifecycle.Begin(await ownerDb.Documents.SingleAsync(), ownerOperation); await ownerDb.SaveChangesAsync();
        }
        var calls = 0;
        var colab = new FakeColabClient { EmbedHandler = _ => { calls++; throw new InvalidOperationException("A stale request must not reach embedding."); } };
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => { calls++; throw new InvalidOperationException("A stale request must not reach chunking."); }));
        var controller = store.Controller(staleDb, colab, http);
        var response = staleOperation == "embedding"
            ? await controller.GenerateEmbeddings(document.WorkspaceId, document.Id)
            : await controller.ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([2], null));
        Assert.IsType<ConflictObjectResult>(response); Assert.Equal(0, calls);
        await using var observe = store.CreateContext(); var owner = await observe.Documents.SingleAsync();
        Assert.Equal(ownerOperation, owner.ProcessingOperation); Assert.Equal(2, owner.Revision);
        Assert.Equal(new[] { accepted.Id, pending.Id }.Order(), (await observe.Chunks.ToListAsync()).Select(c => c.Id).Order());
        Assert.Equal(0, await observe.Chunks.CountAsync(c => c.Embedding != null));
    }

    [Theory]
    [InlineData("embedding")]
    [InlineData("chunking")]
    public async Task A_processing_claim_read_before_a_completed_review_cannot_overwrite_the_review(string operation)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review");
        var chunk = await store.AddChunkAsync(document, operation == "embedding" ? "accepted" : "pending");
        await using var staleDb = store.CreateContext(); await staleDb.Documents.SingleAsync();
        await using (var reviewDb = store.CreateContext())
            Assert.IsType<OkObjectResult>(await new ChunkReviewController(reviewDb, new FakeCurrentUser(store.User)).Review(document.WorkspaceId, chunk.Id,
                new ReviewDto(null, "Reviewed updated source words.", null)));
        var calls = 0;
        var colab = new FakeColabClient { EmbedHandler = _ => { calls++; throw new InvalidOperationException(); } };
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => { calls++; throw new InvalidOperationException(); }));
        var controller = store.Controller(staleDb, colab, http);
        var response = operation == "embedding"
            ? await controller.GenerateEmbeddings(document.WorkspaceId, document.Id)
            : await controller.ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1], null));
        Assert.IsType<ConflictObjectResult>(response); Assert.Equal(0, calls);
        await using var observe = store.CreateContext();
        Assert.Equal("Reviewed updated source words.", (await observe.Chunks.SingleAsync()).Text);
        Assert.Equal(2, (await observe.Documents.SingleAsync()).Revision); Assert.Null((await observe.Documents.SingleAsync()).ProcessingOperation);
    }

    [Fact]
    public async Task Explicit_skips_replace_only_reported_pages_and_never_unreported_or_out_of_batch_pages()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var first = await store.AddChunkAsync(document); await store.AddChunkAsync(document, page: 2);
        var third = await store.AddChunkAsync(document, page: 3);
        var mock = new MockColabHandler { RespondAsync = _ => Task.FromResult<HttpResponseMessage?>(MockColabHandler.SkipResponse([1], [2, 99])) };
        await using var db = store.CreateContext();
        var response = Assert.IsType<OkObjectResult>(await store.Controller(db, http: new FakeHttpClientFactory(mock))
            .ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1, 2, 3], null, BatchSize: 3)));
        var result = DocumentTestStore.Json(response.Value);
        Assert.Equal(1, result.GetProperty("pagesChunked").GetInt32()); Assert.Equal(1, result.GetProperty("pagesSkipped").GetInt32());
        Assert.Equal(3, Assert.Single(result.GetProperty("failedPages").EnumerateArray()).GetInt32());
        Assert.Equal(2, Assert.Single(result.GetProperty("skippedPages").EnumerateArray()).GetProperty("page").GetInt32());
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Equal("[1,2]", failed.ChunkedPageNumbers); Assert.Equal(2, failed.ChunkedPagesCount);
        Assert.NotEqual(first.Id, (await db.Chunks.SingleAsync(c => c.Page == 1)).Id); Assert.False(await db.Chunks.AnyAsync(c => c.Page == 2));
        Assert.Equal(third.Id, (await db.Chunks.SingleAsync(c => c.Page == 3)).Id); Assert.Equal(2, await db.Chunks.CountAsync());
    }

    [Fact]
    public async Task Cancelling_a_hung_chunk_submission_releases_its_claim_before_any_late_result()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); var retained = await store.AddChunkAsync(document);
        using var cancellation = new CancellationTokenSource();
        var reached = Signal<bool>(); var release = Signal<HttpResponseMessage>(); CancellationToken upstreamToken = default;
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, token) =>
        {
            upstreamToken = token; reached.TrySetResult(true); return release.Task;
        }));
        await using var db = store.CreateContext();
        var work = store.Controller(db, http: http, cancellationToken: cancellation.Token)
            .ChunkPortions(document.WorkspaceId, document.Id, new ChunkPortionsDto([1], null));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        var response = Assert.IsType<ObjectResult>(await work.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(503, response.StatusCode);
        Assert.True(upstreamToken.IsCancellationRequested);
        release.SetResult(MockColabHandler.SuccessResponse(1));
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Equal("chunking", failed.FailureStage); Assert.Null(failed.ProcessingOperation);
        Assert.Equal("[]", failed.ChunkedPageNumbers); Assert.Equal(retained.Id, (await db.Chunks.SingleAsync()).Id);
        Assert.Equal(retained.Text, (await db.Chunks.SingleAsync()).Text);
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("bulk_accept")]
    public async Task Stale_multi_chunk_review_racing_a_claim_rolls_back_every_chunk_edit(string operation)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("review");
        var first = await store.AddChunkAsync(document); var second = await store.AddChunkAsync(document, page: 2);
        await using var staleDb = store.CreateContext(); await staleDb.Documents.SingleAsync(); await staleDb.Chunks.ToListAsync();
        await using (var ownerDb = store.CreateContext())
        {
            DocumentLifecycle.Begin(await ownerDb.Documents.SingleAsync(), "chunking"); await ownerDb.SaveChangesAsync();
        }
        var review = new ChunkReviewController(staleDb, new FakeCurrentUser(store.User));
        var response = operation == "merge"
            ? await review.Merge(document.WorkspaceId, new MergeDto([first.Id, second.Id]))
            : await review.BulkAccept(document.WorkspaceId, new BulkAcceptDto(document.Id, null));
        Assert.IsType<ConflictObjectResult>(response);
        await using var observe = store.CreateContext(); var chunks = await observe.Chunks.ToListAsync();
        Assert.Equal(new[] { first.Id, second.Id }.Order(), chunks.Select(c => c.Id).Order());
        Assert.All(chunks, c => { Assert.Equal("pending", c.Status); Assert.Null(c.Embedding); });
        Assert.Equal(first.Text, chunks.Single(c => c.Id == first.Id).Text); Assert.Equal(second.Text, chunks.Single(c => c.Id == second.Id).Text);
        Assert.Equal("chunking", (await observe.Documents.SingleAsync()).ProcessingOperation); Assert.Equal(2, (await observe.Documents.SingleAsync()).Revision);
    }
}
