using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Collection layer: measures the same latency probe idle and again while one direction is
/// saturated. That difference is bufferbloat — the queue in front of the slowest link — and it
/// cannot be inferred from a speed test or from registry state.
/// </summary>
public sealed class LoadedLatencyProbe
{
    /// <summary>Time given to the transfer to fill the queue before the loaded measurement starts.</summary>
    public static readonly TimeSpan DefaultWarmUp = TimeSpan.FromSeconds(3);

    // Upper bound on how long a load may run; the probe stops it as soon as the measurement closes,
    // so this only guards against a stuck measurement leaving a transfer running.
    private static readonly TimeSpan LoadCeiling = TimeSpan.FromMinutes(2);

    private readonly Func<DiagnosticProfile, CancellationToken, Task<ProbeStatistics>> _latency;
    private readonly Func<TransferDirection, TimeSpan, CancellationToken, Task<ThroughputResult>> _load;

    internal LoadedLatencyProbe(
        Func<DiagnosticProfile, CancellationToken, Task<ProbeStatistics>> latency,
        Func<TransferDirection, TimeSpan, CancellationToken, Task<ThroughputResult>> load)
    {
        _latency = latency;
        _load = load;
    }

    /// <summary>Binds the probe to a real latency target and throughput endpoint.</summary>
    public static LoadedLatencyProbe For(
        string latencyTarget,
        string throughputEndpoint,
        int streams,
        Func<string, string, DiagnosticProfile, CancellationToken, Task<ProbeStatistics>> probe,
        ThroughputProbe throughput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(latencyTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(throughputEndpoint);
        return new LoadedLatencyProbe(
            (profile, token) => probe("Loaded latency", latencyTarget, profile, token),
            (direction, duration, token) => throughput.RunAsync(throughputEndpoint, direction, streams, duration, token));
    }

    public async Task<LoadedLatencyResult> RunAsync(
        TransferDirection direction,
        DiagnosticProfile profile,
        TimeSpan warmUp,
        CancellationToken cancellationToken)
    {
        profile.Validate();
        if (warmUp < TimeSpan.Zero || warmUp > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(warmUp));
        }

        var idle = await _latency(profile, cancellationToken);

        using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = _load(direction, LoadCeiling, stopLoad.Token);
        try
        {
            await Task.Delay(warmUp, cancellationToken);
            var loaded = await _latency(profile, cancellationToken);
            await stopLoad.CancelAsync();
            return new LoadedLatencyResult(direction, idle, loaded, await load);
        }
        finally
        {
            // Whatever happened above, the transfer must not outlive this call.
            await stopLoad.CancelAsync();
            await Await(load);
        }
    }

    private static async Task Await(Task<ThroughputResult> load)
    {
        try
        {
            await load;
        }
        catch (OperationCanceledException)
        {
            // The load was stopped on purpose; its own failure is not this measurement's failure.
        }
    }
}
