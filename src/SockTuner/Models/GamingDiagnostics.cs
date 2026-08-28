namespace SockTuner.Models;

/// <param name="PayloadBytes">
/// Bytes of padding in each probe. A game's live packets are tens to a few hundred bytes rather
/// than the 32 a default ping sends, and size is what decides whether one meets a fragmentation or
/// a shaping rule on the way. Capped below the smallest sensible path MTU so a probe cannot become
/// a fragmentation test by accident.
/// </param>
public sealed record DiagnosticProfile(
    string Id,
    string DisplayName,
    int SampleCount,
    TimeSpan Interval,
    TimeSpan Timeout,
    int PayloadBytes = 32)
{
    /// <summary>Ethernet's 1500 byte MTU less a 20 byte IPv4 and an 8 byte ICMP header.</summary>
    public const int MaximumPayloadBytes = 1472;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleCount, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleCount, 300);
        ArgumentOutOfRangeException.ThrowIfNegative(PayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PayloadBytes, MaximumPayloadBytes);
        if (Interval < TimeSpan.Zero || Interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(Interval));
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(Timeout));
    }
}

public static class DiagnosticProfiles
{
    public static IReadOnlyList<DiagnosticProfile> All { get; } =
    [
        new("quick", "Quick", 12, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
        new("standard", "Standard", 30, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)),
        new("extended", "Extended", 60, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))
    ];
}

public sealed record MonitorTarget(string Label, string Target);

public enum MonitorSampleKind
{
    Reply,
    NoReply,
    Unreachable,
    Blocked,
    LocalError
}

public sealed record MonitorSample(
    DateTimeOffset Timestamp,
    string Label,
    string Target,
    double? RoundTripTimeMs,
    MonitorSampleKind Kind,
    string? Error)
{
    public string ResultDisplay => RoundTripTimeMs is { } value ? $"{value:0.0} ms" : Error ?? Kind.ToString();
}

public sealed record MonitorTargetSummary(
    string Label,
    string Target,
    int Samples,
    int Replies,
    int NoReplies,
    int Unreachable,
    int Blocked,
    int LocalErrors,
    ProbeStatistics ReplyStatistics)
{
    public string Summary => $"{Replies}/{Samples} replies · {NoReplies} no reply · {Unreachable} unreachable · {Blocked} blocked · {LocalErrors} local errors" +
        (Replies > 0 ? $" · {ReplyStatistics.AverageMs:0.0} ms avg" : string.Empty);
}

public sealed record MonitorReport(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<MonitorSample> Samples,
    int TotalSampleCount)
{
    public bool SamplesTruncated => TotalSampleCount > Samples.Count;
    public IReadOnlyList<MonitorTargetSummary> Summaries => Samples
        .GroupBy(sample => (sample.Label, sample.Target))
        .Select(group =>
        {
            var values = group.ToArray();
            var replies = values.Where(sample => sample.Kind == MonitorSampleKind.Reply).ToArray();
            return new MonitorTargetSummary(
                group.Key.Label, group.Key.Target, values.Length, replies.Length,
                values.Count(sample => sample.Kind == MonitorSampleKind.NoReply),
                values.Count(sample => sample.Kind == MonitorSampleKind.Unreachable),
                values.Count(sample => sample.Kind == MonitorSampleKind.Blocked),
                values.Count(sample => sample.Kind == MonitorSampleKind.LocalError),
                ProbeStatistics.Calculate(group.Key.Label, group.Key.Target,
                    replies.Select(sample => new ProbeSample(sample.Timestamp, sample.RoundTripTimeMs)).ToArray()));
        }).ToArray();
}

public enum DiagnosticFailureKind
{
    TimeoutOrNoReply,
    IcmpBlocked,
    Unreachable,
    DnsFailure,
    RouteFailure,
    ConnectionRefused,
    LocalApiFailure
}

public sealed record ProbeSample(
    DateTimeOffset Timestamp,
    double? RoundTripTimeMs,
    string? Error = null,
    DiagnosticFailureKind? FailureKind = null);

public sealed record DiagnosticTimelineSample(
    string Label,
    DateTimeOffset Timestamp,
    double? RoundTripTimeMs,
    bool IsSpike,
    DiagnosticFailureKind? FailureKind,
    string? Detail);

