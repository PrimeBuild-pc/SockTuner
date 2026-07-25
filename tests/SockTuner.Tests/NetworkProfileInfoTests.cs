using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class NetworkProfileInfoTests
{
    [Theory]
    [InlineData(0, "Public")]
    [InlineData(1, "Private")]
    [InlineData(2, "Domain authenticated")]
    [InlineData(9, "Category 9")]
    public void CategoryName_PreservesKnownAndUnknownValues(int category, string expected)
    {
        Assert.Equal(expected, WindowsNetworkProfileInventory.CategoryName(category));
    }

    [Theory]
    [InlineData(0u, "Disconnected")]
    [InlineData(0x42u, "IPv6 no traffic, IPv4 internet")]
    [InlineData(0x8u, "Flags 0x8")]
    public void FormatConnectivity_ExpandsNativeFlags(uint connectivity, string expected)
    {
        Assert.Equal(expected, NetworkProfileInfo.FormatConnectivity(connectivity));
    }
}
