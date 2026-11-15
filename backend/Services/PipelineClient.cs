using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClinicalRag.Api.Services;

public sealed record PipelineConversationTurn(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("verifiedSummary")] string VerifiedSummary);

public sealed record PipelineRule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("drug_classes")] JsonElement DrugClasses,
    [property: JsonPropertyName("applies_when")] JsonElement AppliesWhen,
    [property: JsonPropertyName("level")] string? Level,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("caveat")] string? Caveat,
    [property: JsonPropertyName("sources")] JsonElement Sources,
    [property: JsonPropertyName("link_terms")] string? LinkTerms,
    [property: JsonPropertyName("case_level")] bool CaseLevel,
    [property: JsonPropertyName("status")] string Status = "draft",
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("revision")] int Revision = 1);

public sealed record PipelineAnalyzeRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("config")] JsonElement Config,
    [property: JsonPropertyName("rules")] IReadOnlyList<PipelineRule> Rules,
    [property: JsonPropertyName("history")] IReadOnlyList<PipelineConversationTurn> History,
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("retrieveUrl")] string RetrieveUrl,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("drugCatalog")] JsonElement DrugCatalog,
    [property: JsonPropertyName("catalogRevision")] int CatalogRevision);

public sealed record PipelineRulePreviewRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("rule")] PipelineRule Rule,
    [property: JsonPropertyName("drugCatalog")] JsonElement DrugCatalog,
    [property: JsonPropertyName("regexSample")] string RegexSample,
    [property: JsonPropertyName("catalogRevision")] int CatalogRevision);

public interface IRulePreviewClient
{
    Task<JsonElement> PreviewAsync(PipelineRulePreviewRequest request, string? authorization, CancellationToken cancellationToken = default);
}

public sealed record PipelineStreamEvent(string Event, JsonElement Data);

public interface IPipelineClient
{
    Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request, string? authorization,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(PipelineAnalyzeRequest request,
        string? authorization, CancellationToken cancellationToken = default);
}

