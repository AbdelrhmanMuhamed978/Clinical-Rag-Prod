using Microsoft.Extensions.Caching.Memory;

namespace ClinicalRag.Api.Services;

/// <summary>Bounded, single-flight workspace cache; never captures a scoped DbContext.</summary>
public sealed class ChunkVectorCache : IDisposable
{
    private sealed class Stripe
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly object Sync = new();
        public long Generation;
    }
    private sealed record CacheEntry(long DatabaseGeneration, ChunkSearchIndex Index);
    // Fixed metadata as well as bounded vectors. Never retain a slot for every
    // workspace ever edited. Rare hash collisions serialize cold loads only.
    private readonly Stripe[] _stripes = Enumerable.Range(0, 1024).Select(_ => new Stripe()).ToArray();
    private readonly MemoryCache _cache;
    private readonly long _sizeLimit;
    private readonly TimeSpan _absoluteExpiration;

    public ChunkVectorCache(IConfiguration? configuration = null)
    {
        _sizeLimit = Math.Clamp(configuration?.GetValue("Retrieval:CacheSizeMegabytes", 512) ?? 512, 16, 4096) * 1024L * 1024;
        _absoluteExpiration = TimeSpan.FromSeconds(Math.Clamp(configuration?.GetValue("Retrieval:CacheSeconds", 120) ?? 120, 1, 3600));
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _sizeLimit });
    }

    public void Invalidate(Guid workspaceId)
    {
        var stripe = _stripes[workspaceId.GetHashCode() & 1023];
        lock (stripe.Sync)
        {
            stripe.Generation++;
            _cache.Remove(workspaceId);
        }
    }

    // Compatibility overload for local tests/callers that are not serving
    // retrieval. Production retrieval must pass the durable database generation.
    public Task<ChunkSearchIndex> GetAsync(Guid workspaceId, Func<CancellationToken, Task<ChunkSearchIndex>> load,
        CancellationToken cancellationToken = default) =>
        GetAsync(workspaceId, 0, load, cancellationToken);

    public async Task<ChunkSearchIndex> GetAsync(Guid workspaceId, long databaseGeneration,
        Func<CancellationToken, Task<ChunkSearchIndex>> load, CancellationToken cancellationToken = default)
    {
        var stripe = _stripes[workspaceId.GetHashCode() & 1023];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cache.TryGetValue<CacheEntry>(workspaceId, out var cached) && cached is not null
                && cached.DatabaseGeneration == databaseGeneration) return cached.Index;
            await stripe.Gate.WaitAsync(cancellationToken);
            try
            {
                long processGeneration;
                lock (stripe.Sync)
                {
                    if (_cache.TryGetValue<CacheEntry>(workspaceId, out cached) && cached is not null
                        && cached.DatabaseGeneration == databaseGeneration) return cached.Index;
                    processGeneration = stripe.Generation;
                }
                var index = await load(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (index.EstimatedBytes > _sizeLimit) throw new InvalidOperationException("Workspace search index exceeds the configured cache memory budget.");
                lock (stripe.Sync)
                {
                    if (processGeneration != stripe.Generation) continue; // A commit raced the load; do not publish stale data.
                    // Publication and invalidation share this short critical
                    // section, including the window between checking and Set.
                    _cache.Set(workspaceId, new CacheEntry(databaseGeneration, index), new MemoryCacheEntryOptions
                    { Size = index.EstimatedBytes, AbsoluteExpirationRelativeToNow = _absoluteExpiration });
                    return index;
                }
            }
            finally { stripe.Gate.Release(); }
        }
    }

    public void Dispose() => _cache.Dispose();
}
