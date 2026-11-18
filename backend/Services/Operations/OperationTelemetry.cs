using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClinicalRag.Api.Services;

/// <summary>Only fixed operation/outcome labels enter metrics. Inputs never enter logs, spans or labels.</summary>
public static class OperationTelemetry
{
    public const string MeterName = "ClinicalRag.Operations";
    public static readonly ActivitySource Activities = new(MeterName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Durations = Meter.CreateHistogram<double>("clinicalrag.step.duration", "ms");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("clinicalrag.colab.retries");
    private static readonly ConcurrentDictionary<(string Step, string Outcome), Samples> Metrics = new();
    private static long _retryCount;
    private static readonly double[] Buckets = [5, 25, 50, 100, 250, 500, 1000, 5000, 30000, 120000, 300000];
    private static readonly HashSet<string> Steps = new(StringComparer.Ordinal)
    {
        "http.server", "analysis.total", "analysis.stream", "retrieval.total", "documents.parse", "documents.chunk", "documents.embed",
        "database.ready", "pipeline.analyze", "pipeline.preview", "pipeline.ready", "pipeline.stream", "colab.http", "colab.ready",
        "colab.retrieve", "colab.generate", "colab.embed", "colab.rerank", "colab.parse_pdf", "colab.chunk.profile", "colab.chunk.preview",
        "colab.chunk.run", "colab.chunk.submit", "colab.chunk.poll"
    };

    private sealed class Samples
    {
        public readonly object Sync = new();
        public readonly long[] Counts = new long[Buckets.Length];
        public long Count;
        public double Sum;
    }

    public static string InputHash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    public static Timing Start(string step, ILogger? logger = null, string? input = null) => new(step, logger, input);

    public sealed class Timing : IDisposable
    {
        private readonly string _step;
        private readonly ILogger? _logger;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly Activity? _activity;
        private string _outcome = "error";
        private bool _disposed;

        internal Timing(string step, ILogger? logger, string? input)
        {
            RequireLabels(step, "error");
            _step = step; _logger = logger;
            _activity = Activities.StartActivity(step);
            if (input != null)
                logger?.LogInformation("Step started {Step} inputHash={InputHash} inputLength={InputLength}", step, InputHash(input), input.Length);
        }
        public void Success() => _outcome = "success";
        public void Cancelled() => _outcome = "cancelled";
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            Record(_step, _outcome, elapsed);
            _logger?.LogInformation("Step finished {Step} {Outcome} {DurationMilliseconds}", _step, _outcome, Math.Round(elapsed, 3));
            _activity?.SetTag("outcome", _outcome);
            _activity?.Dispose();
        }
    }

    public static void Record(string step, string outcome, double milliseconds)
    {
        RequireLabels(step, outcome);
        if (!double.IsFinite(milliseconds) || milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        Durations.Record(milliseconds, new("step", step), new("outcome", outcome));
        var samples = Metrics.GetOrAdd((step, outcome), _ => new Samples());
        lock (samples.Sync)
        {
            samples.Count++; samples.Sum += milliseconds;
            for (var i = 0; i < Buckets.Length; i++) if (milliseconds <= Buckets[i]) samples.Counts[i]++;
        }
    }

    private static void RequireLabels(string step, string outcome)
    {
        if (!Steps.Contains(step) || outcome is not ("success" or "error" or "cancelled"))
            throw new ArgumentException("Operation metrics require fixed operation and outcome labels.");
    }

    public static void Retry()
    {
        Retries.Add(1);
        Interlocked.Increment(ref _retryCount);
    }

    // A dependency-free Prometheus exporter as well as a standard .NET Meter for OTEL/dotnet-counters.
    public static string Prometheus()
    {
        // ConcurrentDictionary enumeration is safe; its ICollection.CopyTo is not
        // atomic with writers. Snapshot with foreach before sorting/exporting.
        var snapshot = new List<KeyValuePair<(string Step, string Outcome), Samples>>();
        foreach (var pair in Metrics) snapshot.Add(pair);
        snapshot.Sort((left, right) =>
        {
            var byStep = string.CompareOrdinal(left.Key.Step, right.Key.Step);
            return byStep != 0 ? byStep : string.CompareOrdinal(left.Key.Outcome, right.Key.Outcome);
        });

        var text = new StringBuilder("# TYPE clinicalrag_step_duration_milliseconds histogram\n");
        foreach (var (key, samples) in snapshot)
        {
            var labels = $"step=\"{key.Step}\",outcome=\"{key.Outcome}\"";
            lock (samples.Sync)
            {
                for (var i = 0; i < Buckets.Length; i++)
                    text.Append(CultureInfo.InvariantCulture, $"clinicalrag_step_duration_milliseconds_bucket{{{labels},le=\"{Buckets[i]}\"}} {samples.Counts[i]}\n");
                text.Append(CultureInfo.InvariantCulture, $"clinicalrag_step_duration_milliseconds_bucket{{{labels},le=\"+Inf\"}} {samples.Count}\n");
                text.Append(CultureInfo.InvariantCulture, $"clinicalrag_step_duration_milliseconds_count{{{labels}}} {samples.Count}\n");
                text.Append(CultureInfo.InvariantCulture, $"clinicalrag_step_duration_milliseconds_sum{{{labels}}} {samples.Sum}\n");
            }
        }
        text.Append(CultureInfo.InvariantCulture, $"# TYPE clinicalrag_colab_retries_total counter\nclinicalrag_colab_retries_total {Interlocked.Read(ref _retryCount)}\n");
        return text.ToString();
    }
}
