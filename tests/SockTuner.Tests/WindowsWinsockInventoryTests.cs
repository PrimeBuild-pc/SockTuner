using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsWinsockInventoryTests
{
    [Fact]
    public void NativeRowSize_MatchesWindowsSdkAbi()
    {
        Assert.Equal(628, WindowsWinsockInventory.NativeRowSize);
    }

    [Fact]
    public void ProviderDisplays_PreserveProtocolShapeAndUnknownValues()
    {
        var provider = new WinsockProviderInfo(
            1, Guid.Empty, "provider", 23, 2, 17, 1, [1], 4, 1, 2, 3, 4);
        var unknown = provider with
        {
            AddressFamily = 999,
            SocketType = 999,
            Protocol = 999,
            ChainLength = 3,
            ChainEntries = [1, 2, 3]
        };

        Assert.Equal(("IPv6", "Datagram", "UDP", "Base"),
            (provider.AddressFamilyDisplay, provider.SocketTypeDisplay, provider.ProtocolDisplay, provider.ChainDisplay));
        Assert.Equal(("Family 999", "Type 999", "Protocol 999", "Chain (3): 1 → 2 → 3"),
            (unknown.AddressFamilyDisplay, unknown.SocketTypeDisplay, unknown.ProtocolDisplay, unknown.ChainDisplay));
        Assert.Equal("Provider 0x00000004 / Services 0x00000001, 0x00000002, 0x00000003, 0x00000004", provider.FlagsDisplay);
    }
}
