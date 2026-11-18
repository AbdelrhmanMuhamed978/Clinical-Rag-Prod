using System.Numerics.Tensors;

namespace ClinicalRag.Api.Services;

public sealed record ChunkVectorRecord(Guid Id, string Text, string? Section, string? Model, byte[] Vector,
    string? Metadata = null);

/// <summary>One immutable workspace snapshot: contiguous unit vectors and a BM25 inverted index.</summary>
public sealed class ChunkSearchIndex
{
    private readonly Guid[] _ids;
    private readonly HashSet<Guid> _membership;
    private readonly float[] _vectors;
    private readonly Dictionary<Guid, string> _kinds;
    private readonly Dictionary<Guid, string?> _sections;
    public string? Model { get; }
    public int Dimensions { get; }
    public int Count => _ids.Length;
    public bool FullTextAvailable { get; }
    public Bm25Index Lexical { get; }
    public long EstimatedBytes { get; }

    public ChunkSearchIndex(IReadOnlyList<ChunkVectorRecord> records, bool fullTextAvailable = false)
    {
        FullTextAvailable = fullTextAvailable;
        var spaces = records.Select(record => record.Model).Distinct(StringComparer.Ordinal).ToArray();
        if (spaces.Length > 1 || spaces.Any(model => !EmbeddingVectors.ValidModel(model))) throw new EmbeddingModelMismatchException();
        Model = spaces.SingleOrDefault();
        var valid = new List<(ChunkVectorRecord Record, float[] Vector)>();
        foreach (var record in records.OrderBy(record => record.Id))
        {
            // Malformed binary rows cannot become evidence or take down all valid
            // chunks. Different model identities/dimensions are NEVER mixed.
            if (!EmbeddingVectors.TryReadNormalized(record.Vector, out var vector)) continue;
            if (Dimensions != 0 && Dimensions != vector.Length) throw new EmbeddingModelMismatchException();
            Dimensions = vector.Length;
            valid.Add((record, vector));
        }
        _ids = valid.Select(item => item.Record.Id).ToArray();
        _membership = _ids.ToHashSet();
        _kinds = valid.ToDictionary(item => item.Record.Id,
            item => ChunkMetadata.Kind(item.Record.Metadata, item.Record.Text, item.Record.Section));
        _sections = valid.ToDictionary(item => item.Record.Id, item => item.Record.Section);
        _vectors = new float[checked(valid.Count * Dimensions)];
        for (var i = 0; i < valid.Count; i++) valid[i].Vector.CopyTo(_vectors, i * Dimensions);
        Lexical = new Bm25Index(valid.Select(item => (item.Record.Id, item.Record.Text, item.Record.Section)).ToArray());
        EstimatedBytes = _vectors.LongLength * sizeof(float) + _ids.LongLength * 64 + Lexical.EstimatedBytes
            + valid.Sum(item => (long)(item.Record.Text.Length + (item.Record.Section?.Length ?? 0)
                + (item.Record.Metadata?.Length ?? 0)) * 2) + 1024;
    }

    public bool Contains(Guid id) => _membership.Contains(id);
    public string KindOf(Guid id) => _kinds.GetValueOrDefault(id, "clinical_consideration");
    public string? SectionOf(Guid id) => _sections.GetValueOrDefault(id);
    public void RequireCompatible(EmbeddingBatch batch)
    {
        if (Model != null && (!StringComparer.Ordinal.Equals(Model, batch.Model) || (Dimensions != 0 && Dimensions != batch.Dimensions)))
            throw new EmbeddingModelMismatchException();
    }

    public IReadOnlyList<ChunkSearchHit> Dense(float[] unitQuery, int limit, CancellationToken cancellationToken = default)
    {
        if (Count == 0 || limit < 1) return [];
        if (unitQuery.Length != Dimensions) throw new EmbeddingModelMismatchException();
        var hits = new TopChunkHits(Math.Min(limit, Count));
        for (var i = 0; i < Count; i++)
        {
            if (i % 128 == 0) cancellationToken.ThrowIfCancellationRequested();
            // Unit vectors: cosine similarity is exactly a SIMD dot product.
            var score = TensorPrimitives.Dot(unitQuery, _vectors.AsSpan(i * Dimensions, Dimensions));
            hits.Offer(_ids[i], score);
        }
        return hits.Ordered();
    }
}
