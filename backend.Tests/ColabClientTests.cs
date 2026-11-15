using System.Net;
using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class ColabClientTests
{
    [Fact]
    public async Task Embedding_forwards_current_text_credentials_and_request_cancellation_to_http()
    {
        using var store = new DocumentTestStore();
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken upstreamToken = default;
        var http = new FakeHttpClientFactory(new DocumentHttpHandler(async (request, token) =>
        {
            Assert.Equal("/embed", request.RequestUri!.AbsolutePath); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("synthetic-colab-key", request.Headers.GetValues("X-Colab-Key").Single()); Assert.Null(request.Headers.Authorization);
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("Current reviewed source words.", Assert.Single(body.GetProperty("texts").EnumerateArray()).GetString());
            upstreamToken = token; reached.TrySetResult(true);
            return await release.Task.WaitAsync(token);
        }));
        using var cancellation = new CancellationTokenSource();
        var task = TestColabClients.Create(store.Configuration, http).EmbedAsync(["Current reviewed source words."], cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(upstreamToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true, 33)]
    [InlineData(true, 0)]
    [InlineData(false, 31)]
    [InlineData(false, 0)]
    public async Task Oversized_or_empty_embedding_and_rerank_batches_are_rejected_before_http(bool embed, int count)
    {
        using var store = new DocumentTestStore(); var calls = 0;
        var client = TestColabClients.Create(store.Configuration, new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => { calls++; throw new InvalidOperationException(); })));
        var texts = Enumerable.Repeat("Source", count).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => embed ? client.EmbedAsync(texts) : client.RerankAsync("Query", texts));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Reranking_truncates_unicode_and_forwards_only_colab_credentials_and_cancellation()
    {
        using var store = new DocumentTestStore(); var original = string.Concat(Enumerable.Repeat("😀", 2100));
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken upstream = default;
        var client = TestColabClients.Create(store.Configuration, new FakeHttpClientFactory(new DocumentHttpHandler(async (request, token) =>
        {
            Assert.Equal("/rerank", request.RequestUri!.AbsolutePath); Assert.Null(request.Headers.Authorization);
            Assert.Equal("synthetic-colab-key", request.Headers.GetValues("X-Colab-Key").Single());
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(2000, Assert.Single(body.GetProperty("passages").EnumerateArray()).GetString()!.EnumerateRunes().Count());
            upstream = token; reached.SetResult(true); return await release.Task.WaitAsync(token);
        })));
        using var cancellation = new CancellationTokenSource(); var passages = new List<string> { original };
        var work = client.RerankAsync("Query", passages, cancellation.Token); await reached.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.True(upstream.IsCancellationRequested);
        Assert.Equal(original, Assert.Single(passages));
    }
}
