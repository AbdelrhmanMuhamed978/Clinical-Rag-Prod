using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClinicalRag.Api.Services;

public enum QueryIntent
{
    General,
    Broad,
    Definition,
    Narrow
}

/// <summary>
/// Deterministic query expansion and intent alignment for hybrid retrieval.
/// Synonyms come from the shared python-backend/query-synonyms.json asset so the
/// .NET retrieval layer and the Python pipeline can evolve together.
/// </summary>
public static class QueryExpansion
{
    private const string ResourceName = "ClinicalRag.Retrieval.query-synonyms";
    private static readonly Regex Definitional = new(
        @"^\s*(?:what\s+(?:is|are)|define|definition|meaning|overview|summary\s+of)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BroadPopulation = new(
        @"\b(?:adults?|patients?|people|persons?|all|initial|first[- ]line|starting|management|treatment)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NarrowScope = new(
        @"\b(?:pregnan\w*|tubercul\w*|\btb\b|children|infants?|neonat\w*|renal\s+failure|dialysis|contraindicat\w*|only\s+if|except)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SummarySection = new(
        @"\b(?:summary\s+of\s+recommendations?|key\s+recommendations?|recommendations?\s+summary|overview)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DefinitionSection = new(
        @"\b(?:definitions?|defined\s+as|terminology)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly IReadOnlyList<(Regex Pattern, string[] Synonyms)> Synonyms = LoadSynonyms();

    private static IReadOnlyList<(Regex Pattern, string[] Synonyms)> LoadSynonyms()
    {
        var fallback = new (string Phrase, string[] Synonyms)[]
        {
            ("viral load", ["viral suppression", "undetectable viral load", "copies/ml"]),
            ("kidney", ["renal", "acute kidney injury", "creatinine"]),
            ("blood pressure", ["hypotension", "mean arterial pressure", "map"]),
            ("platelet", ["thrombocytopenia", "platelet count"]),
            ("crag", ["cryptococcal antigen", "cryptococcal screening"]),
            ("tb", ["tuberculosis"]),
            ("art", ["antiretroviral therapy"]),
            ("vte", ["venous thromboembolism", "pulmonary embolism"])
        };
        try
        {
            using var stream = typeof(QueryExpansion).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return Build(fallback);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("synonyms", out var map)
                || map.ValueKind != JsonValueKind.Object) return Build(fallback);
            var loaded = new List<(string, string[])>();
            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                var values = property.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item) && item!.Length <= 200)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToArray();
                if (property.Name.Trim().Length is > 0 and <= 120 && values.Length > 0)
                    loaded.Add((property.Name.Trim(), values));
            }
            return loaded.Count > 0 ? Build(loaded) : Build(fallback);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return Build(fallback);
        }
    }

    private static IReadOnlyList<(Regex Pattern, string[] Synonyms)> Build(
        IEnumerable<(string Phrase, string[] Synonyms)> values) =>
        values.OrderByDescending(value => value.Phrase.Length)
            .Select(value => (new Regex(
                $@"(?<![\w-]){Regex.Escape(value.Phrase)}(?![\w-])",
                RegexOptions.IgnoreCase | RegexOptions.Compiled), value.Synonyms))
            .ToArray();

    public static IReadOnlyList<string> Expand(string query)
    {
        var original = Regex.Replace(query ?? "", @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(original)) return [];
        if (original.Length > 4000) original = original[..4000];
        var variants = new List<string> { original };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { original };
        foreach (var (pattern, synonyms) in Synonyms)
        {
            if (!pattern.IsMatch(original)) continue;
            foreach (var synonym in synonyms)
            {
                var expanded = pattern.Replace(original, synonym);
                expanded = Regex.Replace(expanded, @"\s+", " ").Trim();
                if (expanded.Length is 0 or > 4000 || !seen.Add(expanded)) continue;
                variants.Add(expanded);
                if (variants.Count >= 4) return variants;
            }
        }
        return variants;
    }

    public static QueryIntent IntentOf(string query)
    {
        var value = query ?? "";
        if (NarrowScope.IsMatch(value)) return QueryIntent.Narrow;
        if (Definitional.IsMatch(value)) return QueryIntent.Definition;
        if (BroadPopulation.IsMatch(value)) return QueryIntent.Broad;
        return QueryIntent.General;
    }

    public static double SectionBoost(string kind, string? section, QueryIntent intent)
    {
        var summary = SummarySection.IsMatch(section ?? "");
        var definition = DefinitionSection.IsMatch(section ?? "");
        return intent switch
        {
            QueryIntent.Definition when kind == "definition" => 1.30,
            QueryIntent.Definition when kind == "summary" => 1.15,
            QueryIntent.Definition when definition => 1.20,
            QueryIntent.Broad when kind == "summary" => 1.20,
            QueryIntent.Broad when kind == "definition" => 1.10,
            QueryIntent.Broad when summary => 1.15,
            _ => 1.0
        };
    }

    public static double NarrowPenalty(string? metadata, string? text, QueryIntent intent)
    {
        if (intent != QueryIntent.Broad) return 0;
        var population = ChunkMetadata.Population(metadata, text);
        if (string.IsNullOrWhiteSpace(population)) return 0;
        return NarrowScope.IsMatch(population) ? 0.10 : 0;
    }

    public static bool IsBroad(string query) => IntentOf(query) == QueryIntent.Broad;
    public static bool IsDefinitional(string query) => IntentOf(query) == QueryIntent.Definition;
}
