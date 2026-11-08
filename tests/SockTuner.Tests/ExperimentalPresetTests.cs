using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

public sealed class ExperimentalPresetTests
{
    private static readonly Guid Adapter = Guid.Parse("44444444-3333-2222-1111-000000000000");
    private static readonly DriverInfo Driver = new("Intel", "new-driver", "", "oem.inf", "PCI\\VEN_8086&DEV_125C", "", "PCI\\VEN_8086&DEV_125C\\test", 4);
    private static AdapterSettingCapability Capability(string keyword, string offLabel = "Disabled") =>
        new(Adapter, "Ethernet", "Intel unarchived controller", keyword, keyword, "1", "1",
            [new("0", offLabel), new("1", "Enabled")], null, null, null,
            AdapterSettingCapability.RegistrySz, false, TuningArea.Power, ChangeRisk.Medium, "Test tradeoff");

    [Fact]
    public void SupportedPowerControlsPreserveWakeAndNeverSetTheLegacyBitmask()
    {
        var capabilities = new[] { Capability("*SelectiveSuspend"), Capability("*IdleRestriction") with { CurrentValue = "0" },
            Capability("*WakeOnMagicPacket"), Capability("EnablePME") };
        var action = UseCaseProfiles.PowerSavingFor(Adapter, capabilities, wireless: false);
        Assert.Equal(["nic.*SelectiveSuspend", "nic.*IdleRestriction"], action.Changes.Select(c => c.SettingId));
        Assert.Equal("1", action.Changes.Single(c => c.SettingId == "nic.*IdleRestriction").ProposedValue);
        Assert.All(UseCaseProfiles.All, profile => Assert.DoesNotContain(
            UseCaseProfiles.PlanFor(profile, Adapter, capabilities).Changes,
            c => c.SettingId is "adapter.power-saving" or "nic.EnablePME" or "nic.*WakeOnMagicPacket"));
    }

    [Fact]
    public void AGenericWritablePropertyDoesNotImplySupportForDevicePowerControl()
    {
        var result = UseCaseProfiles.PowerSavingFor(Adapter, [Capability("*LsoV2IPv4")], false);
        Assert.Empty(result.Changes);
        var legacy = new AdapterPowerSavingSpecification(new Dictionary<Guid, string> { [Adapter] = "0001" });
        Assert.Equal(EvidenceLevel.Experimental, legacy.Evidence);
        Assert.True(legacy.SupportsAbsentValue);
    }

    [Fact]
    public void BalancedPresetPreservesOffloadsWhileAggressiveAddsOnlyAdvertisedTargets()
    {
        var capabilities = new[] { Capability("*InterruptModeration"), Capability("*LsoV2IPv4"), Capability("ULPMode"), Capability("SipsEnabled", "Enabled") };
        var balanced = UseCaseProfiles.PlanFor(UseCaseProfiles.All[0], Adapter, capabilities);
        Assert.DoesNotContain(balanced.Changes, c => c.SettingId is "nic.*InterruptModeration" or "nic.*LsoV2IPv4" or "nic.ULPMode");
        var aggressive = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("competitive-gaming"), Adapter, capabilities);
        Assert.Contains(aggressive.Changes, c => c.SettingId == "nic.*LsoV2IPv4" && c.ProposedValue == "0");
        Assert.Contains(aggressive.Changes, c => c.SettingId == "nic.ULPMode" && c.ProposedValue == "0");
        Assert.DoesNotContain(aggressive.Changes, c => c.SettingId == "nic.SipsEnabled");
        Assert.Contains("Experimental", aggressive.TradeOff);
    }

    [Theory]
    [InlineData("AutoDisableGigabit")]
    [InlineData("EnablePME")]
    [InlineData("ULPMode")]
    [InlineData("SipsEnabled")]
    public void UnarchivedVendorCandidatesAreUsableButNeverPromotedToVerifiedHardware(string keyword)
    {
        var capability = Capability(keyword);
        Assert.True(NicKeywordCatalog.IsCharacterised(keyword));
        Assert.Null(NicHardwareCompatibility.Restriction(capability, Driver));
        Assert.True(NicHardwareCompatibility.IsUnverifiedTemplate(capability, Driver));
        Assert.Equal(AdapterVerification.Unverified, AdapterVerificationCatalog.For(capability.InterfaceDescription));
        Assert.NotNull(NicHardwareCompatibility.Restriction(capability, null));
        Assert.NotNull(NicHardwareCompatibility.Restriction(capability with { InterfaceDescription = "Other vendor" }, Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(capability with { Choices = [] }, Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(capability with { Rejected = true }, Driver));
    }

    [Fact]
    public void AnUnarchivedStandardTemplateRequiresExperimentalConfirmation()
    {
        var cap = Capability("*LsoV2IPv4") with { UnverifiedHardwareTemplate = true };
        Assert.Equal(EvidenceLevel.Experimental, cap.Evidence);
        var spec = new NicSettingSpecification(cap);
        var address = spec.ResolveAddress(Adapter.ToString());
        Assert.True(new PlannedChange(spec, address, new(true, "1"), new(true, "0")).RequiresExplicitConfirmation);
        Assert.NotNull(NicHardwareCompatibility.Restriction(Capability("NetworkAddress"), Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(Capability("HwOption") with { Rejected = true }, Driver));
    }

    [Fact]
    public void AggressiveSourceSurvivesTheEditableRowAndStillRequiresConfirmationOnVerifiedHardware()
    {
        var cap = Capability("*LsoV2IPv4");
        var row = new SockTuner.Views.CapabilityRow(cap) { ProposedValue = "0", Source = ChangeSource.ExperimentalProfile };
        var spec = new NicSettingSpecification(cap);
        Assert.True(new PlannedChange(spec, spec.ResolveAddress(Adapter.ToString()), new(true, "1"), new(true, row.ProposedValue), row.Source).RequiresExplicitConfirmation);
        row.ProposedValue = "1";
        Assert.Equal(ChangeSource.Manual, row.Source);
        Assert.True(NicHardwareCompatibility.IsUnverifiedTemplate(Capability("ulpmode"), Driver));
    }
}
