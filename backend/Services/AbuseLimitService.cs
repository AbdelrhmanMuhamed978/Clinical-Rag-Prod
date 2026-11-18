using System.Collections.Concurrent;
using System.Security.Claims;
using ClinicalRag.Api.Attributes;
using Microsoft.Extensions.Options;

namespace ClinicalRag.Api.Services;

/// <summary>Bounded per-user abuse limits for expensive analysis routes.</summary>
public sealed class AbuseLimitOptions
{
    public int RequestsPerWindow { get; set; } = 8;
    public int WindowSeconds { get; set; } = 60;
    public int Concurrency { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 300;
}

public interface IAbuseLimitService
{
    /// <summary>Returns a lease when the request may run; otherwise a positive Retry-After.</summary>
    AbuseLease? TryAcquire(string userKey, out int retryAfterSeconds);
}

public sealed class AbuseLease : IDisposable
{
    private readonly UserLimitState _state;
    private int _released;

    internal AbuseLease(UserLimitState state) => _state = state;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _state.Release();
    }
}

internal sealed class UserLimitState
{
    private readonly object _gate = new();
    private DateTimeOffset _windowStart;
    private int _requests;
    private int _active;

    public bool TryAcquire(DateTimeOffset now, TimeSpan window, int requestLimit, int concurrency, out int retryAfterSeconds)
    {
        lock (_gate)
        {
            if (now - _windowStart >= window)
            {
                _windowStart = now;
                _requests = 0;
            }
            if (_active >= concurrency)
            {
                retryAfterSeconds = 1;
                return false;
            }
            if (_requests >= requestLimit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_windowStart + window - now).TotalSeconds));
                return false;
            }
            _requests++;
            _active++;
            retryAfterSeconds = 0;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate) _active = Math.Max(0, _active - 1);
    }
}

public sealed class AbuseLimitService : IAbuseLimitService
{
    private readonly ConcurrentDictionary<string, UserLimitState> _users = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _window;
    private readonly int _requests;
    private readonly int _concurrency;

    public AbuseLimitService(IOptions<AbuseLimitOptions> options, TimeProvider clock)
    {
        _clock = clock;
        var value = options.Value;
        _requests = Math.Clamp(value.RequestsPerWindow, 1, 1000);
        _window = TimeSpan.FromSeconds(Math.Clamp(value.WindowSeconds, 1, 3600));
        _concurrency = Math.Clamp(value.Concurrency, 1, 100);
    }

    public AbuseLease? TryAcquire(string userKey, out int retryAfterSeconds)
    {
        var now = _clock.GetUtcNow();
        var state = _users.GetOrAdd(userKey, _ => new UserLimitState());
        if (!state.TryAcquire(now, _window, _requests, _concurrency, out retryAfterSeconds))
            return null;
        return new AbuseLease(state);
    }
}

public sealed class AbuseLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAbuseLimitService limits)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AbuseLimitedAttribute>() is null)
        {
            await next(context);
            return;
        }

        var user = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("user_id")
            ?? context.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(user)) user = "anonymous";

        using var lease = limits.TryAcquire(user, out var retryAfter);
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Too many analysis requests. Retry shortly.",
                retryAfterSeconds = retryAfter
            });
            return;
        }

        await next(context);
    }
}
