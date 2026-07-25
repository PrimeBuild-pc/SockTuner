namespace SockTuner.Models;

public sealed record DiagnosticProfile(
    string Id,
    string DisplayName,
    int SampleCount,
    TimeSpan Interval,
    TimeSpan Timeout)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleCount, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleCount, 300);
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
    Error
}

public sealed record PathMtuResult(PathMtuState State, int? Mtu, string Detail);

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
    string Action);

public sealed record GamingDiagnosticReport(
    string RequestedTarget,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    DiagnosticProfile Profile,
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
    ProbeStatistics? FirstPublicBoundaryProbe = null);

internal static class DoubleArrayExtensions
{
    public static double? FirstOrDefaultNullable(this double[] values) => values.Length == 0 ? null : values[0];
    public static double? LastOrDefaultNullable(this double[] values) => values.Length == 0 ? null : values[^1];
}
