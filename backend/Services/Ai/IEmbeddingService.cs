namespace ClinicalRag.Api.Services.Ai;

/// <summary>
/// Abstraction for text embedding generation.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Embed a batch of texts and return one float array per input text.
    /// </summary>
    Task<List<float[]>> EmbedAsync(List<string> texts);

    /// <summary>
    /// Convenience wrapper that embeds a single text.
    /// </summary>
    Task<float[]> EmbedSingleAsync(string text);

    /// <summary>
    /// The dimensionality of the vectors returned by this service.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// The underlying model identifier (e.g. "gemini-embedding-2").
    /// </summary>
    string ModelName { get; }
}
