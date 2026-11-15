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

    public async Task<ChunkSearchIndex> GetAsync(Guid workspaceId, Func<CancellationToken, Task<ChunkSearchIndex>> load,
        CancellationToken cancellationToken = default)
    {
        var stripe = _stripes[workspaceId.GetHashCode() & 1023];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cache.TryGetValue<ChunkSearchIndex>(workspaceId, out var cached)) return cached!;
            await stripe.Gate.WaitAsync(cancellationToken);
            try
            {
                long generation;
                lock (stripe.Sync)
                {
                    if (_cache.TryGetValue(workspaceId, out cached)) return cached!;
                    generation = stripe.Generation;
                }
                var index = await load(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (index.EstimatedBytes > _sizeLimit) throw new InvalidOperationException("Workspace search index exceeds the configured cache memory budget.");
                lock (stripe.Sync)
                {
                    if (generation != stripe.Generation) continue; // A commit raced the load; do not publish stale data.
                    // Publication and invalidation share this short critical
                    // section, including the window between checking and Set.
                    _cache.Set(workspaceId, index, new MemoryCacheEntryOptions
                    { Size = index.EstimatedBytes, AbsoluteExpirationRelativeToNow = _absoluteExpiration });
                    return index;
                }
            }
            finally { stripe.Gate.Release(); }
        }
    }

    public void Dispose() => _cache.Dispose();
}
