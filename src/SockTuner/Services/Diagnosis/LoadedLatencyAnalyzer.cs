using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Diagnosis layer: turns an idle-versus-loaded measurement into a grade and an owner. Pure over
/// collected facts — it never measures anything itself.
/// </summary>
public static class LoadedLatencyAnalyzer
{
    /// <summary>
    /// The Waveform/dslreports latency-increase scale. Grades describe queue growth under load, not
    /// link speed: a fast line with a deep queue still grades badly, which is exactly the case users
    /// misread as "my connection is fine, so it must be the game".
    /// </summary>
    public static BufferbloatGrade Grade(double latencyIncreaseMs) => latencyIncreaseMs switch
    {
        < 5 => BufferbloatGrade.APlus,
        < 30 => BufferbloatGrade.A,
        < 60 => BufferbloatGrade.B,
        < 200 => BufferbloatGrade.C,
        < 400 => BufferbloatGrade.D,
        _ => BufferbloatGrade.F
    };

    /// <summary>
    /// A local transfer is called saturating only against a capacity we actually measured. The NIC
    /// link speed is a poor stand-in — a 1 Gbit/s adapter on a 50 Mbit/s line never looks busy — so
    /// without a measured capacity the claim is only made when the adapter itself is clearly full.
    /// </summary>
    public const double LocallySaturatedShare = 0.5;
    private const double AdapterSaturatedPercent = 70;

    public static bool IsLocallySaturating(LinkUtilization utilization, double? measuredCapacityBitsPerSecond) =>
        measuredCapacityBitsPerSecond is { } capacity and > 0
            ? utilization.PeakBitsPerSecond >= capacity * LocallySaturatedShare
            : utilization.LinkSpeedKnown && utilization.PeakPercentOfLink >= AdapterSaturatedPercent;

    /// <summary>
    /// Reports that this machine is filling its own link, so the degradation seen elsewhere in the
    /// run is self-inflicted. Returns null when nothing local stands out.
    /// </summary>
    public static BottleneckAssessment? LocalSaturation(
        IReadOnlyList<LinkUtilization> utilization,
        double? measuredCapacityBitsPerSecond)
    {
        var busiest = utilization
            .Where(item => IsLocallySaturating(item, measuredCapacityBitsPerSecond))
            .MaxBy(item => item.PeakBitsPerSecond);
        if (busiest is null)
        {
            return null;
        }

        return new BottleneckAssessment(
            NetworkSegment.Lan,
            DiagnosticConfidence.Medium,
            ResponsibilityAssigner.Assign(NetworkSegment.Lan, LocalControl.RequiresChoice),
            "This machine was saturating its own link while the run was taken",
            [
                busiest.Summary,
                measuredCapacityBitsPerSecond is { } capacity
                    ? $"That is at least {LocallySaturatedShare:P0} of the {ThroughputResult.FormatRate(capacity)} this connection measured."
                    : $"That is at least {AdapterSaturatedPercent:0}% of the adapter link rate."
            ],
            [
                "Local traffic explains the latency without any fault upstream; stop the transfer and repeat before blaming the ISP.",
                "Another device on the same connection would not show here — only traffic through this adapter is counted."
            ]);
    }

    /// <summary>
    /// Grades one direction. Bufferbloat is fixed by queue management on the device that owns the
    /// bottleneck queue — the router — so it is never offered as a local change.
    /// </summary>
    public static BottleneckAssessment Analyze(
        LoadedLatencyResult result,
        IReadOnlyList<LinkUtilization>? idleUtilization = null)
    {
        // A baseline taken while something else was already filling the link measures that, not the
        // idle path, so it is reported as such instead of being graded.
        if (idleUtilization is { Count: > 0 }
            && LocalSaturation(idleUtilization, result.Load.BitsPerSecond) is { } saturated)
        {
            return saturated with
            {
                Title = "The idle baseline was not idle: this machine was already loading the link",
                Confidence = DiagnosticConfidence.High
            };
        }

        if (result.LatencyIncreaseMs is not { } increase)
        {
            return Inconclusive(
                "Latency under load could not be measured",
                [$"Idle: {result.Idle.Summary}", $"Loaded: {result.Loaded.Summary}"],
                ["The latency target has to answer during both phases; a target that blocks ICMP cannot be graded."]);
        }

        if (result.Load.BitsPerSecond <= 0)
        {
            return Inconclusive(
                "The load never established, so nothing was placed under load",
                [result.Load.Summary],
                ["Without a transfer, the second measurement is a second idle measurement."]);
        }

        var grade = Grade(increase);
        var supporting = new List<string>
        {
            result.Summary,
            $"Latency-increase grade {Display(grade)} for the {result.Direction.ToString().ToLowerInvariant()} direction."
        };
        if (result.JitterIncreaseMs is { } jitter and > 0)
        {
            supporting.Add($"Jitter rises by {jitter:0.0} ms under the same load.");
        }

        if (result.LossIncreasePercent > 1)
        {
            supporting.Add($"Loss rises by {result.LossIncreasePercent:0.#} percentage points under load.");
        }

        var contradicting = new List<string>();
        if (!result.Load.Completed)
        {
            contradicting.Add("The transfer was stopped before its full window, so the link may not have been fully loaded.");
        }

        if (grade is BufferbloatGrade.APlus or BufferbloatGrade.A)
        {
            return new BottleneckAssessment(
                NetworkSegment.Unknown,
                DiagnosticConfidence.Medium,
                RemediationOwner.PresetOrManual,
                $"No meaningful bufferbloat in the {result.Direction.ToString().ToLowerInvariant()} direction (grade {Display(grade)})",
                supporting.ToArray(),
                contradicting.Count == 0
                    ? ["This direction is clean; the opposite direction has to be measured separately."]
                    : contradicting.ToArray());
        }

        return new BottleneckAssessment(
            NetworkSegment.RouterOrAccess,
            grade switch
            {
                BufferbloatGrade.F or BufferbloatGrade.D => DiagnosticConfidence.High,
                BufferbloatGrade.C => DiagnosticConfidence.Medium,
                _ => DiagnosticConfidence.Low
            },
            ResponsibilityAssigner.Assign(NetworkSegment.RouterOrAccess, LocalControl.None),
            $"Latency grows by {increase:0} ms under {result.Direction.ToString().ToLowerInvariant()} load (grade {Display(grade)})",
            supporting.ToArray(),
            contradicting.ToArray());
    }

    public static string Display(BufferbloatGrade grade) => grade == BufferbloatGrade.APlus ? "A+" : grade.ToString();

    private static BottleneckAssessment Inconclusive(
        string title, IReadOnlyList<string> supporting, IReadOnlyList<string> contradicting) =>
        new(NetworkSegment.Unknown, DiagnosticConfidence.Low, RemediationOwner.PresetOrManual,
            title, supporting, contradicting);
}
