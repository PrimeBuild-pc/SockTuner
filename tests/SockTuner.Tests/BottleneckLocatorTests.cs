using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Localisation is a pure function over collected facts, so every rule is exercised against a
/// fixture with no network and no host access.
/// </summary>
public sealed class BottleneckLocatorTests
{
    private readonly BottleneckLocator _locator = new();

    [Fact]
    public void LocalCounterErrors_AreBlamedBeforeAnythingOnTheWire()
    {
        var input = Input(
            local: LocalLinkEvidence.Healthy with { ReceiveErrors = 500, TransmitDiscards = 40 },
            gateway: Clean("Gateway"),
            reference: Clean("Reference"),
            target: Clean("Target"));

        var result = _locator.Locate(input);

        Assert.Equal(NetworkSegment.LocalNicDriver, result.Segment);
        Assert.Equal(DiagnosticConfidence.High, result.Confidence);
        Assert.Contains(result.Supporting, item => item.Contains("500 receive errors", StringComparison.Ordinal));
        Assert.NotEmpty(result.Contradicting);
    }

    [Fact]
    public void DownLink_IsReportedWithoutBlamingTheNetwork()
    {
        var result = _locator.Locate(Input(
            local: LocalLinkEvidence.Healthy with { LinkUp = false },
            gateway: Clean("Gateway"), reference: Clean("Reference"), target: Clean("Target")));

        Assert.Equal(NetworkSegment.LocalNicDriver, result.Segment);
        Assert.Equal(DiagnosticConfidence.High, result.Confidence);
    }

    [Fact]
    public void DegradedGateway_LocalisesToTheLan()
    {
        var result = _locator.Locate(Input(
            local: LocalLinkEvidence.Healthy,
            gateway: Degraded("Gateway", loss: 12, p95: 40, jitter: 15),
            reference: Degraded("Reference", loss: 12, p95: 60, jitter: 16),
            target: Degraded("Target", loss: 12, p95: 70, jitter: 17)));

        Assert.Equal(NetworkSegment.Lan, result.Segment);
        Assert.Equal(RemediationOwner.PresetOrManual, result.Owner);
    }

