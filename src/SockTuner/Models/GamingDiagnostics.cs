namespace SockTuner.Models;

public sealed record ProbeSample(DateTimeOffset Timestamp, double? RoundTripTimeMs, string? Error = null);

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

public sealed record DnsMeasurement(string Host, TimeSpan Duration, IReadOnlyList<string> Addresses, string? Error)
{
    public string Summary => Error is null
        ? $"{Duration.TotalMilliseconds:0.0} ms · {Addresses.Count} address(es)"
        : $"Failed: {Error}";
}

public sealed record ConnectionMeasurement(string Host, int Port, TimeSpan? Duration, string? Error)
{
    public string Summary => Error is null ? $"Connected in {Duration?.TotalMilliseconds:0.0} ms" : $"Not verified: {Error}";
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
    ProbeStatistics Gateway,
    ProbeStatistics Reference,
    ProbeStatistics GameTarget,
    DnsMeasurement Dns,
    ConnectionMeasurement? Connection,
    IReadOnlyList<DiagnosticFinding> Findings);

internal static class DoubleArrayExtensions
{
    public static double? FirstOrDefaultNullable(this double[] values) => values.Length == 0 ? null : values[0];
    public static double? LastOrDefaultNullable(this double[] values) => values.Length == 0 ? null : values[^1];
}
