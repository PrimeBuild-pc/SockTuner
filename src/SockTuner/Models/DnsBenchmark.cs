namespace SockTuner.Models;

/// <summary>One resolver to measure. <paramref name="InUse"/> marks a resolver this machine is already configured to use.</summary>
public sealed record DnsResolverCandidate(string Name, string Address, bool InUse = false)
{
    public string Display => InUse ? $"{Name} ({Address}) — in use" : $"{Name} ({Address})";
}

/// <summary>
/// One resolver's results. A resolver that answered nothing is reported with its error rather than
/// dropped, because "did not answer" is the most important thing a benchmark can say about a
/// resolver someone is considering.
/// </summary>
public sealed record DnsResolverResult(
    DnsResolverCandidate Resolver,
    int Queries,
    int Answered,
    double? MedianMs,
    double? AverageMs,
    double? MinimumMs,
    double? MaximumMs,
    string? Error = null)
{
    public double LossPercent => Queries == 0 ? 0 : (Queries - Answered) * 100d / Queries;

    /// <summary>Ranks on the median and refuses to rank a resolver that did not answer reliably.</summary>
    public bool Usable => Error is null && Answered > 0 && LossPercent <= 20;

    public string Summary => Error is not null
        ? $"Failed: {Error}"
        : Answered == 0
            ? $"No reply to any of {Queries} quer{(Queries == 1 ? "y" : "ies")}"
            : $"median {MedianMs:0.0} ms · min {MinimumMs:0.0} · max {MaximumMs:0.0} · {Answered}/{Queries} answered"
                + (LossPercent > 0 ? $" ({LossPercent:0.#}% lost)" : string.Empty);
}

public sealed record DnsBenchmarkReport(
    IReadOnlyList<DnsResolverResult> Results,
    DnsResolverResult? Fastest,
    DnsResolverResult? Current,
    string Verdict)
{
    /// <summary>
    /// How much faster the best measured resolver is than the one in use. Null when there is nothing
    /// to compare, which is the honest answer rather than an implied improvement.
    /// </summary>
    public double? ImprovementMs => Fastest?.MedianMs is { } fast && Current?.MedianMs is { } current
        ? current - fast
        : null;
}
