using System.Diagnostics;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;

namespace ClinicalRag.Api.Services;

public static class RequestCorrelation
{
    public const string Header = "X-Request-Id";
    public static string Create(string? supplied) => supplied is { Length: 32 } && supplied.All(Uri.IsHexDigit)
        ? supplied.ToLowerInvariant() : Guid.NewGuid().ToString("N");
    public static string Current(IHttpContextAccessor? accessor) => accessor?.HttpContext?.TraceIdentifier is { Length: 32 } id
        && id.All(Uri.IsHexDigit) ? id : Guid.NewGuid().ToString("N");
}

public static class ServiceSecrets
{
    public static bool Matches(string? expected, string? actual) => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
        && actual.Length <= 4096 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}

public sealed class DependencyUnavailableException(string service, int retryAfterSeconds = 30, bool retryable = false)
    : HttpRequestException($"The {service} service is temporarily unavailable.", null, System.Net.HttpStatusCode.ServiceUnavailable)
{
    public string Service { get; } = service;
    public int RetryAfterSeconds { get; } = Math.Clamp(retryAfterSeconds, 1, 300);
    public bool Retryable { get; } = retryable;
    public string PublicMessage => Service == "colab" ? "The Colab model service is offline or not ready. Retry shortly, or ask an administrator to check its connection."
        : Service == "database" ? "The backend database is temporarily unavailable. Retry shortly."
        : "The verified pipeline service is temporarily unavailable. Retry shortly; no unverified answer was returned.";
}

public sealed class RequestObservabilityMiddleware(RequestDelegate next, ILogger<RequestObservabilityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var id = RequestCorrelation.Create(context.Request.Headers[RequestCorrelation.Header].ToString());
        context.TraceIdentifier = id;
        context.Response.OnStarting(() => { context.Response.Headers[RequestCorrelation.Header] = id; return Task.CompletedTask; });
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["requestId"] = id, ["traceId"] = Activity.Current?.TraceId.ToString() });
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception exception)
        {
            // Do not log exception messages/stack traces: SDK failures can include bodies, URLs and credentials.
            logger.LogWarning("Request failed {ExceptionType}", exception.GetType().Name);
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            if (exception is DependencyUnavailableException unavailable)
                await UnavailableAsync(context, unavailable);
            else if (exception is DbException || exception.InnerException is DbException)
                await UnavailableAsync(context, new DependencyUnavailableException("database"));
            else
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed. Retry or contact support with the request ID.", requestId = id });
            }
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // The route template, never a query string, raw path, case, prompt or body.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            logger.LogInformation("Request completed {Method} {Route} {StatusCode} {DurationMilliseconds}", context.Request.Method, route, context.Response.StatusCode, Math.Round(duration, 3));
            OperationTelemetry.Record("http.server", context.Response.StatusCode >= 500 ? "error" : context.Response.StatusCode == 499 ? "cancelled" : "success", duration);
        }
    }

    public static async Task UnavailableAsync(HttpContext context, DependencyUnavailableException exception)
    {
        context.Response.StatusCode = 503;
        context.Response.Headers.RetryAfter = exception.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = exception.PublicMessage, dependency = exception.Service, requestId = context.TraceIdentifier });
    }
}
