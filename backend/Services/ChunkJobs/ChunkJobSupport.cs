using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Models;

namespace ClinicalRag.Api.Services;

public sealed record ChunkPageText(int Page, string Text);

public static class ChunkJobSupport
{
    public const string DefaultGuidance = "keep each recommendation with its strength grade; never split tables";
    public static readonly string[] ActiveStatuses =
        ["queued", "running", "pausing", "paused", "stopping", "interrupted", "blocked"];
    public static readonly string[] TerminalStatuses =
        ["completed", "partial", "failed", "cancelled"];

    private static readonly Regex PageMarker = new(@"(?m)^\s*---\s*Page\s+(\d+)\s*---\s*$");

    public static int[] ReadPages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<int[]>(json) ?? [])
                .Where(page => page > 0).Distinct().Order().ToArray();
        }
        catch (JsonException) { return []; }
    }

    public static string WritePages(IEnumerable<int> pages) =>
        JsonSerializer.Serialize(pages.Where(page => page > 0).Distinct().Order().ToArray());

    public static IReadOnlyList<ChunkPageText> ParsePages(string text)
    {
        var pages = new List<ChunkPageText>();
        var parts = PageMarker.Split(text);
        if (parts.Length == 1)
        {
            pages.Add(new(1, text.Trim()));
            return pages;
        }
        if (parts[0].Trim().Length > 0) pages.Add(new(1, parts[0].Trim()));
        for (var index = 1; index < parts.Length; index += 2)
        {
            var number = int.Parse(parts[index]);
            var body = index + 1 < parts.Length ? parts[index + 1] : "";
            pages.Add(new(number, body.Trim()));
        }
        return pages;
    }

    public static object View(ChunkJob job) => new
    {
        job.Id, job.WorkspaceId, job.DocumentId, job.Status,
        pages = ReadPages(job.PagesJson),
        completedPages = ReadPages(job.CompletedPagesJson),
        failedPages = ReadPages(job.FailedPagesJson),
        skippedPages = ReadPages(job.SkippedPagesJson),
        job.BatchSize, job.MaxTokens, job.BatchTimeoutSeconds, job.MaxRetries, job.FailureMode,
        job.TotalBatches, job.CompletedBatches, job.CurrentBatchStart, job.CurrentBatchEnd,
        job.ErrorCode, job.RetryOfJobId, job.CreatedAt, job.StartedAt, job.CompletedAt, job.LastHeartbeatAt,
        canPause = job.Status is "queued" or "running",
        canResume = job.Status is "paused" or "interrupted" or "blocked",
        canCancel = job.Status is "queued" or "running" or "pausing" or "paused" or "interrupted" or "blocked",
        canRetry = job.Status is "completed" or "partial" or "failed" or "cancelled",
        nextPollAfterMs = job.Status switch
        {
            "queued" => 10000,
            "running" or "pausing" or "stopping" => 2000,
            _ => (int?)null
        },
        serverTime = DateTime.UtcNow
    };

    public static string SafeFailureReason(string? code) => code switch
    {
        "colab_unavailable" => "Chunking is blocked because the Colab model service is unavailable. Completed pages were kept. Resume when the model is ready.",
        "batch_timeout" => "A chunking batch timed out. Completed pages were kept. Retry the failed pages or resume with smaller batches.",
        "invalid_batch_result" => "The chunking service returned an invalid batch. Completed pages were kept. Retry the failed pages.",
        "empty_batch_result" => "The chunking service returned no chunk or skip for at least one page. Completed pages were kept. Retry the failed pages.",
        "worker_error" => "Chunking was interrupted by a server error. Completed pages were kept. Retry the remaining pages.",
        _ => "Chunking stopped. Completed pages were kept. Resume or retry the remaining pages."
    };
}
