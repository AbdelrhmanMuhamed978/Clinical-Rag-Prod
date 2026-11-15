using ClinicalRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed record DependencyReadiness(bool Ready, string? Message = null);
public sealed record ReadinessChecks(DependencyReadiness Database, ColabReadiness Colab, DependencyReadiness Pipeline);
public sealed record ApplicationReadiness(bool Ready, ReadinessChecks Checks);

public sealed class ReadinessService(AppDbContext db, IColabClient colab, PipelineClient pipeline)
{
    public async Task<ApplicationReadiness> CheckAsync(CancellationToken token)
    {
        var databaseReady = false;
        using (var timing = OperationTelemetry.Start("database.ready"))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                databaseReady = await db.Database.CanConnectAsync(deadline.Token).WaitAsync(deadline.Token);
                // Connectivity alone must not claim readiness before the operations migration exists.
                if (databaseReady) await db.ColabConnections.AsNoTracking().AnyAsync(deadline.Token).WaitAsync(deadline.Token);
                if (databaseReady) timing.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { timing.Cancelled(); throw; }
            catch (Exception) { databaseReady = false; }
        }
        // Do not use the scoped DbContext concurrently with the connection provider.
        var colabTask = databaseReady ? colab.ReadyAsync(token)
            : Task.FromResult(new ColabReadiness(false, Message: "Colab connection status is unavailable while the database is offline."));
        var pipelineTask = pipeline.ReadyAsync(token);
        await Task.WhenAll(colabTask, pipelineTask);
        var model = await colabTask;
        var pythonReady = await pipelineTask;
        return new(databaseReady && model.Ready && pythonReady, new(
            new(databaseReady, databaseReady ? null : new DependencyUnavailableException("database").PublicMessage), model,
            new(pythonReady, pythonReady ? null : new DependencyUnavailableException("pipeline").PublicMessage)));
    }
}
