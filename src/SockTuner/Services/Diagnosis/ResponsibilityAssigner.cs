using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Decides who has to act on a finding. The owner is always derived from where the problem is and
/// how much of the lever SockTuner actually holds, so two findings about the same segment cannot
/// disagree about who owns the fix.
/// </summary>
public static class ResponsibilityAssigner
{
    public static RemediationOwner Assign(NetworkSegment segment, LocalControl control) => segment switch
    {
        // Beyond the router nothing local changes the outcome. Offering a "fix" here would be a
        // lie, however tempting: the honest product behaviour is diagnose, explain, escalate.
        NetworkSegment.IspAccess or NetworkSegment.IspCore
            or NetworkSegment.ExternalHop or NetworkSegment.RemoteEndpoint => RemediationOwner.OutOfScope,

        // Queue management, SQM and access-link behaviour live on the router, not the endpoint.
        NetworkSegment.RouterOrAccess => RemediationOwner.Router,

        NetworkSegment.LocalNicDriver or NetworkSegment.Lan => control switch
        {
            LocalControl.AutomaticSafe => RemediationOwner.Automatic,
            LocalControl.RequiresChoice => RemediationOwner.PresetOrManual,
            _ => RemediationOwner.PresetOrManual
        },

        _ => RemediationOwner.PresetOrManual
    };

    /// <summary>Maps the existing scope vocabulary onto the segment chain.</summary>
    public static NetworkSegment SegmentFor(DiagnosticScope scope) => scope switch
    {
        DiagnosticScope.LocalPc => NetworkSegment.LocalNicDriver,
        DiagnosticScope.Lan => NetworkSegment.Lan,
        DiagnosticScope.RouterOrAccess => NetworkSegment.RouterOrAccess,
        DiagnosticScope.IspOrRouting => NetworkSegment.IspCore,
        DiagnosticScope.GameEndpoint => NetworkSegment.RemoteEndpoint,

        // Resolver choice is a local, reversible setting even though DNS is a remote service.
        DiagnosticScope.Dns => NetworkSegment.LocalNicDriver,
        _ => NetworkSegment.Unknown
    };

    /// <summary>Fills in segment and owner for a finding that did not state them.</summary>
    public static DiagnosticFinding Attribute(DiagnosticFinding finding, LocalControl control)
    {
        var segment = finding.Segment == NetworkSegment.Unknown
            ? SegmentFor(finding.Scope)
            : finding.Segment;
        return finding with { Segment = segment, Owner = Assign(segment, control) };
    }

    public static string Explain(RemediationOwner owner) => owner switch
    {
        RemediationOwner.Automatic => "SockTuner can apply this itself; the change is reversible and needs no decision from you.",
        RemediationOwner.PresetOrManual => "SockTuner can change this, but the value is your call: pick a preset or set it manually.",
        RemediationOwner.Router => "This has to change on the router. SockTuner names the exact parameter and value, and can apply it over SSH on OpenWrt when you enable that.",
        RemediationOwner.OutOfScope => "This is an ISP or infrastructure limit. Nothing on this machine or your router fixes it; SockTuner documents it so you can escalate.",
        _ => "Ownership undetermined."
    };
}
