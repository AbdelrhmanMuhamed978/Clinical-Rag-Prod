using System.Reflection;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class CitationDisplayTests
{
    private static string Label(string society, string title, string version, string year, string status)
    {
        var method = typeof(AnalysisController).GetMethod("CitationLabel",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [society, title, version, year, status])!;
    }

    [Fact]
    public void Citation_label_uses_society_comma_title_year_and_status_without_duplicates()
    {
        Assert.Equal("WHO, Synthetic guideline (2026, FINAL)",
            Label("WHO", "Synthetic guideline", "", "2026", "final"));
        Assert.Equal("WHO guideline v2 (2026, FINAL)",
            Label("WHO", "WHO WHO guideline 2026", "v2", "2026", "final"));
        Assert.Equal("WHO, Untitled guideline (2026, FINAL)",
            Label("WHO", "9789240118164-eng.pdf", "", "2026", "final"));
        Assert.DoesNotContain("  ", Label("WHO", "WHO guideline", "", "2026", "final"));
    }

    [Fact]
    public void Section_hierarchy_is_readable_markdown()
    {
        Assert.Equal("Treatment > Induction therapy", ChunkMetadata.CleanSection("TABLES|Treatment|Induction therapy"));
        Assert.Equal("Treatment > Induction therapy", ChunkMetadata.CleanSection("Treatment · Induction therapy"));
        Assert.Equal("", ChunkMetadata.CleanSection(""));
    }

    [Fact]
    public void Grade_comes_from_the_text_and_never_from_an_llm_chunk_field()
    {
        const string narrative = "Cryptococcal disease is an important opportunistic infection in advanced HIV disease.";
        Assert.Null(ChunkMetadata.Strength(narrative, "strong"));
        Assert.Null(ChunkMetadata.Certainty(narrative, "high"));

        const string graded = "Use the preferred regimen. (Strong recommendation, high-certainty evidence)";
        Assert.Equal("strong", ChunkMetadata.Strength(graded, "none"));
        Assert.Equal("high", ChunkMetadata.Certainty(graded, "none"));

        const string conditional = "Consider the alternative. (Conditional recommendation, low-certainty evidence)";
        Assert.Equal("conditional", ChunkMetadata.Strength(conditional, "strong"));
        Assert.Equal("low", ChunkMetadata.Certainty(conditional, "high"));
    }
}
