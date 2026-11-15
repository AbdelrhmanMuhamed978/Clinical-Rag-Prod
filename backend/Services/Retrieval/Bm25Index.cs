using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace ClinicalRag.Api.Services;

/// <summary>Immutable inverted index over reviewed Text + Section; no per-query scan/tokenisation of documents.</summary>
public sealed partial class Bm25Index
{
    private readonly record struct Posting(int Document, double Weight);
    private readonly Guid[] _ids;
    private readonly Dictionary<string, Posting[]> _postings;
    public long EstimatedBytes { get; }

    public Bm25Index(IReadOnlyList<(Guid Id, string Text, string? Section)> documents)
    {
        _ids = documents.Select(document => document.Id).ToArray();
        var lists = new Dictionary<string, List<(int Document, int Frequency)>>(StringComparer.Ordinal);
        var lengths = new int[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in Tokens($"{documents[i].Section}\n{documents[i].Text}"))
            {
                counts[term] = counts.GetValueOrDefault(term) + 1;
                lengths[i]++;
            }
            foreach (var (term, frequency) in counts)
            {
                if (!lists.TryGetValue(term, out var postings)) lists[term] = postings = [];
                postings.Add((i, frequency));
            }
        }
        var averageLength = Math.Max(1, documents.Count == 0 ? 1 : lengths.Average());
        var normalizers = lengths.Select(length => 1.2 * (.25 + .75 * length / averageLength)).ToArray();
        _postings = lists.ToDictionary(pair => pair.Key, pair =>
        {
            var idf = Math.Log(1 + (_ids.Length - pair.Value.Count + .5) / (pair.Value.Count + .5));
            return pair.Value.Select(posting => new Posting(posting.Document,
                idf * posting.Frequency * 2.2 / (posting.Frequency + normalizers[posting.Document]))).ToArray();
        }, StringComparer.Ordinal);
        // The double weight is fixed for an immutable corpus. Account for the
        // aligned (int, double) posting rather than the old (int, int) layout.
        EstimatedBytes = _ids.LongLength * 16 + _postings.Sum(pair => (long)pair.Key.Length * 2 + pair.Value.LongLength * 16 + 96);
    }

    public IReadOnlyList<ChunkSearchHit> Search(string query, int limit, CancellationToken cancellationToken = default)
    {
        if (_ids.Length == 0 || limit < 1) return [];
        var scores = ArrayPool<double>.Shared.Rent(_ids.Length);
        scores.AsSpan(0, _ids.Length).Clear();
        try
        {
            foreach (var term in Tokens(query).Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_postings.TryGetValue(term, out var postings)) continue;
                for (var i = 0; i < postings.Length; i++)
                {
                    if (i % 128 == 0) cancellationToken.ThrowIfCancellationRequested();
                    var posting = postings[i];
                    scores[posting.Document] += posting.Weight;
                }
            }
            var hits = new TopChunkHits(Math.Min(limit, _ids.Length));
            for (var i = 0; i < _ids.Length; i++)
            {
                if (i % 128 == 0) cancellationToken.ThrowIfCancellationRequested();
                hits.Offer(_ids[i], scores[i]);
            }
            return hits.Ordered();
        }
        finally { ArrayPool<double>.Shared.Return(scores, clearArray: true); }
    }

    private static IEnumerable<string> Tokens(string text) => TokenPattern().Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        .Select(match => match.Value);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TokenPattern();
}
