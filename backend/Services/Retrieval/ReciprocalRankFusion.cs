namespace ClinicalRag.Api.Services;

public readonly record struct ChunkSearchHit(Guid Id, double Score);

public static class ReciprocalRankFusion
{
    public static IReadOnlyList<ChunkSearchHit> Fuse(IReadOnlyList<ChunkSearchHit> dense, IReadOnlyList<ChunkSearchHit> lexical, int limit)
    {
        if (limit < 1) return [];
        var scores = new Dictionary<Guid, double>();
        foreach (var leg in new[] { dense, lexical })
        {
            var seen = new HashSet<Guid>();
            var rank = 0;
            foreach (var item in leg)
            {
                if (!seen.Add(item.Id)) continue; // At most one contribution per leg.
                scores[item.Id] = scores.GetValueOrDefault(item.Id) + 1.0 / (RetrievalLimits.RrfK + ++rank);
            }
        }
        return scores.Select(pair => new ChunkSearchHit(pair.Key, pair.Value))
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Id).Take(limit).ToArray();
    }
}
