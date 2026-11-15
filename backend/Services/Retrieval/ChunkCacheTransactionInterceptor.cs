using System.Data.Common;
using ClinicalRag.Api.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClinicalRag.Api.Services;

internal sealed class ChunkCacheTransactionInterceptor : DbTransactionInterceptor
{
    public static readonly ChunkCacheTransactionInterceptor Instance = new();
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as AppDbContext)?.CommitSearchInvalidations(eventData.TransactionId);
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.CommitSearchInvalidations(eventData.TransactionId);
        return Task.CompletedTask;
    }
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as AppDbContext)?.RollbackSearchInvalidations(eventData.TransactionId);
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.RollbackSearchInvalidations(eventData.TransactionId);
        return Task.CompletedTask;
    }
}
