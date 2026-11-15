using System.Net;
using ClinicalRag.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

internal sealed class ColabTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}

public class ColabRecoveryTests
{
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(token); }
    }

    private static ColabCircuitBreaker Breaker(ColabTestClock clock) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Colab:BreakerFailures"] = "1", ["Colab:BreakerSeconds"] = "30" }).Build(), clock);

    private static HttpRequestMessage Request()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://colab.test/ready");
        request.Options.Set(ColabResilienceHandler.Options, new("runtime", "colab.ready", TimeSpan.FromSeconds(10), true, 0));
        return request;
    }

    private static HttpClient Client(ColabCircuitBreaker breaker, ColabTestClock clock, Handler inner) => new(
        new ColabResilienceHandler(breaker, new ColabRetryDelay(new ConfigurationBuilder().Build(), clock), clock)
        { InnerHandler = inner });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_one_half_open_call_reaches_transport_and_its_result_controls_recovery(bool success)
    {
        var clock = new ColabTestClock(); var breaker = Breaker(clock);
        using (var failed = breaker.Enter("runtime")) failed.Failure();
        clock.Advance(TimeSpan.FromSeconds(30));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inner = new Handler(_ => { reached.TrySetResult(); return release.Task; });
        using var client = Client(breaker, clock, inner);
        using var probeRequest = Request();
        var probe = client.SendAsync(probeRequest);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Advancing well past the original cooldown must not admit a second live probe.
        clock.Advance(TimeSpan.FromMinutes(5));
        using var blockedRequest = Request();
        var blocked = await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(blockedRequest));
        Assert.True(blocked.Retryable); Assert.Equal(30, blocked.RetryAfterSeconds); Assert.Equal(1, inner.Calls);
        release.SetResult(new(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        if (success)
        {
            using var response = await probe;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var next = breaker.Enter("runtime"); next.Success();
        }
        else
        {
            await Assert.ThrowsAsync<DependencyUnavailableException>(() => probe);
            Assert.Equal(30, Assert.Throws<DependencyUnavailableException>(() => breaker.Enter("runtime")).RetryAfterSeconds);
            clock.Advance(TimeSpan.FromSeconds(30));
            using var next = breaker.Enter("runtime"); next.Success();
        }
    }

    [Fact]
    public async Task Caller_cancellation_releases_probe_without_reopening_or_closing_circuit()
    {
        var clock = new ColabTestClock(); var breaker = Breaker(clock);
        using (var failed = breaker.Enter("runtime")) failed.Failure();
        clock.Advance(TimeSpan.FromSeconds(30));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inner = new Handler(async token =>
        { reached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); });
        using var client = Client(breaker, clock, inner);
        using var cancellation = new CancellationTokenSource(); using var request = Request();
        var probe = client.SendAsync(request, cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        using var replacement = breaker.Enter("runtime");
        Assert.Throws<DependencyUnavailableException>(() => breaker.Enter("runtime"));
        replacement.Success();
        using var recovered = breaker.Enter("runtime"); recovered.Success();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stale_pre_open_completion_cannot_release_the_active_probe(bool success)
    {
        var clock = new ColabTestClock(); var breaker = Breaker(clock);
        using var stale = breaker.Enter("runtime");
        using (var failed = breaker.Enter("runtime")) failed.Failure();
        clock.Advance(TimeSpan.FromSeconds(30));
        using var probe = breaker.Enter("runtime");
        if (success) stale.Success(); else stale.Failure();
        Assert.Throws<DependencyUnavailableException>(() => breaker.Enter("runtime"));
        probe.Success();
        using var recovered = breaker.Enter("runtime"); recovered.Success();
    }
}
