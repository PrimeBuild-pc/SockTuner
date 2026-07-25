using System.Net.NetworkInformation;
using System.Net.Sockets;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class NetworkDiagnosticServiceTests
{
    [Fact]
    public async Task RunAsync_PropagatesCallerCancellationWithoutSendingProbes()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new NetworkDiagnosticService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync("127.0.0.1", "127.0.0.1", 9, 3, cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_ProbesGatewayReferenceGameAndDiscoveredBoundaryConcurrently()
    {
        var path = new PathDiagnosticService((target, payload, dontFragment, ttl, timeout, token) =>
            Task.FromResult(dontFragment
                ? new PathPingResult(IPStatus.Success, target, 1)
                : ttl == 1
                    ? new PathPingResult(IPStatus.TtlExpired, "203.0.113.1", 1)
                    : new PathPingResult(IPStatus.Success, target, 2)));
        var labels = new List<string>();
        var active = 0;
        var maximumActive = 0;
        var service = new NetworkDiagnosticService(path, async (label, target, profile, token) =>
        {
            lock (labels) labels.Add(label);
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            await Task.Delay(10, token);
            Interlocked.Decrement(ref active);
            return ProbeStatistics.Calculate(label, target, [new ProbeSample(DateTimeOffset.UtcNow, 5)]);
        });
        var profile = new DiagnosticProfile("test", "Test", 3, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var report = await service.RunAsync("198.51.100.10", "192.0.2.1", null, profile, CancellationToken.None);

        Assert.Contains("First public boundary", labels);
        Assert.Equal("203.0.113.1", report.FirstPublicBoundaryProbe?.Target);
        Assert.True(maximumActive >= 4);
    }

    [Theory]
    [InlineData(IPStatus.TimedOut, DiagnosticFailureKind.TimeoutOrNoReply)]
    [InlineData(IPStatus.DestinationProhibited, DiagnosticFailureKind.IcmpBlocked)]
    [InlineData(IPStatus.DestinationHostUnreachable, DiagnosticFailureKind.Unreachable)]
    [InlineData(IPStatus.BadOption, DiagnosticFailureKind.LocalApiFailure)]
    public void ClassifyPingStatus_DistinguishesNetworkAndLocalFailures(IPStatus status, DiagnosticFailureKind expected)
    {
        Assert.Equal(expected, NetworkDiagnosticService.ClassifyPingStatus(status));
    }

    [Fact]
    public void ClassifyDnsFailure_DistinguishesResolverAndUnexpectedFailures()
    {
        Assert.Equal(DiagnosticFailureKind.DnsFailure, NetworkDiagnosticService.ClassifyDnsFailure(new SocketException()));
        Assert.Equal(DiagnosticFailureKind.DnsFailure, NetworkDiagnosticService.ClassifyDnsFailure(new ArgumentException()));
        Assert.Equal(DiagnosticFailureKind.LocalApiFailure, NetworkDiagnosticService.ClassifyDnsFailure(new InvalidOperationException()));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, DiagnosticFailureKind.ConnectionRefused)]
    [InlineData(SocketError.TimedOut, DiagnosticFailureKind.TimeoutOrNoReply)]
    [InlineData(SocketError.HostUnreachable, DiagnosticFailureKind.Unreachable)]
    [InlineData(SocketError.AccessDenied, DiagnosticFailureKind.LocalApiFailure)]
    public void ClassifySocketError_DistinguishesRefusalTimeoutReachabilityAndLocalFailure(SocketError error, DiagnosticFailureKind expected)
    {
        Assert.Equal(expected, NetworkDiagnosticService.ClassifySocketError(error));
    }
}
