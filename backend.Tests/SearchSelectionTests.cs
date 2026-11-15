using System.Numerics.Tensors;
using System.Text;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class SearchSelectionTests
{
    private static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:D12}");

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(60)]
    [InlineData(1000)]
    public void Streaming_dense_top_k_matches_a_full_sort_including_ties_and_nonpositive_scores(int limit)
    {
        var random = new Random(41);
        var records = Enumerable.Range(1, 300).Select(number => new ChunkVectorRecord(Id(number), "Synthetic source", null,
            EmbeddingTestData.Model, EmbeddingVectors.EncodeNormalized(number % 3 == 0 ? [1, 0, 0, 0]
                : Enumerable.Range(0, 4).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray())))
            .OrderBy(_ => random.Next()).ToArray();
        float[] query = [1, 0, 0, 0];
        var expected = records.Select(record => new ChunkSearchHit(record.Id,
                TensorPrimitives.Dot(query, EmbeddingVectors.ReadNormalized(record.Vector))))
            .Where(hit => hit.Score > 0).OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Id).Take(limit).ToArray();
        Assert.Equal(expected, new ChunkSearchIndex(records).Dense(query, limit));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(60)]
    [InlineData(1000)]
    public void Precomputed_bm25_weights_and_streaming_top_k_match_the_original_formula(int limit)
    {
        var documents = Enumerable.Range(1, 150).Select(number => (Id: Id(number),
            Text: number % 7 == 0 ? "" : $"term{number % 5} common common {string.Join(' ', Enumerable.Repeat("monitor", number % 3))}",
            Section: number % 4 == 0 ? "Ｃｙｓｔａｔｉｎ" : (string?)null)).Reverse().ToArray();
        const string query = "COMMON common monitor cystatin term2";
        var expected = ReferenceBm25(documents, query, limit);
        Assert.Equal(expected, new Bm25Index(documents).Search(query, limit));
    }

    [Fact]
    public void Pooled_bm25_scores_are_not_retained_by_later_queries_or_other_indexes()
    {
        var first = new Bm25Index([(Id(1), "renal", null), (Id(2), "cystatin", null)]);
        var other = new Bm25Index([(Id(3), "unrelated", null), (Id(4), "cystatin", null)]);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(Id(1), Assert.Single(first.Search("renal", 30)).Id);
            Assert.Empty(first.Search("absent", 30));
            Assert.Equal(Id(4), Assert.Single(other.Search("cystatin", 30)).Id);
        }
    }

    [Fact]
    public async Task Concurrent_lexical_searches_have_independent_score_buffers()
    {
        var documents = Enumerable.Range(1, 300).Select(number => (Id(number), $"term{number % 8} common", (string?)null)).ToArray();
        var index = new Bm25Index(documents);
        var expected = Enumerable.Range(0, 8).Select(term => index.Search($"term{term}", 30).ToArray()).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(iteration => Task.Run(() =>
            Assert.Equal(expected[iteration % 8], index.Search($"term{iteration % 8}", 30)))));
    }

    [Fact]
    public void Cancellation_does_not_leak_a_score_buffer_or_poison_the_next_search()
    {
        var records = Enumerable.Range(1, 300).Select(number => new ChunkVectorRecord(Id(number), "cystatin", null,
            EmbeddingTestData.Model, EmbeddingVectors.EncodeNormalized([1, 0]))).ToArray();
        var index = new ChunkSearchIndex(records);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => index.Dense([1, 0], 30, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => index.Lexical.Search("cystatin", 30, cancellation.Token));
        Assert.Equal(30, index.Lexical.Search("cystatin", 30).Count);
        Assert.Empty(index.Dense([1, 0], 0)); Assert.Empty(index.Lexical.Search("cystatin", 0));
    }

    private static ChunkSearchHit[] ReferenceBm25(IReadOnlyList<(Guid Id, string Text, string? Section)> documents, string query, int limit)
    {
        static string[] Tokens(string text) => Regex.Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value).ToArray();
        var terms = documents.Select(document => Tokens($"{document.Section}\n{document.Text}")).ToArray();
        var average = Math.Max(1, terms.Average(document => document.Length));
        var scores = new double[documents.Count];
        foreach (var term in Tokens(query).Distinct(StringComparer.Ordinal))
        {
            var postings = terms.Select((tokens, document) => (Document: document, Frequency: tokens.Count(token => token == term)))
                .Where(posting => posting.Frequency > 0).ToArray();
            if (postings.Length == 0) continue;
            var idf = Math.Log(1 + (documents.Count - postings.Length + .5) / (postings.Length + .5));
            foreach (var posting in postings)
                scores[posting.Document] += idf * posting.Frequency * 2.2
                    / (posting.Frequency + 1.2 * (.25 + .75 * terms[posting.Document].Length / average));
        }
        return scores.Select((score, document) => new ChunkSearchHit(documents[document].Id, score)).Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Id).Take(limit).ToArray();
    }
}
