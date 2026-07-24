using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsRouteInventoryTests
{
    [Fact]
    public void NativeRowSize_MatchesMibIpForwardRowAbi()
    {
        Assert.Equal(56, WindowsRouteInventory.NativeRowSize);
    }

    [Theory]
    [InlineData(0x0101A8C0u, "192.168.1.1")]
    [InlineData(0u, "0.0.0.0")]
    public void FormatAddress_UsesWindowsRouteTableByteOrder(uint address, string expected)
    {
        Assert.Equal(expected, WindowsRouteInventory.FormatAddress(address));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0x00FFFFFFu, 24)]
    [InlineData(uint.MaxValue, 32)]
    public void PrefixLength_CountsMaskBits(uint mask, int expected)
    {
        Assert.Equal(expected, WindowsRouteInventory.PrefixLength(mask));
    }
}
