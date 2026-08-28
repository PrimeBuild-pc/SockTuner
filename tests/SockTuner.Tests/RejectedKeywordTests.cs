using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// Some advertised keywords are unsafe to write at any value. These lock the refusal in place:
/// a driver advertising one is not enough to make it offerable.
/// </summary>
public sealed class RejectedKeywordTests
{
    // Distilled from docs/JACKPOTS_ZENIT_NDIS_CANDIDATES.md §C.
    public static TheoryData<string> Rejected =>
    [
        "HwOption", "HwOptionV2", "HwOptionV3",
        "ThreadPoll", "DisablePhyReset", "PnPCapabilities", "DropHighlyFragmentedPacket"
    ];

    [Theory]
    [MemberData(nameof(Rejected))]
    public void ResearchFlaggedKeywordsStayRejected(string keyword)
    {
        Assert.True(NicKeywordCatalog.IsRejected(keyword), $"{keyword} must remain rejected.");
        Assert.Equal(ChangeRisk.High, NicKeywordCatalog.For(keyword).Risk);
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void ARejectedKeywordExplainsItself(string keyword)
    {
        // The refusal has to be legible: "blocked" with no reason is not useful to anyone.
        Assert.False(string.IsNullOrWhiteSpace(NicKeywordCatalog.For(keyword).TradeOff));
    }

    [Fact]
    public void ARejectedCapabilityReportsBlockedEvidence()
    {
        Assert.Equal(EvidenceLevel.Blocked, Capability("HwOption", rejected: true).Evidence);
        Assert.NotEqual(EvidenceLevel.Blocked, Capability("*InterruptModeration").Evidence);
    }

    [Fact]
    public void ARejectedCapabilityRefusesEveryValueTheDriverAdvertises()
    {
        // The driver says 0 and 1 are both fine. The refusal is ours, and it wins.
        var capability = Capability("PnPCapabilities", rejected: true, choices: ["0", "24"]);

        Assert.Throws<InvalidOperationException>(() => capability.Validate("0"));
        Assert.Throws<InvalidOperationException>(() => capability.Validate("24"));
    }

    [Fact]
    public void AnOrdinaryCapabilityStillValidatesNormally()
    {
        var capability = Capability("*InterruptModeration", choices: ["0", "1"]);

        capability.Validate("1");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("2"));
    }

    [Fact]
    public void RejectionSurvivesKeywordCasing()
    {
        Assert.True(NicKeywordCatalog.IsRejected("hwoption"));
        Assert.True(NicKeywordCatalog.IsRejected("THREADPOLL"));
    }

    [Fact]
    public void EveryRejectedKeywordIsAlsoCharacterised()
    {
        // Rejection is a curated statement, never the fallback for an unknown keyword — an
        // uncharacterised keyword is high risk, not blocked.
        foreach (var keyword in NicKeywordCatalog.RejectedKeywords)
        {
            Assert.True(NicKeywordCatalog.IsCharacterised(keyword));
        }

        Assert.False(NicKeywordCatalog.IsRejected("SomeKeywordNobodyHasSeen"));
    }

    private static AdapterSettingCapability Capability(
        string keyword,
        bool rejected = false,
        string[]? choices = null)
    {
        var profile = NicKeywordCatalog.For(keyword);
        return new AdapterSettingCapability(
            Guid.NewGuid(),
            "Ethernet",
            "Contoso 2.5GbE",
            keyword,
            keyword,
            choices?[0] ?? "0",
            choices?[0] ?? "0",
            (choices ?? []).Select(value => new CapabilityChoice(value, value)).ToArray(),
            null,
            null,
            null,
            AdapterSettingCapability.RegistrySz,
            false,
            profile.Areas,
            profile.Risk,
            profile.TradeOff,
            rejected);
    }
}
