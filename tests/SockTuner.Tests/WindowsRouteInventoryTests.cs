using System.Net;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsRouteInventoryTests
{
    [Fact]
    public void NativeRowSize_MatchesMibIpForwardRowAbi()
    {
        Assert.Equal(56, WindowsRouteInventory.NativeRowSize);
    }

    [Fact]
    public void NativeRow2Layout_MatchesWindowsSdkAbi()
    {
        Assert.Equal(104, WindowsRouteInventory.NativeRow2Size);
        Assert.Equal(8, WindowsRouteInventory.NativeTable2RowOffset);
    }

    [Theory]
    [InlineData(0x0101A8C0u, "192.168.1.1")]
    [InlineData(0u, "0.0.0.0")]
    public void FormatAddress_UsesWindowsRouteTableByteOrder(uint address, string expected)
    {
        Assert.Equal(expected, WindowsRouteInventory.FormatAddress(address));
    }

    [Theory]
    [InlineData("::")]
    [InlineData("2001:db8::1")]
    public void FormatIpv6Address_PreservesNativeAddressBytes(string expected)
    {
        var bytes = IPAddress.Parse(expected).GetAddressBytes();

        var actual = WindowsRouteInventory.FormatIpv6Address(
            BitConverter.ToUInt32(bytes, 0),
            BitConverter.ToUInt32(bytes, 4),
            BitConverter.ToUInt32(bytes, 8),
            BitConverter.ToUInt32(bytes, 12),
            0);

        Assert.Equal(expected, actual);
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
