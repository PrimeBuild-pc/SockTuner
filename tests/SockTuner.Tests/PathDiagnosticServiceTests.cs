using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class PathDiagnosticServiceTests
{
    [Fact]
    public async Task RunAsync_RepeatsRouteFindsFirstPublicBoundaryAndDiscoversMtu()
    {
        var service = new PathDiagnosticService((target, payload, dontFragment, ttl, timeout, token) =>
        {
            if (dontFragment)
                return Task.FromResult(new PathPingResult(payload <= 1400 ? IPStatus.Success : IPStatus.PacketTooBig, target, 1));
            return Task.FromResult(ttl switch
            {
                1 => new PathPingResult(IPStatus.TtlExpired, "192.168.1.1", 1),
                2 => new PathPingResult(IPStatus.TtlExpired, "203.0.113.1", 5),
                _ => new PathPingResult(IPStatus.Success, target, 10)
            });
        });

        var result = await service.RunAsync("198.51.100.10", CancellationToken.None);

        Assert.Equal(3, result.Routes.Count);
        Assert.Equal("203.0.113.1", result.FirstPublicBoundary);
        Assert.Equal((PathMtuState.Discovered, 1428), (result.Mtu.State, result.Mtu.Mtu));
    }

    [Fact]
    public async Task DiscoverMtu_NoReply_RemainsInconclusive()
    {
        var service = new PathDiagnosticService((target, payload, fragment, ttl, timeout, token) =>
            Task.FromResult(new PathPingResult(IPStatus.TimedOut, null, null)));

        var result = await service.DiscoverMtuAsync("198.51.100.10", CancellationToken.None);

        Assert.Equal(PathMtuState.IcmpBlockedOrInconclusive, result.State);
        Assert.Null(result.Mtu);
    }

    [Fact]
    public async Task DiscoverMtu_SilentDropOfLargePackets_IsReportedAsABlackHole()
    {
        // The path drops oversized packets without the "fragmentation needed" reply, but forwards
        // the same size once it is allowed to fragment. That is the signature of a PMTUD black hole.
        var service = new PathDiagnosticService((target, payload, dontFragment, ttl, timeout, token) =>
            Task.FromResult(new PathPingResult(
                payload <= 1400 || !dontFragment ? IPStatus.Success : IPStatus.TimedOut, target, 1)));

        var result = await service.DiscoverMtuAsync("198.51.100.10", CancellationToken.None);

        Assert.Equal((PathMtuState.IcmpBlackHole, 1428), (result.State, result.Mtu));
        Assert.True(result.HasMtu);
        Assert.Contains("without a 'fragmentation needed' reply", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverMtu_LargePacketsLostInBothModes_StaysInconclusive()
    {
        var service = new PathDiagnosticService((target, payload, dontFragment, ttl, timeout, token) =>
            Task.FromResult(new PathPingResult(payload <= 1400 ? IPStatus.Success : IPStatus.TimedOut, target, 1)));

        var result = await service.DiscoverMtuAsync("198.51.100.10", CancellationToken.None);

        Assert.Equal(PathMtuState.IcmpBlockedOrInconclusive, result.State);
        Assert.Null(result.Mtu);
    }

    [Fact]
    public async Task TraceAsync_NoRespondingHops_IsClassifiedAsRouteFailure()
    {
        var service = new PathDiagnosticService((target, payload, fragment, ttl, timeout, token) =>
            Task.FromResult(new PathPingResult(IPStatus.TimedOut, null, null)));

        var result = await service.TraceAsync("198.51.100.10", CancellationToken.None);

        Assert.Equal(DiagnosticFailureKind.RouteFailure, result.FailureKind);
        Assert.Empty(result.Hops);
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("203.0.113.1", true)]
    [InlineData("fd00::1", false)]
    [InlineData("2001:db8::1", true)]
    public void IsPublicAddress_ClassifiesPrivateAndPublicRanges(string address, bool expected)
    {
        Assert.Equal(expected, PathDiagnosticService.IsPublicAddress(address));
    }
}
