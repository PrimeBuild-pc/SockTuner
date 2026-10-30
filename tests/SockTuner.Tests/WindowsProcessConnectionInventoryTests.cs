using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsProcessConnectionInventoryTests
{
    [Fact]
    public void NativeTcpRowSize_MatchesMibTcprowOwnerPidAbi()
    {
        Assert.Equal(24, WindowsProcessConnectionInventory.NativeTcpRowSize);
    }

    [Fact]
    public void NativeTcp6RowSize_MatchesMibTcp6rowOwnerPidAbi()
    {
        Assert.Equal(56, WindowsProcessConnectionInventory.NativeTcp6RowSize);
    }

    [Theory]
    [InlineData(0x0000_BB01u, 443)] // port 443 = 0x01BB; stored as low byte 0x01, high byte 0xBB
    [InlineData(0x0000_5000u, 80)]  // port 80 = 0x0050; stored as low byte 0x00, high byte 0x50
    [InlineData(0u, 0)]
    public void Port_SwapsTheNetworkByteOrderLowWord(uint raw, int expected)
    {
        Assert.Equal(expected, WindowsProcessConnectionInventory.Port(raw));
    }
}
