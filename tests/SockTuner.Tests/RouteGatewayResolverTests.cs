using System.Net;
using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class RouteGatewayResolverTests
{
    [Fact]
    public void SelectGateway_UsesAdapterOwningTheChosenLocalAddress()
    {
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "PC", 8, false, DateTimeOffset.UnixEpoch),
            [
                Adapter("VPN", NetworkInterfaceType.Tunnel, "10.0.0.2", "10.0.0.1"),
                Adapter("Ethernet", NetworkInterfaceType.Ethernet, "192.168.1.20", "192.168.1.1")
            ]);

        var gateway = RouteGatewayResolver.SelectGateway(snapshot, IPAddress.Parse("192.168.1.20"));

        Assert.Equal("192.168.1.1", gateway);
    }

    [Fact]
    public void SelectGateway_DoesNotUseUnspecifiedOrWrongFamilyGateway()
    {
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "PC", 8, false, DateTimeOffset.UnixEpoch),
            [Adapter("Ethernet", NetworkInterfaceType.Ethernet, "192.168.1.20", "::")]);

        Assert.Null(RouteGatewayResolver.SelectGateway(snapshot, IPAddress.Parse("192.168.1.20")));
    }

    private static AdapterInfo Adapter(string name, NetworkInterfaceType type, string address, string gateway) =>
        new(name, name, name, type, OperationalStatus.Up, 1_000_000_000, "00-00-00-00-00-00",
            [address], [gateway], [], true, true, null, null, [], false, null);
}
