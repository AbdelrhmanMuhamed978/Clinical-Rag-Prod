using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClinicalRag.Api.Services;

/// <summary>
/// Normalizes evidence text and recommendation metadata at the ingestion
/// boundary. This is the .NET counterpart of evidence_text.py; both must keep
/// the same visible words so quote verification sees what the reader sees.
/// </summary>
public static class ChunkMetadata
{
    private static readonly Regex SummaryKind = new(
        @"\b(?:summary\s+of\s+recommendations?|key\s+recommendations?|recommendations?\s+summary|overview)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DefinitionKind = new(
        @"\b(?:definitions?|defined\s+as|is\s+defined\s+as|refers?\s+to|means?|in\s+this\s+guideline)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NarrowPopulation = new(
        @"\b(?:pregnan\w*|tubercul\w*|\btb\b|children|infants?|neonat\w*|renal\s+failure|dialysis|contraindicat\w*|only\s+if|except)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Kind(string? metadata, string? text, string? section = null)
    {
        var supplied = MetadataString(metadata, "kind");
        if (supplied is "recommendation" or "definition" or "table_row" or "clinical_consideration" or "summary")
            return supplied;
        var normalized = NormalizeText(text);
        if (normalized.Contains("[TABLES]", StringComparison.OrdinalIgnoreCase)
            || normalized.TrimStart().StartsWith('|')) return "table_row";
        var sectionText = NormalizeText(section);
        if (SummaryKind.IsMatch(sectionText)
            || SummaryKind.IsMatch(normalized[..Math.Min(300, normalized.Length)])) return "summary";
        if (Strength(normalized) != null) return "recommendation";
        if (DefinitionKind.IsMatch(normalized)) return "definition";
        return "clinical_consideration";
    }

    public static string? Population(string? metadata, string? text)
    {
        var supplied = MetadataString(metadata, "population")?.Trim();
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
        var match = NarrowPopulation.Match(NormalizeText(text));
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    public static string? RecommendationId(string? metadata)
        => MetadataString(metadata, "recommendation_id")?.Trim();

    private static string? MetadataString(string? metadata, string name)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string NormalizeText(string? value)
    {
        var text = value ?? "";
        for (var pass = 0; pass < 3; pass++)
        {
            var decoded = WebUtility.HtmlDecode(text);
            if (decoded == text) break;
            text = decoded;
        }
        text = text.Replace('\u00a0', ' ').Replace("\u00ad", "-");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"(?<=\w)-[ \t]*\n(?=[a-z])", "");
        text = Regex.Replace(text, @"(?<=\w)[\u2013\u2014-][ \t]*(?:\n[ \t]*)?(?=[a-z])",
            match => match.Value[0].ToString());
        text = Regex.Replace(text, @"[ \t]+", " ");
        return text.Trim();
    }

    public static string? Strength(string? text, string? provided = null)
    {
        // The LLM's chunk-level grade is never authoritative for display.
        // Derive only from an explicit grade pattern in the evidence text.
        var lower = NormalizeText(text).ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\bstrong\s+recommendation")) return "strong";
        if (Regex.IsMatch(lower, @"\bweak\s+recommendation")) return "weak";
        if (Regex.IsMatch(lower, @"\bconditional\s+recommendation")) return "conditional";
        return null;
    }

    public static string? Certainty(string? text, string? provided = null)
    {
        var lower = NormalizeText(text).ToLowerInvariant();
        if (Regex.IsMatch(lower, @"very[\s-]low[\s-]certainty\s+evidence")) return "very_low";
        if (Regex.IsMatch(lower, @"high[\s-]certainty\s+evidence")) return "high";
        if (Regex.IsMatch(lower, @"moderate[\s-]certainty\s+evidence")) return "moderate";
        if (Regex.IsMatch(lower, @"low[\s-]certainty\s+evidence")) return "low";
        return null;
    }

    public static string CleanSection(string? value)
    {
        var text = NormalizeText(value)
            .Replace("[TABLES]", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("[/TABLES]", " ", StringComparison.OrdinalIgnoreCase);
        var parts = text.Split(['|', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.Equals("TABLES", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return parts.Length == 0 ? "" : string.Join(" > ", parts);
    }
}
