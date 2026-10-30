namespace SockTuner.Models;

/// <summary>
/// One driver's total ISR and DPC time inside a single bounded capture window — the same kind of
/// figure LatencyMon reports, attributed by resolving each event's kernel routine address back to
/// the module (driver file) that owns it.
/// </summary>
public sealed record DpcIsrDriverSample(string DriverName, double ElapsedIsrMs, double ElapsedDpcMs, int EventCount)
{
    public double TotalElapsedMs => ElapsedIsrMs + ElapsedDpcMs;
}

/// <summary>The raw capture: every driver seen, ordered by nothing yet — judgement happens downstream.</summary>
public sealed record DpcIsrCaptureResult(
    IReadOnlyList<DpcIsrDriverSample> Drivers, TimeSpan Duration, string? Error,
    IReadOnlyList<DpcIsrWindow>? Windows = null, ProbeStatistics? Baseline = null);

/// <summary>One second of driver activity and ICMP replies measured inside that same ETW session.</summary>
public sealed record DpcIsrWindow(
    DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    IReadOnlyList<DpcIsrDriverSample> Drivers, ProbeStatistics Latency);

/// <summary>One logical processor's utilisation at the moment it was read.</summary>
public sealed record CpuCoreUsage(int CoreIndex, double PercentProcessorTime);

public sealed record CpuCoreUsageResult(IReadOnlyList<CpuCoreUsage> Cores, string? Error);

/// <summary>
/// Ranked interrupt activity plus a conservative repeated/coincident latency-evidence flag.
/// Cleanup eligibility is deliberately independent of this finding.
/// </summary>
public sealed record DpcIsrFinding(
    string? CulpritDriver,
    double CulpritSharePercent,
    IReadOnlyList<DpcIsrDriverSample> Ranking,
    double? JitterMsInSameWindow,
    string Reason,
    bool HasRepeatedEvidence = false);
