using System.Net;

namespace ClinicalRag.Api.Services;

public sealed record ColabCallOptions(string CircuitKey, string Step, TimeSpan AttemptTimeout, bool Idempotent, int MaxRetries = 2);

/// <summary>Bounded process-local breaker state, separated by endpoint + credential revision.</summary>
public sealed class ColabCircuitBreaker(IConfiguration configuration, TimeProvider clock)
{
    private readonly TimeProvider _clock = clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, Circuit> _circuits = new(StringComparer.Ordinal);
    private readonly int _threshold = Math.Clamp(configuration.GetValue("Colab:BreakerFailures", 5), 1, 100);
    private readonly TimeSpan _break = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Colab:BreakerSeconds", 30), 1, 300));
    internal sealed class Circuit { public int Failures; public DateTimeOffset OpenUntil; public bool Probe; public DateTimeOffset Used; public long Epoch; }

    public Lease Enter(string key)
    {
        lock (_sync)
        {
            if (!_circuits.TryGetValue(key, out var circuit))
            {
                if (_circuits.Count >= 32) _circuits.Remove(_circuits.MinBy(pair => pair.Value.Used).Key);
                circuit = new(); _circuits[key] = circuit;
            }
            var now = _clock.GetUtcNow(); circuit.Used = now;
            if (circuit.OpenUntil > now)
                throw new DependencyUnavailableException("colab", (int)Math.Max(1, Math.Ceiling((circuit.OpenUntil - now).TotalSeconds)), retryable: true);
            // Break period elapsed: allow a single probe attempt.
            var probe = circuit.Failures >= _threshold;
            if (probe)
            {
                if (circuit.Probe)
                    throw new DependencyUnavailableException("colab", (int)_break.TotalSeconds, retryable: true);
                circuit.Probe = true;
            }
            return new Lease(this, circuit, probe);
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly ColabCircuitBreaker _owner;
        private readonly Circuit _circuit;
        private readonly bool _probe;
        private readonly long _epoch;
        private bool _finished;
        internal Lease(ColabCircuitBreaker owner, Circuit circuit, bool probe)
        { _owner = owner; _circuit = circuit; _probe = probe; _epoch = circuit.Epoch; }
        public void Success()
        {
            lock (_owner._sync)
            {
                if (_finished) return;
                // An old in-flight success must not close a circuit opened by a newer failure.
                if (_circuit.Epoch == _epoch)
                {
                    _circuit.Failures = 0; _circuit.OpenUntil = default; _circuit.Probe = false;
                    if (_probe) _circuit.Epoch++;
                }
                _finished = true;
            }
        }
        public void Failure()
        {
            lock (_owner._sync)
            {
                if (_finished) return;
                if (_circuit.Epoch == _epoch)
                {
                    if (++_circuit.Failures >= _owner._threshold || _probe)
                    {
                        _circuit.OpenUntil = _owner._clock.GetUtcNow() + _owner._break;
                        _circuit.Epoch++;
                    }
                    _circuit.Probe = false;
                }
                _finished = true;
            }
        }
        public void Dispose() { lock (_owner._sync) { if (!_finished && _probe && _circuit.Epoch == _epoch) _circuit.Probe = false; _finished = true; } }
    }
}

public interface IColabRetryDelay
{
    Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken);
}

public sealed class ColabRetryDelay(IConfiguration configuration, TimeProvider clock) : IColabRetryDelay
{
    public Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var basis = Math.Clamp(configuration.GetValue("Colab:RetryBaseMilliseconds", 250), 1, 5000);
        var jitter = TimeSpan.FromMilliseconds(Math.Min(10000, basis * (1 << attempt)) * (.5 + Random.Shared.NextDouble()));
        var delay = retryAfter.HasValue && retryAfter.Value > jitter ? retryAfter.Value : jitter;
        return Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, clock, cancellationToken);
    }
}

/// <summary>Retries only explicitly replayable calls; generated/chunk-job work is never submitted twice.</summary>
public sealed class ColabResilienceHandler(ColabCircuitBreaker breaker, IColabRetryDelay delay, TimeProvider clock) : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<ColabCallOptions> Options = new("ClinicalRag.ColabCall");
    private const long MaxResponseBytes = 32 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = request.Options.TryGetValue(Options, out var configured) ? configured
            : new ColabCallOptions(request.RequestUri!.Authority, "colab.http", TimeSpan.FromSeconds(30), request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);
        using var lease = breaker.Enter(call.CircuitKey);
        using var timing = OperationTelemetry.Start(call.Step);
        var retries = call.Idempotent ? Math.Clamp(call.MaxRetries, 0, 5) : 0;
        try
        {
            // Buffer only replayable request bodies, before the first send. Never clone a job submission.
            var payload = retries > 0 && request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : null;
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = new CancellationTokenSource(call.AttemptTimeout, clock);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                using var copy = attempt > 0 ? Copy(request, payload) : null;
                HttpResponseMessage? response = null;
                TimeSpan? retryAfter = null;
                try
                {
                    response = await HttpResponseDeadline.AwaitAsync(base.SendAsync(copy ?? request, deadline.Token), deadline.Token);
                    // The deadline covers body reads too, including handlers/content that ignore cancellation.
                    await HttpResponseDeadline.AwaitBodyAsync(response.Content.LoadIntoBufferAsync(MaxResponseBytes, deadline.Token), deadline.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || (int)response.StatusCode is >= 300 and < 400)
                    {
                        lease.Failure();
                        throw new DependencyUnavailableException("colab"); // Bad credentials/redirects cannot be repaired by replaying.
                    }
                    if (!Transient(response.StatusCode))
                    {
                        lease.Success();
                        if (response.IsSuccessStatusCode) timing.Success();
                        return response;
                    }
                    retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
                }
                catch (DependencyUnavailableException) { response?.Dispose(); throw; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { response?.Dispose(); throw; }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or TimeoutException)
                { response?.Dispose(); response = null; }
                response?.Dispose();
                if (attempt >= retries)
                {
                    lease.Failure();
                    var hint = retryAfter.HasValue ? (int)Math.Clamp(Math.Ceiling(retryAfter.Value.TotalSeconds), 1, 300) : 30;
                    throw new DependencyUnavailableException("colab", hint, retryable: true);
                }
                OperationTelemetry.Retry();
                await delay.DelayAsync(attempt, retryAfter, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { timing.Cancelled(); throw; }
    }

    private static bool Transient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;
    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? payload)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (payload != null)
        {
            copy.Content = new ByteArrayContent(payload);
            foreach (var header in request.Content!.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }
}
