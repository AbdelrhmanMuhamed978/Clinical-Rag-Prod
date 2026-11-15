namespace ClinicalRag.Api.Services;

/// <summary>Enforce deadlines even for non-cooperative handlers, and own late responses/faults.</summary>
internal static class HttpResponseDeadline
{
    public static async Task<HttpResponseMessage> AwaitAsync(Task<HttpResponseMessage> pending, CancellationToken token)
    {
        try { return await pending.WaitAsync(token); }
        catch
        {
            _ = pending.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public static async Task AwaitBodyAsync(Task pending, CancellationToken token)
    {
        try { await pending.WaitAsync(token); }
        catch { Observe(pending); throw; }
    }

    public static async Task<T> AwaitBodyAsync<T>(Task<T> pending, CancellationToken token)
    {
        try { return await pending.WaitAsync(token); }
        catch { Observe(pending); throw; }
    }

    private static void Observe(Task pending) => _ = pending.ContinueWith(task => { _ = task.Exception; },
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
