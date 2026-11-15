using System.Net;
using System.Text.Json;
using ClinicalRag.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

public class EmbeddingBatchClientTests
{
    private static IConfiguration Settings(params (string Name, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Name, value.Value))).Build();

    [Theory]
    [InlineData(null, 32)]
    [InlineData("16", 16)]
    [InlineData("0", 1)]
    [InlineData("-10", 1)]
    [InlineData("1000", 32)]
    public void Batch_size_defaults_to_thirty_two_and_cannot_exceed_the_shared_cap(string? configured, int expected)
    {
        var settings = configured == null ? Settings() : Settings(("Embedding:BatchSize", configured));
        Assert.Equal(expected, new EmbeddingBatchClient(new FakeColabClient(), settings).BatchSize);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(408, 3)]
    [InlineData(429, 3)]
    [InlineData(500, 3)]
    [InlineData(503, 3)]
    [InlineData(400, 1)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(413, 1)]
    [InlineData(422, 1)]
    public async Task Only_transient_http_failures_retry_and_the_default_retry_budget_is_two(int status, int expectedCalls)
    {
        var calls = 0; var client = new FakeColabClient { EmbedHandler = _ =>
        {
            calls++; throw new HttpRequestException("PRIVATE_UPSTREAM", null, status == 0 ? null : (HttpStatusCode)status);
        } };
        var batches = new EmbeddingBatchClient(client, Settings(("Embedding:RetryBaseMilliseconds", "0")));
        await Assert.ThrowsAsync<HttpRequestException>(() => batches.EmbedAsync(["Reviewed source"]));
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task Caller_cancellation_during_backoff_stops_retries()
    {
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var client = new FakeColabClient { EmbedHandler = _ =>
        {
            calls++; reached.TrySetResult(true); throw new HttpRequestException("PRIVATE_UPSTREAM", null, HttpStatusCode.TooManyRequests);
        } };
        var batches = new EmbeddingBatchClient(client, Settings(("Embedding:RetryBaseMilliseconds", "5000")));
        using var cancellation = new CancellationTokenSource(); var work = batches.EmbedAsync(["Reviewed source"], cancellation.Token);
        await reached.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_attempt_deadline_cancels_only_that_attempt_and_can_retry_successfully()
    {
        var never = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        CancellationToken firstAttempt = default; var client = new FakeColabClient();
        client.EmbedHandler = texts =>
        {
            if (++calls == 1) { firstAttempt = client.LastEmbeddingCancellationToken; return never.Task; }
            return Task.FromResult(EmbeddingTestData.Result(texts.Count, EmbeddingTestData.Model, 3, 4));
        };
        var batches = new EmbeddingBatchClient(client, Settings(("Embedding:TimeoutSeconds", "1"), ("Embedding:MaxRetries", "1"), ("Embedding:RetryBaseMilliseconds", "0")));
        var result = await batches.EmbedAsync(["Reviewed source"]);
        Assert.True(firstAttempt.IsCancellationRequested); Assert.Equal(2, calls);
        Assert.Equal(new[] { .6f, .8f }, EmbeddingVectors.ReadNormalized(Assert.Single(result.Vectors)));
    }

    [Theory]
    [InlineData(0, "Source")]
    [InlineData(33, "Source")]
    [InlineData(1, " ")]
    public async Task Invalid_batches_are_rejected_before_any_upstream_request(int count, string text)
    {
        var calls = 0; var client = new FakeColabClient { EmbedHandler = _ => { calls++; throw new InvalidOperationException(); } };
        await Assert.ThrowsAsync<ArgumentException>(() => new EmbeddingBatchClient(client, Settings()).EmbedAsync(Enumerable.Repeat(text, count).ToArray()));
        Assert.Equal(0, calls);
    }
}
