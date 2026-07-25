using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsIpInterfaceInventoryTests
{
    [Fact]
    public void NativeLayout_MatchesWindowsSdkAbi()
    {
        Assert.Equal(168, WindowsIpInterfaceInventory.NativeRowSize);
        Assert.Equal(8, WindowsIpInterfaceInventory.NativeTableRowOffset);
    }
}
