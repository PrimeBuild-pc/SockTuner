using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Views;

namespace SockTuner.Tests;

public sealed class TuningPresetTests
{
    [Fact]
    public void AllPropertiesPreset_IncludesEverythingIncludingUncharacterisedKeywords()
    {
        var all = TuningPreset.All[0];

        Assert.Equal(TuningArea.None, all.Area);
        Assert.True(all.Includes(Capability("*InterruptModeration")));
        Assert.True(all.Includes(Capability("VendorSecretKnob")));
    }

    [Theory]
    [InlineData("Latency", "*InterruptModeration", "*JumboPacket")]
    [InlineData("Bandwidth", "*JumboPacket", "BandSelection")]
    [InlineData("Wi-Fi radio", "BandSelection", "*InterruptModeration")]
    [InlineData("VLAN & identity", "NetworkAddress", "*InterruptModeration")]
    public void Preset_ShowsOnlyPropertiesRelevantToItsArea(string presetName, string included, string excluded)
    {
        var preset = TuningPreset.All.Single(item => item.Name == presetName);

        Assert.True(preset.Includes(Capability(included)));
        Assert.False(preset.Includes(Capability(excluded)));
    }

    [Fact]
    public void PowerAndWakePreset_SpansBothAreas()
    {
        var preset = TuningPreset.All.Single(item => item.Name == "Power & wake");

        Assert.True(preset.Includes(Capability("*WakeOnMagicPacket")));
        Assert.True(preset.Includes(Capability("*EEE")));
    }

    [Fact]
    public void UncharacterisedKeyword_IsHiddenFromEveryFocusedPresetButNeverLost()
    {
        var unknown = Capability("VendorSecretKnob");
        var focused = TuningPreset.All.Where(preset => preset.Area != TuningArea.None).ToArray();

        Assert.All(focused, preset => Assert.False(preset.Includes(unknown)));
        Assert.True(TuningPreset.All[0].Includes(unknown));
    }

    [Fact]
    public void Row_TracksProposalsAgainstTheCurrentValueAndResets()
    {
        var row = new CapabilityRow(Capability("*InterruptModeration") with { CurrentValue = "1" });

        Assert.Equal("1", row.ProposedValue);
        Assert.False(row.HasChange);

        row.ProposedValue = "0";
        Assert.True(row.HasChange);

        row.Reset();
        Assert.False(row.HasChange);
        Assert.Equal("1", row.ProposedValue);
    }

    [Fact]
    public void Row_OffersTheDriversAdvertisedValuesAsOptions()
    {
        var capability = Capability("*FlowControl") with
        {
            Choices = [new("0", "Disabled"), new("3", "Rx & Tx Enabled")]
        };

        Assert.Equal(["0", "3"], new CapabilityRow(capability).Options);
        Assert.Empty(new CapabilityRow(Capability("*JumboPacket")).Options);
    }

    private static AdapterSettingCapability Capability(string keyword)
    {
        var profile = NicKeywordCatalog.For(keyword);
        return new AdapterSettingCapability(
            Guid.NewGuid(), "Ethernet", "Test adapter", keyword, keyword, "0", null, [],
            null, null, null, AdapterSettingCapability.RegistrySz, false,
            profile.Areas, profile.Risk, profile.TradeOff);
    }
}
