using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>Everything localisation needs, already collected. This layer never touches the network.</summary>
public sealed record BottleneckInput(
    LocalLinkEvidence Local,
    ProbeStatistics Gateway,
    ProbeStatistics Reference,
    ProbeStatistics Target,
    RoutePathDiagnostic? Route = null);

/// <summary>
/// Finds the first place along the chain where quality drops, rather than reporting only that it
/// dropped. Walks outward — NIC, LAN, router, ISP, external hop, endpoint — and stops at the first
/// segment that degrades relative to the one before it.
/// </summary>
public sealed class BottleneckLocator
{
    // A hop or segment must add at least this much over its predecessor before it counts as the
    // place the problem starts. Below it, the difference is measurement noise on a short run.
    private const double LatencyStepMs = 25;
    private const double JitterStepMs = 8;
    private const double LossStepPercent = 2;

    public BottleneckAssessment Locate(BottleneckInput input)
    {
        var supporting = new List<string>();
        var contradicting = new List<string>();

        if (TryLocalFault(input, supporting, contradicting) is { } local) return local;
        if (TryLanFault(input, supporting, contradicting) is { } lan) return lan;
        if (TryCarrierGradeNat(input) is { } cgnat) return cgnat;
        if (TryRouteFault(input) is { } route) return route;
        if (TryUpstreamFault(input, supporting, contradicting) is { } upstream) return upstream;
        if (TryEndpointFault(input, supporting, contradicting) is { } endpoint) return endpoint;

        return new BottleneckAssessment(
            NetworkSegment.Unknown,
            DiagnosticConfidence.Low,
            RemediationOwner.PresetOrManual,
            "No bottleneck isolated by this run",
            [$"Gateway: {input.Gateway.Summary}", $"Reference: {input.Reference.Summary}"],
            ["A short run cannot rule out an intermittent fault; repeat while the problem is happening."]);
    }

    // Driver and cable faults show up as counter errors and link problems, not as latency, so they
    // are checked before anything on the wire is blamed.
    private static BottleneckAssessment? TryLocalFault(
        BottleneckInput input, List<string> supporting, List<string> contradicting)
    {
        if (!input.Local.LinkUp)
        {
            return new BottleneckAssessment(
                NetworkSegment.LocalNicDriver, DiagnosticConfidence.High, RemediationOwner.PresetOrManual,
                "The adapter has no link",
                ["The selected adapter reports its link as down."],
                []);
        }

        if (input.Local.TotalErrors > 0)
        {
            supporting.Add(
                $"NIC counters: {input.Local.ReceiveErrors} receive errors, {input.Local.ReceiveDiscards} receive discards, "
                + $"{input.Local.TransmitErrors} transmit errors, {input.Local.TransmitDiscards} transmit discards.");
            if (input.Gateway.Received > 0 && input.Gateway.LossPercent == 0)
            {
                contradicting.Add("The gateway answered every probe, so the errors are not currently costing reachability.");
            }

            return new BottleneckAssessment(
                NetworkSegment.LocalNicDriver,
                input.Local.TotalErrors > 100 ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, LocalControl.RequiresChoice),
                "The local adapter is discarding or corrupting frames",
                supporting.ToArray(),
                contradicting.ToArray());
        }

