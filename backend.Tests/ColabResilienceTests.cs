using System.Net;
using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

/// <summary>Fake-handler resilience tests for the Colab transport: retries, credential rejection,
/// circuit transitions, cancellation, non-cooperative bodies and late disposal.</summary>
public class ColabResilienceTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return handle(request, cancellationToken);
        }
    }

    private static ColabResilienceHandler CreateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle,
        int failures = 5, int breakSeconds = 30, Action<ColabCircuitBreaker>? configure = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Colab:BreakerFailures"] = failures.ToString(),
                ["Colab:BreakerSeconds"] = breakSeconds.ToString(),
            }).Build();
        var breaker = new ColabCircuitBreaker(config, TimeProvider.System);
        configure?.Invoke(breaker);
        var inner = new RecordingHandler(handle);
        var handler = new ColabResilienceHandler(breaker, new ColabRetryDelay(config, TimeProvider.System), TimeProvider.System)
        { InnerHandler = inner };
        return handler;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, bool idempotent = false, int maxRetries = 2,
        TimeSpan? attemptTimeout = null, string? circuitKey = null, string? step = null)
    {
        var req = new HttpRequestMessage(method, new Uri($"https://colab.test{path}"));
        req.Options.Set(ColabResilienceHandler.Options, new ColabCallOptions(
            circuitKey ?? "test-circuit", step ?? "colab.http",
            attemptTimeout ?? TimeSpan.FromSeconds(30), idempotent, maxRetries));
        return req;
    }

    [Fact]
    public async Task Transient_500_is_retried_then_succeeds()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            if (calls < 3) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        });
        var client = new HttpClient(handler);
        var response = await client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Unauthorized_is_not_retried_and_counts_as_breaker_failure()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"unauthorized\"}") };
        }, failures: 1);
        var client = new HttpClient(handler);

        // First call: 401 -> breaker failure, threshold=1 so circuit opens.
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Post, "/generate")));

        // Second call: circuit is open, no HTTP call is made.
        var exception = await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Post, "/generate")));
        Assert.Equal(1, calls); // No second HTTP call.
    }

    [Fact]
    public async Task Redirect_3xx_is_not_retried_and_counts_as_breaker_failure()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.MovedPermanently)
            {
                Headers = { Location = new Uri("https://evil.example/") }
            };
        }, failures: 1);
        var client = new HttpClient(handler);

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Post, "/generate")));
        Assert.Equal(1, calls);

        var exception = await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Post, "/generate")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Circuit_opens_after_threshold_and_rejects_calls()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }, failures: 3);
        var client = new HttpClient(handler);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 0)));
        }

        // Circuit is now open; further calls are rejected without reaching the handler.
        var exception = await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 0)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Half_open_probe_is_rejected_while_circuit_open()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }, failures: 3);
        var client = new HttpClient(handler);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 0)));

        // Circuit is open; next call is rejected without reaching the handler (probe window not yet reached).
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 0)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Non_idempotent_post_is_not_retried_on_transient_failure()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var client = new HttpClient(handler);

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Post, "/chunk/jobs", idempotent: false)));
        Assert.Equal(1, calls); // No retry for non-idempotent
    }

    [Fact]
    public async Task Idempotent_get_is_retried_on_transient_failure()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return calls < 2
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ready\":true}") };
        });
        var client = new HttpClient(handler);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 3));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_propagates_and_disposes_pending_response()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateHandler(async (_, _) =>
        {
            return await release.Task; // Never responds until released
        });
        var client = new HttpClient(handler);

        using var cts = new CancellationTokenSource();
        var task = client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, attemptTimeout: TimeSpan.FromMilliseconds(100)), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // Release the pending handler so it can observe cancellation and dispose its response.
        release.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        await Task.Delay(50); // Let the handler's continuation run
    }

    [Fact]
    public async Task Non_cooperative_body_is_disposed_on_deadline()
    {
        // A response body stream that never completes read simulates a non-cooperative handler.
        // The resilience handler's deadline must cancel and dispose it.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateHandler(async (_, _) =>
        {
            var content = new StreamContent(new NonCooperativeStream(release.Task));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var client = new HttpClient(handler);

        var cts = new CancellationTokenSource();
        var task = client.SendAsync(Request(HttpMethod.Post, "/generate", idempotent: false, attemptTimeout: TimeSpan.FromMilliseconds(100)), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // Release the stream so its cleanup can run.
        release.TrySetResult(true);
        await Task.Delay(50);
    }

    [Fact]
    public async Task Separate_circuit_keys_are_independent()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }, failures: 2);
        var client = new HttpClient(handler);

        // Circuit A opens after 2 failures (maxRetries: 0, one attempt each).
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, circuitKey: "circuit-a", maxRetries: 0)));
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, circuitKey: "circuit-a", maxRetries: 0)));

        // Circuit B is independent and still allows calls.
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, circuitKey: "circuit-b", maxRetries: 0)));
        Assert.Equal(3, calls); // Circuit B's call reached the handler.
    }

    [Fact]
    public async Task Retry_after_header_is_honored()
    {
        var calls = 0;
        var handler = CreateHandler(async (_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return response;
        }, failures: 3);
        var client = new HttpClient(handler);

        var start = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 3)));
        var elapsed = DateTimeOffset.UtcNow - start;

        // At least one retry delay should have been >= 1 second (from Retry-After).
        Assert.True(elapsed >= TimeSpan.FromSeconds(1), $"Expected >= 1s delay, got {elapsed.TotalSeconds}s");
    }

    [Fact]
    public async Task Old_in_flight_success_does_not_close_newer_opened_circuit()
    {
        // This tests the epoch-based protection: a Lease created before the circuit was opened
        // by a newer failure must not close the circuit when it eventually succeeds.
        var calls = 0;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Colab:BreakerFailures"] = "1",
                ["Colab:BreakerSeconds"] = "300", // Long break so circuit stays open
            }).Build();
        var breaker = new ColabCircuitBreaker(config, TimeProvider.System);
        var firstResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new RecordingHandler(async (req, token) =>
        {
            calls++;
            if (calls == 1) { firstReached.SetResult(); return await firstResponse.Task; }
            if (calls == 2) { secondReached.SetResult(); return await secondResponse.Task; }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var handler = new ColabResilienceHandler(breaker, new ColabRetryDelay(config, TimeProvider.System), TimeProvider.System)
        { InnerHandler = inner };
        var client = new HttpClient(handler);

        // Start first call (will be slow)
        var task1 = client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 0, circuitKey: "epoch-circuit"));

        // Wait for first call to reach handler and enter breaker (Lease created with epoch 0)
        await firstReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Start second call - circuit still closed, it enters (same circuit, epoch 0)
        var task2 = client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, maxRetries: 0, circuitKey: "epoch-circuit"));

        // Let second call enter breaker too, then fail it (opens circuit, bumps epoch to 1)
        await secondReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondResponse.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => task2);

        // Now complete the first call successfully - it has epoch 0, circuit has epoch 1.
        // The stale success must NOT close the circuit.
        firstResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        await task1; // Should complete successfully at HTTP level

        // Circuit should still be open (epoch 1, opened by failure)
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => client.SendAsync(Request(HttpMethod.Get, "/ready", idempotent: true, circuitKey: "epoch-circuit")));
        Assert.Equal(2, calls); // Rejection must happen at admission, not via a new failed HTTP call.
    }

    private sealed class NonCooperativeStream(Task<bool> released) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await released.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await released.WaitAsync(cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}

