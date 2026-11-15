using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalRag.Api.Services;

public interface IColabClient
{
    Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default);
    Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default);
    Task<bool> HealthAsync();
    Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default);
    Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default);
    Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default);
    Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default);
    Task<JsonElement?> RunChunkJobAsync(JsonElement body, CancellationToken cancellationToken = default);
}

public sealed record ColabGpuInfo(bool Available, string? Name, string Device, long? TotalMemoryBytes);
public sealed record ColabReadiness(bool Ready, string? Model = null, string? EmbeddingModel = null,
    int? EmbeddingDimensions = null, ColabGpuInfo? Gpu = null, string? Message = null);

public sealed class ColabClient : IColabClient
{
    private readonly IConfiguration _cfg;
    private readonly HttpClient _http;
    private readonly IColabConnectionProvider _connections;
    private readonly IHttpContextAccessor? _context;
    private readonly ILogger<ColabClient> _log;

    public ColabClient(HttpClient http, IColabConnectionProvider connections, IConfiguration cfg,
        IHttpContextAccessor? context = null, ILogger<ColabClient>? log = null)
    {
        _cfg = cfg; _http = http; _connections = connections; _context = context; _log = log ?? NullLogger<ColabClient>.Instance;
        _http.Timeout = Timeout.InfiniteTimeSpan; // Each call has a body-covered deadline in the resilience handler.
    }

