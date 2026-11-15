using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClinicalRag.Api.Services.Ai;

/// <summary>
/// Generates text embeddings via the Google Gemini Embedding API
/// (batchEmbedContents endpoint).
/// </summary>
public sealed class GeminiEmbeddingService : IEmbeddingService
{
    // ── Constants ────────────────────────────────────────────────────────
    private const int MaxTextsPerBatch = 100;
    private const string DefaultModel = "gemini-embedding-2";
    private const int DefaultDimensions = 768;
    private const string HttpClientName = "Gemini";

    // ── Fields ───────────────────────────────────────────────────────────
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiEmbeddingService> _logger;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _dimensions;

    // ── JSON serialisation options (camelCase, ignore nulls) ─────────────
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Constructor ─────────────────────────────────────────────────────
    public GeminiEmbeddingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiEmbeddingService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _apiKey = configuration["Ai:Gemini:ApiKey"]
            ?? throw new InvalidOperationException(
                "Missing required configuration value 'Ai:Gemini:ApiKey'.");

        _model = configuration["Ai:Gemini:EmbeddingModel"] ?? DefaultModel;
        _dimensions = configuration.GetValue("Ai:Gemini:EmbeddingDimensions", DefaultDimensions);

        _logger.LogInformation(
            "GeminiEmbeddingService initialised — model: {Model}, dimensions: {Dims}",
            _model, _dimensions);
    }

    // ── Public properties ───────────────────────────────────────────────
    public int Dimensions => _dimensions;
    public string ModelName => _model;

    // ── Public API ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<float[]> EmbedSingleAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var results = await EmbedAsync([text]).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc />
    public async Task<List<float[]>> EmbedAsync(List<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return [];

        _logger.LogDebug("Embedding {Count} text(s) with model {Model}", texts.Count, _model);

        // If everything fits in one call, skip the batching overhead.
        if (texts.Count <= MaxTextsPerBatch)
            return await SendBatchAsync(texts).ConfigureAwait(false);

        // Otherwise, chunk into batches of MaxTextsPerBatch and merge.
        var allEmbeddings = new List<float[]>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += MaxTextsPerBatch)
        {
            var batch = texts.GetRange(offset, Math.Min(MaxTextsPerBatch, texts.Count - offset));

            _logger.LogDebug(
                "Sending batch {Offset}–{End} of {Total}",
                offset, offset + batch.Count - 1, texts.Count);

            var batchResult = await SendBatchAsync(batch).ConfigureAwait(false);
            allEmbeddings.AddRange(batchResult);
        }

        return allEmbeddings;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Sends a single batchEmbedContents request for up to 100 texts.
    /// </summary>
    private async Task<List<float[]>> SendBatchAsync(List<string> texts)
    {
        var input = string.Join('\n', texts);
        _logger.LogInformation("Embedding batch inputHash={InputHash} inputLength={InputLength}", OperationTelemetry.InputHash(input), input.Length);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:batchEmbedContents?key={_apiKey}";

        var requestBody = new BatchEmbedRequest
        {
            Requests = texts.Select(t => new EmbedRequest
            {
                Model = $"models/{_model}",
                Content = new ContentBody
                {
                    Parts = [new TextPart { Text = t }]
                },
                OutputDimensionality = _dimensions,
            }).ToList(),
        };

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .PostAsJsonAsync(url, requestBody, s_jsonOptions)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Network error calling Gemini Embedding API {ExceptionType}", ex.GetType().Name);
            throw new EmbeddingServiceException("Failed to reach the Gemini Embedding API.");
        }

        using var responseLease = response;
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini Embedding API returned {StatusCode}", (int)response.StatusCode);

            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => new EmbeddingServiceException(
                        $"Authentication failed ({(int)response.StatusCode}). Check 'Ai:Gemini:ApiKey'."),
                HttpStatusCode.TooManyRequests
                    => new EmbeddingServiceException(
                        "Rate-limited by Gemini API (429)."),
                _ => new EmbeddingServiceException(
                        $"Gemini Embedding API error {(int)response.StatusCode}."),
            };
        }

        BatchEmbedResponse? result;
        try
        {
            result = await response.Content
                .ReadFromJsonAsync<BatchEmbedResponse>(s_jsonOptions)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to deserialise Gemini embedding response {ExceptionType}", ex.GetType().Name);
            throw new EmbeddingServiceException("Invalid JSON in Gemini Embedding API response.");
        }

        if (result?.Embeddings is null || result.Embeddings.Count != texts.Count)
        {
            throw new EmbeddingServiceException(
                $"Expected {texts.Count} embedding(s) but received {result?.Embeddings?.Count ?? 0}.");
        }

        _logger.LogDebug("Received {Count} embedding(s)", result.Embeddings.Count);

        return result.Embeddings
            .Select(e => e.Values)
            .ToList();
    }

    // ── DTOs (request) ──────────────────────────────────────────────────

    private sealed class BatchEmbedRequest
    {
        public List<EmbedRequest> Requests { get; init; } = [];
    }

    private sealed class EmbedRequest
    {
        public string Model { get; init; } = string.Empty;
        public ContentBody Content { get; init; } = null!;
        public int OutputDimensionality { get; init; }
    }

    private sealed class ContentBody
    {
        public List<TextPart> Parts { get; init; } = [];
    }

    private sealed class TextPart
    {
        public string Text { get; init; } = string.Empty;
    }

    // ── DTOs (response) ─────────────────────────────────────────────────

    private sealed class BatchEmbedResponse
    {
        public List<EmbeddingValue> Embeddings { get; init; } = [];
    }

    private sealed class EmbeddingValue
    {
        public float[] Values { get; init; } = [];
    }
}

// ── Custom exception ────────────────────────────────────────────────────

/// <summary>
/// Thrown when the embedding service encounters an unrecoverable error.
/// </summary>
public sealed class EmbeddingServiceException : Exception
{
    public EmbeddingServiceException(string message) : base(message) { }
    public EmbeddingServiceException(string message, Exception inner) : base(message, inner) { }
}
