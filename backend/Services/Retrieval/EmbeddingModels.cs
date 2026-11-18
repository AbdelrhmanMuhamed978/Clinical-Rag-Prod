using ClinicalRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public static class EmbeddingModels
{
    /// <summary>Called inside the same transaction as a batch's vector/progress write.</summary>
    public static async Task RequireCompatibleAsync(AppDbContext db, Guid workspaceId, EmbeddingBatch batch, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, cancellationToken);
        var spaces = await db.Chunks.AsNoTracking().Where(c => c.WorkspaceId == workspaceId
                && !c.ReReviewRequired
                && (c.Status == "accepted" || c.Status == "embedded") && c.Embedding != null)
            .Select(c => new { c.EmbeddingModel, Bytes = c.Embedding!.Length }).Distinct().ToListAsync(cancellationToken);
        if (spaces.Any(space => !EmbeddingVectors.ValidModel(space.EmbeddingModel)
            || !StringComparer.Ordinal.Equals(space.EmbeddingModel, batch.Model) || space.Bytes != batch.Dimensions * sizeof(float)))
            throw new EmbeddingModelMismatchException();
        // A model can change only after ALL active vectors have been invalidated.
        // The concurrency-token update below prevents simultaneous empty-workspace
        // writers from committing different models even across API processes.
        if (spaces.Count > 0 && workspace.EmbeddingModel != null
            && (!StringComparer.Ordinal.Equals(workspace.EmbeddingModel, batch.Model) || workspace.EmbeddingDimensions != batch.Dimensions))
            throw new EmbeddingModelMismatchException();
        workspace.EmbeddingModel = batch.Model;
        workspace.EmbeddingDimensions = batch.Dimensions;
        db.Entry(workspace).Property(w => w.EmbeddingModel).IsModified = true;
        db.Entry(workspace).Property(w => w.EmbeddingDimensions).IsModified = true;
    }
}
