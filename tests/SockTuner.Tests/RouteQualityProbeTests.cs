using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

public sealed class RouteQualityProbeTests
{
    [Fact]
    public async Task RunAsync_AggregatesEveryHopAcrossRounds()
    {
        var probe = new RouteQualityProbe((target, _, _, ttl, _, _) => Task.FromResult(ttl switch
        {
            1 => new PathPingResult(IPStatus.TtlExpired, "192.168.1.1", 1),
            2 => new PathPingResult(IPStatus.TtlExpired, "100.70.0.1", 8),
            3 => new PathPingResult(IPStatus.TtlExpired, "203.0.113.1", 14),
            _ => new PathPingResult(IPStatus.Success, target, 20)
        }));

        var result = await probe.RunAsync("198.51.100.10", rounds: 4, maximumHops: 10, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.ReachedTarget);
        Assert.Equal(4, result.Hops.Count);
        Assert.All(result.Hops, hop => Assert.Equal(4, hop.RoundsObserved));
        Assert.Equal(4, result.Rounds);
    }

    [Fact]
    public async Task RunAsync_ClassifiesPrivateCarrierGradeAndPublicHops()
    {
        var probe = new RouteQualityProbe((target, _, _, ttl, _, _) => Task.FromResult(ttl switch
        {
            1 => new PathPingResult(IPStatus.TtlExpired, "192.168.1.1", 1),
            2 => new PathPingResult(IPStatus.TtlExpired, "100.70.0.1", 8),
            _ => new PathPingResult(IPStatus.Success, "203.0.113.9", 20)
        }));

        var result = await probe.RunAsync("203.0.113.9", 2, 6, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(HopAddressKind.Private, result.Hops[0].AddressKind);
        Assert.Equal(HopAddressKind.CarrierGrade, result.Hops[1].AddressKind);
        Assert.Equal(HopAddressKind.Public, result.Hops[2].AddressKind);
        Assert.NotNull(result.CarrierGradeNatHop);
        Assert.Equal("203.0.113.9", result.FirstPublicHop!.Address);
    }

    [Fact]
    public async Task RunAsync_RecordsAlternateAddressesWhenTheRouteChanges()
    {
        var round = 0;
        var probe = new RouteQualityProbe((target, _, _, ttl, _, _) =>
        {
            if (ttl == 1) round++;
            return Task.FromResult(ttl == 2
                ? new PathPingResult(IPStatus.TtlExpired, round % 2 == 1 ? "203.0.113.1" : "203.0.113.2", 10)
                : new PathPingResult(IPStatus.TtlExpired, "192.168.1.1", 1));
        });

        var result = await probe.RunAsync("198.51.100.10", 4, 2, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(result.HasUnstableRouting);
        Assert.NotEmpty(result.Hops[1].AlternateAddresses);
    }

    [Fact]
    public async Task RunAsync_NoHopReplies_IsClassifiedAsRouteFailure()
    {
        var probe = new RouteQualityProbe((_, _, _, _, _, _) =>
            Task.FromResult(new PathPingResult(IPStatus.TimedOut, null, null)));

        var result = await probe.RunAsync("198.51.100.10", 2, 4, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(DiagnosticFailureKind.RouteFailure, result.FailureKind);
        Assert.False(result.ReachedTarget);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var probe = new RouteQualityProbe((_, _, _, _, _, _) =>
            Task.FromResult(new PathPingResult(IPStatus.Success, "203.0.113.1", 1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.RunAsync("198.51.100.10", 2, 4, TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(51, 4)]
    [InlineData(2, 0)]
    [InlineData(2, 65)]
    public async Task RunAsync_RejectsOutOfRangeParameters(int rounds, int maximumHops)
    {
        var probe = new RouteQualityProbe((_, _, _, _, _, _) =>
            Task.FromResult(new PathPingResult(IPStatus.Success, "203.0.113.1", 1)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            probe.RunAsync("198.51.100.10", rounds, maximumHops, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Theory]
    [InlineData("10.0.0.1", HopAddressKind.Private)]
    [InlineData("192.168.1.1", HopAddressKind.Private)]
    [InlineData("172.16.5.4", HopAddressKind.Private)]
    [InlineData("169.254.1.1", HopAddressKind.Private)]
    [InlineData("100.64.0.1", HopAddressKind.CarrierGrade)]
    [InlineData("100.127.255.254", HopAddressKind.CarrierGrade)]
    [InlineData("100.128.0.1", HopAddressKind.Public)]
    [InlineData("203.0.113.1", HopAddressKind.Public)]
    [InlineData("fd00::1", HopAddressKind.Private)]
    [InlineData("2001:db8::1", HopAddressKind.Public)]
    [InlineData("*", HopAddressKind.Unknown)]
    public void ClassifyAddress_SeparatesPrivateCarrierGradeAndPublic(string address, HopAddressKind expected)
    {
        Assert.Equal(expected, RouteQualityProbe.ClassifyAddress(address));
    }
}
