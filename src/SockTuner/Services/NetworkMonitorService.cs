using System.Diagnostics;
using System.Net.NetworkInformation;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class NetworkMonitorService
{
    private readonly Func<MonitorTarget, TimeSpan, CancellationToken, Task<MonitorSample>> _probe;

    public NetworkMonitorService() : this(ProbeAsync) { }

    internal NetworkMonitorService(Func<MonitorTarget, TimeSpan, CancellationToken, Task<MonitorSample>> probe) =>
        _probe = probe;

    public async Task<MonitorReport> RunAsync(
        IReadOnlyList<MonitorTarget> targets,
        TimeSpan duration,
        TimeSpan interval,
        TimeSpan timeout,
        int maximumSamples,
        IProgress<MonitorSample>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targets.Count, 1);
        if (targets.Any(target => string.IsNullOrWhiteSpace(target.Label) || string.IsNullOrWhiteSpace(target.Target)))
            throw new ArgumentException("Monitor targets require a label and target.", nameof(targets));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (interval < TimeSpan.FromMilliseconds(10) || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSamples, targets.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSamples, 100_000);

        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = DateTimeOffset.Now;
        var elapsed = Stopwatch.StartNew();
        var samples = new Queue<MonitorSample>(maximumSamples);
        var totalSampleCount = 0;
        while (elapsed.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(targets.Select(target => _probe(target, timeout, cancellationToken)));
            totalSampleCount += results.Length;
            foreach (var sample in results)
            {
                if (samples.Count == maximumSamples) samples.Dequeue();
                samples.Enqueue(sample);
                progress?.Report(sample);
            }

            var remaining = duration - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining < interval ? remaining : interval, cancellationToken);
        }

        elapsed.Stop();
        return new MonitorReport(startedAt, elapsed.Elapsed, samples.ToArray(), totalSampleCount);
    }

    private static async Task<MonitorSample> ProbeAsync(MonitorTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Now;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target.Target, timeout, new byte[32], new PingOptions(64, false), cancellationToken);
            return reply.Status == IPStatus.Success
                ? new(timestamp, target.Label, target.Target, reply.RoundtripTime, MonitorSampleKind.Reply, null)
                : new(timestamp, target.Label, target.Target, null, Classify(reply.Status), reply.Status.ToString());
        }
        catch (PingException exception)
        {
            return new(timestamp, target.Label, target.Target, null, MonitorSampleKind.LocalError, exception.InnerException?.Message ?? exception.Message);
        }
    }

    internal static MonitorSampleKind Classify(IPStatus status) => status switch
    {
        IPStatus.Success => MonitorSampleKind.Reply,
        IPStatus.TimedOut => MonitorSampleKind.NoReply,
        IPStatus.DestinationProhibited => MonitorSampleKind.Blocked,
        IPStatus.DestinationUnreachable or IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationHostUnreachable or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable
            or IPStatus.BadDestination => MonitorSampleKind.Unreachable,
        _ => MonitorSampleKind.LocalError
    };
}