    public Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default) =>
        PostAsync("retrieve", new { prompt = query }, "colab.retrieve", query, 30, true, cancellationToken);

    public Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default) =>
        PostAsync("generate", new { prompt, max_new_tokens = maxNewTokens, temperature = 0.0, repetition_penalty = 1.0 },
            "colab.generate", prompt, TimeoutSeconds, false, cancellationToken);

    public async Task<bool> HealthAsync() => (await ReadyAsync()).Ready;

    public async Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var connection = await _connections.GetAsync(deadline.Token);
            var body = await SendAsync(connection, HttpMethod.Get, "ready", null, "colab.ready", 3, true, deadline.Token, retries: 0);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("ready", out var ready)
                || ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException();
            var model = SafeName(body, "model");
            var embeddingModel = SafeName(body, "embeddingModel");
            int? dimensions = body.TryGetProperty("embeddingDimensions", out var dimension) && dimension.TryGetInt32(out var size)
                && size >= 1 && size <= RetrievalLimits.MaxEmbeddingDimensions ? size : null;
            ColabGpuInfo? gpu = null;
            if (body.TryGetProperty("gpu", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                var device = SafeName(details, "device");
                long? memory = details.TryGetProperty("totalMemoryBytes", out var bytes) && bytes.TryGetInt64(out var count)
                    && count is > 0 and <= 1099511627776 ? count : null;
                gpu = new(details.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True,
                    SafeName(details, "name"), device is "cuda" or "cpu" ? device : "unknown", memory);
            }
            return new(ready.GetBoolean() && model != null, model, embeddingModel, dimensions, gpu,
                ready.GetBoolean() && model != null ? null : new DependencyUnavailableException("colab").PublicMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        { return new(false, Message: new DependencyUnavailableException("colab").PublicMessage); }
    }

    public Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default)
    {
        path = path.Trim('/');
        var step = path switch
        {
            "chunk/profile" => "colab.chunk.profile", "chunk/preview" => "colab.chunk.preview", "chunk/run" => "colab.chunk.run",
            _ => throw new ArgumentException("Unsupported Colab operation.", nameof(path))
        };
        return PostAsync(path, body, step, Text(body), TimeoutSeconds, false, cancellationToken);
    }

    public Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count is < 1 || texts.Count > RetrievalLimits.MaxEmbeddingBatchSize || texts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Embedding batch size exceeds the server limit or contains empty text.", nameof(texts));
        // EmbeddingBatchClient owns retries and durable checkpoints. Do not stack transport retries on it.
        return PostAsync("embed", new { texts }, "colab.embed", string.Join('\n', texts),
            Math.Clamp(_cfg.GetValue("Embedding:TimeoutSeconds", 120), 1, 300), true, cancellationToken, retries: 0);
    }

    public Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default)
    {
        if (passages.Count is < 1 || passages.Count > RetrievalLimits.MaxRerankPassages || passages.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Rerank batch size exceeds the server limit or contains empty text.", nameof(passages));
        passages = passages.Select(passage => RetrievalLimits.RerankPassage(passage)).ToList();
        return PostAsync("rerank", new { query, passages }, "colab.rerank", query + "\n" + string.Join('\n', passages),
            30, true, cancellationToken);
    }

    public async Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default)
    {
        var connection = await _connections.GetAsync(cancellationToken);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", fileName);
        _log.LogInformation("Step input {Step} {InputHash} {InputLength}", "colab.parse_pdf",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);
        return await SendAsync(connection, HttpMethod.Post, "parse-pdf", content, "colab.parse_pdf", TimeoutSeconds, true, cancellationToken);
    }

    public async Task<JsonElement?> RunChunkJobAsync(JsonElement body, CancellationToken cancellationToken = default)
    {
        // Pin submission and every poll to one runtime. Rotation must not poll a different runtime's job IDs.
        var connection = await _connections.GetAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var token = deadline.Token;
        var input = Text(body);
        _log.LogInformation("Step input {Step} {InputHash} {InputLength}", "colab.chunk.submit", OperationTelemetry.InputHash(input), input.Length);
        try
        {
            var submitted = await SendAsync(connection, HttpMethod.Post, "chunk/jobs", JsonContent.Create(body),
                "colab.chunk.submit", 10, false, token);
            if (!submitted.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: >= 1 and <= 64 } jobId || jobId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                return null;
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(_cfg.GetValue("Colab:ChunkJobPollMilliseconds", 500), 100, 10000));
            while (true)
            {
                await Task.Delay(interval, token);
                var status = await SendAsync(connection, HttpMethod.Get, $"chunk/jobs/{jobId}", null, "colab.chunk.poll", 5, true, token);
                if (!status.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String) return null;
                if (state.GetString() == "completed") return status.TryGetProperty("result", out var result) ? result.Clone() : null;
                if (state.GetString() is not ("running" or "queued")) return null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new DependencyUnavailableException("colab", retryable: true); }
        catch (HttpRequestException exception) when (exception is not DependencyUnavailableException && exception.StatusCode is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError)
        { return null; }
    }

    private int TimeoutSeconds => Math.Clamp(_cfg.GetValue("Colab:TimeoutSeconds", 300), 30, 3600);
    private static string Text(JsonElement body) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("text", out var text)
        && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
    private static string? SafeName(JsonElement body, string property) => body.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: >= 1 and <= 256 } name
        && !name.Any(char.IsControl) ? name : null;

    private async Task<JsonElement> PostAsync<T>(string path, T payload, string step, string input, int seconds, bool idempotent,
        CancellationToken token, int? retries = null)
    {
        token.ThrowIfCancellationRequested();
        _log.LogInformation("Step input {Step} {InputHash} {InputLength}", step, OperationTelemetry.InputHash(input), input.Length);
        var connection = await _connections.GetAsync(token);
        return await SendAsync(connection, HttpMethod.Post, path, JsonContent.Create(payload), step, seconds, idempotent, token, retries);
    }

    private async Task<JsonElement> SendAsync(ColabConnection connection, HttpMethod method, string path, HttpContent? content,
        string step, int seconds, bool idempotent, CancellationToken token, int? retries = null)
    {
        using var message = new HttpRequestMessage(method, new Uri(connection.BaseUri, path)) { Content = content };
        message.Headers.TryAddWithoutValidation("X-Colab-Key", connection.ApiKey);
        message.Headers.TryAddWithoutValidation(RequestCorrelation.Header, RequestCorrelation.Current(_context));
        message.Options.Set(ColabResilienceHandler.Options, new(connection.CircuitKey, step, TimeSpan.FromSeconds(seconds),
            idempotent, retries ?? Math.Clamp(_cfg.GetValue("Colab:MaxRetries", 2), 0, 5)));
        try
        {
            using var response = await HttpResponseDeadline.AwaitAsync(_http.SendAsync(message, token), token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden || (int)response.StatusCode is >= 300 and < 400)
                throw new DependencyUnavailableException("colab");
            if (response.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new DependencyUnavailableException("colab", retryable: true);
            response.EnsureSuccessStatusCode();
            return await HttpResponseDeadline.AwaitBodyAsync(response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token), token);
        }
        catch (HttpRequestException exception) when (exception is not DependencyUnavailableException && exception.StatusCode == null)
        { throw new DependencyUnavailableException("colab", retryable: true); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new DependencyUnavailableException("colab", retryable: true); }
    }
}
