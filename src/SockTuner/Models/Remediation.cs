namespace SockTuner.Models;

/// <summary>
/// One thing that can be done about one finding. Every action carries what it is expected to
/// achieve, what it costs, and how to tell whether it worked — an action nobody can verify is a
/// guess, however confident it sounds.
/// </summary>
/// <remarks>
/// <see cref="Changes"/> is empty for anything SockTuner does not control. That is the normal case
/// past the router, and it is deliberately not hidden: an action with no changes is guidance, and
/// the owner says who has to carry it out.
/// </remarks>
public sealed record RemediationAction(
    string Id,
    string Title,
    NetworkSegment Segment,
    RemediationOwner Owner,
    IReadOnlyList<ChangeRequest> Changes,
    string ExpectedEffect,
    string TradeOff,
    string Verification)
{
    public bool AppliesLocally => Changes.Count > 0;

    public string ChangesDisplay => Changes.Count == 0
        ? "No local change"
        : string.Join(", ", Changes.Select(change => $"{change.SettingId} = {change.ProposedValue ?? "remove"}"));
}

/// <summary>
/// What the user is actually asking the connection to deliver. Targets are the preset tier: they
/// decide whether a measurement is good enough, which is a judgement no measurement can make for
/// itself.
/// </summary>
public sealed record RemediationTargets(
    double? MinimumThroughputMbps = null,
    double? MaximumPingMs = null,
    double? MaximumJitterMs = null)
{
    public bool Any => MinimumThroughputMbps is not null || MaximumPingMs is not null || MaximumJitterMs is not null;

    /// <summary>
    /// Compares a measurement against the targets. A target with nothing measured against it is
    /// reported as unmet with the reason, never quietly counted as met.
    /// </summary>
    public TargetEvaluation Evaluate(ProbeStatistics endpoint, ThroughputResult? throughput = null)
    {
        var outcomes = new List<TargetOutcome>();
        if (MaximumPingMs is { } ping)
        {
            outcomes.Add(TargetOutcome.AtMost("Median ping", endpoint.MedianMs, ping, "ms"));
        }

        if (MaximumJitterMs is { } jitter)
        {
            outcomes.Add(TargetOutcome.AtMost("Jitter", endpoint.JitterMs, jitter, "ms"));
        }

        if (MinimumThroughputMbps is { } mbps)
        {
            outcomes.Add(TargetOutcome.AtLeast(
                "Throughput", throughput is null ? null : throughput.BitsPerSecond / 1_000_000, mbps, "Mbit/s"));
        }

        return new TargetEvaluation(outcomes.Count > 0 && outcomes.All(outcome => outcome.Met), outcomes);
    }
}

public sealed record TargetOutcome(string Metric, double? Measured, double Target, bool Met, string Summary)
{
    public static TargetOutcome AtMost(string metric, double? measured, double target, string unit) => measured is null
        ? new TargetOutcome(metric, null, target, false, $"{metric}: not measured, so the {target:0.#} {unit} target cannot be confirmed")
        : new TargetOutcome(metric, measured, target, measured <= target,
            $"{metric}: {measured:0.#} {unit} against a {target:0.#} {unit} ceiling");

    public static TargetOutcome AtLeast(string metric, double? measured, double target, string unit) => measured is null
        ? new TargetOutcome(metric, null, target, false, $"{metric}: not measured, so the {target:0.#} {unit} target cannot be confirmed")
        : new TargetOutcome(metric, measured, target, measured >= target,
            $"{metric}: {measured:0.#} {unit} against a {target:0.#} {unit} floor");
}

public sealed record TargetEvaluation(bool AllMet, IReadOnlyList<TargetOutcome> Outcomes)
{
    public IReadOnlyList<TargetOutcome> Unmet => Outcomes.Where(outcome => !outcome.Met).ToArray();
}
