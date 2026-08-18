using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class ResponsibilityAssignerTests
{
    [Theory]
    [InlineData(NetworkSegment.IspAccess)]
    [InlineData(NetworkSegment.IspCore)]
    [InlineData(NetworkSegment.ExternalHop)]
    [InlineData(NetworkSegment.RemoteEndpoint)]
    public void BeyondTheRouter_IsAlwaysOutOfScopeWhateverControlIsClaimed(NetworkSegment segment)
    {
        // Even if a caller claims SockTuner can act, nothing local changes an ISP or remote fault.
        // Offering a fix here would be dishonest, so the segment wins over the claimed control.
        foreach (var control in Enum.GetValues<LocalControl>())
        {
            Assert.Equal(RemediationOwner.OutOfScope, ResponsibilityAssigner.Assign(segment, control));
        }
    }

    [Fact]
    public void RouterSegment_IsAlwaysOwnedByTheRouter()
    {
        foreach (var control in Enum.GetValues<LocalControl>())
        {
            Assert.Equal(
                RemediationOwner.Router,
                ResponsibilityAssigner.Assign(NetworkSegment.RouterOrAccess, control));
        }
    }

    [Theory]
    [InlineData(LocalControl.AutomaticSafe, RemediationOwner.Automatic)]
    [InlineData(LocalControl.RequiresChoice, RemediationOwner.PresetOrManual)]
    [InlineData(LocalControl.None, RemediationOwner.PresetOrManual)]
    public void LocalSegments_FollowHowMuchOfTheLeverSockTunerHolds(
        LocalControl control, RemediationOwner expected)
    {
        Assert.Equal(expected, ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, control));
        Assert.Equal(expected, ResponsibilityAssigner.Assign(NetworkSegment.Lan, control));
    }

    [Theory]
    [InlineData(DiagnosticScope.LocalPc, NetworkSegment.LocalNicDriver)]
    [InlineData(DiagnosticScope.Lan, NetworkSegment.Lan)]
    [InlineData(DiagnosticScope.RouterOrAccess, NetworkSegment.RouterOrAccess)]
    [InlineData(DiagnosticScope.IspOrRouting, NetworkSegment.IspCore)]
    [InlineData(DiagnosticScope.GameEndpoint, NetworkSegment.RemoteEndpoint)]
    [InlineData(DiagnosticScope.Dns, NetworkSegment.LocalNicDriver)]
    [InlineData(DiagnosticScope.General, NetworkSegment.Unknown)]
    public void ScopeMapsOntoTheSegmentChain(DiagnosticScope scope, NetworkSegment expected)
    {
        Assert.Equal(expected, ResponsibilityAssigner.SegmentFor(scope));
    }

    [Fact]
    public void Attribute_FillsSegmentAndOwnerButKeepsAnExplicitSegment()
    {
        var implicitSegment = new DiagnosticFinding(
            DiagnosticScope.IspOrRouting, DiagnosticConfidence.Medium, "t", "e", "a");
        var explicitSegment = implicitSegment with { Segment = NetworkSegment.RouterOrAccess };

        Assert.Equal(
            NetworkSegment.IspCore,
            ResponsibilityAssigner.Attribute(implicitSegment, LocalControl.None).Segment);
        Assert.Equal(
            NetworkSegment.RouterOrAccess,
            ResponsibilityAssigner.Attribute(explicitSegment, LocalControl.None).Segment);
        Assert.Equal(
            RemediationOwner.Router,
            ResponsibilityAssigner.Attribute(explicitSegment, LocalControl.None).Owner);
    }

    [Fact]
    public void EveryOwnerHasAnExplanationTheUserCanActOn()
    {
        foreach (var owner in Enum.GetValues<RemediationOwner>())
        {
            var explanation = ResponsibilityAssigner.Explain(owner);
            Assert.False(string.IsNullOrWhiteSpace(explanation));
            Assert.EndsWith(".", explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnalyzerFindings_AllCarryADerivedSegmentAndOwner()
    {
        var findings = new GamingDiagnosisAnalyzer().Analyze(
            Probe("Gateway", loss: 20, p95: 60),
            Probe("Reference", loss: 5, p95: 120),
            Probe("Game", loss: 8, p95: 150),
            new DnsMeasurement("game.test", TimeSpan.FromMilliseconds(900), ["192.0.2.1"], null));

        Assert.NotEmpty(findings);
        Assert.All(findings, finding =>
        {
            // The owner must be consistent with the segment: derivation, not per-finding opinion.
            Assert.Equal(
                finding.Owner,
                ResponsibilityAssigner.Assign(finding.Segment, ControlFor(finding.Scope)));
            Assert.False(string.IsNullOrWhiteSpace(finding.OwnerDisplay));
        });
    }

    [Fact]
    public void EndpointAndRoutingFindings_AreNeverPresentedAsFixable()
    {
        var findings = new GamingDiagnosisAnalyzer().Analyze(
            Probe("Gateway", loss: 0, p95: 3),
            Probe("Reference", loss: 0, p95: 12),
            Probe("Game", loss: 0, p95: 200) with { MinimumMs = 180 },
            new DnsMeasurement("game.test", TimeSpan.FromMilliseconds(10), ["192.0.2.1"], null));

        Assert.All(
            findings.Where(finding => finding.Scope is DiagnosticScope.GameEndpoint or DiagnosticScope.IspOrRouting),
            finding => Assert.Equal(RemediationOwner.OutOfScope, finding.Owner));
    }

    private static LocalControl ControlFor(DiagnosticScope scope) => scope switch
    {
        DiagnosticScope.Dns or DiagnosticScope.LocalPc or DiagnosticScope.Lan => LocalControl.RequiresChoice,
        _ => LocalControl.None
    };

    private static ProbeStatistics Probe(string label, double loss, double p95) =>
        new(label, label, 20, 18, loss, 5, 10, 12, p95, p95, p95 + 5, 4, []);
}
