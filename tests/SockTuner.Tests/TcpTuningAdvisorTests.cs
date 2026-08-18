using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

/// <summary>
/// The auto-tuning level derived from the measured path. This is the setting where a fixed
/// recommendation is wrong in both directions, so every case here checks that the proposal follows
/// the numbers rather than a table.
/// </summary>
public sealed class TcpTuningAdvisorTests
{
    private const string Template = TcpTuningAdvisor.DefaultTcpTemplate;

    [Fact]
    public void BandwidthDelayProductIsBandwidthTimesRoundTrip()
    {
        // 50 Mbit/s over 20 ms needs 125 KB in flight to keep the link busy.
        Assert.Equal(125_000, TcpTuningAdvisor.BandwidthDelayProductBytes(50_000_000, 20), 0);
        Assert.Equal(0, TcpTuningAdvisor.BandwidthDelayProductBytes(50_000_000, 0));
    }

    [Fact]
    public void TheUnscaledWindowCeilingIsTheArithmeticThatMakesTheCostConcrete()
    {
        // 64 KB over 20 ms is about 26 Mbit/s, whatever the line can do.
        Assert.Equal(26.2, TcpTuningAdvisor.CeilingBitsPerSecond(TcpTuningAdvisor.UnscaledWindowBytes, 20) / 1_000_000, 1);
    }

    [Fact]
    public void BadDownloadBufferbloatProposesOneStepDownWithTheThroughputCostStated()
    {
        var action = TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.D), [AutoTuning("3")], Template);

        Assert.NotNull(action);
        var change = Assert.Single(action.Changes);
        Assert.Equal(("cim.MSFT_NetTCPSetting.AutoTuningLevelLocal", Template, "2"),
            (change.SettingId, change.TargetId, change.ProposedValue));
        Assert.Equal(RemediationOwner.PresetOrManual, action.Owner);
        Assert.Contains("mitigation, not a fix", action.TradeOff, StringComparison.Ordinal);
        Assert.Contains("122.1 KB", action.TradeOff, StringComparison.Ordinal);
        Assert.Contains("26.2 Mbit/s", action.TradeOff, StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyRestrictedGoesOneStepFurtherAndNoLower()
    {
        var restricted = TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.F), [AutoTuning("2")], Template);
        var highlyRestricted = TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.F), [AutoTuning("1")], Template);

        Assert.Equal("1", Assert.Single(restricted!.Changes).ProposedValue);
        // Disabled is never proposed: it pins the window whatever the path needs.
        Assert.Null(highlyRestricted);
    }

    [Fact]
    public void AGoodGradeProposesNothing() =>
        Assert.Null(TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.A), [AutoTuning("3")], Template));

    [Fact]
    public void AMachineThrottlingItselfIsToldToLetTheWindowGrowAgain()
    {
        // 500 Mbit/s over 100 ms needs 6 MB in flight; a held-down window caps it near 5 Mbit/s.
        var action = TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(500_000_000, 100), [AutoTuning("1")], Template);

        Assert.NotNull(action);
        Assert.Equal("3", Assert.Single(action.Changes).ProposedValue);
        Assert.Contains("capping its own downloads", action.Title, StringComparison.Ordinal);
        Assert.Contains("5.2 Mbit/s", action.ExpectedEffect, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestrictedWindowThatStillCoversThePathIsLeftAlone()
    {
        // 10 Mbit/s over 20 ms needs 25 KB, well inside the unscaled window: nothing is being lost.
        Assert.Null(TcpTuningAdvisor.Advise(new TcpPathMeasurement(10_000_000, 20), [AutoTuning("1")], Template));
    }

    [Fact]
    public void AValueTheProviderDoesNotOfferIsNeverProposed()
    {
        var withoutRestricted = AutoTuning("3") with
        {
            Choices = [new CapabilityChoice("3", "Normal"), new CapabilityChoice("4", "Experimental")]
        };

        Assert.Null(TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.D), [withoutRestricted], Template));
    }

    [Fact]
    public void AnotherTemplatesCapabilityIsNotUsedForThisOne()
    {
        var elsewhere = AutoTuning("3") with { InstanceKey = "Datacenter" };

        Assert.Null(TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.D), [elsewhere], Template));
    }

    [Fact]
    public void TheProposedValuePassesTheProvidersOwnValidation()
    {
        var capability = AutoTuning("3");
        var action = TcpTuningAdvisor.Advise(
            new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.D), [capability], Template);

        capability.Validate(Assert.Single(action!.Changes).ProposedValue!);
    }

    [Fact]
    public void BufferbloatKeepsItsRouterGuidanceAndGainsTheLocalMitigationBesideIt()
    {
        var finding = new DiagnosticFinding(
            DiagnosticScope.RouterOrAccess, DiagnosticConfidence.High,
            "Latency grows by 320 ms under download load", "Grade D.",
            "Shape the link on the router.", NetworkSegment.RouterOrAccess, RemediationOwner.Router);

        var actions = RemediationPlanner.Plan([finding], new RemediationContext(
            GlobalCapabilities: [AutoTuning("3")],
            Path: new TcpPathMeasurement(50_000_000, 20, BufferbloatGrade.D)));

        Assert.Equal(2, actions.Count);
        Assert.Equal(RemediationOwner.Router, actions[0].Owner);
        Assert.False(actions[0].AppliesLocally);
        Assert.Equal(RemediationOwner.PresetOrManual, actions[1].Owner);
        Assert.True(actions[1].AppliesLocally);
    }

    [Fact]
    public void WithoutAMeasurementNothingIsProposedForTheRouterFinding()
    {
        var finding = new DiagnosticFinding(
            DiagnosticScope.RouterOrAccess, DiagnosticConfidence.High, "Bufferbloat", "Grade D.",
            "Shape the link on the router.", NetworkSegment.RouterOrAccess, RemediationOwner.Router);

        Assert.Single(RemediationPlanner.Plan([finding], new RemediationContext(GlobalCapabilities: [AutoTuning("3")])));
    }

    [Fact]
    public void NoProfileShipsAGlobalTcpValue()
    {
        // The stack defaults are right on current Windows, and the one setting worth moving depends
        // on the measured path. A profile that carried a value here would be guessing.
        Assert.All(UseCaseProfiles.All, profile =>
            Assert.DoesNotContain(profile.System, change =>
                change.SettingId.StartsWith(SettingSpecifications.CimPrefix, StringComparison.Ordinal)));
    }

    private static GlobalSettingCapability AutoTuning(string current) => new(
        CimGlobalPropertyCatalog.TcpSettingClass, Template, "AutoTuningLevelLocal", "Receive window auto-tuning",
        "TCP receive window", current,
        [
            new CapabilityChoice("0", "Disabled"),
            new CapabilityChoice("1", "HighlyRestricted"),
            new CapabilityChoice("2", "Restricted"),
            new CapabilityChoice("3", "Normal"),
            new CapabilityChoice("4", "Experimental")
        ],
        null, null, EvidenceLevel.Documented, ChangeRisk.Medium, "None", "Test trade-off.");
}
