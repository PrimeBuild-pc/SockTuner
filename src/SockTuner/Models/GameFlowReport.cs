namespace SockTuner.Models;

/// <summary>
/// A capture-derived report of one game's traffic, as produced by an external analyzer.
/// </summary>
/// <remarks>
/// This is the shape SockTuner reads, not the file format itself. It carries only what can be acted
/// on: which server the game was actually talking to, how the flow behaved, and what the game's own
/// tick rate is — the number that decides whether a given jitter figure matters.
/// </remarks>
public sealed record GameFlowReport(
    string Game,
    DateTimeOffset CapturedAt,
    string? RemoteAddress,
    string? RemoteHost,
    string? RemotePort,
    string? RegionHint,
    double? ExpectedTickMs,
    GameFlowStatistics? Flow,
    IReadOnlyDictionary<string, string> Scores)
{
    /// <summary>The endpoint a diagnosis should be pointed at: the server the game actually used.</summary>
    public string? DiagnosticTarget => string.IsNullOrWhiteSpace(RemoteHost) ? RemoteAddress : RemoteHost;

    public string Summary => Flow is null
        ? $"{Game}: no flow statistics in the report."
        : $"{Game} against {DiagnosticTarget ?? "an unknown server"}"
            + (string.IsNullOrWhiteSpace(RegionHint) ? string.Empty : $" ({RegionHint})")
            + $" — {Flow.Summary}";
}

public sealed record GameFlowStatistics(
    int PacketCount,
    double DurationSeconds,
    double PacketsPerSecond,
    double AverageDeltaMs,
    double MaximumDeltaMs,
    double AverageJitterMs,
    double MaximumJitterMs,
    double BurstRatio,
    double SpikeRatio)
{
    public string Summary =>
        $"{PacketCount} packets over {DurationSeconds:0.#} s at {PacketsPerSecond:0} pkt/s, "
        + $"jitter avg {AverageJitterMs:0.0} ms / max {MaximumJitterMs:0.0} ms, "
        + $"gaps up to {MaximumDeltaMs:0.0} ms";
}
