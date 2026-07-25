using System.Net.NetworkInformation;
using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class AdapterInfoTests
{
    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(999, "999 bps")]
    [InlineData(1_000, "1 Kbps")]
    [InlineData(100_000_000, "100 Mbps")]
    [InlineData(2_500_000_000, "2.5 Gbps")]
    public void FormatSpeed_UsesReadableUnits(long bitsPerSecond, string expected)
    {
        Assert.Equal(expected, AdapterInfo.FormatSpeed(bitsPerSecond));
    }

    [Fact]
    public void ClassifyAdapter_UsesTypeAndDriverCharacteristicsWithoutAdapterNames()
    {
        var physical = new DriverInfo("Intel", "1", "—", "—", "—", "—", "PCI\\VEN_8086", 0x5);
        var virtualAdapter = new DriverInfo("Microsoft", "1", "—", "—", "—", "—", "ROOT\\VMS_MP", 0x1);
        var hyperV = new DriverInfo("Microsoft", "1", "—", "—", "VMBUS\\{device}", "—", "—", 0x4);

        Assert.Equal(AdapterKind.Physical, AdapterInfo.ClassifyAdapter(NetworkInterfaceType.Ethernet, physical, true, true));
        Assert.Equal(AdapterKind.Virtual, AdapterInfo.ClassifyAdapter(NetworkInterfaceType.Ethernet, virtualAdapter, true, true));
        Assert.Equal(AdapterKind.Virtual, AdapterInfo.ClassifyAdapter(NetworkInterfaceType.Ethernet, hyperV, true, true));
        Assert.Equal(AdapterKind.Loopback, AdapterInfo.ClassifyAdapter(NetworkInterfaceType.Loopback, null, true, true));
        Assert.Equal(AdapterKind.Filter, AdapterInfo.ClassifyAdapter(NetworkInterfaceType.Ethernet, null, false, false));
    }

    [Fact]
    public void ActiveAdapterCount_ExcludesFiltersLoopbackTunnelsAndDownInterfaces()
    {
        var physical = new DriverInfo("Intel", "1", "—", "—", "—", "—", "PCI\\VEN_8086", 0x4);
        var virtualAdapter = new DriverInfo("Microsoft", "1", "—", "—", "—", "—", "ROOT\\VMS_MP", 0x1);
        var adapters = new[]
        {
            CreateAdapter(NetworkInterfaceType.Ethernet, OperationalStatus.Up, true, true, physical),
            CreateAdapter(NetworkInterfaceType.Ethernet, OperationalStatus.Up, true, true, virtualAdapter),
            CreateAdapter(NetworkInterfaceType.Ethernet, OperationalStatus.Up, false, false, null),
            CreateAdapter(NetworkInterfaceType.Loopback, OperationalStatus.Up, true, true, null),
            CreateAdapter(NetworkInterfaceType.Tunnel, OperationalStatus.Up, true, true, null),
            CreateAdapter(NetworkInterfaceType.Ethernet, OperationalStatus.Down, true, true, physical)
        };
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "1", "machine", 1, false, DateTimeOffset.MinValue),
            adapters, [], null);

        Assert.Equal(2, snapshot.ActiveAdapterCount);
    }

    [Theory]
    [InlineData(null, "Complete")]
    [InlineData("Driver rejected query", "Partial: Driver rejected query")]
    public void InventoryStatus_SurfacesPerAdapterErrors(string? error, string expected)
    {
        var adapter = new AdapterInfo(
            "id", "name", "description", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 1_000_000_000, "00-00-00-00-00-00",
            [], [], [], 0, 0, 0, 0, true, true, error, null, [], false, null);

        Assert.Equal(expected, adapter.InventoryStatus);
        Assert.Equal("Unsupported", adapter.NdisPropertyCountDisplay);
    }

    private static AdapterInfo CreateAdapter(
        NetworkInterfaceType type,
        OperationalStatus status,
        bool supportsIPv4,
        bool supportsIPv6,
        DriverInfo? driver) => new(
            "id", "name", "description", type, status, 1_000_000_000, "00-00-00-00-00-00",
            [], [], [], 1, 1500, 1, 1500, supportsIPv4, supportsIPv6, null, driver, [], false, null);
}
