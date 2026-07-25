using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class NetworkDiagnosticService
{
    private const string ReferenceTarget = "1.1.1.1";
    private readonly GamingDiagnosisAnalyzer _analyzer = new();
    private readonly PathDiagnosticService _pathDiagnostics = new();

    public Task<GamingDiagnosticReport> RunAsync(
        string target,
        string? gateway,
        int? tcpPort,
        int sampleCount,
        CancellationToken cancellationToken) => RunAsync(
            target,
            gateway,
            tcpPort,
            new DiagnosticProfile("custom", "Custom", sampleCount, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            cancellationToken);

    public async Task<GamingDiagnosticReport> RunAsync(
        string target,
        string? gateway,
        int? tcpPort,
        DiagnosticProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        profile.Validate();
        if (tcpPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var dns = await MeasureDnsAsync(target, cancellationToken);
        var resolvedTarget = dns.Addresses.FirstOrDefault() ?? target;

        var gatewayTask = string.IsNullOrWhiteSpace(gateway)
            ? Task.FromResult(ProbeStatistics.Calculate("Gateway", "Not detected", [], "No active default gateway detected"))
            : ProbeAsync("Gateway", gateway, profile, cancellationToken);
        var referenceTask = ProbeAsync("Reference", ReferenceTarget, profile, cancellationToken);
        var gameTask = ProbeAsync("Game endpoint", resolvedTarget, profile, cancellationToken);
        var connectionTask = tcpPort.HasValue
            ? MeasureConnectionAsync(target, tcpPort.Value, cancellationToken)
            : Task.FromResult<ConnectionMeasurement?>(null);
        var pathTask = _pathDiagnostics.RunAsync(resolvedTarget, cancellationToken);

        await Task.WhenAll(gatewayTask, referenceTask, gameTask, connectionTask, pathTask);
        var gatewayResult = await gatewayTask;
        var referenceResult = await referenceTask;
        var gameResult = await gameTask;
        stopwatch.Stop();

        return new GamingDiagnosticReport(
            target,
            startedAt,
            stopwatch.Elapsed,
            gatewayResult,
            referenceResult,
            gameResult,
            dns,
            await connectionTask,
            _analyzer.Analyze(gatewayResult, referenceResult, gameResult, dns),
            (await pathTask).Routes,
            (await pathTask).FirstPublicBoundary,
            (await pathTask).Mtu);
    }

    private static async Task<ProbeStatistics> ProbeAsync(
        string label,
        string target,
        DiagnosticProfile profile,
        CancellationToken cancellationToken)
    {
        var samples = new List<ProbeSample>(profile.SampleCount);
        using var ping = new Ping();
        var payload = new byte[32];

        for (var index = 0; index < profile.SampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = DateTimeOffset.Now;
            try
            {
                var reply = await ping.SendPingAsync(target, profile.Timeout, payload, new PingOptions(64, false), cancellationToken);
                samples.Add(reply.Status == IPStatus.Success
                    ? new ProbeSample(timestamp, reply.RoundtripTime)
                    : new ProbeSample(timestamp, null, reply.Status.ToString()));
            }
            catch (PingException exception)
            {
                samples.Add(new ProbeSample(timestamp, null, exception.InnerException?.Message ?? exception.Message));
            }

            if (index + 1 < profile.SampleCount)
            {
                await Task.Delay(profile.Interval, cancellationToken);
            }
        }

        return ProbeStatistics.Calculate(label, target, samples);
    }

    private static async Task<DnsMeasurement> MeasureDnsAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return new DnsMeasurement(host, TimeSpan.Zero, [address.ToString()], null);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            stopwatch.Stop();
            return new DnsMeasurement(host, stopwatch.Elapsed, addresses.Select(item => item.ToString()).ToArray(), null);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            stopwatch.Stop();
            return new DnsMeasurement(host, stopwatch.Elapsed, [], exception.Message);
        }
    }

    private static async Task<ConnectionMeasurement?> MeasureConnectionAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeout.Token);
            stopwatch.Stop();
            return new ConnectionMeasurement(host, port, stopwatch.Elapsed, null);
        }
        catch (SocketException exception)
        {
            stopwatch.Stop();
            return new ConnectionMeasurement(host, port, null, exception.Message);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ConnectionMeasurement(host, port, null, $"Connection timed out: {exception.Message}");
        }
    }
}
