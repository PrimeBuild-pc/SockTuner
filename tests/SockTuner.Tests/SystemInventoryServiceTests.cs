using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class SystemInventoryServiceTests
{
    [Fact]
    public void SelectIpInterfaces_MatchesAddressFamilyAndIndexTogether()
    {
        var interfaces = new[]
        {
            Interface("IPv4", 10, 10),
            Interface("IPv6", 20, 20),
            Interface("IPv6", 10, 30),
            Interface("IPv4", 20, 40)
        };

        var selected = SystemInventoryService.SelectIpInterfaces(interfaces, 10, 20);

        Assert.Collection(
            selected,
            item => Assert.Equal(("IPv4", 10u), (item.AddressFamily, item.Metric)),
            item => Assert.Equal(("IPv6", 20u), (item.AddressFamily, item.Metric)));
    }

    private static IpInterfaceInfo Interface(string family, int index, uint metric) =>
        new(family, index, metric, 1500, true, true, false);
}
