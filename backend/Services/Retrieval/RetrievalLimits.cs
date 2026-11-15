using System.Text;
using System.Text.Json;

namespace ClinicalRag.Api.Services;

/// <summary>Resource-backed GPU/search limits shared with the Colab server.</summary>
public static class RetrievalLimits
{
    private static readonly JsonElement Limits = Load();
    public static int EmbeddingBatchSize => Limits.GetProperty("embeddingBatchSize").GetInt32();
    public static int MaxEmbeddingBatchSize => Limits.GetProperty("maxEmbeddingBatchSize").GetInt32();
    public static int MaxEmbeddingDimensions => Limits.GetProperty("maxEmbeddingDimensions").GetInt32();
    public static int MaxRerankPassages => Limits.GetProperty("maxRerankPassages").GetInt32();
    public static int MaxRerankPassageCharacters => Limits.GetProperty("maxRerankPassageCharacters").GetInt32();
    public static int RrfK => Limits.GetProperty("rrfK").GetInt32();

    public static string RerankPassage(string text, string? section = null)
    {
        var passage = string.IsNullOrEmpty(section) ? text : $"{section}\n{text}";
        // Python [:2000] counts Unicode code points, not UTF-16 code units.
        var maximum = MaxRerankPassageCharacters;
        var characters = 0;
        var length = 0;
        foreach (var rune in passage.EnumerateRunes())
        {
            if (characters++ == maximum) break;
            length += rune.Utf16SequenceLength;
        }
        return passage[..length];
    }

    private static JsonElement Load()
    {
        using var stream = typeof(RetrievalLimits).Assembly.GetManifestResourceStream("ClinicalRag.Config.retrieval-limits.json")
            ?? throw new InvalidOperationException("Missing retrieval limits resource.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
