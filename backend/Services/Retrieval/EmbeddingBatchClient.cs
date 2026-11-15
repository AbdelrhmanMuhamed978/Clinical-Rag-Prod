using System.Net;

namespace ClinicalRag.Api.Services;

/// <summary>One bounded GPU batch, with cancellation and transient-only retries.</summary>
public sealed class EmbeddingBatchClient(IColabClient colab, IConfiguration configuration)
{
    public int BatchSize => Math.Clamp(configuration.GetValue("Embedding:BatchSize", RetrievalLimits.EmbeddingBatchSize), 1, RetrievalLimits.MaxEmbeddingBatchSize);

    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count < 1 || texts.Count > RetrievalLimits.MaxEmbeddingBatchSize || texts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Embedding batches must contain 1–32 nonempty texts.", nameof(texts));
        var batch = texts.ToList();
        var retries = Math.Clamp(configuration.GetValue("Embedding:MaxRetries", 2), 0, 5);
        var delay = Math.Clamp(configuration.GetValue("Embedding:RetryBaseMilliseconds", 250), 0, 5000);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Embedding:TimeoutSeconds", 120), 1, 300));
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                var result = await colab.EmbedAsync(batch, deadline.Token).WaitAsync(deadline.Token);
                return EmbeddingVectors.Parse(result, batch.Count);
            }
            catch (Exception exception) when (attempt < retries && !cancellationToken.IsCancellationRequested && Transient(exception))
            {
                // No input, credentials or upstream bodies are logged or persisted.
                OperationTelemetry.Retry();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10000, delay * (1 << attempt)) * (.5 + Random.Shared.NextDouble())), cancellationToken);
            }
        }
    }

    private static bool Transient(Exception exception) => exception is DependencyUnavailableException unavailable ? unavailable.Retryable
        : exception is OperationCanceledException or TimeoutException
        || exception is HttpRequestException http && (http.StatusCode == null
            || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)http.StatusCode.Value >= 500);
}
