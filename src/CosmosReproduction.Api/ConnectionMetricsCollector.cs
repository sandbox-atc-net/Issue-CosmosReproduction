using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CosmosReproduction.Api;

/// <summary>
/// In-process collector that listens to experimental System.Net activity sources
/// and aggregates connection establishment durations (with percentiles) for
/// the .NET 9 vs .NET 10 comparison requested in dotnet/runtime#124888.
/// </summary>
public sealed class ConnectionMetricsCollector : IHostedService
{
    private ActivityListener? _listener;
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _durations = new();
    // counts[0] = success, counts[1] = failure — using Interlocked for thread safety
    private readonly ConcurrentDictionary<string, long[]> _counts = new();
    private DateTimeOffset _collectionStartTime;

    private static readonly Dictionary<string, string> SourceToMetric = new()
    {
        ["Experimental.System.Net.Http.Connections"] = "connectionSetup",
        ["Experimental.System.Net.NameResolution"] = "dnsLookup",
        ["Experimental.System.Net.Sockets"] = "socketConnect",
        ["Experimental.System.Net.Security"] = "tlsHandshake",
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _collectionStartTime = DateTimeOffset.UtcNow;

        _listener = new ActivityListener
        {
            ShouldListenTo = source => SourceToMetric.ContainsKey(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped,
        };

        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    private void OnActivityStopped(Activity activity)
    {
        if (!SourceToMetric.TryGetValue(activity.Source.Name, out var metric))
            return;

        _durations.GetOrAdd(metric, _ => new ConcurrentBag<double>())
            .Add(activity.Duration.TotalMilliseconds);

        var counts = _counts.GetOrAdd(metric, _ => new long[2]);
        if (activity.Status == ActivityStatusCode.Error)
            Interlocked.Increment(ref counts[1]);
        else
            Interlocked.Increment(ref counts[0]);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        return Task.CompletedTask;
    }

    public Dictionary<string, object> GetReport()
    {
        var elapsed = DateTimeOffset.UtcNow - _collectionStartTime;

        var report = new Dictionary<string, object>
        {
            ["runtimeVersion"] = RuntimeInformation.FrameworkDescription,
            ["collectionDurationMinutes"] = Math.Round(elapsed.TotalMinutes, 1),
            ["unit"] = "ms",
        };

        foreach (var metric in new[] { "connectionSetup", "dnsLookup", "socketConnect", "tlsHandshake" })
        {
            var values = _durations.TryGetValue(metric, out var bag) ? bag.ToArray() : [];
            Array.Sort(values);

            _counts.TryGetValue(metric, out var counts);
            counts ??= new long[2];

            var stats = new Dictionary<string, object>
            {
                ["count"] = values.Length,
                ["p50"] = Percentile(values, 50),
                ["p95"] = Percentile(values, 95),
                ["p99"] = Percentile(values, 99),
                ["max"] = values.Length > 0 ? Math.Round(values[^1], 1) : 0.0,
                ["mean"] = values.Length > 0 ? Math.Round(values.Average(), 1) : 0.0,
                ["success"] = Interlocked.Read(ref counts[0]),
                ["failure"] = Interlocked.Read(ref counts[1]),
            };

            // Track the > 500ms connections — this is the exact threshold the Cosmos SDK
            // uses for its first-attempt metadata timeout.
            if (metric == "connectionSetup")
                stats["over500ms"] = values.Count(v => v > 500);

            report[metric] = stats;
        }

        return report;
    }

    public void Reset()
    {
        _durations.Clear();
        _counts.Clear();
        _collectionStartTime = DateTimeOffset.UtcNow;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0.0;
        var index = percentile / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return Math.Round(sorted[lower], 1);
        var weight = index - lower;
        return Math.Round(sorted[lower] * (1 - weight) + sorted[upper] * weight, 1);
    }
}
