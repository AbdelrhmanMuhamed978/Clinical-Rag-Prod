using System.Text.Json;
using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class ClinicalTextTests
{
    private static JsonElement Vectors()
    {
        using var stream = typeof(ClinicalTextTests).Assembly.GetManifestResourceStream("ClinicalRag.Tests.ClinicalTextVectors.json")!;
        return JsonDocument.Parse(stream).RootElement.Clone();
    }
    public static IEnumerable<object[]> Aliases => Vectors().GetProperty("aliases").EnumerateArray().Select(v => new object[] { v.GetProperty("input").GetString()!, v.GetProperty("expected").GetString()! });
    public static IEnumerable<object[]> Urls => Vectors().GetProperty("urls").EnumerateArray().Select(v => new object[] { v.GetProperty("input").GetString()!, v.GetProperty("valid").GetBoolean() });
    public static IEnumerable<object[]> Patterns => Vectors().GetProperty("regex").EnumerateArray().Select(v => new object[] { v.GetProperty("input").GetString()!, v.GetProperty("valid").GetBoolean() });
    public static IEnumerable<object[]> CompilationPatterns => Vectors().GetProperty("regex_compilation").EnumerateArray().Select(v => new object[] { v.GetProperty("input").GetString()!, v.GetProperty("valid").GetBoolean() });
    public static IEnumerable<object[]> Names => Vectors().GetProperty("names").EnumerateArray().Select((v, index) => new object[] { index, v.GetProperty("valid").GetBoolean() });
    private static string NameInput(JsonElement v)
    {
        var input = v.TryGetProperty("input", out var text) ? text.GetString()! : string.Concat(v.GetProperty("codepoints").EnumerateArray()
            .Select(point => point.GetInt32() <= 65535 ? ((char)point.GetInt32()).ToString() : char.ConvertFromUtf32(point.GetInt32())));
        if (v.TryGetProperty("repeat", out var repeat)) input = string.Concat(Enumerable.Repeat(input, repeat.GetInt32()));
        return input;
    }

    [Theory, MemberData(nameof(Aliases))]
    public void Shared_alias_identity_and_catalog_collision(string input, string expected)
    {
        Assert.Equal(expected, ClinicalText.Alias(input));
        var catalog = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["first"] = new { classes = new[] { "nsaid" }, brands = new[] { input }, regional_names = Array.Empty<string>() },
            ["second"] = new { classes = new[] { "metformin" }, brands = new[] { expected }, regional_names = Array.Empty<string>() }
        });
        Assert.Throws<RuleValidationException>(() => RuleValidator.ValidateCatalog(catalog));
    }

    [Theory, MemberData(nameof(Names))]
    public void Shared_name_validation_uses_utf16_and_pinned_categories(int index, bool valid)
    {
        // Build invalid UTF-16 inside the test; xUnit's discovery serializer replaces lone surrogates.
        var input = NameInput(Vectors().GetProperty("names")[index]);
        Assert.Equal(valid, ClinicalText.ValidName(input));
    }

    [Theory, MemberData(nameof(Urls))]
    public void Shared_source_url_syntax_is_also_enforced_on_rules(string input, bool valid)
    {
        Assert.Equal(valid, ClinicalText.HttpSourceUrl(input));
        var rule = RuleTestDefinitions.Create().ToRule();
        rule.Sources = JsonSerializer.Serialize(new[] { new { title = "Synthetic", url = input } });
        if (valid) RuleValidator.Validate(rule);
        else Assert.Throws<RuleValidationException>(() => RuleValidator.Validate(rule));
    }

    [Theory, MemberData(nameof(Patterns))]
    public void Shared_regex_subset_also_compiles_in_dotnet(string input, bool valid)
    {
        Assert.Equal(valid, ClinicalText.PortableRegexProblem(input) == null);
        if (valid) RuleValidator.ValidateLinkTerms(input);
        else Assert.Throws<RuleValidationException>(() => RuleValidator.ValidateLinkTerms(input));
    }

    [Theory, MemberData(nameof(CompilationPatterns))]
    public void Lexical_acceptance_never_substitutes_for_actual_regex_compilation(string input, bool valid)
    {
        Assert.Null(ClinicalText.PortableRegexProblem(input));
        if (valid) RuleValidator.ValidateLinkTerms(input);
        else Assert.Throws<RuleValidationException>(() => RuleValidator.ValidateLinkTerms(input));
    }

    [Fact]
    public void Malformed_unicode_catalog_keys_and_aliases_are_validation_errors_not_server_errors()
    {
        foreach (var raw in new[] { "{\"bad\\ud800\":{\"classes\":[\"nsaid\"],\"brands\":[],\"regional_names\":[]}}",
                     "{\"generic\":{\"classes\":[\"nsaid\"],\"brands\":[\"bad\\ud800\"],\"regional_names\":[]}}" })
            Assert.Throws<RuleValidationException>(() => RuleValidator.ValidateCatalog(JsonDocument.Parse(raw).RootElement));
    }
}
