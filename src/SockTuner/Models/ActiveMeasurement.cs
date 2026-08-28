namespace SockTuner.Models;

/// <summary>Which way the measured bytes are moving. Bufferbloat is per direction: an upload can be awful while the download is clean.</summary>
public enum TransferDirection
{
    Download,
    Upload
}

/// <summary>
/// One throughput run against a user-chosen endpoint. A run stopped early still carries a valid
/// rate — bytes over elapsed time — so <see cref="Completed"/> records whether the full window ran
/// rather than the result being discarded.
/// </summary>
public sealed record ThroughputResult(
    string Endpoint,
    TransferDirection Direction,
    int Streams,
    long Bytes,
    TimeSpan Duration,
    bool Completed,
    string? Error = null,
    DiagnosticFailureKind? FailureKind = null)
{
    public double BitsPerSecond => Duration <= TimeSpan.Zero ? 0 : Bytes * 8d / Duration.TotalSeconds;

    public string Summary => Error is not null
        ? $"Failed [{FailureKind ?? DiagnosticFailureKind.LocalApiFailure}]: {Error}"
        : $"{FormatRate(BitsPerSecond)} · {Streams} stream(s) · {Duration.TotalSeconds:0.0} s"
            + (Completed ? string.Empty : " · stopped early");

    public static string FormatRate(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000:0.00} Gbit/s",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000:0.0} Mbit/s",
        >= 1_000 => $"{bitsPerSecond / 1_000:0.0} kbit/s",
        _ => $"{bitsPerSecond:0} bit/s"
    };
}

/// <summary>
/// Latency increase under load, on the Waveform/dslreports scale. The grade describes how much the
/// queue in front of the slowest link grows, not the speed of the link.
/// </summary>
public enum BufferbloatGrade
{
    APlus,
    A,
    B,
    C,
    D,
    F
}

/// <summary>
/// One direction of a bufferbloat run: the same latency probe measured idle and again while a
/// controlled transfer saturates that direction.
/// </summary>
public sealed record LoadedLatencyResult(
    TransferDirection Direction,
    ProbeStatistics Idle,
    ProbeStatistics Loaded,
    ThroughputResult Load)
{
    /// <summary>
    /// Median rather than average: a handful of very large samples would otherwise decide the grade
    /// on their own, and the queue depth is what the typical packet actually meets.
    /// </summary>
    public double? LatencyIncreaseMs => Idle.MedianMs is { } idle && Loaded.MedianMs is { } loaded
        ? loaded - idle
        : null;

    public double? JitterIncreaseMs => Idle.JitterMs is { } idle && Loaded.JitterMs is { } loaded
        ? loaded - idle
        : null;

    public double LossIncreasePercent => Loaded.LossPercent - Idle.LossPercent;

    public string Summary => LatencyIncreaseMs is { } increase
        ? $"{Direction}: idle {Idle.MedianMs:0.0} ms → loaded {Loaded.MedianMs:0.0} ms (+{increase:0.0} ms) at {Load.Summary}"
        : $"{Direction}: not measurable ({Idle.Summary} / {Loaded.Summary})";
}

/// <summary>
/// How much of the local link the machine itself was using over a sampling window. Without this,
/// another process saturating the link looks exactly like an ISP fault.
/// </summary>
public sealed record LinkUtilization(
    string AdapterId,
    string AdapterName,
    double ReceiveBitsPerSecond,
    double SendBitsPerSecond,
    long LinkSpeedBitsPerSecond)
{
    public double PeakBitsPerSecond => Math.Max(ReceiveBitsPerSecond, SendBitsPerSecond);

    /// <summary>Zero when the link speed is unknown, so an unknown speed never reads as saturation.</summary>
    public double PeakPercentOfLink => LinkSpeedBitsPerSecond <= 0
        ? 0
        : PeakBitsPerSecond * 100d / LinkSpeedBitsPerSecond;

    public bool LinkSpeedKnown => LinkSpeedBitsPerSecond > 0;

    public string Summary => $"{AdapterName}: ↓{ThroughputResult.FormatRate(ReceiveBitsPerSecond)} "
        + $"↑{ThroughputResult.FormatRate(SendBitsPerSecond)}"
        + (LinkSpeedKnown ? $" · {PeakPercentOfLink:0.#}% of link" : " · link speed unknown");

    /// <summary>
    /// Turns a counter delta into a rate. A delta of <c>null</c> means the counter reset or was
    /// unavailable, and is reported as zero rather than as a guess.
    /// </summary>
    public static LinkUtilization Calculate(AdapterCounterDelta delta, TimeSpan elapsed, long linkSpeedBitsPerSecond)
    {
        var seconds = elapsed.TotalSeconds;
        return new LinkUtilization(
            delta.AdapterId,
            delta.AdapterName,
            Rate(delta.ReceivedBytes, seconds),
            Rate(delta.SentBytes, seconds),
            linkSpeedBitsPerSecond);
    }

    private static double Rate(long? bytes, double seconds) => bytes is null || seconds <= 0 ? 0 : bytes.Value * 8d / seconds;
}

public enum StabilityEventKind
{
    /// <summary>Consecutive probes with no reply: the path stopped carrying traffic for a while.</summary>
    LossBurst,

    /// <summary>Consecutive replies far above the run's own baseline.</summary>
    LatencySpike
}

/// <summary>
/// A stretch of consecutive bad samples in a long run. Episodes are what distinguishes an
/// intermittent fault from steady mediocrity, and a spot test cannot see them at all.
/// </summary>
public sealed record StabilityEpisode(
    StabilityEventKind Kind,
    string Label,
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int Samples,
    double? PeakMs)
{
    public TimeSpan Duration => EndedAt - StartedAt;

    public string Summary => Kind == StabilityEventKind.LossBurst
        ? $"{StartedAt:T} {Label}: {Samples} consecutive probe(s) with no reply over {Duration.TotalSeconds:0.#} s"
        : $"{StartedAt:T} {Label}: {Samples} sample(s) up to {PeakMs:0.0} ms over {Duration.TotalSeconds:0.#} s";
}

public sealed record StabilityReport(
    TimeSpan Window,
    IReadOnlyList<StabilityEpisode> Episodes,
    IReadOnlyList<MonitorTargetSummary> Targets,
    bool SamplesTruncated,
    string Verdict)
{
    public bool Stable => Episodes.Count == 0;

    /// <summary>
    /// The share of the measured window the line was actually answering, taken from the target that
    /// lost the most time rather than averaged across targets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A share of time, not of packets, and the distinction is the whole point: a line that answers
    /// 99.98 % of probes but goes silent for four seconds twice an evening is precisely the fault a
    /// game notices and a packet average hides.
    /// </para>
    /// <para>
    /// Its resolution is the probe interval — a hole shorter than one interval cannot be seen at
    /// all — so this is a floor on the damage, never a clean bill of health.
    /// </para>
    /// </remarks>
    public double? AvailabilityPercent
    {
        get
        {
            if (Window <= TimeSpan.Zero)
            {
                return null;
            }

            var worst = Episodes
                .Where(episode => episode.Kind == StabilityEventKind.LossBurst)
                .GroupBy(episode => (episode.Label, episode.Target))
                .Select(target => target.Sum(episode => episode.Duration.TotalSeconds))
                .DefaultIfEmpty(0)
                .Max();

            return Math.Clamp(100 - (worst * 100 / Window.TotalSeconds), 0, 100);
        }
    }
}