public sealed class PipelineClient : IPipelineClient, IRulePreviewClient
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly IHttpContextAccessor? _context;
    private readonly ILogger<PipelineClient>? _log;

    public PipelineClient(IConfiguration configuration, IHttpClientFactory factory,
        IHttpContextAccessor? context = null, ILogger<PipelineClient>? log = null)
    {
        _configuration = configuration;
        _http = factory.CreateClient("Pipeline");
        _context = context; _log = log;
        _timeout = TimeSpan.FromSeconds(
            Math.Max(30, configuration.GetValue<int>("Pipeline:TimeoutSeconds", 240)));
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    private HttpRequestMessage CreateRequest<T>(HttpMethod method, string path,
        T request, string? authorization)
    {
        return CreateMessage(method, path, JsonContent.Create(request), authorization);
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string path, HttpContent? content, string? authorization)
    {
        var serviceKey = _configuration["Pipeline:ApiKey"];
        if (string.IsNullOrWhiteSpace(serviceKey))
            throw new DependencyUnavailableException("pipeline");
        var baseUrl = _configuration["Pipeline:BaseUrl"]?.TrimEnd('/')
            ?? "http://localhost:8100";
        var message = new HttpRequestMessage(method, baseUrl + path)
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(authorization)
            && AuthenticationHeaderValue.TryParse(authorization, out var authHeader))
        {
            message.Headers.Authorization = authHeader;
        }
        message.Headers.TryAddWithoutValidation("X-Pipeline-Key", serviceKey);
        message.Headers.TryAddWithoutValidation(RequestCorrelation.Header, RequestCorrelation.Current(_context));
        return message;
    }

    public async Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request,
        string? authorization, CancellationToken cancellationToken = default)
    {
        using var timing = OperationTelemetry.Start("pipeline.analyze", _log, request.Text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        using var message = CreateRequest(HttpMethod.Post, "/analyze", request, authorization);
        using var response = await SendAsync(message, deadline.Token, cancellationToken);
        await EnsureSuccessAsync(response, deadline.Token, cancellationToken);
        var result = await ReadJsonAsync(response, deadline.Token, cancellationToken);
        timing.Success();
        return result;
    }

    public async Task<JsonElement> PreviewAsync(PipelineRulePreviewRequest request, string? authorization, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timing = OperationTelemetry.Start("pipeline.preview", _log, request.Text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var message = CreateRequest(HttpMethod.Post, "/rules/preview", request, authorization);
        message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using var response = await SendAsync(message, deadline.Token, cancellationToken);
        await EnsureSuccessAsync(response, deadline.Token, cancellationToken);
        var result = await ReadJsonAsync(response, deadline.Token, cancellationToken);
        timing.Success();
        return result;
    }

    public async Task<bool> ReadyAsync(CancellationToken cancellationToken = default)
    {
        using var timing = OperationTelemetry.Start("pipeline.ready");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Python's sequential connection + model probes have a four-second total budget.
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("Pipeline:ReadyTimeoutSeconds", 6), 5, 30)));
        try
        {
            using var message = CreateMessage(HttpMethod.Get, "/ready", null, null);
            using var response = await SendAsync(message, deadline.Token, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var result = await ReadJsonAsync(response, deadline.Token, cancellationToken);
            var ready = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ready", out var value) && value.ValueKind == JsonValueKind.True;
            if (ready) timing.Success();
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { timing.Cancelled(); throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException) { return false; }
    }

    public async IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(
        PipelineAnalyzeRequest request,
        string? authorization,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timing = OperationTelemetry.Start("pipeline.stream", _log, request.Text);
        // HttpClient's timeout ends at response headers for an SSE request. Bound
        // all subsequent reads too, and cancel them when the browser disconnects.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var streamToken = deadline.Token;
        using var message = CreateRequest(HttpMethod.Post, "/analyze/stream", request, authorization);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await SendAsync(message, streamToken, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, streamToken, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InvalidDataException("Pipeline returned an invalid stream content type.");
        await using var stream = await response.Content.ReadAsStreamAsync(streamToken);
        using var reader = new StreamReader(stream);

        var eventName = "message";
        var data = new StringBuilder();
        while (true)
        {
            var line = await ReadLineAsync(reader, streamToken, cancellationToken);
            if (line is null)
                break;
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    yield return new PipelineStreamEvent(eventName, document.RootElement.Clone());
                    if (eventName is "result" or "error")
                    {
                        if (eventName == "result") timing.Success();
                        yield break;
                    }
                }
                eventName = "message";
                data.Clear();
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
                if (data.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("Pipeline stream event is too large.");
            }
        }
        throw new InvalidDataException("Pipeline stream ended without a result.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token,
        CancellationToken caller, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try { return await HttpResponseDeadline.AwaitAsync(_http.SendAsync(message, completion, token), token); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
            throw new DependencyUnavailableException("pipeline");
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken token, CancellationToken caller)
    {
        try { return await HttpResponseDeadline.AwaitBodyAsync(response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token), token); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
            throw new DependencyUnavailableException("pipeline");
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token, CancellationToken caller)
    {
        try { return await HttpResponseDeadline.AwaitBodyAsync(reader.ReadLineAsync(token).AsTask(), token); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new DependencyUnavailableException("pipeline"); }
        catch (IOException) { throw new DependencyUnavailableException("pipeline"); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token, CancellationToken caller)
    {
        if ((int)response.StatusCode >= 500 || response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode is >= 300 and < 400)
        {
            var service = "pipeline";
            // Only a fixed dependency label crosses the boundary, never an upstream error message/body.
            if (response.Content.Headers.ContentLength is <= 16384)
            {
                try
                {
                    var body = await ReadJsonAsync(response, token, caller);
                    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("dependency", out var dependency)
                        && dependency.ValueKind == JsonValueKind.String && dependency.GetString() == "colab") service = "colab";
                }
                catch (Exception exception) when (exception is JsonException or DependencyUnavailableException) { }
            }
            var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 30;
            throw new DependencyUnavailableException(service, (int)Math.Clamp(Math.Ceiling(seconds), 1, 300));
        }
        response.EnsureSuccessStatusCode();
    }
}