public sealed record ProbeStatistics(
    string Label,
    string Target,
    int Sent,
    int Received,
    double LossPercent,
    double? MinimumMs,
    double? MedianMs,
    double? AverageMs,
    double? P95Ms,
    double? P99Ms,
    double? MaximumMs,
    double? JitterMs,
    IReadOnlyList<ProbeSample> Samples,
    string? Note = null)
{
    public int Lost => Sent - Received;
    public string JitterMethod => "Mean absolute consecutive RTT difference";

    /// <summary>
    /// Jitter measured over fixed one-second windows — the standard deviation of the round trips
    /// inside each second, averaged over the seconds that hold at least two replies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JitterMs"/> is the difference between consecutive samples, and that makes the
    /// figure depend on how fast the probe sent: the same line reports one number at a 100 ms
    /// interval and a different one at 500 ms. A fixed window does not care how many samples fell
    /// inside it, so two runs of different profiles against the same path are comparable, and so is
    /// a run against a game's tick budget.
    /// </para>
    /// <para>
    /// Null when no single second collected two replies — at a 500 ms interval and above that is
    /// the normal case, and a fabricated figure would be worse than none.
    /// </para>
    /// </remarks>
    public double? WindowedJitterMs
    {
        get
        {
            var windows = Samples
                .Where(sample => sample.RoundTripTimeMs.HasValue)
                .GroupBy(sample => sample.Timestamp.ToUnixTimeMilliseconds() / 1000)
                .Select(window => window.Select(sample => sample.RoundTripTimeMs!.Value).ToArray())
                .Where(values => values.Length >= 2)
                .Select(StandardDeviation)
                .ToArray();

            return windows.Length == 0 ? null : windows.Average();
        }
    }

    /// <summary>Population standard deviation: the window is the whole of what was measured in that second, not a sample of it.</summary>
    private static double StandardDeviation(double[] values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Length);
    }

    public IReadOnlyList<ProbeSample> SpikeSamples => MedianMs is not { } median
        ? []
        : Samples.Where(sample => sample.RoundTripTimeMs > median + Math.Max(10, (JitterMs ?? 0) * 3)).ToArray();
    public string Summary => Received == 0
        ? Note ?? "No replies"
        : $"{AverageMs:0.0} ms avg · {P95Ms:0.0} ms P95 · {(JitterMs.HasValue ? $"{JitterMs:0.0} ms jitter" : "jitter n/a")} · {LossPercent:0.#}% loss";

    public static ProbeStatistics Calculate(string label, string target, IReadOnlyList<ProbeSample> samples, string? note = null)
    {
        var values = samples.Where(sample => sample.RoundTripTimeMs.HasValue)
            .Select(sample => sample.RoundTripTimeMs!.Value)
            .Order()
            .ToArray();
        var sent = samples.Count;
        var received = values.Length;
        var chronological = samples.Where(sample => sample.RoundTripTimeMs.HasValue)
            .Select(sample => sample.RoundTripTimeMs!.Value)
            .ToArray();
        double? jitter = chronological.Length < 2
            ? null
            : chronological.Zip(chronological.Skip(1), (left, right) => Math.Abs(right - left)).Average();

        return new ProbeStatistics(
            label,
            target,
            sent,
            received,
            sent == 0 ? 0 : (sent - received) * 100d / sent,
            values.FirstOrDefaultNullable(),
            Percentile(values, 0.5),
            values.Length == 0 ? null : values.Average(),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values.LastOrDefaultNullable(),
            jitter,
            samples,
            note);
    }

    private static double? Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            return null;
        }

        var position = (sortedValues.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? sortedValues[lower]
            : sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * (position - lower));
    }
}

public sealed record AdapterCounterSample(string AdapterId, string AdapterName, AdapterCounters Counters);

public sealed record AdapterCounterDelta(
    string AdapterId,
    string AdapterName,
    long? ReceivedBytes,
    long? SentBytes,
    long? ReceiveErrors,
    long? ReceiveDiscards,
    long? SendErrors,
    long? SendDiscards)
{
    public string Summary => $"{AdapterName}: RX {Format(ReceivedBytes)}, TX {Format(SentBytes)}, " +
        $"issues {Sum(ReceiveErrors, ReceiveDiscards, SendErrors, SendDiscards)}";
    private static string Format(long? value) => value is null ? "reset/unavailable" : AdapterInfo.FormatByteCount(value.Value);
    private static string Sum(params long?[] values) => values.Any(value => value is null)
        ? "reset/unavailable"
        : values.Sum(value => value!.Value).ToString();
}

