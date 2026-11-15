namespace ClinicalRag.Api.Services;

/// <summary>Streaming top-k selection; score ties retain the smallest chunk IDs.</summary>
internal sealed class TopChunkHits(int limit)
{
    private sealed class WorstFirst : IComparer<ChunkSearchHit>
    {
        public static readonly WorstFirst Instance = new();
        public int Compare(ChunkSearchHit left, ChunkSearchHit right)
        {
            var score = left.Score.CompareTo(right.Score);
            return score != 0 ? score : right.Id.CompareTo(left.Id);
        }
    }

    private readonly PriorityQueue<ChunkSearchHit, ChunkSearchHit> _hits = new(limit, WorstFirst.Instance);

    public void Offer(Guid id, double score)
    {
        if (!(score > 0)) return;
        var hit = new ChunkSearchHit(id, score);
        if (_hits.Count < limit) _hits.Enqueue(hit, hit);
        else if (WorstFirst.Instance.Compare(hit, _hits.Peek()) > 0) _hits.EnqueueDequeue(hit, hit);
    }

    public IReadOnlyList<ChunkSearchHit> Ordered()
    {
        var hits = _hits.UnorderedItems.Select(item => item.Element).ToArray();
        Array.Sort(hits, static (left, right) =>
        {
            var score = right.Score.CompareTo(left.Score);
            return score != 0 ? score : left.Id.CompareTo(right.Id);
        });
        return hits;
    }
}
