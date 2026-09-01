using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>What a re-measurement says about the change that was applied between the two runs.</summary>
public enum ApplyOutcome
{
    /// <summary>At least one metric improved beyond the noise floor, and none got worse beyond it.</summary>
    Improved,

    /// <summary>Everything moved by less than the measurement can resolve.</summary>
    NoMeasurableChange,

    /// <summary>Something got worse beyond the noise floor. Rolling back is on the table.</summary>
    Worse,

    /// <summary>Better in one place and worse in another: a trade, not a win.</summary>
    Mixed,

    /// <summary>The two runs cannot be compared, so nothing can be concluded.</summary>
    NotComparable
}

/// <summary>One metric, before and after, and whether the difference means anything.</summary>
public sealed record VerifiedMetric(
    string Name,
    double? Before,
    double? After,
    double NoiseFloor,
    string Unit)
{
    public double? Delta => Before is { } before && After is { } after ? after - before : null;

    /// <summary>True when the movement is larger than what this measurement can resolve.</summary>
    public bool IsSignificant => Delta is { } delta && Math.Abs(delta) > NoiseFloor;

    /// <summary>Lower is better for every metric this analyzer handles.</summary>
    public bool Improved => IsSignificant && Delta < 0;

    public bool Worsened => IsSignificant && Delta > 0;

    public string Display => (Before, After) switch
    {
        (null, _) or (_, null) => $"{Name}: not measured in both runs",
        var (before, after) => $"{Name}: {before:0.0} → {after:0.0} {Unit} "
            + (IsSignificant
                ? $"({Delta:+0.0;-0.0} {Unit})"
                : $"(±{Math.Abs(Delta!.Value):0.0} {Unit}, within the ±{NoiseFloor:0.0} this run can resolve)")
    };

    public string VerdictBadge => !IsSignificant
        ? $"{Badges.Unknown} No change"
        : Improved ? $"{Badges.Good} Better" : $"{Badges.Bad} Worse";
}

/// <summary>The whole answer to "did that help?".</summary>
public sealed record ApplyVerification(
    ApplyOutcome Outcome,
    string Headline,
    string Detail,
    IReadOnlyList<VerifiedMetric> Metrics)
{
    /// <summary>Whether the app should put rolling back in front of the user.</summary>
    public bool SuggestsRollback => Outcome is ApplyOutcome.Worse;
}

/// <summary>
/// Answers the question this app exists to answer: did the change help?
/// </summary>
/// <remarks>
/// <para>
/// Every tuning tool tells you what it changed. Almost none tell you whether it worked, which is
/// how a folklore setting survives for fifteen years. The comparison itself already existed here;
/// what it lacked was a noise floor, so a 0.3 ms difference between two runs read as an
/// improvement when it was the same measurement twice.
/// </para>
/// <para>
/// The floor is derived, not chosen. Windows reports an ICMP round trip in whole milliseconds, so
/// anything under 1 ms is below the instrument regardless of how many samples were taken; and a
/// path that already swings by its own jitter between packets will swing by about that much
/// between runs. The floor is therefore the larger of the two, per metric. Loss is treated
/// differently: it is counted, not timed, so the floor is the smallest loss a single lost packet
/// would represent in the baseline run.
/// </para>
/// <para>
/// A result that is better in one place and worse in another is reported as a trade rather than
/// resolved into a single winner. Deciding which of latency and jitter matters more is the user's
/// call, and flattening it would be exactly the flattery the rest of this app avoids.
/// </para>
/// </remarks>
public static class ApplyVerificationAnalyzer
{
    /// <summary>Windows times an ICMP round trip to the millisecond, so less than one means nothing.</summary>
    public const double IcmpResolutionMs = 1.0;

    public static ApplyVerification Verify(GamingDiagnosticReport baseline, GamingDiagnosticReport after)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(after);

        if (!DiagnosticComparisonService.SameParameters(baseline, after))
        {
            return new(
                ApplyOutcome.NotComparable,
                "These two runs cannot be compared",
                "The target, profile, load condition and optional TCP port must all match. A difference "
                + "between runs with different parameters is a difference in the parameters.",
                []);
        }

        var before = baseline.GameTarget;
        var now = after.GameTarget;
        var timingFloor = Math.Max(IcmpResolutionMs, before.WindowedJitterMs ?? before.JitterMs ?? IcmpResolutionMs);

        var metrics = new List<VerifiedMetric>
        {
            new("Median round trip", before.MedianMs, now.MedianMs, timingFloor, "ms"),
            new("95th percentile", before.P95Ms, now.P95Ms, timingFloor, "ms"),
            new("Jitter", before.JitterMs, now.JitterMs, timingFloor, "ms"),
            new("No reply", before.LossPercent, now.LossPercent, LossFloor(before), "%")
        };

        var improved = metrics.Where(metric => metric.Improved).ToArray();
        var worsened = metrics.Where(metric => metric.Worsened).ToArray();

        return (improved.Length, worsened.Length) switch
        {
            (0, 0) => new(
                ApplyOutcome.NoMeasurableChange,
                "No measurable difference",
                "Every metric moved by less than this measurement can resolve. That is not proof the change "
                + "did nothing — it is proof this path cannot tell, which is the honest answer and a good "
                + "reason not to keep a change that costs something.",
                metrics),
            ( > 0, 0) => new(
                ApplyOutcome.Improved,
                $"Better: {Name(improved)}",
                $"{Sentence(improved, "improved")} Nothing measured got worse beyond the noise floor. "
                + "Re-run it once more before believing it: one pair of runs is one pair of runs.",
                metrics),
            (0, > 0) => new(
                ApplyOutcome.Worse,
                $"Worse: {Name(worsened)}",
                $"{Sentence(worsened, "got worse")} The audit history holds the exact values from before "
                + "the change, so rolling back restores them.",
                metrics),
            _ => new(
                ApplyOutcome.Mixed,
                "A trade, not a win",
                $"{Sentence(improved, "improved")} {Sentence(worsened, "got worse")} "
                + "Which of those matters more depends on what you are playing, so this is left as a choice "
                + "rather than resolved into a single verdict.",
                metrics)
        };
    }

    /// <summary>
    /// The smallest loss one packet represents in the baseline run. Below that, a difference in
    /// loss percentage is a difference of nothing.
    /// </summary>
    private static double LossFloor(ProbeStatistics baseline) =>
        baseline.Sent > 0 ? 100d / baseline.Sent : 100d;

    private static string Name(IReadOnlyList<VerifiedMetric> metrics) =>
        string.Join(", ", metrics.Select(metric => metric.Name.ToLowerInvariant()));

    private static string Sentence(IReadOnlyList<VerifiedMetric> metrics, string verb) =>
        metrics.Count == 0
            ? string.Empty
            : string.Join(" ", metrics.Select(metric =>
                $"{metric.Name} {verb} by {Math.Abs(metric.Delta!.Value):0.0} {metric.Unit} "
                + $"({metric.Before:0.0} → {metric.After:0.0})."));
}
