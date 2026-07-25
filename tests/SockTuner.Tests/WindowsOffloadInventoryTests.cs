using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsOffloadInventoryTests
{
    [Theory]
    [InlineData(0, false, false, "Disabled")]
    [InlineData(1, false, false, "Enabled")]
    [InlineData(2, true, false, "Automatic")]
    [InlineData(0, false, true, "Blocked")]
    [InlineData(1, false, true, "Allowed")]
    [InlineData(99, false, false, "Value 99")]
    public void FormatSwitch_PreservesPropertySemantics(byte value, bool automatic, bool blocked, string expected)
    {
        Assert.Equal(expected, WindowsOffloadInventory.FormatSwitch(value, automatic, blocked));
    }

    [Theory]
    [InlineData(0, "Disabled")]
    [InlineData(1, "Transmit")]
    [InlineData(2, "Receive")]
    [InlineData(3, "Transmit + receive")]
    [InlineData(99, "Value 99")]
    public void FormatChecksum_PreservesDirection(uint value, string expected)
    {
        Assert.Equal(expected, WindowsOffloadInventory.FormatChecksum(value));
    }

    [Theory]
    [InlineData(1, "Closest")]
    [InlineData(2, "Closest static")]
    [InlineData(3, "NUMA scaling")]
    [InlineData(4, "NUMA scaling static")]
    [InlineData(5, "Conservative scaling")]
    [InlineData(6, "Balanced")]
    [InlineData(99, "Value 99")]
    public void FormatRssProfile_PreservesKnownAndUnknownValues(uint value, string expected)
    {
        Assert.Equal(expected, WindowsOffloadInventory.FormatRssProfile(value));
    }

    [Fact]
    public void FormatUroFailure_ExpandsFlagsAndPreservesUnknownBits()
    {
        Assert.Equal("None", WindowsOffloadInventory.FormatUroFailure(0));
        Assert.Equal("WFP compatibility, Capability, Flags 0x2000", WindowsOffloadInventory.FormatUroFailure(2 | 32 | 8192));
    }
}
