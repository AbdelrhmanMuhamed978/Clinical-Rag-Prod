using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class ChunkMetadataTests
{
    [Fact]
    public void Structured_kind_and_population_win_over_text_heuristics()
    {
        const string metadata = "{\"kind\":\"definition\",\"population\":\"adults\",\"recommendation_id\":\"abc123\"}";
        Assert.Equal("definition", ChunkMetadata.Kind(metadata, "Use drug A. (Strong recommendation)"));
        Assert.Equal("adults", ChunkMetadata.Population(metadata, "Pregnant patients with TB"));
        Assert.Equal("abc123", ChunkMetadata.RecommendationId(metadata));
    }

    [Theory]
    [InlineData("Advanced HIV disease is defined as a CD4 count below 200 cells/mm3.", "definition")]
    [InlineData("Summary of recommendations\nUse drug A. (Strong recommendation)", "summary")]
    [InlineData("[TABLES]\n|Intervention|Grade|\n|---|---|\n|Drug A|Strong|\n[/TABLES]", "table_row")]
    [InlineData("Use drug A. (Strong recommendation, high-certainty evidence)", "recommendation")]
    public void Legacy_rows_without_metadata_are_classified_deterministically(string text, string expected)
        => Assert.Equal(expected, ChunkMetadata.Kind(null, text, null));

    [Fact]
    public void Malformed_metadata_never_throws_or_becomes_a_kind()
    {
        Assert.Equal("recommendation",
            ChunkMetadata.Kind("{not-json", "Use drug A. (Strong recommendation)"));
        Assert.Null(ChunkMetadata.Population("{not-json", "General adult guidance"));
    }
}
