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
    private readonly PathDiagnosticService _pathDiagnostics;
    private readonly Func<string, string, DiagnosticProfile, CancellationToken, Task<ProbeStatistics>> _probe;

    public NetworkDiagnosticService() : this(new PathDiagnosticService(), ProbeAsync) { }

    internal NetworkDiagnosticService(
        PathDiagnosticService pathDiagnostics,
        Func<string, string, DiagnosticProfile, CancellationToken, Task<ProbeStatistics>> probe)
    {
        _pathDiagnostics = pathDiagnostics;
        _probe = probe;
    }

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

        var initialRoute = await _pathDiagnostics.TraceAsync(resolvedTarget, cancellationToken);
        var firstPublicBoundary = PathDiagnosticService.FindFirstPublicBoundary([initialRoute]);
        var gatewayTask = string.IsNullOrWhiteSpace(gateway)
            ? Task.FromResult(ProbeStatistics.Calculate("Gateway", "Not detected", [], "No active default gateway detected"))
            : _probe("Gateway", gateway, profile, cancellationToken);
        var referenceTask = _probe("Reference", ReferenceTarget, profile, cancellationToken);
        var gameTask = _probe("Game endpoint", resolvedTarget, profile, cancellationToken);
        var boundaryTask = string.IsNullOrWhiteSpace(firstPublicBoundary)
            ? Task.FromResult<ProbeStatistics?>(null)
            : ProbeBoundaryAsync(firstPublicBoundary, profile, cancellationToken);
        var connectionTask = tcpPort.HasValue
            ? MeasureConnectionAsync(target, tcpPort.Value, cancellationToken)
            : Task.FromResult<ConnectionMeasurement?>(null);
        var pathTask = _pathDiagnostics.RunAsync(resolvedTarget, cancellationToken);

        await Task.WhenAll(gatewayTask, referenceTask, gameTask, boundaryTask, connectionTask, pathTask);
        var gatewayResult = await gatewayTask;
        var referenceResult = await referenceTask;
        var gameResult = await gameTask;
        var pathResult = await pathTask;
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
            new[] { initialRoute }.Concat(pathResult.Routes).ToArray(),
            firstPublicBoundary,
            pathResult.Mtu,
            null,
            await boundaryTask);
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
                    : new ProbeSample(timestamp, null, reply.Status.ToString(), ClassifyPingStatus(reply.Status)));
            }
            catch (PingException exception)
            {
                samples.Add(new ProbeSample(timestamp, null, exception.InnerException?.Message ?? exception.Message, DiagnosticFailureKind.LocalApiFailure));
            }

            if (index + 1 < profile.SampleCount)
            {
                await Task.Delay(profile.Interval, cancellationToken);
            }
        }

        return ProbeStatistics.Calculate(label, target, samples);
    }

    private async Task<ProbeStatistics?> ProbeBoundaryAsync(
        string target, DiagnosticProfile profile, CancellationToken cancellationToken) =>
        await _probe("First public boundary", target, profile, cancellationToken);

    internal static DiagnosticFailureKind ClassifyPingStatus(IPStatus status) => status switch
    {
        IPStatus.TimedOut => DiagnosticFailureKind.TimeoutOrNoReply,
        IPStatus.DestinationProhibited => DiagnosticFailureKind.IcmpBlocked,
        IPStatus.DestinationUnreachable or IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationHostUnreachable or IPStatus.DestinationProtocolUnreachable
            or IPStatus.DestinationPortUnreachable or IPStatus.BadDestination => DiagnosticFailureKind.Unreachable,
        _ => DiagnosticFailureKind.LocalApiFailure
    };

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
            return new DnsMeasurement(host, stopwatch.Elapsed, [], exception.Message, ClassifyDnsFailure(exception));
        }
    }

    internal static DiagnosticFailureKind ClassifyDnsFailure(Exception exception) => exception is SocketException or ArgumentException
        ? DiagnosticFailureKind.DnsFailure
        : DiagnosticFailureKind.LocalApiFailure;

    internal static DiagnosticFailureKind ClassifySocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => DiagnosticFailureKind.ConnectionRefused,
        SocketError.TimedOut => DiagnosticFailureKind.TimeoutOrNoReply,
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => DiagnosticFailureKind.Unreachable,
        _ => DiagnosticFailureKind.LocalApiFailure
    };

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
            var kind = ClassifySocketError(exception.SocketErrorCode);
            return new ConnectionMeasurement(host, port, null, exception.Message, kind);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ConnectionMeasurement(host, port, null, $"Connection timed out: {exception.Message}", DiagnosticFailureKind.TimeoutOrNoReply);
        }
    }
}
