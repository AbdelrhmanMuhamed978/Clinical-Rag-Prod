using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class QueryExpansionTests
{
    [Fact]
    public void Expansion_keeps_the_original_and_adds_domain_synonyms()
    {
        var variants = QueryExpansion.Expand("viral load target");
        Assert.Contains("viral load target", variants);
        Assert.True(variants.Count is >= 2 and <= 4);
        Assert.Contains(variants, value => value.Contains("viral suppression", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(variants.Count, variants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Expansion_does_not_match_a_synonym_inside_another_word()
    {
        var variants = QueryExpansion.Expand("cardiac risk assessment");
        Assert.Contains("cardiac risk assessment", variants);
        Assert.DoesNotContain(variants, value => value.Contains("antiretroviral", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("What is the viral load target?", QueryIntent.Definition)]
    [InlineData("Starting ART in adults with HIV", QueryIntent.Broad)]
    [InlineData("ART timing in tuberculosis", QueryIntent.Narrow)]
    public void Intent_detection_is_closed_and_deterministic(string query, QueryIntent expected)
        => Assert.Equal(expected, QueryExpansion.IntentOf(query));

    [Fact]
    public void Definition_and_summary_boosts_are_limited_to_matching_intents()
    {
        Assert.True(QueryExpansion.SectionBoost("definition", "Definitions", QueryIntent.Definition) > 1);
        Assert.True(QueryExpansion.SectionBoost("summary", "Summary of recommendations", QueryIntent.Broad) > 1);
        Assert.Equal(1, QueryExpansion.SectionBoost("recommendation", "Treatment", QueryIntent.Broad));
        Assert.Equal(1, QueryExpansion.SectionBoost("definition", "Definitions", QueryIntent.Narrow));
    }

    [Fact]
    public void Narrow_exception_passages_are_penalized_only_on_broad_questions()
    {
        const string metadata = "{\"kind\":\"recommendation\",\"population\":\"tuberculosis\"}";
        Assert.True(QueryExpansion.NarrowPenalty(metadata, "TB-specific ART timing", QueryIntent.Broad) > 0);
        Assert.Equal(0, QueryExpansion.NarrowPenalty(metadata, "TB-specific ART timing", QueryIntent.Narrow));
        Assert.Equal(0, QueryExpansion.NarrowPenalty(
            "{\"kind\":\"recommendation\",\"population\":\"adults\"}", "General adult guidance", QueryIntent.Broad));
    }
}
