using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClinicalRag.Tests;

public class DocumentParserTests
{
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Parsed() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { text = "--- Page 1 ---\nRetained exact source words.", pageCount = 1 }) };

    private sealed class ParseCommitObserver(TaskCompletionSource<bool> completed) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Document>().Any(e => e.Entity.Status == "parsed"
                && e.Entity.ProcessingOperation == null && !string.IsNullOrWhiteSpace(e.Entity.ParsedText)))
                completed.TrySetResult(true); // Signal only after the relational save committed.
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Persisted_uploads_are_parsed_oldest_first_without_any_browser_parse_request_or_auto_chunking()
    {
        using var store = new DocumentTestStore();
        var older = await store.AddDocumentAsync("uploaded", parsed: false);
        var newer = await store.AddDocumentAsync("uploaded", parsed: false);
        await using (var seed = store.CreateContext())
        {
            (await seed.Documents.SingleAsync(d => d.Id == older.Id)).CreatedAt = DateTime.UtcNow.AddDays(-1);
            await seed.SaveChangesAsync();
        }
        var requests = 0;
        var http = new FakeHttpClientFactory(new DocumentHttpHandler(async (request, token) =>
        {
            requests++;
            Assert.Equal("/parse-pdf", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-colab-key", request.Headers.GetValues("X-Colab-Key").Single());
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("%PDF-", await request.Content!.ReadAsStringAsync(token));
            return Parsed();
        }));
        await using var workerDb = store.CreateContext();
        var parser = new DocumentParser(workerDb, TestColabClients.Create(store.Configuration, http), store.Configuration);
        Assert.True(await parser.ProcessNextAsync());
        await using (var observe = store.CreateContext())
        {
            var first = await observe.Documents.SingleAsync(d => d.Id == older.Id);
            Assert.Equal("parsed", first.Status); Assert.Null(first.ProcessingOperation);
            Assert.Equal("Synthetic Society", first.Society); Assert.Equal(2024, first.Year); Assert.Equal("draft", first.GuidelineStatus);
            Assert.Equal("[]", first.ChunkedPageNumbers); Assert.Equal(0, first.ChunkedPagesCount); Assert.Equal("not_started", first.ChunkingStatus);
            Assert.Equal("uploaded", (await observe.Documents.SingleAsync(d => d.Id == newer.Id)).Status);
            Assert.Empty(await observe.Chunks.ToListAsync());
        }
        workerDb.ChangeTracker.Clear();
        Assert.True(await parser.ProcessNextAsync());
        workerDb.ChangeTracker.Clear();
        Assert.False(await parser.ProcessNextAsync()); Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Duplicate_worker_and_retry_claims_parse_only_once_even_with_a_stale_tracked_upload()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("uploaded", parsed: false);
        var reached = Signal<bool>(); var release = Signal<HttpResponseMessage>(); var calls = 0;
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => { calls++; reached.TrySetResult(true); return release.Task; }));
        await using var workerDb = store.CreateContext(); await using var retryDb = store.CreateContext();
        await retryDb.Documents.SingleAsync(); // A request that read before the worker's claim.
        var work = new DocumentParser(workerDb, TestColabClients.Create(store.Configuration, http), store.Configuration).ParseAsync(document.WorkspaceId, document.Id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await new DocumentParser(retryDb, TestColabClients.Create(store.Configuration, http), store.Configuration).ParseAsync(document.WorkspaceId, document.Id);
        Assert.Equal("conflict", duplicate.Outcome); Assert.Equal(1, calls);
        release.SetResult(Parsed()); Assert.Equal("parsed", (await work).Outcome);
        await using var observe = store.CreateContext(); Assert.Equal(3, (await observe.Documents.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Image_only_pages_are_persisted_and_reported_without_losing_parsed_pages()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync("uploaded", parsed: false);
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    text = "--- Page 1 ---\nSynthetic extractable text.",
                    pageCount = 3,
                    needsOcrPages = new[] { 2, 3 }
                })
            })));
        await using var db = store.CreateContext();
        var parser = new DocumentParser(db, TestColabClients.Create(store.Configuration, http), store.Configuration);
        var result = await parser.ParseAsync(document.WorkspaceId, document.Id);

        Assert.Equal("parsed", result.Outcome);
        Assert.Equal(new[] { 2, 3 }, result.NeedsOcrPages!.ToArray());
        db.ChangeTracker.Clear();
        var saved = await db.Documents.SingleAsync();
        Assert.Equal("[2,3]", saved.NeedsOcrPages);
        Assert.Contains("Synthetic extractable text.", saved.ParsedText);
        var view = DocumentLifecycle.View(saved, DocumentChunkCounts.Empty);
        Assert.Equal("parsed", view.Status);
        Assert.Equal(new[] { 2, 3 }, view.NeedsOcrPages!.ToArray());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"text\":\" \" ,\"pageCount\":2}")]
    [InlineData("{\"text\":\"Source\",\"pageCount\":0}")]
    [InlineData("{\"text\":\"Source\",\"pageCount\":1.5}")]
    [InlineData("{\"text\":\"Source\",\"pageCount\":\"2\"}")]
    public async Task Invalid_or_empty_parse_results_fail_safely_and_are_not_automatically_retried(string body)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("uploaded", parsed: false);
        var calls = 0;
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }));
        await using var db = store.CreateContext(); var parser = new DocumentParser(db, TestColabClients.Create(store.Configuration, http), store.Configuration);
        Assert.Equal("failed", (await parser.ParseAsync(document.WorkspaceId, document.Id)).Outcome);
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Equal("parsing", failed.FailureStage); Assert.Null(failed.ParsedText); Assert.Null(failed.ProcessingOperation);
        Assert.Contains("Retry Parse", failed.FailureReason); Assert.Empty(await db.Chunks.ToListAsync());
        Assert.False(await parser.ProcessNextAsync()); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("file")]
    [InlineData("cancel")]
    public async Task Parsing_failure_is_persisted_without_upstream_bodies_paths_or_case_text(string failure)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("uploaded", parsed: false);
        if (failure == "file") File.Delete(document.StoragePath!);
        using var cancellation = new CancellationTokenSource();
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) =>
        {
            if (failure == "cancel") { cancellation.Cancel(); throw new OperationCanceledException("PRIVATE_UPSTREAM_BODY", cancellation.Token); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("PRIVATE_UPSTREAM_BODY") });
        }));
        await using var db = store.CreateContext();
        var result = await new DocumentParser(db, TestColabClients.Create(store.Configuration, http), store.Configuration).ParseAsync(document.WorkspaceId, document.Id, cancellation.Token);
        Assert.Equal("failed", result.Outcome); Assert.DoesNotContain("PRIVATE", result.Error); Assert.DoesNotContain(store.Root, result.Error);
        db.ChangeTracker.Clear(); var failed = await db.Documents.SingleAsync();
        Assert.Equal("failed", failed.Status); Assert.Null(failed.ProcessingOperation); Assert.Null(failed.ParsedText); Assert.Equal(3, failed.Revision);
    }

    [Theory]
    [InlineData("parsing")]
    [InlineData("chunking")]
    [InlineData("embedding")]
    public async Task Expired_claim_recovery_retains_checkpoints_chunks_and_metadata_and_does_not_retry_failed_documents(string operation)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(operation == "embedding" ? "review" : operation);
        var chunk = await store.AddChunkAsync(document, "accepted");
        await using var db = store.CreateContext();
        var stale = await db.Documents.SingleAsync(); stale.ProcessingOperation = operation;
        stale.ChunkedPageNumbers = "[1,3]"; stale.ChunkedPagesCount = 2; stale.ChunkingStatus = "in_progress";
        stale.UpdatedAt = DateTime.UtcNow.AddHours(-1); await db.SaveChangesAsync();
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => throw new InvalidOperationException("Failed work must not be retried automatically.")));
        Assert.False(await new DocumentParser(db, TestColabClients.Create(store.Configuration, http), store.Configuration).ProcessNextAsync());
        db.ChangeTracker.Clear(); var recovered = await db.Documents.SingleAsync();
        Assert.Equal("failed", recovered.Status); Assert.Equal(operation, recovered.FailureStage); Assert.Null(recovered.ProcessingOperation);
        Assert.Equal("[1,3]", recovered.ChunkedPageNumbers); Assert.Equal(2, recovered.ChunkedPagesCount); Assert.Equal(document.ParsedText, recovered.ParsedText);
        Assert.Equal("draft", recovered.GuidelineStatus); Assert.Equal("Synthetic Society", recovered.Society);
        Assert.Equal(chunk.Id, (await db.Chunks.SingleAsync()).Id); Assert.Null((await db.Chunks.SingleAsync()).Embedding);
        Assert.Equal(operation == "chunking" ? "failed" : "in_progress", recovered.ChunkingStatus);
    }

    [Fact]
    public async Task A_recent_processing_checkpoint_is_not_expired_or_retried()
    {
        using var store = new DocumentTestStore(); await store.AddDocumentAsync("chunking");
        await using var db = store.CreateContext(); var document = await db.Documents.SingleAsync();
        document.ProcessingOperation = "chunking"; document.UpdatedAt = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => throw new InvalidOperationException("An active owner must not be replaced.")));
        Assert.False(await new DocumentParser(db, TestColabClients.Create(store.Configuration, http), store.Configuration).ProcessNextAsync());
        db.ChangeTracker.Clear(); document = await db.Documents.SingleAsync();
        Assert.Equal("chunking", document.ProcessingOperation); Assert.Equal(1, document.Revision); Assert.Null(document.FailureReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_expired_owner_cannot_revive_success_or_replace_the_recovery_failure(bool upstreamFails)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync("uploaded", parsed: false);
        var reached = Signal<bool>(); var release = Signal<HttpResponseMessage>();
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => { reached.TrySetResult(true); return release.Task; }));
        await using var ownerDb = store.CreateContext();
        var work = new DocumentParser(ownerDb, TestColabClients.Create(store.Configuration, http), store.Configuration).ParseAsync(document.WorkspaceId, document.Id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var recoveryDb = store.CreateContext();
        (await recoveryDb.Documents.SingleAsync()).UpdatedAt = DateTime.UtcNow.AddHours(-1); await recoveryDb.SaveChangesAsync();
        Assert.False(await new DocumentParser(recoveryDb, TestColabClients.Create(store.Configuration, http), store.Configuration).ProcessNextAsync());
        recoveryDb.ChangeTracker.Clear(); var recovered = await recoveryDb.Documents.SingleAsync(); var revision = recovered.Revision; var reason = recovered.FailureReason;
        if (upstreamFails) release.SetException(new HttpRequestException("PRIVATE_LATE_FAILURE")); else release.SetResult(Parsed());
        Assert.Equal("conflict", (await work).Outcome);
        recoveryDb.ChangeTracker.Clear(); var retained = await recoveryDb.Documents.SingleAsync();
        Assert.Equal(revision, retained.Revision); Assert.Equal(reason, retained.FailureReason); Assert.Null(retained.ParsedText); Assert.Equal("failed", retained.Status);
    }

    [Fact]
    public async Task Hosted_worker_processes_durable_uploads_and_stops_cleanly_without_a_browser()
    {
        using var store = new DocumentTestStore(); await store.AddDocumentAsync("uploaded", parsed: false);
        var committed = Signal<bool>();
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => Task.FromResult(Parsed())));
        var options = new DbContextOptionsBuilder<AppDbContext>(store.Options).AddInterceptors(new ParseCommitObserver(committed)).Options;
        var services = new ServiceCollection();
        services.AddSingleton(store.Configuration); services.AddSingleton<IHttpClientFactory>(http);
        services.AddSingleton<IColabClient>(TestColabClients.Create(store.Configuration, http));
        services.AddScoped<AppDbContext>(_ => new AppDbContext(options)); services.AddScoped<DocumentParser>();
        await using var provider = services.BuildServiceProvider();
        using var worker = new DocumentParsingWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DocumentParsingWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try { await committed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        await using var observe = store.CreateContext(); var document = await observe.Documents.SingleAsync();
        Assert.Equal("parsed", document.Status); Assert.Equal(3, document.Revision); Assert.Equal(1, document.PageCount);
        Assert.Contains("Retained exact source words.", document.ParsedText); Assert.Null(document.FailureReason);
        Assert.Null(document.ProcessingOperation); Assert.Empty(await observe.Chunks.ToListAsync());
    }

    [Fact]
    public async Task Hosted_worker_shutdown_cancels_inflight_parsing_and_persists_a_retryable_failure_before_stopping()
    {
        using var store = new DocumentTestStore(); await store.AddDocumentAsync("uploaded", parsed: false);
        var reached = Signal<bool>(); var release = Signal<HttpResponseMessage>(); CancellationToken serviceToken = default;
        var http = new FakeHttpClientFactory(new DocumentHttpHandler((_, token) =>
        {
            serviceToken = token; reached.TrySetResult(true);
            return release.Task; // Deliberately ignore cancellation: a late response must not revive the worker.
        }));
        var services = new ServiceCollection();
        services.AddSingleton(store.Configuration); services.AddSingleton<IHttpClientFactory>(http);
        services.AddSingleton<IColabClient>(TestColabClients.Create(store.Configuration, http));
        services.AddScoped<AppDbContext>(_ => store.CreateContext()); services.AddScoped<DocumentParser>();
        await using var provider = services.BuildServiceProvider();
        using var worker = new DocumentParsingWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DocumentParsingWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(serviceToken.IsCancellationRequested);
        release.SetResult(Parsed());
        await using var observe = store.CreateContext(); var document = await observe.Documents.SingleAsync();
        Assert.Equal("failed", document.Status); Assert.Equal("parsing", document.FailureStage); Assert.Equal(3, document.Revision);
        Assert.Contains("Retry Parse", document.FailureReason); Assert.Null(document.ParsedText); Assert.Null(document.PageCount);
        Assert.Null(document.ProcessingOperation); Assert.Empty(await observe.Chunks.ToListAsync());
    }
}
