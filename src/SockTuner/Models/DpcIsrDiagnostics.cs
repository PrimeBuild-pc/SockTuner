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
public sealed record DpcIsrCaptureResult(IReadOnlyList<DpcIsrDriverSample> Drivers, TimeSpan Duration, string? Error);

/// <summary>One logical processor's utilisation at the moment it was read.</summary>
public sealed record CpuCoreUsage(int CoreIndex, double PercentProcessorTime);

public sealed record CpuCoreUsageResult(IReadOnlyList<CpuCoreUsage> Cores, string? Error);

/// <summary>
/// The capture judged against what the same diagnostic run actually measured: one named culprit
/// (the worst combined ISR+DPC share of the window), the full ranking as supporting detail, and
/// whether it is eligible for the uninstall suggestion specifically — never on the strength of one
/// instantaneous spike, and never for a driver class SockTuner has decided not to second-guess.
/// </summary>
public sealed record DpcIsrFinding(
    string? CulpritDriver,
    double CulpritSharePercent,
    IReadOnlyList<DpcIsrDriverSample> Ranking,
    double? JitterMsInSameWindow,
    string Reason);
