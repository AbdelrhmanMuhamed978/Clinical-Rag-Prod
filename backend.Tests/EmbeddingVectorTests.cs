using System.Buffers.Binary;
using System.Text.Json;
using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class EmbeddingVectorTests
{
    [Fact]
    public void Storage_is_normalized_little_endian_float32_and_dense_cosine_is_a_dot_product()
    {
        var bytes = EmbeddingVectors.EncodeNormalized([3, 4]);
        Assert.Equal(8, bytes.Length);
        Assert.Equal(.6f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 4)), 6);
        Assert.Equal(.8f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4)), 6);
        var id = Guid.NewGuid(); var index = new ChunkSearchIndex([new(id, "Text", "Section", EmbeddingTestData.Model, bytes)]);
        Assert.Equal(.6, Assert.Single(index.Dense([1, 0], 30)).Score, 6);
    }

    [Fact]
    public void Finite_float32_extremes_are_normalized_without_double_accumulator_overflow_or_underflow()
    {
        Assert.Equal(new[] { 1f, 0f }, EmbeddingVectors.ReadNormalized(EmbeddingVectors.EncodeNormalized([float.MaxValue, 0])));
        Assert.Equal(new[] { 1f, 0f }, EmbeddingVectors.ReadNormalized(EmbeddingVectors.EncodeNormalized([float.Epsilon, 0])));
    }

    [Theory]
    [InlineData("{\"embeddings\":[[1,0]],\"model\":\"model\",\"dimensions\":\"2\"}")]
    [InlineData("{\"embeddings\":[[1,0]],\"model\":\"model\",\"dimensions\":3}")]
    [InlineData("{\"embeddings\":[[1,0]],\"model\":\" model\",\"dimensions\":2}")]
    [InlineData("{\"embeddings\":[[1,0]],\"model\":\"\",\"dimensions\":2}")]
    [InlineData("{\"embeddings\":[[1,0]],\"model\":\"model\"}")]
    [InlineData("{\"embeddings\":[[1,0]],\"dimensions\":2}")]
    public void Model_and_exact_dimension_metadata_are_required(string json) =>
        Assert.Throws<InvalidDataException>(() => EmbeddingVectors.Parse(JsonDocument.Parse(json).RootElement, 1));

    [Fact]
    public void Malformed_or_nonunit_binary_vectors_never_become_dense_or_lexical_evidence()
    {
        var malformed = new byte[] { 1, 2, 3 };
        var notUnit = new byte[8]; BinaryPrimitives.WriteSingleLittleEndian(notUnit.AsSpan(0, 4), 2);
        var goodId = Guid.NewGuid();
        var index = new ChunkSearchIndex([
            new(Guid.NewGuid(), "bad term", null, EmbeddingTestData.Model, malformed),
            new(Guid.NewGuid(), "bad term", null, EmbeddingTestData.Model, notUnit),
            new(goodId, "valid term", null, EmbeddingTestData.Model, EmbeddingVectors.EncodeNormalized([1, 0]))]);
        Assert.Equal(1, index.Count); Assert.Equal(goodId, Assert.Single(index.Dense([1, 0], 30)).Id);
        Assert.Empty(index.Lexical.Search("bad", 30));
    }

    [Fact]
    public void Different_valid_dimensions_or_models_are_refused_even_if_one_row_is_malformed()
    {
        var first = new ChunkVectorRecord(Guid.NewGuid(), "one", null, "model-a", EmbeddingVectors.EncodeNormalized([1, 0]));
        Assert.Throws<EmbeddingModelMismatchException>(() => new ChunkSearchIndex([first,
            new(Guid.NewGuid(), "two", null, "model-a", EmbeddingVectors.EncodeNormalized([1, 0, 0]))]));
        Assert.Throws<EmbeddingModelMismatchException>(() => new ChunkSearchIndex([first,
            new(Guid.NewGuid(), "two", null, "model-b", [1, 2, 3])]));
    }

    [Fact]
    public void Bm25_indexes_both_text_and_section_and_normalizes_unicode_terms()
    {
        var sectionId = Guid.NewGuid(); var textId = Guid.NewGuid();
        var index = new Bm25Index([(sectionId, "Unrelated quotation.", "Ｃｙｓｔａｔｉｎ"), (textId, "Cystatin cystatin monitoring.", null)]);
        var result = index.Search("CYSTATIN", 30);
        Assert.Equal(2, result.Count); Assert.Contains(result, hit => hit.Id == sectionId); Assert.Contains(result, hit => hit.Id == textId);
        Assert.Empty(index.Search("unmatched", 30));
    }
}
