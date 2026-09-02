using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>What this adapter is doing for the machine right now.</summary>
public enum InterfaceRole
{
    /// <summary>Carries the default route with the best metric. Everything leaves through this.</summary>
    Carrying,

    /// <summary>Has a default route, but a worse metric than the carrying one.</summary>
    Standby,

    /// <summary>Up, with an address, and no default route: it can still win a per-destination race.</summary>
    Idle,

    /// <summary>Already down, disabled, or unplugged.</summary>
    Inactive,

    /// <summary>Loopback, a filter pseudo-interface, or anything else the user does not own.</summary>
    NotApplicable
}

/// <summary>The verdict for one adapter, and the reason for it.</summary>
public enum InterfaceVerdict
{
    /// <summary>Never offer to disable this: it is how the machine reaches the network.</summary>
    Keep,

    /// <summary>Nothing to gain from disabling it, and nothing lost by leaving it.</summary>
    Leave,

    /// <summary>Costs something measurable while it is up, and the profile would rather it were not.</summary>
    ConsiderDisabling
}

/// <summary>
/// One adapter, its role, and whether this profile wants it switched off — with the evidence that
/// decided it, so the advice can be argued with rather than obeyed.
/// </summary>
public sealed record InterfaceAdvice(
    AdapterInfo Adapter,
    InterfaceRole Role,
    InterfaceVerdict Verdict,
    string Reason,
    string Evidence)
{
    public string Name => Adapter.Name;
    public string Description => Adapter.Description;
    public string KindDisplay => Adapter.AdapterKindDisplay;
    public string RoleDisplay => Role switch
    {
        InterfaceRole.Carrying => "Carrying traffic",
        InterfaceRole.Standby => "Standby route",
        InterfaceRole.Idle => "Up, unused",
        InterfaceRole.Inactive => "Down",
        _ => "Not applicable"
    };

    public string VerdictDisplay => Verdict switch
    {
        InterfaceVerdict.Keep => "Keep",
        InterfaceVerdict.ConsiderDisabling => "Consider disabling",
        _ => "Leave as is"
    };

    /// <summary>Only an adapter this app is willing to switch off may reach the write path.</summary>
    public bool CanDisable => Verdict == InterfaceVerdict.ConsiderDisabling && Adapter.IsUp;
}

/// <summary>
/// Which network interfaces a machine has, and which of them are earning their place.
/// </summary>
/// <remarks>
/// <para>
/// Every extra interface that is up is a second candidate in route selection, a second set of
/// filter drivers on the stack, and one more thing that can answer a name lookup. None of that is
/// automatically harmful — this is why the advice names the cost rather than asserting a win.
/// </para>
/// <para>
/// The one rule that is not advice: the adapter carrying the default route is never offered for
/// disabling, at any profile, on any machine. It is the way back in. Everything else this analyzer
/// says is a suggestion; that is a refusal.
/// </para>
/// </remarks>
public static class InterfaceAdvisor
{
    /// <summary>
    /// Interfaces the user cannot meaningfully act on: Windows owns them, and they are not devices
    /// in Device Manager terms. A single NIC produces several of these — the QoS packet scheduler,
    /// the WFP filters, a capture driver — so they are left out of the list entirely rather than
    /// listed as "not applicable" and burying the handful of real devices.
    /// </summary>
    public static bool IsOutOfScope(AdapterInfo adapter) =>
        adapter.Kind is AdapterKind.Loopback or AdapterKind.Filter or AdapterKind.System
        || IsWindowsPlumbing(adapter);

