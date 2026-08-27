using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Services.Diagnosis;

public sealed record BaselineChange(
    string Metric,
    double? Baseline,
    double? Recent,
    double? ChangePercent,
    bool Significant,
    string Summary);

public sealed record BaselineReport(
    bool Comparable,
    string Reason,
    int BaselineRuns,
    int RecentRuns,
    IReadOnlyList<BaselineChange> Changes)
{
    public IReadOnlyList<BaselineChange> Degraded => Changes.Where(change => change.Significant && change.ChangePercent > 0).ToArray();
    public IReadOnlyList<BaselineChange> Improved => Changes.Where(change => change.Significant && change.ChangePercent < 0).ToArray();

    public string Verdict => !Comparable
        ? Reason
        : Degraded.Count > 0
            ? $"Degraded against the earlier baseline: {string.Join("; ", Degraded.Select(change => change.Summary))}."
            : Improved.Count > 0
                ? $"Improved against the earlier baseline: {string.Join("; ", Improved.Select(change => change.Summary))}."
                : "No significant change against the earlier baseline.";
}

/// <summary>
/// Diagnosis layer: compares recent runs against older ones so a connection that has drifted is
/// visible as drift rather than as a bad day. A single spot test cannot answer "has this got
/// worse"; only the history can, and only if the runs are actually comparable.
/// </summary>
public static class BaselineAnalyzer
{
    /// <summary>Below this, a difference is not worth a user's attention whatever the percentage says.</summary>
    private const double SignificantPercent = 20;

    // Percentage change on a small number is noise: 2.0 ms to 2.5 ms is 25% and means nothing. Each
    // metric therefore also has to move by an absolute amount that matters on its own scale.
    private const double LatencyFloorMs = 5;
    private const double JitterFloorMs = 2;
    private const double LossFloorPercent = 1;

    /// <summary>Fewest runs on each side before a comparison is worth making at all.</summary>
    public const int MinimumRunsPerSide = 2;

    public static BaselineReport Compare(
        IReadOnlyList<DiagnosticHistoryEntry> entries,
        TimeSpan recentWindow,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(recentWindow, TimeSpan.Zero);

        var ordered = entries.OrderBy(entry => entry.SavedAt).ToArray();
        if (ordered.Length < MinimumRunsPerSide * 2)
        {
            return Incomparable($"At least {MinimumRunsPerSide * 2} saved runs are needed to separate a baseline from a recent window.");
        }

        var first = ordered[0].Report;
        if (ordered.Any(entry => !DiagnosticComparisonService.SameParameters(first, entry.Report)))
        {
            return Incomparable("Every run must use the same target, profile, load condition and optional TCP port.");
        }

        var cutoff = now - recentWindow;
        var baseline = ordered.Where(entry => entry.SavedAt < cutoff).ToArray();
        var recent = ordered.Where(entry => entry.SavedAt >= cutoff).ToArray();
        if (baseline.Length < MinimumRunsPerSide || recent.Length < MinimumRunsPerSide)
        {
            return Incomparable(
                $"The window leaves {baseline.Length} baseline run(s) and {recent.Length} recent run(s); "
                + $"{MinimumRunsPerSide} of each are needed.");
        }

        return new BaselineReport(true, "Comparable runs with identical parameters.", baseline.Length, recent.Length,
        [
            Change("Median ping", baseline, recent, report => report.GameTarget.MedianMs, LatencyFloorMs, "ms"),
            Change("P95 ping", baseline, recent, report => report.GameTarget.P95Ms, LatencyFloorMs, "ms"),
            Change("Jitter", baseline, recent, report => report.GameTarget.JitterMs, JitterFloorMs, "ms"),
            Change("No-reply rate", baseline, recent, report => report.GameTarget.LossPercent, LossFloorPercent, "%")
        ]);
    }

    private static BaselineChange Change(
        string metric,
        IReadOnlyList<DiagnosticHistoryEntry> baseline,
        IReadOnlyList<DiagnosticHistoryEntry> recent,
        Func<GamingDiagnosticReport, double?> select,
        double absoluteFloor,
        string unit)
    {
        // Median of each side rather than mean: one bad run in a week should not become the verdict.
        var before = Median(baseline.Select(entry => select(entry.Report)));
        var after = Median(recent.Select(entry => select(entry.Report)));
        if (before is not { } from || after is not { } to)
        {
            return new BaselineChange(metric, before, after, null, false, $"{metric}: not measured in every run");
        }

        var absolute = to - from;
        var percent = from == 0 ? (to == 0 ? 0 : 100) : absolute / from * 100;
        var significant = Math.Abs(absolute) >= absoluteFloor && Math.Abs(percent) >= SignificantPercent;
        var direction = absolute > 0 ? "degraded" : "improved";
        return new BaselineChange(metric, from, to, percent, significant,
            $"{metric} {direction} {Math.Abs(percent):0}% ({from:0.#} → {to:0.#} {unit})");
    }

    private static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static BaselineReport Incomparable(string reason) => new(false, reason, 0, 0, []);
}