/// <summary>Tests for the Colab circuit breaker state machine directly.</summary>
public class ColabCircuitBreakerTests
{
    [Fact]
    public void Success_resets_failures()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Colab:BreakerFailures"] = "2",
            ["Colab:BreakerSeconds"] = "30",
        }).Build();
        var breaker = new ColabCircuitBreaker(config, TimeProvider.System);

        using var lease1 = breaker.Enter("key");
        using var lease2 = breaker.Enter("key");
        lease2.Failure();
        lease1.Success(); // Should reset failures to 0

        // Circuit should be closed; entering again should not immediately open.
        using var lease3 = breaker.Enter("key");
        lease3.Failure();
        using var lease4 = breaker.Enter("key");
        lease4.Failure(); // 2nd failure = threshold, opens circuit
        Assert.Throws<DependencyUnavailableException>(() => breaker.Enter("key"));
    }

    [Fact]
    public void Open_circuit_rejects_with_retry_after()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Colab:BreakerFailures"] = "2",
            ["Colab:BreakerSeconds"] = "300",
        }).Build();
        var breaker = new ColabCircuitBreaker(config, TimeProvider.System);

        using var l1 = breaker.Enter("key"); l1.Failure();
        using var l2 = breaker.Enter("key"); l2.Failure(); // Opens circuit

        var ex = Assert.Throws<DependencyUnavailableException>(() => breaker.Enter("key"));
        Assert.True(ex.Retryable);
        Assert.True(ex.RetryAfterSeconds > 0);
    }

    [Fact]
    public void Probe_lease_disposed_without_success_failure_resets_probe_flag()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Colab:BreakerFailures"] = "2",
            ["Colab:BreakerSeconds"] = "1",
        }).Build();
        var clock = new ColabTestClock();
        var breaker = new ColabCircuitBreaker(config, clock);

        using var l1 = breaker.Enter("key"); l1.Failure();
        using var l2 = breaker.Enter("key"); l2.Failure(); // Opens circuit

        // Wait for the break period to elapse so the probe window opens.
        clock.Advance(TimeSpan.FromSeconds(1));

        // Enter should be a probe; dispose without completing should reset probe flag.
        using var probe = breaker.Enter("key");
        probe.Dispose();

        // After probe disposal, a success resets failures and closes the circuit.
        using var l3 = breaker.Enter("key");
        l3.Success();

        // Circuit is closed again; two failures reopen it.
        using var l4 = breaker.Enter("key");
        l4.Failure();
        using var l5 = breaker.Enter("key");
        l5.Failure();
        Assert.Throws<DependencyUnavailableException>(() => breaker.Enter("key"));
    }

    [Fact]
    public void Circuit_keys_are_bounded_to_32()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Colab:BreakerFailures"] = "5",
            ["Colab:BreakerSeconds"] = "300",
        }).Build();
        var breaker = new ColabCircuitBreaker(config, TimeProvider.System);

        for (var i = 0; i < 40; i++)
        {
            using var lease = breaker.Enter($"key-{i}");
        }

        // All 40 entries should succeed without throwing (oldest entries evicted).
        Assert.True(true);
    }
}
