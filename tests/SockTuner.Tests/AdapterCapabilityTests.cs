using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class AdapterCapabilityTests
{
    [Fact]
    public void Validate_EnumeratedKeyword_AcceptsOnlyDriverAdvertisedValues()
    {
        // Intel I226-V advertises 0|1|2|3|4 for *FlowControl; Realtek RTL8125 advertises 0|3.
        var capability = Enumerated("*FlowControl", "0", "1", "2", "3", "4");

        capability.Validate("0");
        capability.Validate("4");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("5"));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("01"));
    }

    [Fact]
    public void Validate_NumericRange_HonoursMinimumMaximumAndStep()
    {
        // Realtek RTL8125 advertises *ReceiveBuffers as 32-4096 step 8.
        var capability = Range("*ReceiveBuffers", 32, 4096, 8);

        capability.Validate("32");
        capability.Validate("40");
        capability.Validate("4096");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("31"));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("4104"));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("41"));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("040"));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("forty"));
    }

    [Fact]
    public void Validate_NumericRangeWithoutStep_AcceptsEveryValueInRange()
    {
        // Intel I226-V advertises *JumboPacket as 1514-9014 step 1.
        var capability = Range("*JumboPacket", 1514, 9014, 1);

        capability.Validate("1514");
        capability.Validate("4088");
        capability.Validate("9014");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("9015"));
    }

    [Fact]
    public void Validate_UnconstrainedKeyword_BoundsPayloadWithoutInventingAConstraint()
    {
        var capability = FreeForm("NetworkAddress");

        capability.Validate("00AABBCCDDEE");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate(new string('a', 256)));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("00AA\0BB"));
    }

    [Fact]
    public void EvidenceAndDisplay_DistinguishStandardAndVendorKeywords()
    {
        var standard = Enumerated("*InterruptModeration", "0", "1");
        var vendor = Enumerated("GigaLite", "0", "1");

        Assert.True(standard.IsStandardKeyword);
        Assert.Equal(EvidenceLevel.DriverAdvertised, standard.Evidence);
        Assert.False(vendor.IsStandardKeyword);
        Assert.Equal(EvidenceLevel.Experimental, vendor.Evidence);
        Assert.Equal("nic.*InterruptModeration", standard.SettingId);
    }

    [Fact]
    public void DisplayHelpers_PreferDriverDisplayTextAndFlagDrift()
    {
        var capability = Enumerated("*InterruptModeration", "0", "1") with
        {
            Choices = [new("0", "Disabled"), new("1", "Enabled")],
            CurrentValue = "0",
            DefaultValue = "1"
        };

        Assert.Equal("Disabled", capability.CurrentDisplay);
        Assert.Equal("Enabled", capability.DefaultDisplay);
        Assert.True(capability.IsModifiedFromDefault);
        Assert.Equal("unlisted", capability.DisplayFor("unlisted"));
    }

    [Fact]
    public void KeywordCatalog_MatchesKeywordsCaseInsensitivelyAcrossVendors()
    {
        // Intel spells it *PriorityVLANTag, Realtek spells it *PriorityVlanTag.
        var intel = NicKeywordCatalog.For("*PriorityVLANTag");
        var realtek = NicKeywordCatalog.For("*PriorityVlanTag");

        Assert.Equal(intel, realtek);
        Assert.Equal(TuningArea.Vlan, intel.Areas);
    }

    [Fact]
    public void KeywordCatalog_TreatsUncharacterisedKeywordsAsHighRisk()
    {
        var profile = NicKeywordCatalog.For("VendorSecretKnob");

        Assert.Equal(NicKeywordCatalog.Unknown, profile);
        Assert.Equal(ChangeRisk.High, profile.Risk);
        Assert.Equal(TuningArea.Other, profile.Areas);
        Assert.False(NicKeywordCatalog.IsCharacterised("VendorSecretKnob"));
    }

    [Theory]
    [InlineData("*InterruptModeration", TuningArea.Latency)]
    [InlineData("ITR", TuningArea.Latency)]
    [InlineData("*JumboPacket", TuningArea.Throughput)]
    [InlineData("*ReceiveBuffers", TuningArea.Throughput)]
    [InlineData("*WakeOnMagicPacket", TuningArea.Wake)]
    [InlineData("NetworkAddress", TuningArea.Identity)]
    [InlineData("BandSelection", TuningArea.WiFiRadio)]
    [InlineData("*EEE", TuningArea.Power)]
    public void KeywordCatalog_AssignsObservedCorpusKeywordsToTheirArea(string keyword, TuningArea expected)
    {
        Assert.True(NicKeywordCatalog.For(keyword).Areas.HasFlag(expected));
    }

    [Fact]
    public void KeywordCatalog_MarksLinkSeveringKeywordsHighRisk()
    {
        foreach (var keyword in new[] { "*SpeedDuplex", "*JumboPacket", "NetworkAddress", "RegVlanid", "MTU" })
        {
            Assert.Equal(ChangeRisk.High, NicKeywordCatalog.For(keyword).Risk);
        }
    }

    private static AdapterSettingCapability Enumerated(string keyword, params string[] values) =>
        Capability(keyword) with { Choices = values.Select(value => new CapabilityChoice(value, value)).ToArray() };

    private static AdapterSettingCapability Range(string keyword, long minimum, long maximum, long step) =>
        Capability(keyword) with { Minimum = minimum, Maximum = maximum, Step = step };

    private static AdapterSettingCapability FreeForm(string keyword) => Capability(keyword);

    private static AdapterSettingCapability Capability(string keyword)
    {
        var profile = NicKeywordCatalog.For(keyword);
        return new AdapterSettingCapability(
            Guid.Parse("DBE23C40-A216-4351-BC0F-CBF9519BC5CE"),
            "Ethernet",
            "Test adapter",
            keyword,
            keyword,
            "0",
            null,
            [],
            null,
            null,
            null,
            AdapterSettingCapability.RegistrySz,
            false,
            profile.Areas,
            profile.Risk,
            profile.TradeOff);
    }
}
