using ClinicalRag.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed class ChunkSearchService(AppDbContext db, ChunkVectorCache cache)
{
    public async Task<IReadOnlyList<Guid>> CandidatesAsync(Guid workspaceId, string query, EmbeddingBatch embedding,
        int candidates, CancellationToken cancellationToken = default)
    {
        var index = await cache.GetAsync(workspaceId, async token =>
        {
            var rows = await db.Chunks.AsNoTracking().Where(c => c.WorkspaceId == workspaceId
                    && (c.Status == "accepted" || c.Status == "embedded") && c.Embedding != null)
                .Select(c => new ChunkVectorRecord(c.Id, c.Text, c.Section, c.EmbeddingModel, c.Embedding!)).ToListAsync(token);
            var fullText = db.Database.IsSqlServer() && await FullTextAvailableAsync(token);
            return new ChunkSearchIndex(rows, fullText);
        }, cancellationToken);
        index.RequireCompatible(embedding);
        var limit = Math.Clamp(candidates, 1, RetrievalLimits.MaxRerankPassages);
        var legLimit = Math.Max(60, limit * 2);
        var dense = index.Dense(EmbeddingVectors.ReadNormalized(embedding.Vectors[0]), legLimit, cancellationToken);
        IReadOnlyList<ChunkSearchHit> lexical;
        if (index.FullTextAvailable)
        {
            try { lexical = await FullTextAsync(workspaceId, query, embedding.Model, index, legLimit, cancellationToken); }
            catch (SqlException exception) when (exception.Number is 7601 or 7603 or 7609 or 7635 or 30053)
            {
                // An index/catalog removed after capability detection is an expected
                // fallback. Network/database errors are NOT silently swallowed.
                cache.Invalidate(workspaceId);
                lexical = index.Lexical.Search(query, legLimit, cancellationToken);
            }
        }
        else lexical = index.Lexical.Search(query, legLimit, cancellationToken);
        return ReciprocalRankFusion.Fuse(dense, lexical, limit).Select(hit => hit.Id).ToArray();
    }

    private async Task<bool> FullTextAvailableAsync(CancellationToken cancellationToken)
    {
        // The KEY must be Chunk.Id, and BOTH lexical fields must be indexed. An
        // installed SQL component alone is not proof that this corpus has an index.
        var available = await db.Database.SqlQueryRaw<int>("""
            SELECT CASE WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1 AND EXISTS (
                SELECT 1 FROM sys.fulltext_indexes f
                JOIN sys.index_columns k ON k.object_id = f.object_id AND k.index_id = f.unique_index_id AND k.key_ordinal = 1
                WHERE f.object_id = OBJECT_ID(N'dbo.Chunks') AND f.is_enabled = 1
                  AND COL_NAME(k.object_id, k.column_id) = N'Id'
                  AND (SELECT COUNT(*) FROM sys.fulltext_index_columns c WHERE c.object_id = f.object_id
                       AND COL_NAME(c.object_id, c.column_id) IN (N'Text', N'Section')) = 2
            ) THEN 1 ELSE 0 END AS [Value]
            """).SingleAsync(cancellationToken);
        return available == 1;
    }

    private async Task<IReadOnlyList<ChunkSearchHit>> FullTextAsync(Guid workspaceId, string query, string model,
        ChunkSearchIndex index, int limit, CancellationToken cancellationToken)
    {
        // Limit AFTER workspace filtering. A global top_n_by_rank could suppress
        // this workspace's results when other tenants contain stronger matches.
        var hits = await db.Database.SqlQuery<SqlLexicalHit>($"""
            SELECT TOP ({limit}) c.[Id], hits.[RANK] AS [Rank]
            FROM FREETEXTTABLE([dbo].[Chunks], ([Text], [Section]), {query}) AS hits
            INNER JOIN [dbo].[Chunks] c ON c.[Id] = hits.[KEY]
            WHERE c.[WorkspaceId] = {workspaceId} AND c.[Status] IN (N'accepted', N'embedded')
              AND c.[Embedding] IS NOT NULL AND c.[EmbeddingModel] = {model}
            ORDER BY hits.[RANK] DESC, c.[Id]
            """).ToListAsync(cancellationToken);
        return hits.Where(hit => index.Contains(hit.Id)).Select(hit => new ChunkSearchHit(hit.Id, hit.Rank)).ToArray();
    }

    private sealed class SqlLexicalHit
    {
        public Guid Id { get; set; }
        public int Rank { get; set; }
    }
}
