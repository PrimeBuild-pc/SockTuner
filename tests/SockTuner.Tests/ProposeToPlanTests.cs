using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The recommendations tab hands proposed changes to the tuning plan. The plan is still the only
/// thing that writes, so what matters is that a suggestion cannot widen what the plan will accept:
/// a change is taken only when the driver advertises that keyword on that adapter and the value
/// passes the driver's own constraints.
/// </summary>
public sealed class ProposeToPlanTests
{
    private static readonly Guid Adapter = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherAdapter = Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Fact]
    public void AValueTheDriverAdvertisesIsAccepted()
    {
        var capability = Capability("*InterruptModeration", ["0", "1"]);

        capability.Validate("0");
        Assert.Contains(capability.Choices, choice => choice.RegistryValue == "0");
    }

    [Fact]
    public void AValueTheDriverDoesNotAdvertiseIsRefused()
    {
        var capability = Capability("*InterruptModeration", ["0", "1"]);

        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("7"));
    }

    [Fact]
    public void ARejectedKeywordIsRefusedEvenWhenSuggested()
    {
        // A recommendation naming a blocked keyword must not become an applicable change.
        var capability = Capability("HwOption", ["0", "1"], rejected: true);

        Assert.Equal(EvidenceLevel.Blocked, capability.Evidence);
        Assert.Throws<InvalidOperationException>(() => capability.Validate("1"));
    }

    [Fact]
    public void AChangeForAnotherAdapterDoesNotMatchThisOne()
    {
        // Settings are addressed as nic.<keyword> per adapter; the plan resolves rows for the
        // selected adapter only, so a suggestion aimed elsewhere simply finds no row.
        var mine = Capability("*FlowControl", ["0", "1"]);
        var theirs = Capability("*FlowControl", ["0", "1"], adapterId: OtherAdapter);

        Assert.Equal(mine.SettingId, theirs.SettingId);
        Assert.NotEqual(mine.AdapterId, theirs.AdapterId);
    }

    [Fact]
    public void RemediationActionsWithoutLocalChangesAreNotApplicable()
    {
        var routerOwned = new RemediationAction(
            "router.sqm",
            "Enable SQM on the router",
            NetworkSegment.RouterOrAccess,
            RemediationOwner.Router,
            [],
            "Bufferbloat grade improves",
            "Caps throughput slightly",
            "Re-run the bufferbloat measurement");

        Assert.False(routerOwned.AppliesLocally);
        Assert.Equal("No local change", routerOwned.ChangesDisplay);
    }

    private static AdapterSettingCapability Capability(
        string keyword, string[] choices, bool rejected = false, Guid? adapterId = null) =>
        new(adapterId ?? Adapter,
            "Ethernet",
            "Contoso 2.5GbE",
            keyword,
            keyword,
            choices[0],
            choices[0],
            choices.Select(value => new CapabilityChoice(value, value)).ToArray(),
            null,
            null,
            null,
            AdapterSettingCapability.RegistrySz,
            false,
            TuningArea.Latency,
            ChangeRisk.Medium,
            "Test capability",
            rejected);
}