    /// <summary>
    /// The INFs that install Windows' own network plumbing. Every dial-up, PPPoE and VPN
    /// connection is built on the RAS WAN miniports, and telling someone to disable them would
    /// cost them a connection and buy nothing.
    /// </summary>
    /// <remarks>
    /// Matched on the installing INF rather than on the driver provider, because "Microsoft" also
    /// covers the Hyper-V virtual switch — which is a legitimate thing to switch off — and matched
    /// on the INF rather than the PnP instance ID because these adapters do not report one.
    /// Observed on a live machine: netrasa.inf installs IP, IPv6, Network Monitor, L2TP, PPTP and
    /// PPPOE; netsstpa.inf installs SSTP; netavpna.inf installs IKEv2; kdnic.inf installs the
    /// kernel debug adapter. Third-party virtual adapters carry their own INF (oem*.inf,
    /// wvms_mp_windows.inf) and are unaffected.
    /// </remarks>
    private static readonly IReadOnlySet<string> WindowsPlumbingInfs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "netrasa.inf", "netsstpa.inf", "netavpna.inf", "kdnic.inf"
        };

    private static bool IsWindowsPlumbing(AdapterInfo adapter) =>
        adapter.Driver?.InfPath is { } inf && WindowsPlumbingInfs.Contains(inf);

    public static IReadOnlyList<InterfaceAdvice> Advise(
        IReadOnlyList<AdapterInfo> adapters,
        bool singlePathPreferred)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // The carrying interface is the one with a gateway and the lowest route metric. Ties go to
        // the faster link, because that is the one Windows also prefers.
        var carrying = adapters
            .Where(adapter => adapter.IsUp && adapter.HasDefaultRoute && !IsOutOfScope(adapter))
            .OrderBy(adapter => adapter.RouteMetric ?? uint.MaxValue)
            .ThenByDescending(adapter => adapter.SpeedBitsPerSecond)
            .FirstOrDefault();

        return
        [
            .. adapters
                .Where(adapter => !IsOutOfScope(adapter))
                .Select(adapter => Judge(adapter, carrying, singlePathPreferred))
        ];
    }

    private static InterfaceAdvice Judge(AdapterInfo adapter, AdapterInfo? carrying, bool singlePathPreferred)
    {
        if (carrying is not null && ReferenceEquals(adapter, carrying))
        {
            return new(adapter, InterfaceRole.Carrying, InterfaceVerdict.Keep,
                "This is how the machine reaches the network. SockTuner will not offer to disable it.",
                $"Default route present, metric {Metric(adapter)}, link {adapter.SpeedDisplay}.");
        }

        if (!adapter.IsUp)
        {
            return new(adapter, InterfaceRole.Inactive, InterfaceVerdict.Leave,
                "Already down, so it costs nothing while it stays that way.",
                $"Status {adapter.Status}.");
        }

        if (adapter.Kind == AdapterKind.Tunnel)
        {
            return new(adapter, InterfaceRole.Idle, InterfaceVerdict.ConsiderDisabling,
                "A tunnel interface offers an alternative path that a name lookup or a socket can pick "
                + "over the real one, usually without telling you.",
                $"Tunnel interface, status {adapter.Status}.");
        }

        if (adapter.Kind == AdapterKind.Virtual)
        {
            return new(adapter, InterfaceRole.Idle, InterfaceVerdict.ConsiderDisabling,
                "A virtual adapter from a VM or VPN product keeps its filter drivers on the stack and adds "
                + "routes of its own. Disabling it is reversible and does not uninstall anything.",
                $"Virtual adapter, status {adapter.Status}"
                + (adapter.HasDefaultRoute ? $", and it has a default route at metric {Metric(adapter)}." : "."));
        }

        // A second physical NIC is the judgement call, so it is the one the profile controls.
        if (adapter.HasDefaultRoute)
        {
            return singlePathPreferred
                ? new(adapter, InterfaceRole.Standby, InterfaceVerdict.ConsiderDisabling,
                    "A second default route means Windows picks between two paths, and it can move mid-session. "
                    + "One path is one fewer thing that changes under a game.",
                    $"Default route at metric {Metric(adapter)} against {Metric(carrying)} on {carrying?.Name ?? "the carrying adapter"}.")
                : new(adapter, InterfaceRole.Standby, InterfaceVerdict.Leave,
                    "A usable second path. Worth keeping unless you want exactly one route at all times.",
                    $"Default route at metric {Metric(adapter)}.");
        }

        return singlePathPreferred
            ? new(adapter, InterfaceRole.Idle, InterfaceVerdict.ConsiderDisabling,
                "Up with an address but no default route. It cannot carry the session, and it can still answer "
                + "for a destination on its own subnet.",
                $"Status {adapter.Status}, no gateway, metric {Metric(adapter)}.")
            : new(adapter, InterfaceRole.Idle, InterfaceVerdict.Leave,
                "Up but not routing anything. Harmless unless you are chasing a path that changes.",
                $"Status {adapter.Status}, no gateway.");
    }

    private static string Metric(AdapterInfo? adapter) =>
        adapter?.RouteMetric is { } metric ? metric.ToString() : "unknown";
}
