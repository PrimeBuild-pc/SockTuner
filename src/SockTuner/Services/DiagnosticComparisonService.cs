using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Services;

public sealed record MetricDelta(string Metric, double? Baseline, double? After, double? Delta)
{
    public string Summary => Baseline is null || After is null ? $"{Metric}: unavailable" : $"{Metric}: {Baseline:0.0} → {After:0.0} ({Delta:+0.0;-0.0;0.0})";
}

public sealed record DiagnosticComparisonResult(bool Comparable, string Reason, IReadOnlyList<MetricDelta> Metrics);

public sealed record DiagnosticTrendPoint(DateTimeOffset SavedAt, double? AverageMs, double? P95Ms, double? JitterMs, double LossPercent)
{
    public string Summary => $"{SavedAt:g}: avg {AverageMs:0.0} ms · P95 {P95Ms:0.0} ms · jitter {JitterMs:0.0} ms · no reply {LossPercent:0.#}%";
}

public sealed record DiagnosticTrendResult(bool Comparable, string Reason, IReadOnlyList<DiagnosticTrendPoint> Points);

public static class DiagnosticComparisonService
{
    public static DiagnosticComparisonResult Compare(GamingDiagnosticReport baseline, GamingDiagnosticReport after)
    {
        if (!SameParameters(baseline, after))
            return new(false, "Targets and every diagnostic profile parameter must match.", []);

        return new(true, "Comparable runs with identical target and profile parameters.",
        [
            Delta("Game average ms", baseline.GameTarget.AverageMs, after.GameTarget.AverageMs),
            Delta("Game P95 ms", baseline.GameTarget.P95Ms, after.GameTarget.P95Ms),
            Delta("Game P99 ms", baseline.GameTarget.P99Ms, after.GameTarget.P99Ms),
            Delta("Game jitter ms", baseline.GameTarget.JitterMs, after.GameTarget.JitterMs),
            Delta("Game no-reply %", baseline.GameTarget.LossPercent, after.GameTarget.LossPercent),
            Delta("Reference P95 ms", baseline.Reference.P95Ms, after.Reference.P95Ms),
            Delta("Gateway P95 ms", baseline.Gateway.P95Ms, after.Gateway.P95Ms)
        ]);
    }

    public static DiagnosticTrendResult Trend(IReadOnlyList<DiagnosticHistoryEntry> entries)
    {
        if (entries.Count < 2) return new(false, "Select at least two runs.", []);
        var first = entries[0].Report;
        if (entries.Any(entry => !SameParameters(first, entry.Report)))
            return new(false, "Every selected run must use the same target, profile, and optional TCP port.", []);
        return new(true, "Comparable multi-run trend with identical parameters.", entries
            .OrderBy(entry => entry.SavedAt)
            .Select(entry => new DiagnosticTrendPoint(entry.SavedAt, entry.Report.GameTarget.AverageMs,
                entry.Report.GameTarget.P95Ms, entry.Report.GameTarget.JitterMs, entry.Report.GameTarget.LossPercent))
            .ToArray());
    }

    private static bool SameParameters(GamingDiagnosticReport baseline, GamingDiagnosticReport after) =>
        string.Equals(baseline.RequestedTarget, after.RequestedTarget, StringComparison.OrdinalIgnoreCase)
        && baseline.Profile == after.Profile
        && baseline.LoadCondition == after.LoadCondition
        && baseline.Connection?.Port == after.Connection?.Port;

    private static MetricDelta Delta(string metric, double? baseline, double? after) =>
        new(metric, baseline, after, baseline is null || after is null ? null : after - baseline);
}
