using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClinicalRag.Api.Services;

public sealed class ConfigValidationException : ArgumentException
{
    public IReadOnlyList<string> Errors { get; }
    public ConfigValidationException(IReadOnlyList<string> errors) : base("Invalid configuration.") => Errors = errors;
}

/// <summary>Embedded shared assets also work in published deployments, outside the source tree.</summary>
public static class ConfigSchema
{
    public static JsonElement Schema { get; } = Read("schema.json");
    public static JsonElement PromptRules => Schema.GetProperty("properties").GetProperty("prompts").GetProperty("properties");

    public static JsonObject Defaults()
    {
        var defaults = DefaultsFor(Schema)!.AsObject();
        defaults["prompts"] = JsonNode.Parse(Read("default_prompts.json").GetRawText());
        return defaults;
    }

    private static JsonElement Read(string name)
    {
        using var stream = typeof(ConfigSchema).Assembly.GetManifestResourceStream($"ClinicalRag.Config.{name}")
            ?? throw new InvalidOperationException("Shared configuration resources are unavailable.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonNode? DefaultsFor(JsonElement schema)
    {
        if (schema.TryGetProperty("default", out var value)) return JsonNode.Parse(value.GetRawText());
        if (schema.GetProperty("type").GetString() != "object") return null;
        var result = new JsonObject();
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            var child = DefaultsFor(property.Value);
            if (child != null) result[property.Name] = child;
        }
        return result;
    }
}

/// <summary>
/// The Prompt 5 literal-placeholder validator, driven by the same schema contract as config.py.
/// Unknown/JSON braces are intentional literals, not format-string expressions.
/// </summary>
public static class PromptValidator
{
    public static string? Validate(string name, string text)
    {
        if (!ConfigSchema.PromptRules.TryGetProperty(name, out var rule)) return $"unknown prompt name: {name}";
        var missing = rule.GetProperty("x-requiredPlaceholders").EnumerateArray()
            .Select(item => item.GetString()!)
            .Where(key => !text.Contains("{" + key + "}", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        return missing.Count == 0 ? null
            : $"prompt '{name}' missing required placeholder(s): {string.Join(", ", missing.Select(key => "{" + key + "}"))}";
    }
}

public static class ConfigValidator
{
    public const int MaxJsonLength = 262144;

    public static string Normalize(string configJson, string? publishedJson = null)
    {
        var edits = ParseObject(configJson);
        var complete = ConfigSchema.Defaults();
        if (publishedJson != null) complete = Merge(complete, ParseObject(publishedJson));
        complete = Merge(complete, edits);
        if (complete["safety"] is JsonObject safety) safety["verifierLocked"] = true;

        var errors = new List<string>();
        Validate(JsonSerializer.SerializeToElement(complete), ConfigSchema.Schema, "configuration", errors);
        if (complete["prompts"] is JsonObject prompts)
        {
            foreach (var rule in ConfigSchema.PromptRules.EnumerateObject())
            {
                if (prompts[rule.Name] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                var error = PromptValidator.Validate(rule.Name, text);
                if (error != null) errors.Add(error);
            }
        }
        if (errors.Count > 0) throw new ConfigValidationException(errors);

        NormalizeIntegers(complete, ConfigSchema.Schema);
        var normalized = complete.ToJsonString();
        if (normalized.Length > MaxJsonLength)
            throw new ConfigValidationException(new[] { "configuration is too large" });
        return normalized;
    }

    public static string Fingerprint(string configJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson))).ToLowerInvariant();

    private static JsonObject ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
            throw new ConfigValidationException(new[] { "configuration must be a JSON object of at most 262144 characters" });
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ConfigValidationException(new[] { "configuration must be a JSON object" });
            CheckDuplicateKeys(document.RootElement);
            return JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        }
        catch (JsonException)
        {
            // Do not echo prompt/config content or parser exception messages to clients or logs.
            throw new ConfigValidationException(new[] { "configuration must contain valid JSON" });
        }
    }

    private static void CheckDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ConfigValidationException(new[] { "configuration must not contain duplicate keys" });
                CheckDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckDuplicateKeys(child);
    }

    private static JsonObject Merge(JsonObject original, JsonObject edits)
    {
        var merged = original.DeepClone().AsObject();
        foreach (var (key, value) in edits)
            merged[key] = value is JsonObject editObject && merged[key] is JsonObject baseObject
                ? Merge(baseObject, editObject) : value?.DeepClone();
        return merged;
    }

    /// <summary>
    /// Evaluate exactly the JSON Schema keywords used by the shared schema, without a new runtime dependency.
    /// Additional properties are deliberately retained. This is not a general-purpose schema engine.
    /// </summary>
    private static void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        var type = schema.GetProperty("type").GetString();
        var numeric = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
        var matches = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => numeric,
            "integer" => numeric && Math.Truncate(value.GetDouble()) == value.GetDouble(),
            _ => throw new InvalidOperationException("Unsupported shared configuration schema type.")
        };
        if (!matches) { errors.Add($"{path} must be {type}"); return; }

        if (type == "object")
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var key in required.EnumerateArray())
                    if (!value.TryGetProperty(key.GetString()!, out _)) errors.Add($"{ChildPath(path, key.GetString()!)} is required");
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
                if (value.TryGetProperty(property.Name, out var child)) Validate(child, property.Value, ChildPath(path, property.Name), errors);
        }
        if (type is "number" or "integer")
        {
            var minimum = schema.GetProperty("minimum").GetDouble();
            var maximum = schema.GetProperty("maximum").GetDouble();
            if (value.GetDouble() < minimum || value.GetDouble() > maximum)
                errors.Add($"{path} must be between {minimum} and {maximum}");
        }
        if (type == "string")
        {
            var length = value.GetString()!.EnumerateRunes().Count();
            if (length < schema.GetProperty("minLength").GetInt32() || length > schema.GetProperty("maxLength").GetInt32())
                errors.Add($"{path} must contain 1–20000 characters");
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
            errors.Add($"{path} is locked");
    }

    private static string ChildPath(string path, string key) => path == "configuration" ? key : $"{path}.{key}";

    private static void NormalizeIntegers(JsonObject value, JsonElement schema)
    {
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            if (value[property.Name] is JsonObject child) NormalizeIntegers(child, property.Value);
            else if (property.Value.GetProperty("type").GetString() == "integer")
                value[property.Name] = (int)JsonSerializer.SerializeToElement(value[property.Name]).GetDouble();
        }
    }
}