        return null;
    }

    private static BottleneckAssessment? TryLanFault(
        BottleneckInput input, List<string> supporting, List<string> contradicting)
    {
        if (input.Gateway.Sent == 0 || input.Gateway.Received == 0)
        {
            return null;
        }

        var unstable = input.Gateway.LossPercent > LossStepPercent
            || input.Gateway.P95Ms > 10
            || input.Gateway.JitterMs > 3;
        if (!unstable)
        {
            return null;
        }

        supporting.Add($"Gateway: {input.Gateway.Summary}.");
        if (input.Local.IsWireless)
        {
            supporting.Add("The adapter is wireless, so interference, distance and channel congestion are plausible causes before any cabling fault.");
        }

        return new BottleneckAssessment(
            NetworkSegment.Lan,
            input.Gateway.LossPercent >= 10 || input.Gateway.P95Ms > 25 ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
            ResponsibilityAssigner.Assign(NetworkSegment.Lan, LocalControl.RequiresChoice),
            input.Local.IsWireless
                ? "Quality degrades on the wireless link before it leaves the building"
                : "Quality degrades inside the local network",
            supporting.ToArray(),
            contradicting.ToArray());
    }

    // CGNAT is not a performance fault, but it is the answer to a whole class of "my router is
    // broken" reports, so it is surfaced explicitly and marked unfixable.
    private static BottleneckAssessment? TryCarrierGradeNat(BottleneckInput input)
    {
        if (input.Route?.CarrierGradeNatHop is not { } hop)
        {
            return null;
        }

        return new BottleneckAssessment(
            NetworkSegment.IspAccess, DiagnosticConfidence.High, RemediationOwner.OutOfScope,
            "The connection is behind carrier-grade NAT",
            [
                $"Hop {hop.TimeToLive} is {hop.Address}, inside the ISP shared range 100.64.0.0/10.",
                "Inbound connections, port forwarding and peer-to-peer NAT traversal cannot work reliably through it."
            ],
            ["This does not by itself add latency or loss; it affects reachability, not speed."]);
    }

    private static BottleneckAssessment? TryRouteFault(BottleneckInput input)
    {
        if (input.Route is not { } route)
        {
            return null;
        }

        var persistent = route.PersistentLossHops;
        if (persistent.Count == 0)
        {
            return null;
        }

        var first = persistent[0];
        var segment = first.AddressKind switch
        {
            HopAddressKind.Private => NetworkSegment.Lan,
            HopAddressKind.CarrierGrade => NetworkSegment.IspAccess,
            _ => first.TimeToLive <= 3 ? NetworkSegment.IspAccess : NetworkSegment.IspCore
        };

        var contradicting = new List<string>();
        if (route.RateLimitedHops.Count > 0)
        {
            contradicting.Add(
                $"{route.RateLimitedHops.Count} other hop(s) show loss that does not continue downstream; "
                + "those are rate-limiting ICMP rather than dropping traffic.");
        }

        return new BottleneckAssessment(
            segment,
            DiagnosticConfidence.Medium,
            ResponsibilityAssigner.Assign(segment, LocalControl.None),
            $"Loss begins at hop {first.TimeToLive} and continues to every hop beyond it",
            [
                $"Hop {first.TimeToLive} ({first.Address}) loses {first.LossPercent:0.#}% across {first.RoundsObserved} round(s).",
                "Every later hop loses at least as much, so this is the path rather than one router deprioritising ICMP."
            ],
            contradicting.ToArray());
    }

    private static BottleneckAssessment? TryUpstreamFault(
        BottleneckInput input, List<string> supporting, List<string> contradicting)
    {
        if (input.Reference.Received == 0 || input.Gateway.Received == 0)
        {
            return null;
        }

        var gatewayClean = input.Gateway.LossPercent == 0 && input.Gateway.P95Ms <= 10 && input.Gateway.JitterMs <= 3;
        var referenceDegraded = input.Reference.LossPercent > LossStepPercent
            || input.Reference.P95Ms > input.Gateway.P95Ms + LatencyStepMs
            || input.Reference.JitterMs > input.Gateway.JitterMs + JitterStepMs;
        if (!gatewayClean || !referenceDegraded)
        {
            return null;
        }

        supporting.Add($"Gateway is clean ({input.Gateway.Summary}) while the neutral reference is {input.Reference.Summary}.");
        supporting.Add("The step appears after the router, so the local network and the router queue are not the cause.");
        contradicting.Add("A single short run cannot separate a congested access link from transient ISP load; repeat under idle and loaded conditions.");

        return new BottleneckAssessment(
            NetworkSegment.IspAccess,
            DiagnosticConfidence.Medium,
            RemediationOwner.OutOfScope,
            "Quality degrades on the access link, past the router",
            supporting.ToArray(),
            contradicting.ToArray());
    }

    private static BottleneckAssessment? TryEndpointFault(
        BottleneckInput input, List<string> supporting, List<string> contradicting)
    {
        if (input.Target.Received == 0 || input.Reference.Received == 0)
        {
            return null;
        }

        var referenceHealthy = input.Reference.LossPercent <= 1 && input.Reference.JitterMs <= 5;
        var targetWorse = input.Target.LossPercent > input.Reference.LossPercent + LossStepPercent
            || input.Target.JitterMs > input.Reference.JitterMs + JitterStepMs
            || input.Target.MinimumMs > input.Reference.MinimumMs + LatencyStepMs;
        if (!referenceHealthy || !targetWorse)
        {
            return null;
        }

        supporting.Add($"Neutral reference is healthy ({input.Reference.Summary}) while the target is {input.Target.Summary}.");
        if (input.Target.MinimumMs > input.Reference.MinimumMs + LatencyStepMs)
        {
            contradicting.Add(
                "A higher best-case RTT is distance or region, not a fault: no local change removes propagation delay.");
        }

        return new BottleneckAssessment(
            NetworkSegment.RemoteEndpoint,
            DiagnosticConfidence.Medium,
            RemediationOwner.OutOfScope,
            "The problem is specific to the chosen endpoint or its region",
            supporting.ToArray(),
            contradicting.ToArray());
    }
}
