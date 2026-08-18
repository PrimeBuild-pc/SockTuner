using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Services.Diagnosis;

/// <summary>How many translation layers sit between this machine and a public address.</summary>
public enum NatTopology
{
    Unknown,

    /// <summary>One router translating one private network onto one public address: the normal case.</summary>
    SingleNat,

    /// <summary>Two devices translating in series — commonly an ISP modem left in router mode behind the user's own router.</summary>
    DoubleNat,

    /// <summary>The ISP itself is translating: no public address reaches the premises at all.</summary>
    CarrierGradeNat
}

/// <summary>
/// Everything the topology call needs. The router-reported WAN address and the observed public
/// address are optional: the first arrives with router integration, the second only from an
/// explicit external lookup. The verdict degrades honestly when they are absent rather than
/// guessing.
/// </summary>
public sealed record TopologyInput(
    RoutePathDiagnostic? Route = null,
    string? RouterWanAddress = null,
    string? ObservedPublicAddress = null,
    PathMtuResult? PathMtu = null,
    int? LocalInterfaceMtu = null);

public sealed record TopologyDiagnostic(NatTopology Topology, IReadOnlyList<DiagnosticFinding> Findings);

/// <summary>
/// Diagnosis layer: the shape of the path rather than its speed. How many NAT layers the connection
/// crosses is the answer to a whole class of "my router is broken" reports about port forwarding,
/// peer-to-peer and game hosting, and a path MTU black hole is the answer to "ping works but
/// downloads stall". Neither shows up in a latency measurement at all.
/// </summary>
public static class TopologyAnalyzer
{
    public static TopologyDiagnostic Analyze(TopologyInput input)
    {
        var findings = new List<DiagnosticFinding>();
        if (PathMtuFinding(input) is { } mtu)
        {
            findings.Add(mtu);
        }

        var routerWanKind = RouteQualityProbe.ClassifyAddress(input.RouterWanAddress);

        var cgnatHop = input.Route?.CarrierGradeNatHop;
        if (cgnatHop is not null || routerWanKind == HopAddressKind.CarrierGrade)
        {
            var evidence = cgnatHop is null
                ? $"The router reports its WAN address as {input.RouterWanAddress}, inside the ISP shared range 100.64.0.0/10."
                : $"Hop {cgnatHop.TimeToLive} is {cgnatHop.Address}, inside the ISP shared range 100.64.0.0/10 (RFC 6598).";
            findings.Add(new DiagnosticFinding(
                DiagnosticScope.IspOrRouting,
                DiagnosticConfidence.High,
                "The connection is behind carrier-grade NAT",
                evidence + " Inbound connections, port forwarding and peer-to-peer NAT traversal cannot work reliably through it, "
                    + "and no router setting changes that. It does not by itself add latency or loss.",
                "Ask the ISP for a public IPv4 address — many offer one on request or as a paid option — or use IPv6 where the "
                    + "application supports it. Nothing on this machine or on the router can work around it.",
                NetworkSegment.IspAccess,
                RemediationOwner.OutOfScope));
            return new TopologyDiagnostic(NatTopology.CarrierGradeNat, findings);
        }

        var cascade = PrivateHopsBeforeFirstPublic(input.Route);
        if (routerWanKind == HopAddressKind.Private)
        {
            findings.Add(DoubleNat(
                $"The router reports its WAN address as {input.RouterWanAddress}, which is a private address, "
                + "so a second device upstream is translating as well.",
                DiagnosticConfidence.High));
            return new TopologyDiagnostic(NatTopology.DoubleNat, findings);
        }

        if (cascade.Count > 1)
        {
            findings.Add(DoubleNat(
                $"The path crosses {cascade.Count} private hops before reaching a public address: "
                + string.Join(" → ", cascade.Select(hop => $"{hop.TimeToLive}:{hop.Address}")) + ".",
                DiagnosticConfidence.Medium,
                "Some ISPs number their own access equipment out of private space, which produces the same pattern without a "
                + "second NAT. The router's own WAN address settles it."));
            return new TopologyDiagnostic(NatTopology.DoubleNat, findings);
        }

        // A public WAN address that is not the address the internet sees means something upstream is
        // still translating, even though neither address looks private.
        if (routerWanKind == HopAddressKind.Public
            && input.ObservedPublicAddress is { Length: > 0 } observed
            && !string.Equals(input.RouterWanAddress, observed, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(DoubleNat(
                $"The router reports its WAN address as {input.RouterWanAddress}, but this connection is seen from the "
                + $"internet as {observed}.",
                DiagnosticConfidence.Medium,
                "A router that reports a stale or cached WAN address produces the same mismatch; re-read it before acting."));
            return new TopologyDiagnostic(NatTopology.DoubleNat, findings);
        }

        if (routerWanKind == HopAddressKind.Public || (cascade.Count == 1 && input.Route?.FirstPublicHop is not null))
        {
            return new TopologyDiagnostic(NatTopology.SingleNat, findings);
        }

        return new TopologyDiagnostic(NatTopology.Unknown, findings);
    }

    /// <summary>
    /// The fix for a black hole is local even though the cause is not: the machine stops emitting
    /// packets the path silently discards. The exact value comes from the measurement, but it
    /// briefly disrupts the link, so it stays a confirmed change rather than an automatic one.
    /// </summary>
    private static DiagnosticFinding? PathMtuFinding(TopologyInput input)
    {
        if (input.PathMtu is not { State: PathMtuState.IcmpBlackHole, Mtu: { } discovered })
        {
            return null;
        }

        var configured = input.LocalInterfaceMtu is { } local ? $" This interface is set to {local} bytes." : string.Empty;
        return new DiagnosticFinding(
            DiagnosticScope.IspOrRouting,
            DiagnosticConfidence.High,
            "Path MTU discovery is black-holed on this path",
            $"Packets above {discovered} bytes are dropped without the 'fragmentation needed' reply that path MTU discovery "
                + $"depends on; the same packets get through when they may be fragmented.{configured} A sender left guessing "
                + "produces stalled transfers and half-loading pages while ping keeps working.",
            $"Set the interface MTU to {discovered} bytes so the machine stops emitting packets the path discards silently. "
                + "The value comes from the measurement, but the change interrupts the link briefly, so confirm it first.",
            NetworkSegment.LocalNicDriver,
            RemediationOwner.PresetOrManual);
    }

    private static DiagnosticFinding DoubleNat(string evidence, DiagnosticConfidence confidence, string? caveat = null) =>
        new(DiagnosticScope.RouterOrAccess,
            confidence,
            "The connection crosses two NAT layers",
            evidence + " Two devices translating in series break inbound connections and NAT traversal, and each adds its own "
                + "connection table to exhaust." + (caveat is null ? string.Empty : " " + caveat),
            "Exactly one device should perform NAT. Put the upstream modem or ONT into bridge mode, or disable NAT and DHCP on "
                + "the inner router and let it work as an access point.",
            NetworkSegment.RouterOrAccess,
            RemediationOwner.Router);

    /// <summary>
    /// Distinct private addresses seen before the first public hop. Two of them means two routers,
    /// which is the traceroute signature of a cascaded NAT.
    /// </summary>
    private static IReadOnlyList<HopMeasurement> PrivateHopsBeforeFirstPublic(RoutePathDiagnostic? route)
    {
        if (route is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HopMeasurement>();
        foreach (var hop in route.Hops.Where(hop => hop.Responded))
        {
            if (hop.AddressKind != HopAddressKind.Private)
            {
                break;
            }

            if (seen.Add(hop.Address))
            {
                result.Add(hop);
            }
        }

        return result;
    }
}
