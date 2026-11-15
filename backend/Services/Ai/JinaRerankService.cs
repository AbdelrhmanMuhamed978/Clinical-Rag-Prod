using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClinicalRag.Api.Services.Ai;

public sealed class JinaRerankService : IRerankService
{
    private const string DefaultModel = "jina-reranker-v2-base-multilingual";
    private const int MaxDocumentLength = 2000;
    private const int DefaultTopN = 30;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JinaRerankService> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    public JinaRerankService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<JinaRerankService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _apiKey = configuration["Ai:Jina:ApiKey"]
            ?? throw new InvalidOperationException("Missing configuration: Ai:Jina:ApiKey");

        _model = configuration["Ai:Jina:RerankModel"] ?? DefaultModel;
    }

    public async Task<List<RerankResult>> RerankAsync(
        string query,
        List<string> documents,
        int? topN = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
            return [];

        var truncatedDocuments = documents
            .Select(d => d.Length > MaxDocumentLength ? d[..MaxDocumentLength] : d)
            .ToList();

        var effectiveTopN = topN ?? Math.Min(DefaultTopN, documents.Count);

        var requestBody = new JinaRerankRequest
        {
            Model = _model,
            Query = query,
            Documents = truncatedDocuments,
            TopN = effectiveTopN
        };

        _logger.LogDebug(
            "Reranking {DocumentCount} documents with model {Model}, topN={TopN}, inputHash={InputHash}, inputLength={InputLength}",
            documents.Count, _model, effectiveTopN, OperationTelemetry.InputHash(query), query.Length);

        using var client = _httpClientFactory.CreateClient("Jina");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.jina.ai/v1/rerank");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Network error calling Jina Rerank API {ExceptionType}", ex.GetType().Name);
            throw new RerankException("Failed to connect to Jina Rerank API.");
        }

        using var responseLease = response;
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Jina Rerank API returned {StatusCode}", (int)response.StatusCode);

            throw new RerankException($"Jina Rerank API returned HTTP {(int)response.StatusCode}.");
        }

        JinaRerankResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<JinaRerankResponse>(JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to deserialize Jina Rerank API response {ExceptionType}", ex.GetType().Name);
            throw new RerankException("Invalid response from Jina Rerank API.");
        }

        if (result?.Results is null)
        {
            _logger.LogError("Jina Rerank API returned null results");
            throw new RerankException("Jina Rerank API returned an empty response.");
        }

        _logger.LogDebug("Reranking complete — received {Count} results", result.Results.Count);

        return result.Results
            .Select(r => new RerankResult(
                r.Index,
                r.RelevanceScore,
                r.Document?.Text ?? documents[r.Index]))
            .ToList();
    }

    // ── JSON serialization options ──────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── DTOs ────────────────────────────────────────────────────────────

    private sealed class JinaRerankRequest
    {
        public string Model { get; init; } = default!;
        public string Query { get; init; } = default!;
        public List<string> Documents { get; init; } = default!;
        public int TopN { get; init; }
    }

    private sealed class JinaRerankResponse
    {
        public List<JinaRerankResultItem> Results { get; init; } = default!;
    }

    private sealed class JinaRerankResultItem
    {
        public int Index { get; init; }
        public double RelevanceScore { get; init; }
        public JinaDocumentResult? Document { get; init; }
    }

    private sealed class JinaDocumentResult
    {
        public string Text { get; init; } = default!;
    }
}

// ── Custom exception ────────────────────────────────────────────────────

public class RerankException : Exception
{
    public RerankException(string message) : base(message) { }
    public RerankException(string message, Exception innerException) : base(message, innerException) { }
}
