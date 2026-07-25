using SockTuner.Models;

namespace SockTuner.Services;

public sealed record MetricDelta(string Metric, double? Baseline, double? After, double? Delta)
{
    public string Summary => Baseline is null || After is null ? $"{Metric}: unavailable" : $"{Metric}: {Baseline:0.0} → {After:0.0} ({Delta:+0.0;-0.0;0.0})";
}

public sealed record DiagnosticComparisonResult(bool Comparable, string Reason, IReadOnlyList<MetricDelta> Metrics);

public static class DiagnosticComparisonService
{
    public static DiagnosticComparisonResult Compare(GamingDiagnosticReport baseline, GamingDiagnosticReport after)
    {
        if (!string.Equals(baseline.RequestedTarget, after.RequestedTarget, StringComparison.OrdinalIgnoreCase)
            || baseline.Profile != after.Profile
            || baseline.Connection?.Port != after.Connection?.Port)
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

    private static MetricDelta Delta(string metric, double? baseline, double? after) =>
        new(metric, baseline, after, baseline is null || after is null ? null : after - baseline);
}
