using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class NicKeywordCatalogTests
{
    [Fact]
    public void UnknownStandardizedKeywordFallsBackToDriverAdvertised()
    {
        // A '*' keyword SockTuner has never characterised is still a Microsoft-defined keyword
        // the driver publishes a range for, so the honest default is driver-advertised.
        var described = NicKeywordCatalog.Describe("*SomeFutureStandardKeyword");

        Assert.Equal(NicKeywordClass.Standardized, described.Class);
        Assert.Equal(NicKeywordDisposition.DriverAdvertised, described.Disposition);
        Assert.NotEmpty(described.Note);
    }

    [Fact]
    public void UnknownVendorKeywordFallsBackToUncharacterised()
    {
        // No '*' means a private vendor keyword. It must never be silently promoted to a
        // safe candidate just because a driver happens to advertise it.
        var described = NicKeywordCatalog.Describe("SomeVendorPrivateKnob");

        Assert.Equal(NicKeywordClass.Vendor, described.Class);
        Assert.Equal(NicKeywordDisposition.Uncharacterised, described.Disposition);
        Assert.NotEmpty(described.Note);
    }

    [Fact]
    public void ResearchFlaggedKeywordsAreRejected()
    {
        Assert.Equal(NicKeywordDisposition.Rejected, NicKeywordCatalog.Describe("HwOption").Disposition);
        Assert.Equal(NicKeywordDisposition.Rejected, NicKeywordCatalog.Describe("ThreadPoll").Disposition);
        Assert.Equal(NicKeywordDisposition.Rejected, NicKeywordCatalog.Describe("DisablePhyReset").Disposition);
        Assert.Equal(NicKeywordDisposition.Rejected, NicKeywordCatalog.Describe("PnPCapabilities").Disposition);
        Assert.Equal(NicKeywordDisposition.Rejected, NicKeywordCatalog.Describe("DropHighlyFragmentedPacket").Disposition);
    }

    [Fact]
    public void DocumentedTradeOffKeywordsAreSituationalNotSafe()
    {
        // These are real, documented keywords, but each can regress something. They must not
        // read as ordinary driver-advertised candidates.
        Assert.Equal(NicKeywordDisposition.Situational, NicKeywordCatalog.Describe("*FlowControl").Disposition);
        Assert.Equal(NicKeywordDisposition.Situational, NicKeywordCatalog.Describe("*RscIPv4").Disposition);
        Assert.Equal(NicKeywordDisposition.Situational, NicKeywordCatalog.Describe("*JumboPacket").Disposition);
        Assert.Equal(NicKeywordDisposition.Situational, NicKeywordCatalog.Describe("*ReceiveBuffers").Disposition);
    }

    [Fact]
    public void CoreLatencyLeversStayDriverAdvertised()
    {
        Assert.Equal(NicKeywordDisposition.DriverAdvertised, NicKeywordCatalog.Describe("*InterruptModeration").Disposition);
        Assert.Equal(NicKeywordDisposition.DriverAdvertised, NicKeywordCatalog.Describe("*EEE").Disposition);
    }

    [Fact]
    public void LookupIgnoresKeywordCasingAndSurroundingWhitespace()
    {
        // Drivers spell standardized keywords inconsistently (*Rss vs *RSS), and registry key
        // names can carry stray whitespace.
        Assert.Equal(NicKeywordDisposition.Situational, NicKeywordCatalog.Describe("*flowcontrol").Disposition);
        Assert.Equal(NicKeywordDisposition.DriverAdvertised, NicKeywordCatalog.Describe("  *rss  ").Disposition);
        Assert.Equal("*rss", NicKeywordCatalog.Describe("  *rss  ").Keyword);
    }

    [Fact]
    public void MissingKeywordIsDescribedWithoutThrowing()
    {
        var described = NicKeywordCatalog.Describe(null);

        Assert.Equal(string.Empty, described.Keyword);
        Assert.Equal(NicKeywordClass.Vendor, described.Class);
        Assert.Equal(NicKeywordDisposition.Uncharacterised, described.Disposition);
    }

    [Fact]
    public void VendorKeywordIsNeverPresentedAsDriverAdvertised()
    {
        // The safety invariant behind the whole catalog: a private vendor keyword has no public
        // specification, so it can never be characterised as a safe advertised candidate —
        // neither by the fallback rule nor by a future curated entry.
        foreach (var keyword in NicKeywordCatalog.CharacterisedKeywords)
        {
            var described = NicKeywordCatalog.Describe(keyword);
            if (described.Class == NicKeywordClass.Vendor)
            {
                Assert.NotEqual(NicKeywordDisposition.DriverAdvertised, described.Disposition);
            }
        }
    }

    [Fact]
    public void EveryCharacterisedKeywordExplainsItself()
    {
        // A disposition without a reason is the assertion-instead-of-evidence problem this
        // catalog exists to avoid.
        foreach (var keyword in NicKeywordCatalog.CharacterisedKeywords)
        {
            var described = NicKeywordCatalog.Describe(keyword);
            Assert.False(string.IsNullOrWhiteSpace(described.Note), $"{keyword} has no explanatory note.");
        }
    }

    [Fact]
    public void CatalogAnnotatesButDoesNotUnlockAnyNicWrite()
    {
        // Characterisation is advisory. The writable allowlist is SettingCatalog, which must
        // still contain no NIC keyword: Step 7b has not passed its hardware gate.
        Assert.DoesNotContain(
            SettingCatalog.All,
            definition => definition.ValueName.StartsWith('*'));
    }
}
