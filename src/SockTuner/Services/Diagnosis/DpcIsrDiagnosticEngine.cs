using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Diagnosis layer: judges a DPC/ISR capture against what the same run actually measured, instead
/// of a fixed microsecond threshold table — the same philosophy <c>PlayabilityAnalyzer</c> already
/// applies to jitter. Never runs on its own: it always rides an existing network/gaming diagnostic,
/// so SockTuner never becomes a general system-health scanner independent of a network context.
/// </summary>
/// <remarks>
/// This layer only names the culprit and what was measured. Whether an uninstall is worth
/// suggesting is a separate, factual question — does a genuinely outdated duplicate of this exact
/// driver sit in the store? — answered by <c>DriverStoreDuplicateFinder</c> against data this
/// engine never reads, so it is resolved in <c>Services.Remediation.DpcIsrGuidance</c> instead.
/// </remarks>
public static class DpcIsrDiagnosticEngine
{
    public static DpcIsrFinding Analyze(DpcIsrCaptureResult capture, double? jitterMsInSameWindow)
    {
        if (capture.Error is { } error)
        {
            return new(null, 0, [], jitterMsInSameWindow, error);
        }

        var ranking = capture.Drivers.OrderByDescending(driver => driver.TotalElapsedMs).ToArray();
        if (ranking.Length == 0)
        {
            return new(null, 0, ranking, jitterMsInSameWindow,
                "No driver used enough ISR or DPC time during the capture to report.");
        }

        var culprit = ranking[0];
        var windowMs = capture.Duration.TotalMilliseconds;
        var share = windowMs <= 0 ? 0 : culprit.TotalElapsedMs / windowMs * 100;

        var jitterClause = jitterMsInSameWindow is { } jitter
            ? $" The same check measured {jitter:0.#} ms of jitter right before this capture; a share this size is "
                + "worth weighing against that figure yourself — this is a correlation across one run, not a proof."
            : " No jitter figure from this run was available to compare it against.";

        var reason = $"{culprit.DriverName} held {culprit.TotalElapsedMs:0.##} ms of ISR/DPC time "
            + $"({share:0.#}% of the {capture.Duration.TotalSeconds:0.#}s capture) across {culprit.EventCount} event(s)."
            + jitterClause;

        return new(culprit.DriverName, share, ranking, jitterMsInSameWindow, reason);
    }
}
