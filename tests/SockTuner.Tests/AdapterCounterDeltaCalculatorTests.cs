using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class AdapterCounterDeltaCalculatorTests
{
    [Fact]
    public void Calculate_MatchesStableIdAndDetectsCounterReset()
    {
        var before = Snapshot(Adapter("A", "Old name", new(100, 200, 1, 2, 3, 4)));
        var after = Snapshot(Adapter("a", "New name", new(150, 260, 2, 4, 1, 5)));

        var delta = Assert.Single(AdapterCounterDeltaCalculator.Calculate(before, after));

        Assert.Equal((50L, 60L, 1L, 2L, null, 1L),
            (delta.ReceivedBytes, delta.SentBytes, delta.ReceiveDiscards, delta.ReceiveErrors, delta.SendDiscards, delta.SendErrors));
        Assert.Contains("reset/unavailable", delta.Summary);
    }

    private static NetworkSnapshot Snapshot(AdapterInfo adapter) => new(
        new SystemOverview("Windows", "10", "PC", 4, false, DateTimeOffset.UnixEpoch), [adapter], [], null);

    private static AdapterInfo Adapter(string id, string name, AdapterCounters counters) => new(
        id, name, "adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Up, 1, "", [], [], [],
        1, 1500, 1, 1500, true, true, null, null, [], false, null, counters);
}