    [Fact]
    public void WirelessLan_IsCalledOutAsRadioRatherThanCabling()
    {
        var result = _locator.Locate(Input(
            local: LocalLinkEvidence.Healthy with { IsWireless = true },
            gateway: Degraded("Gateway", loss: 6, p95: 30, jitter: 12),
            reference: Degraded("Reference", loss: 6, p95: 40, jitter: 12),
            target: Degraded("Target", loss: 6, p95: 45, jitter: 13)));

        Assert.Equal(NetworkSegment.Lan, result.Segment);
        Assert.Contains(result.Supporting, item => item.Contains("wireless", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanGatewayWithDegradedReference_PointsPastTheRouterAndIsUnfixable()
    {
        var result = _locator.Locate(Input(
            local: LocalLinkEvidence.Healthy,
            gateway: Clean("Gateway"),
            reference: Degraded("Reference", loss: 6, p95: 90, jitter: 20),
            target: Degraded("Target", loss: 6, p95: 95, jitter: 21)));

        Assert.Equal(NetworkSegment.IspAccess, result.Segment);
        Assert.Equal(RemediationOwner.OutOfScope, result.Owner);
        Assert.NotEmpty(result.Contradicting);
    }

    [Fact]
    public void CarrierGradeNat_IsReportedAsAnIspLimitNotAPerformanceFault()
    {
        var route = Route(
            Hop(1, "192.168.1.1", HopAddressKind.Private, observed: 5, responded: 5),
            Hop(2, "100.70.0.1", HopAddressKind.CarrierGrade, observed: 5, responded: 5),
            Hop(3, "203.0.113.1", HopAddressKind.Public, observed: 5, responded: 5));

        var result = _locator.Locate(Input(
            LocalLinkEvidence.Healthy, Clean("Gateway"), Clean("Reference"), Clean("Target"), route));

        Assert.Equal(NetworkSegment.IspAccess, result.Segment);
        Assert.Equal(RemediationOwner.OutOfScope, result.Owner);
        Assert.Contains("carrier-grade NAT", result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Contradicting, item => item.Contains("reachability, not speed", StringComparison.Ordinal));
    }

    [Fact]
    public void HopThatOnlyRateLimitsIcmp_IsNotBlamed()
    {
        // Hop 2 drops 60% of probes addressed to itself, but hops 3 and 4 are clean, which proves
        // it was forwarding traffic the whole time.
        var route = Route(
            Hop(1, "192.168.1.1", HopAddressKind.Private, 10, 10),
            Hop(2, "203.0.113.1", HopAddressKind.Public, 10, 4),
            Hop(3, "203.0.113.2", HopAddressKind.Public, 10, 10),
            Hop(4, "203.0.113.3", HopAddressKind.Public, 10, 10));

        Assert.Contains(route.RateLimitedHops, hop => hop.TimeToLive == 2);
        Assert.Empty(route.PersistentLossHops);

        var result = _locator.Locate(Input(
            LocalLinkEvidence.Healthy, Clean("Gateway"), Clean("Reference"), Clean("Target"), route));

        Assert.NotEqual(NetworkSegment.IspCore, result.Segment);
    }

    [Fact]
    public void LossThatContinuesDownstream_IsBlamedAtTheHopWhereItStarts()
    {
        var route = Route(
            Hop(1, "192.168.1.1", HopAddressKind.Private, 10, 10),
            Hop(2, "203.0.113.1", HopAddressKind.Public, 10, 10),
            Hop(3, "203.0.113.2", HopAddressKind.Public, 10, 5),
            Hop(4, "203.0.113.3", HopAddressKind.Public, 10, 4),
            Hop(5, "203.0.113.4", HopAddressKind.Public, 10, 4));

        var persistent = Assert.Single(route.PersistentLossHops);
        Assert.Equal(3, persistent.TimeToLive);

        var result = _locator.Locate(Input(
            LocalLinkEvidence.Healthy, Clean("Gateway"), Clean("Reference"), Clean("Target"), route));

        // The first few public hops are the ISP access network; deeper hops are its core.
        Assert.Equal(NetworkSegment.IspAccess, result.Segment);
        Assert.Equal(RemediationOwner.OutOfScope, result.Owner);
        Assert.Contains("hop 3", result.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LossStartingDeepInThePath_IsAttributedToTheIspCoreNotTheAccessLink()
    {
        var route = Route(
            Hop(1, "192.168.1.1", HopAddressKind.Private, 10, 10),
            Hop(2, "203.0.113.1", HopAddressKind.Public, 10, 10),
            Hop(3, "203.0.113.2", HopAddressKind.Public, 10, 10),
            Hop(4, "203.0.113.3", HopAddressKind.Public, 10, 10),
            Hop(5, "203.0.113.4", HopAddressKind.Public, 10, 10),
            Hop(6, "198.51.100.1", HopAddressKind.Public, 10, 4),
            Hop(7, "198.51.100.2", HopAddressKind.Public, 10, 4));

        var result = _locator.Locate(Input(
            LocalLinkEvidence.Healthy, Clean("Gateway"), Clean("Reference"), Clean("Target"), route));

        Assert.Equal(NetworkSegment.IspCore, result.Segment);
        Assert.Equal(RemediationOwner.OutOfScope, result.Owner);
        Assert.Contains("hop 6", result.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthyReferenceWithWorseTarget_PointsAtTheEndpointAndSaysDistanceIsNotAFault()
    {
        var result = _locator.Locate(Input(
            LocalLinkEvidence.Healthy,
            Clean("Gateway"),
            Clean("Reference"),
            Degraded("Target", loss: 0, p95: 90, jitter: 3) with { MinimumMs = 80 }));

        Assert.Equal(NetworkSegment.RemoteEndpoint, result.Segment);
        Assert.Equal(RemediationOwner.OutOfScope, result.Owner);
        Assert.Contains(result.Contradicting, item => item.Contains("propagation delay", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingWrong_StaysInconclusiveRatherThanInventingACause()
    {
        var result = _locator.Locate(Input(
            LocalLinkEvidence.Healthy, Clean("Gateway"), Clean("Reference"), Clean("Target")));

        Assert.Equal(NetworkSegment.Unknown, result.Segment);
        Assert.False(result.IsConclusive);
        Assert.Equal(DiagnosticConfidence.Low, result.Confidence);
    }

    private static BottleneckInput Input(
        LocalLinkEvidence local,
        ProbeStatistics gateway,
        ProbeStatistics reference,
        ProbeStatistics target,
        RoutePathDiagnostic? route = null) => new(local, gateway, reference, target, route);

    private static RoutePathDiagnostic Route(params HopMeasurement[] hops) =>
        new("198.51.100.10", DateTimeOffset.UnixEpoch, 10, hops, true, null);

    private static HopMeasurement Hop(int ttl, string address, HopAddressKind kind, int observed, int responded) =>
        new(ttl, address, kind,
            ProbeStatistics.Calculate($"Hop {ttl}", address,
                Enumerable.Range(0, observed)
                    .Select(index => index < responded
                        ? new ProbeSample(DateTimeOffset.UnixEpoch.AddSeconds(index), 10 + ttl)
                        : new ProbeSample(DateTimeOffset.UnixEpoch.AddSeconds(index), null, "TimedOut"))
                    .ToArray()),
            observed, responded, []);

    private static ProbeStatistics Clean(string label) =>
        new(label, label, 20, 20, 0, 1, 2, 2, 4, 5, 6, 1, []);

    private static ProbeStatistics Degraded(string label, double loss, double p95, double jitter) =>
        new(label, label, 20, 18, loss, 5, 10, 12, p95, p95, p95 + 5, jitter, []);
}
