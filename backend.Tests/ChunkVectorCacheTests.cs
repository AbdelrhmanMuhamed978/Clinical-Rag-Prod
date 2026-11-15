using ClinicalRag.Api.Data;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

public class ChunkVectorCacheTests
{
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ChunkSearchIndex Index(string text = "Indexed source") => new([
        new(Guid.NewGuid(), text, null, EmbeddingTestData.Model, EmbeddingVectors.EncodeNormalized([1, 0]))]);

    [Fact]
    public async Task A_workspace_is_loaded_once_until_invalidated_and_other_workspaces_are_isolated()
    {
        using var cache = new ChunkVectorCache(); var first = Guid.NewGuid(); var other = Guid.NewGuid(); var loads = 0;
        Task<ChunkSearchIndex> Load(CancellationToken _) { loads++; return Task.FromResult(Index()); }
        var original = await cache.GetAsync(first, Load); var otherIndex = await cache.GetAsync(other, Load);
        Assert.Same(original, await cache.GetAsync(first, Load)); Assert.Equal(2, loads);
        cache.Invalidate(first);
        Assert.NotSame(original, await cache.GetAsync(first, Load)); Assert.Same(otherIndex, await cache.GetAsync(other, Load)); Assert.Equal(3, loads);
    }

    [Fact]
    public async Task Concurrent_cache_misses_share_one_load()
    {
        using var cache = new ChunkVectorCache(); var id = Guid.NewGuid(); var release = Signal<ChunkSearchIndex>(); var loads = 0;
        Task<ChunkSearchIndex> Load(CancellationToken _) { Interlocked.Increment(ref loads); return release.Task; }
        var requests = Enumerable.Range(0, 20).Select(_ => cache.GetAsync(id, Load)).ToArray();
        var index = Index(); release.SetResult(index);
        Assert.All(await Task.WhenAll(requests), result => Assert.Same(index, result)); Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Invalidation_during_a_load_does_not_publish_the_stale_snapshot()
    {
        using var cache = new ChunkVectorCache(); var id = Guid.NewGuid(); var release = Signal<ChunkSearchIndex>(); var loads = 0; var latest = Index("Edited source");
        Task<ChunkSearchIndex> Load(CancellationToken _) => ++loads == 1 ? release.Task : Task.FromResult(latest);
        var request = cache.GetAsync(id, Load); cache.Invalidate(id); release.SetResult(Index("Old source"));
        Assert.Same(latest, await request); Assert.Same(latest, await cache.GetAsync(id, Load)); Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Failed_or_cancelled_loads_do_not_poison_the_next_request()
    {
        using var cache = new ChunkVectorCache(); var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(id, _ => throw new InvalidOperationException()));
        using var cancellation = new CancellationTokenSource(); var reached = Signal<bool>();
        var cancelled = cache.GetAsync(id, async token => { reached.SetResult(true); await Task.Delay(Timeout.Infinite, token); return Index(); }, cancellation.Token);
        await reached.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var good = Index(); Assert.Same(good, await cache.GetAsync(id, _ => Task.FromResult(good)));
    }

    [Fact]
    public async Task The_memory_budget_limits_admission_and_refuses_an_oversized_workspace()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Retrieval:CacheSizeMegabytes"] = "16" }).Build();
        using var cache = new ChunkVectorCache(settings);
        var values = new float[RetrievalLimits.MaxEmbeddingDimensions]; values[0] = 1;
        var bytes = EmbeddingVectors.EncodeNormalized(values);
        ChunkSearchIndex Large(int count) => new(Enumerable.Range(0, count)
            .Select(_ => new ChunkVectorRecord(Guid.NewGuid(), "source", null, EmbeddingTestData.Model, bytes)).ToArray());
        var index = Large(140); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var loads = 0;
        Task<ChunkSearchIndex> Load(CancellationToken _) { loads++; return Task.FromResult(index); }
        await cache.GetAsync(first, Load); await cache.GetAsync(second, Load);
        await cache.GetAsync(first, Load); await cache.GetAsync(second, Load);
        Assert.True(loads >= 3); // Both ~9 MiB entries cannot be retained in 16 MiB.
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Guid.NewGuid(), _ => Task.FromResult(Large(270))));
    }

    private static async Task<ChunkSearchIndex> ReadIndex(DocumentTestStore store)
    {
        return await store.VectorCache.GetAsync(store.Workspace.Id, async token =>
        {
            await using var db = store.CreateContext();
            var records = await db.Chunks.AsNoTracking().Where(c => c.WorkspaceId == store.Workspace.Id
                && (c.Status == "accepted" || c.Status == "embedded") && c.Embedding != null)
                .Select(c => new ChunkVectorRecord(c.Id, c.Text, c.Section, c.EmbeddingModel, c.Embedding!)).ToListAsync(token);
            return new ChunkSearchIndex(records);
        });
    }

    [Theory]
    [InlineData("embed", "accepted", false)]
    [InlineData("accept", "pending", false)]
    [InlineData("edit", "embedded", true)]
    [InlineData("section", "embedded", true)]
    [InlineData("delete", "embedded", true)]
    [InlineData("bulk_accept", "pending", true)]
    public async Task Committed_embedding_accept_edit_section_and_delete_invalidate_the_workspace(string action, string status, bool vector)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var chunk = await store.AddChunkAsync(document, status, vector ? "[1,0]" : null);
        var before = await ReadIndex(store);
        await using (var db = store.CreateContext())
        {
            var review = new ChunkReviewController(db, new FakeCurrentUser(store.User));
            IActionResult result = action switch
            {
                "embed" => await store.Controller(db, new FakeColabClient
                    { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 1, 0)) }).GenerateEmbeddings(store.Workspace.Id, document.Id),
                "delete" => await store.Controller(db).Delete(store.Workspace.Id, document.Id),
                "bulk_accept" => await review.BulkAccept(store.Workspace.Id, new BulkAcceptDto(document.Id, null)),
                "accept" => await review.Review(store.Workspace.Id, chunk.Id, new ReviewDto("accepted", null, null)),
                "section" => await review.Review(store.Workspace.Id, chunk.Id, new ReviewDto(null, null, "Edited section")),
                _ => await review.Review(store.Workspace.Id, chunk.Id, new ReviewDto(null, "Edited source", null))
            };
            Assert.IsType<OkObjectResult>(result);
        }
        var after = await ReadIndex(store); Assert.NotSame(before, after);
        Assert.Equal(action == "embed" ? 1 : 0, after.Count);
    }

    [Fact]
    public async Task Exact_review_no_ops_preserve_the_cached_index()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); var chunk = await store.AddChunkAsync(document, "embedded", "[1,0]");
        var before = await ReadIndex(store); await using var db = store.CreateContext();
        Assert.IsType<OkObjectResult>(await new ChunkReviewController(db, new FakeCurrentUser(store.User)).Review(store.Workspace.Id, chunk.Id, new ReviewDto(null, chunk.Text, null)));
        Assert.Same(before, await ReadIndex(store));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_transactions_invalidate_only_after_commit_not_on_save_or_rollback(bool commit)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await store.AddChunkAsync(document, "embedded", "[1,0]");
        var original = await ReadIndex(store);
        await using var db = store.CreateContext(); await using var transaction = await db.Database.BeginTransactionAsync();
        (await db.Chunks.SingleAsync()).Text = "Changed source"; await db.SaveChangesAsync();
        Assert.Same(original, await ReadIndex(store));
        if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        var after = await ReadIndex(store);
        if (commit) Assert.NotSame(original, after); else Assert.Same(original, after);
    }

    [Fact]
    public async Task Disposing_an_uncommitted_transaction_cannot_leak_invalidations_into_a_later_save()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await store.AddChunkAsync(document, "embedded", "[1,0]");
        var original = await ReadIndex(store); await using var db = store.CreateContext();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await db.Chunks.SingleAsync()).Text = "Rolled back source"; await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear(); await db.SaveChangesAsync();
        Assert.Same(original, await ReadIndex(store));
        Assert.NotEqual("Rolled back source", (await db.Chunks.SingleAsync()).Text);
    }
}