public sealed record RouteHop(int TimeToLive, string Address, double? RoundTripTimeMs, string State);

public sealed record RouteSample(
    DateTimeOffset Timestamp,
    IReadOnlyList<RouteHop> Hops,
    string? Error,
    DiagnosticFailureKind? FailureKind = null)
{
    public string HopsDisplay => Hops.Count == 0 ? "No responding hops" : string.Join(" → ", Hops.Select(hop => $"{hop.TimeToLive}:{hop.Address}"));
}

public enum PathMtuState
{
    Discovered,
    IcmpBlockedOrInconclusive,
    UnsupportedAddressFamily,
    Error,

    /// <summary>
    /// Oversized packets are dropped without the "fragmentation needed" reply that path MTU
    /// discovery depends on. The size was still found, but only because the same packet was retried
    /// fragmentable — the sender is left guessing, which is why connections hang rather than fail.
    /// </summary>
    IcmpBlackHole
}

public sealed record PathMtuResult(PathMtuState State, int? Mtu, string Detail)
{
    public bool HasMtu => Mtu is > 0 && State is PathMtuState.Discovered or PathMtuState.IcmpBlackHole;
}

public sealed record DnsMeasurement(
    string Host,
    TimeSpan Duration,
    IReadOnlyList<string> Addresses,
    string? Error,
    DiagnosticFailureKind? FailureKind = null)
{
    public string Summary => Error is null
        ? $"{Duration.TotalMilliseconds:0.0} ms · {Addresses.Count} address(es)"
        : $"Failed [{FailureKind ?? DiagnosticFailureKind.LocalApiFailure}]: {Error}";
}

public sealed record ConnectionMeasurement(
    string Host,
    int Port,
    TimeSpan? Duration,
    string? Error,
    DiagnosticFailureKind? FailureKind = null)
{
    public string Summary => Error is null
        ? $"Connected in {Duration?.TotalMilliseconds:0.0} ms"
        : $"Not verified [{FailureKind ?? DiagnosticFailureKind.LocalApiFailure}]: {Error}";
}

public enum DiagnosticScope
{
    LocalPc,
    Lan,
    RouterOrAccess,
    IspOrRouting,
    GameEndpoint,
    Dns,
    General
}

public enum DiagnosticConfidence
{
    Low,
    Medium,
    High
}

public sealed record DiagnosticFinding(
    DiagnosticScope Scope,
    DiagnosticConfidence Confidence,
    string Title,
    string Evidence,
    string Action,
    NetworkSegment Segment = NetworkSegment.Unknown,
    RemediationOwner Owner = RemediationOwner.PresetOrManual)
{
    public string OwnerDisplay => Owner switch
    {
        RemediationOwner.Automatic => "Automatic",
        RemediationOwner.PresetOrManual => "Preset or manual",
        RemediationOwner.Router => "Router",
        RemediationOwner.OutOfScope => "Out of scope (ISP or infrastructure)",
        _ => Owner.ToString()
    };
    public string SegmentDisplay => Segment.ToString();
}

public enum DiagnosticLoadCondition
{
    Unspecified = 1,
    Idle = 2,
    UnderLoad = 3
}

/// <param name="Game">
/// The tick rate the run was judged against. Carried in the report because the same numbers mean
/// different things to different games, so a result sent to a provider without it is a measurement
/// with its interpretation removed.
/// </param>
public sealed record GamingDiagnosticReport(
    string RequestedTarget,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    DiagnosticProfile Profile,
    DiagnosticLoadCondition LoadCondition,
    ProbeStatistics Gateway,
    ProbeStatistics Reference,
    ProbeStatistics GameTarget,
    DnsMeasurement Dns,
    ConnectionMeasurement? Connection,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<RouteSample>? RouteSamples = null,
    string? FirstPublicBoundary = null,
    PathMtuResult? PathMtu = null,
    IReadOnlyList<AdapterCounterDelta>? CounterDeltas = null,
    ProbeStatistics? FirstPublicBoundaryProbe = null,
    GameProfile? Game = null);

internal static class DoubleArrayExtensions
{
    public static double? FirstOrDefaultNullable(this double[] values) => values.Length == 0 ? null : values[0];
    public static double? LastOrDefaultNullable(this double[] values) => values.Length == 0 ? null : values[^1];
}
