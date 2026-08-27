using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// NAT topology and path-MTU facts. Pure over collected route data: no network, no host access.
/// </summary>
public sealed class TopologyAnalyzerTests
{
    [Fact]
    public void CarrierGradeNatHop_IsOutOfScopeAndNeverBlamedOnTheRouter()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(Route("192.168.1.1", "100.72.0.1", "203.0.113.1")));

        Assert.Equal(NatTopology.CarrierGradeNat, result.Topology);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(RemediationOwner.OutOfScope, finding.Owner);
        Assert.Equal(NetworkSegment.IspAccess, finding.Segment);
        Assert.Contains("100.64.0.0/10", finding.Evidence, StringComparison.Ordinal);
        Assert.Contains("does not by itself add latency", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterReportingAPrivateWanAddress_IsDoubleNatWithAnExactFix()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(
            Route("192.168.1.1", "203.0.113.1"), RouterWanAddress: "192.168.100.2"));

        Assert.Equal(NatTopology.DoubleNat, result.Topology);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(RemediationOwner.Router, finding.Owner);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains("bridge mode", finding.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPrivateHops_AreDoubleNatButOnlyAtMediumConfidence()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(Route("192.168.1.1", "10.0.0.1", "203.0.113.1")));

        Assert.Equal(NatTopology.DoubleNat, result.Topology);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);
        Assert.Contains("private space", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void OneRouterOntoAPublicHop_IsSingleNatWithNothingToReport()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(Route("192.168.1.1", "203.0.113.1", "198.51.100.1")));

        Assert.Equal(NatTopology.SingleNat, result.Topology);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void PublicWanAddressThatDiffersFromTheObservedOne_IsUpstreamTranslation()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(
            Route("192.168.1.1", "203.0.113.1"),
            RouterWanAddress: "203.0.113.9",
            ObservedPublicAddress: "198.51.100.77"));

        Assert.Equal(NatTopology.DoubleNat, result.Topology);
        Assert.Contains("stale or cached", Assert.Single(result.Findings).Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEvidenceAtAll_StaysUnknown()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput());

        Assert.Equal(NatTopology.Unknown, result.Topology);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BlackHoledPathMtu_ProducesALocalWorkaroundWithTheMeasuredValue()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(
            Route("192.168.1.1", "203.0.113.1"),
            PathMtu: new PathMtuResult(PathMtuState.IcmpBlackHole, 1420, "measured"),
            LocalInterfaceMtu: 1500));

        var finding = Assert.Single(result.Findings, item => item.Segment == NetworkSegment.LocalNicDriver);
        Assert.Equal(RemediationOwner.PresetOrManual, finding.Owner);
        Assert.Contains("1420", finding.Action, StringComparison.Ordinal);
        Assert.Contains("set to 1500 bytes", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkingPathMtuDiscovery_ProducesNoFinding()
    {
        var result = TopologyAnalyzer.Analyze(new TopologyInput(
            Route("192.168.1.1", "203.0.113.1"),
            PathMtu: new PathMtuResult(PathMtuState.Discovered, 1500, "measured")));

        Assert.Empty(result.Findings);
    }

    private static RoutePathDiagnostic Route(params string[] addresses) => new(
        "198.51.100.10",
        DateTimeOffset.Now,
        3,
        addresses.Select((address, index) => new HopMeasurement(
            index + 1,
            address,
            SockTuner.Services.Collection.RouteQualityProbe.ClassifyAddress(address),
            ProbeStatistics.Calculate($"Hop {index + 1}", address, [new ProbeSample(DateTimeOffset.Now, 5)]),
            3,
            3,
            [])).ToArray(),
        true,
        null);
}
