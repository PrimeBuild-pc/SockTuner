using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class TcpSettingInfoTests
{
    [Theory]
    [InlineData(0, "Disabled")]
    [InlineData(1, "Highly restricted")]
    [InlineData(2, "Restricted")]
    [InlineData(3, "Normal")]
    [InlineData(4, "Experimental")]
    [InlineData(99, "Value 99")]
    public void FormatAutoTuning_PreservesKnownAndUnknownValues(byte value, string expected)
    {
        Assert.Equal(expected, TcpSettingInfo.FormatAutoTuning(value));
    }

    [Theory]
    [InlineData(0, "Default")]
    [InlineData(1, "NewReno")]
    [InlineData(2, "CTCP")]
    [InlineData(3, "DCTCP")]
    [InlineData(4, "LEDBAT")]
    [InlineData(5, "CUBIC")]
    [InlineData(6, "BBR2")]
    [InlineData(99, "Value 99")]
    public void FormatCongestionProvider_PreservesKnownAndUnknownValues(byte value, string expected)
    {
        Assert.Equal(expected, TcpSettingInfo.FormatCongestionProvider(value));
    }

    [Fact]
    public void Displays_IncludePolicySourcePortsAndTiming()
    {
        var setting = Setting() with
        {
            AutoTuningLevelEffective = 0,
            AutoTuningLevelGroupPolicy = 254,
            AutoTuningLevelLocal = 3,
            DynamicPortRangeStartPort = 1024,
            DynamicPortRangeNumberOfPorts = 64511,
            InitialRto = 2000,
            MinRto = 300,
            DelayedAckTimeout = 40,
            DelayedAckFrequency = 2
        };

        Assert.Equal("Normal (local; policy not configured)", setting.AutoTuningDisplay);
        Assert.Equal("1024–65534 (64511 ports)", setting.DynamicPortRangeDisplay);
        Assert.Equal("Initial RTO 2000 ms; minimum RTO 300 ms; delayed ACK timeout 40 ms; frequency 2", setting.TimingDisplay);
        Assert.Equal("Unavailable", setting.AutomaticUseCustomDisplay);
        Assert.Equal("Enabled", (setting with { AutomaticUseCustom = 1 }).AutomaticUseCustomDisplay);
        Assert.Equal("No ports (start 0)", (setting with
        {
            DynamicPortRangeStartPort = 0,
            DynamicPortRangeNumberOfPorts = 0
        }).DynamicPortRangeDisplay);
    }

    private static TcpSettingInfo Setting() => new(
        "Internet", null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null);
}
