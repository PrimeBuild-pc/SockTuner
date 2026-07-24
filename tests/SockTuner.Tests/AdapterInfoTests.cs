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

    [Theory]
    [InlineData(null, "Complete")]
    [InlineData("Driver rejected query", "Partial: Driver rejected query")]
    public void InventoryStatus_SurfacesPerAdapterErrors(string? error, string expected)
    {
        var adapter = new AdapterInfo(
            "id", "name", "description", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 1_000_000_000, "00-00-00-00-00-00",
            [], [], [], true, true, error, null, [], false, null);

        Assert.Equal(expected, adapter.InventoryStatus);
        Assert.Equal("Unsupported", adapter.NdisPropertyCountDisplay);
    }
}
