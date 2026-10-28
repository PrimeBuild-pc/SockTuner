using System.Net;

namespace SockTuner.Models;

public enum NatMappingObservation { NotMeasured, SameOnTestedDestinations, ChangedOnTestedDestinations }
public enum NatFilteringObservation { NotMeasured, Unsupported, Inconclusive, AlternatePortReceived, AlternatePortFiltered }
public enum NatProbeFailure { None, PortUnavailable, NoBaseline, NetworkError }

public sealed record NatDiagnosticResult(
    int LocalPort,
    IPEndPoint? PublicEndpoint,
    NatMappingObservation Mapping,
    NatFilteringObservation Filtering,
    NatProbeFailure Failure = NatProbeFailure.None,
    string? Detail = null);

public static class NatGamerReading
{
    /// <summary>Plain-language reading for peer-to-peer play. Client-to-server games do not depend on it.</summary>
    public static string Of(NatDiagnosticResult r) => (r.Mapping, r.Filtering) switch
    {
        (NatMappingObservation.ChangedOnTestedDestinations, _) =>
            "Strict-like: the public port changes per destination. Peer-to-peer, party chat and hosting may fail or use a relay; normal play on dedicated game servers is usually unaffected.",
        (NatMappingObservation.SameOnTestedDestinations, NatFilteringObservation.AlternatePortReceived) =>
            "Open-like: the public port is stable and replies from another server port arrive. Peer connections should work.",
        (NatMappingObservation.SameOnTestedDestinations, NatFilteringObservation.AlternatePortFiltered) =>
            "Moderate-like: the public port is stable, replies only after contact. Peer connections usually work; dedicated game servers are unaffected.",
        (NatMappingObservation.SameOnTestedDestinations, _) =>
            "Stable public port across the tested destinations; filtering was not determined. Peer connections should usually work.",
        _ => "Not enough evidence to read this as Open, Moderate or Strict."
    };
}

public sealed record GamePortPreset(string Name, int SuggestedPort, string PublishedUdpPorts, string SourceUrl, string Scope)
{
    public override string ToString() => Name;
}

/// <summary>Offline references, not instructions to open every listed port. Reviewed 2026-10-19.</summary>
public static class GamePortPresets
{
    private const string Activision = "https://support.activision.com/articles/ports-used-for-call-of-duty-games";
    private const string PcPorts = "3074, 3478, 4379-4380, 6112-6119, 20500, 20510, 27000-27031, 27036, 28960";
    public static IReadOnlyList<GamePortPreset> All { get; } =
    [
        new("Generic / custom UDP", 0, "", "", "Choose one local UDP port; 0 requests a temporary port."),
        new("Call of Duty: Warzone / Black Ops 6 / Black Ops 7 (PC)", 3074, PcPorts, Activision,
            "Publisher PC list; launchers and versions can use different ports."),
        new("Call of Duty: Modern Warfare II / III (Steam)", 3074, "3074, 27015, 27031-27036", Activision,
            "Publisher Steam list; not every listed port is a local listening port."),
        new("Minecraft Bedrock (hosting)", 19132, "19132 (IPv4)",
            "https://learn.microsoft.com/en-us/minecraft/creator/documents/bedrockserver/server-properties",
            "Dedicated server default, not a claim about a client's source port."),
        new("Counter-Strike 2 (hosting)", 27015, "27015 (default server port)",
            "https://developer.valvesoftware.com/wiki/Counter-Strike_2/Dedicated_Servers",
            "Dedicated server default, not a claim about a client's source port.")
    ];
}

public sealed record NatGuidance(string Priority, string Action, string When, string Risk);

public static class NatSafetyGuidance
{
    // Ordered by intervention preference, not a universal security ranking: scope matters.
    public static IReadOnlyList<NatGuidance> All { get; } =
    [
        new("1", "Keep a working connection", "No game, party or voice-chat symptoms.", "No extra exposure. A restrictive test is not a fault or a latency measurement."),
        new("2", "Check the affected game and network", "Reproduce a specific problem; compare the router WAN IPv4 with the public mapping and check game firewall rules.", "Read-only first. A private traceroute hop alone does not prove double NAT or CGNAT."),
        new("3", "Forward only required ports", "A confirmed inbound requirement, a public WAN address and a stable LAN address.", "Exposes selected services. Use the exact game/platform documentation, not every port in a generic list."),
        new("4", "Consider UPnP on a trusted LAN", "A game needs automatic mappings, especially with multiple devices.", "LAN applications can request openings. Avoid overlapping manual rules; review mappings and keep firmware updated."),
        new("5", "Consider Full Cone NAT only if needed", "A reproducible peer-to-peer issue remains; compare before and after, then revert if it does not help.", "Broadens who can reach active mappings. Not equivalent to DMZ, not guaranteed to improve games or ping."),
        new("6", "Avoid DMZ and disabling firewalls", "Not a remedy for an orange NAT badge.", "Broad exposure of listening services. Keep router and host firewalls enabled.")
    ];
}
