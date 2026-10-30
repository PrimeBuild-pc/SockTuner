using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>Ranks observed activity; actionable evidence needs repeated, concurrent latency observations.</summary>
public static class DpcIsrDiagnosticEngine
{
    public static DpcIsrFinding Analyze(DpcIsrCaptureResult capture, double? baselineJitterMs)
    {
        if (capture.Error is { } error)
            return new(null, 0, [], null, error);

        var ranking = capture.Drivers.OrderByDescending(driver => driver.TotalElapsedMs).ToArray();
        if (ranking.Length == 0)
            return new(null, 0, ranking, null, "No driver interrupt activity was resolved during the capture.");

        var busiest = ranking[0];
        var share = capture.Duration.TotalMilliseconds <= 0 ? 0
            : busiest.TotalElapsedMs / capture.Duration.TotalMilliseconds * 100;
        var windows = capture.Windows ?? [];
        var usable = windows.Where(window => window.Latency.Sent >= 3
            && window.Latency.Received == window.Latency.Sent && window.Latency.JitterMs is >= 0
            && window.Latency.Samples.All(sample => sample.Timestamp >= window.StartedAt
                && sample.Timestamp < window.EndedAt)).ToArray();
        var repeated = usable.Count(window =>
        {
            var ordered = window.Drivers.OrderByDescending(driver => driver.TotalElapsedMs).ToArray();
            return ordered.Length > 0 && ordered[0].TotalElapsedMs > 0
                && string.Equals(ordered[0].DriverName, busiest.DriverName, StringComparison.OrdinalIgnoreCase)
                && (ordered.Length == 1 || ordered[0].TotalElapsedMs > ordered[1].TotalElapsedMs)
                // Windows Ping reports whole milliseconds: changes within one tick are inconclusive.
                && baselineJitterMs is >= 0 && window.Latency.JitterMs > baselineJitterMs + 1;
        });
        var evidence = usable.Length >= 3 && repeated > windows.Count / 2
            && !string.Equals(busiest.DriverName, "(unresolved)", StringComparison.OrdinalIgnoreCase);
        double? jitter = usable.Length == 0 ? null : usable.Average(window => window.Latency.JitterMs!.Value);
        var reason = $"{busiest.DriverName} used {busiest.TotalElapsedMs:0.##} ms of ISR/DPC time "
            + $"({share:0.#}% of {capture.Duration.TotalSeconds:0.#}s wall time; activity can span multiple CPUs). "
            + $"{usable.Length}/{windows.Count} concurrent latency windows were usable; "
            + $"{repeated} showed this driver uniquely busiest with jitter above the initial baseline and the ICMP resolution. "
            + (evidence
                ? "This is repeated co-occurrence, not proof of causation. Any change needs a before/after check."
                : "Evidence is inconclusive: no affinity or device-disable action is proposed. Driver-store cleanup is independent of this result.");
        return new(busiest.DriverName, share, ranking, jitter, reason, evidence);
    }
}
