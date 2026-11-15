using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Models;

namespace ClinicalRag.Api.Services;

public sealed class RuleValidationException : ArgumentException
{
    public IReadOnlyList<string> Errors { get; }
    public RuleValidationException(IEnumerable<string> errors) : base("Invalid rule definition.") => Errors = errors.ToArray();
}

/// <summary>One vocabulary is shared with Python; seeds are embedded for published deployments.</summary>
public static class RuleAssets
{
    public static JsonElement Vocabulary { get; } = Read("vocabulary.json");
    public static JsonElement Text { get; } = Read("text.json");
    public static JsonElement DefaultRules { get; } = Read("rules.json");
    public static JsonElement DefaultDrugs { get; } = Read("drugs.json");
    public static IReadOnlySet<string> Fields { get; } = Vocabulary.GetProperty("fields").EnumerateObject().Select(p => p.Name).ToHashSet();
    public static IReadOnlySet<string> Classes { get; } = Vocabulary.GetProperty("drug_classes").EnumerateObject().Select(p => p.Name).ToHashSet();
    public static IReadOnlySet<string> Operators { get; } = Vocabulary.GetProperty("operators").EnumerateArray().Select(p => p.GetString()!).ToHashSet();

    private static JsonElement Read(string name)
    {
        using var stream = typeof(RuleAssets).Assembly.GetManifestResourceStream($"ClinicalRag.Rules.{name}")
            ?? throw new InvalidOperationException("Shared rule assets are missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}

public static class RuleValidator
{
    public const int MaxJsonLength = 200000;

    public static void Validate(Rule rule)
    {
        try { ValidateCore(rule); }
        catch (InvalidOperationException) { throw new RuleValidationException(["Rule JSON contains invalid Unicode text or values."]); }
    }

    private static void ValidateCore(Rule rule)
    {
        var errors = new List<string>();
        if (!Regex.IsMatch(rule.RuleId, @"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant))
            errors.Add("Rule ID must use 1–64 letters, digits, underscores or hyphens.");
        if (!ClinicalText.HasText(rule.Title) || rule.Title.Length > 256) errors.Add("A title of at most 256 characters is required.");
        if (rule.Level?.Length > 64) errors.Add("Level must be at most 64 characters.");
        if (rule.Message?.Length > 10000 || rule.Caveat?.Length > 10000) errors.Add("Message and caveat must be at most 10000 characters each.");

        var conditions = Parse(rule.AppliesWhen, "Conditions", errors);
        if (conditions is { } group)
        {
            var properties = group.ValueKind == JsonValueKind.Object ? group.EnumerateObject().ToArray() : [];
            if (properties.Length != 1 || properties[0].Name is not ("any" or "all")
                || properties[0].Value.ValueKind != JsonValueKind.Array || properties[0].Value.GetArrayLength() is < 1 or > 64)
                errors.Add("Conditions require exactly one 'any' or 'all' array with 1–64 rows.");
            else foreach (var row in properties[0].Value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 3
                    || !row.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String || !RuleAssets.Fields.Contains(field.GetString()!)
                    || !row.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || !RuleAssets.Operators.Contains(op.GetString()!)
                    || !row.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                    errors.Add("Each condition needs a known field, < / <= / > / >=, and a finite numeric value, without extra keys.");
            }
        }

        var classes = Parse(rule.DrugClasses, "Drug classes", errors);
        if (classes is { } drugClasses && (drugClasses.ValueKind != JsonValueKind.Array
            || drugClasses.GetArrayLength() > RuleAssets.Classes.Count || (!rule.CaseLevel && drugClasses.GetArrayLength() == 0)
            || drugClasses.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String || !RuleAssets.Classes.Contains(c.GetString()!))
            || drugClasses.EnumerateArray().Select(c => c.GetRawText()).Distinct().Count() != drugClasses.GetArrayLength()))
            errors.Add("Choose distinct known drug classes; medication rules require at least one class.");

        var sources = Parse(rule.Sources, "Sources", errors);
        if (sources is { } citations)
        {
            if (citations.ValueKind != JsonValueKind.Array || citations.GetArrayLength() is < 1 or > 32)
                errors.Add("Provide at least one source with title and URL (up to 32).");
            else foreach (var source in citations.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object || !Text(source, "title", 512, required: true)
                    || !Text(source, "url", 2048, required: true) || !Text(source, "quote", 10000)
                    || !ClinicalText.HttpSourceUrl(source.GetProperty("url").GetString()!))
                    errors.Add("Each source requires a title and HTTP(S) URL without embedded credentials; quotes must be text of at most 10000 characters.");
            }
        }
        try { ValidateLinkTerms(rule.LinkTerms); }
        catch (RuleValidationException error) { errors.AddRange(error.Errors); }
        if (errors.Count != 0) throw new RuleValidationException(errors.Distinct());
    }

    public static void ValidateLinkTerms(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return;
        if (ClinicalText.PortableRegexProblem(pattern) is { } problem) throw new RuleValidationException([problem]);
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { throw new RuleValidationException(["Link terms must compile as a portable regular expression."]); }
    }

    public static string ValidateCatalog(JsonElement catalog)
    {
        try { return ValidateCatalogCore(catalog); }
        catch (InvalidOperationException) { throw new RuleValidationException(["Drug catalog contains invalid Unicode text or JSON values."]); }
    }

    private static string ValidateCatalogCore(JsonElement catalog)
    {
        var errors = new List<string>();
        if (catalog.ValueKind != JsonValueKind.Object || catalog.GetRawText().Length > MaxJsonLength || catalog.EnumerateObject().Count() > 512)
            throw new RuleValidationException(["Drug catalog must be an object with at most 512 generics and 200000 characters."]);
        CheckDuplicateKeys(catalog, errors);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var drug in catalog.EnumerateObject())
        {
            if (!Name(drug.Name)) { errors.Add("Each generic needs a nonempty name of at most 128 UTF-16 units, without control, format, private-use or unassigned characters."); continue; }
            var entry = drug.Value;
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3
                || !entry.TryGetProperty("classes", out var classes) || !entry.TryGetProperty("brands", out var brands)
                || !entry.TryGetProperty("regional_names", out var regional))
            { errors.Add("Each drug requires classes, brands and regional_names arrays, without unknown fields."); continue; }
            if (classes.ValueKind != JsonValueKind.Array || classes.GetArrayLength() is < 1 || classes.GetArrayLength() > RuleAssets.Classes.Count
                || classes.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String || !RuleAssets.Classes.Contains(c.GetString()!))
                || classes.EnumerateArray().Select(c => c.GetRawText()).Distinct().Count() != classes.GetArrayLength())
                errors.Add("Each drug must have at least one distinct known class.");
            AddAlias(drug.Name, drug.Name);
            foreach (var names in new[] { brands, regional })
            {
                if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() > 64
                    || names.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.String || !Name(n.GetString()!)))
                { errors.Add("Brands and regional names must contain up to 64 nonempty names of at most 128 characters, without control characters."); continue; }
                foreach (var name in names.EnumerateArray()) AddAlias(name.GetString()!, drug.Name);
            }
        }
        if (errors.Count != 0) throw new RuleValidationException(errors.Distinct());
        return catalog.GetRawText();

        void AddAlias(string name, string generic)
        {
            var key = ClinicalText.Alias(name);
            if (aliases.TryGetValue(key, out var owner) && owner != generic)
                errors.Add("A normalized generic, brand or regional name cannot identify two different drugs.");
            aliases[key] = generic;
        }
    }

    public static PipelineRule ToPipeline(Rule rule) => new(rule.RuleId, rule.Title,
        JsonSerializer.Deserialize<JsonElement>(rule.DrugClasses!), JsonSerializer.Deserialize<JsonElement>(rule.AppliesWhen!),
        rule.Level, rule.Message, rule.Caveat, JsonSerializer.Deserialize<JsonElement>(rule.Sources!), rule.LinkTerms,
        rule.CaseLevel, rule.Status, rule.Enabled, rule.Revision);

    public static Rule FromSeed(JsonElement seed) => new()
    {
        RuleId = seed.GetProperty("id").GetString()!, Title = seed.GetProperty("title").GetString()!,
        DrugClasses = seed.GetProperty("drug_classes").GetRawText(), AppliesWhen = seed.GetProperty("applies_when").GetRawText(),
        Level = Optional(seed, "level"), Message = Optional(seed, "message"), Caveat = Optional(seed, "caveat"),
        Sources = seed.GetProperty("sources").GetRawText(), LinkTerms = Optional(seed, "link_terms"),
        CaseLevel = seed.TryGetProperty("case_level", out var caseLevel) && caseLevel.GetBoolean(), Status = "draft", Enabled = false
    };

    private static string? Optional(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool Name(string value) => ClinicalText.ValidName(value);
    private static bool Text(JsonElement value, string name, int max, bool required = false) =>
        value.TryGetProperty(name, out var property) ? property.ValueKind == JsonValueKind.String && property.GetString()!.Length <= max
            && (!required || ClinicalText.HasText(property.GetString()!)) || !required && property.ValueKind == JsonValueKind.Null : !required;

    private static JsonElement? Parse(string? json, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength) { errors.Add($"{label} must be JSON of at most {MaxJsonLength} characters."); return null; }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            CheckDuplicateKeys(document.RootElement, errors);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { errors.Add($"{label} must be valid JSON and Unicode text."); return null; }
    }

    private static void CheckDuplicateKeys(JsonElement value, List<string> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) errors.Add("JSON must not contain duplicate keys.");
                CheckDuplicateKeys(property.Value, errors);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicateKeys(item, errors);
    }
}
