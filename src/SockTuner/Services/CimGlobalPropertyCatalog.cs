using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Editorial metadata for a writable CIM global property: what it costs, how risky it is, and —
/// for the numeric ones — the documented range. It never supplies the accepted values of an
/// enumerated property; those come from the provider's own <c>ValueMap</c>.
/// </summary>
public sealed record CimGlobalProperty(
    string ClassName,
    string Property,
    string DisplayName,
    string Category,
    ChangeRisk Risk,
    string RestartRequirement,
    string TradeOff,
    long? Minimum = null,
    long? Maximum = null);

/// <summary>
/// Which CIM global properties SockTuner will write, and what each one costs. Everything the
/// SpeedGuide TCP Optimizer reaches through <c>netsh int tcp set global</c> lives here — minus the
/// settings Windows no longer honours, which are documented as inert in
/// <see cref="InertSettingCatalog"/> rather than written.
/// </summary>
public static class CimGlobalPropertyCatalog
{
    public const string TcpSettingClass = "MSFT_NetTCPSetting";
    public const string OffloadGlobalClass = "MSFT_NetOffloadGlobalSetting";

    /// <summary>
    /// The property naming each class's instances. TCP settings exist once per template; the global
    /// offload switches are a singleton, so they have no key.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> InstanceKeyProperty { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [TcpSettingClass] = "SettingName",
            [OffloadGlobalClass] = null
        };

    /// <summary>
    /// The property whose value proves a write actually took effect. Windows keeps a separate
    /// "effective" reading for auto-tuning because group policy and the template a connection is
    /// mapped to can both override what was written.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EffectiveCounterpart { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AutoTuningLevelLocal"] = "AutoTuningLevelEffective"
        };

    private const string TcpRestart = "None (applies to connections opened after the change)";

    public static IReadOnlyList<CimGlobalProperty> All { get; } =
    [
        new(TcpSettingClass, "AutoTuningLevelLocal", "Receive window auto-tuning", "TCP receive window",
            ChangeRisk.Medium, TcpRestart,
            "Caps how much data can be in flight towards this machine. Restricting it shortens the queue a download "
            + "can build in front of a slow link — the one endpoint-side lever against download bufferbloat — but it "
            + "also caps throughput: the ceiling is window ÷ round-trip time, so 64 KB over 20 ms is about 26 Mbit/s "
            + "whatever the line can do."),
        new(TcpSettingClass, "ScalingHeuristics", "Window scaling heuristics", "TCP receive window",
            ChangeRisk.Medium, TcpRestart,
            "Lets Windows restrict auto-tuning on its own when it suspects a middlebox is mangling window scaling. "
            + "Disabling it removes surprise throttling and also removes the workaround for the broken equipment it "
            + "was built for."),
        new(TcpSettingClass, "CongestionProvider", "Congestion control provider", "TCP congestion control",
            ChangeRisk.Medium, TcpRestart,
            "Decides how fast a sender ramps up and how hard it backs off after loss. CUBIC is the modern default; "
            + "the alternatives are legacy or datacentre-specific and are not faster on a domestic line."),
        new(TcpSettingClass, "EcnCapability", "Explicit congestion notification", "TCP congestion control",
            ChangeRisk.Medium, TcpRestart,
            "Lets a router mark congestion instead of dropping a packet, which pairs well with a router running "
            + "modern queue management. Older equipment on the path can drop ECN-marked packets outright, which looks "
            + "like a broken connection rather than a slow one."),
        new(TcpSettingClass, "Timestamps", "TCP timestamps (RFC 1323)", "TCP options",
            ChangeRisk.Medium, TcpRestart,
            "Improves round-trip estimation and protects against wrapped sequence numbers, at 12 bytes per packet. "
            + "Off by default on Windows; the gain is real but small on a domestic line."),
        new(TcpSettingClass, "NonSackRttResiliency", "Non-SACK RTT resiliency", "TCP options",
            ChangeRisk.Low, TcpRestart,
            "Only affects peers that do not support selective acknowledgement, which is almost none of them today."),
        new(TcpSettingClass, "CwndRestart", "Restart congestion window when idle", "TCP congestion control",
            ChangeRisk.Medium, TcpRestart,
            "Restarting after an idle gap is the conservative, standards-conforming behaviour. Leaving the window open "
            + "makes the first burst after a pause faster and can overwhelm a queue that has meanwhile filled."),
        new(TcpSettingClass, "ForceWS", "Force window scaling", "TCP receive window",
            ChangeRisk.High, TcpRestart,
            "Forces window scaling even where the heuristics would disable it. On a path with equipment that mangles "
            + "the option, connections stall rather than slow down."),
        new(TcpSettingClass, "MemoryPressureProtection", "Memory pressure protection", "TCP resources",
            ChangeRisk.Medium, TcpRestart,
            "Sheds connections under memory exhaustion instead of letting the stack starve. Disabling it removes a "
            + "safety valve and gains nothing on a machine that is not under attack."),
        new(TcpSettingClass, "AutomaticUseCustom", "Use custom templates automatically", "TCP templates",
            ChangeRisk.Medium, TcpRestart,
            "Decides whether the Automatic template resolves to the Custom templates. Without it, changes written to "
            + "a Custom template may never apply to real traffic."),
        new(TcpSettingClass, "MaxSynRetransmissions", "SYN retransmission attempts", "TCP connection setup",
            ChangeRisk.Medium, TcpRestart,
            "Fewer attempts fail an unreachable host sooner; too few give up on a slow but working path.",
            2, 8),
        new(TcpSettingClass, "InitialRto", "Initial retransmission timeout (ms)", "TCP connection setup",
            ChangeRisk.Medium, TcpRestart,
            "How long the stack waits before retransmitting the first unanswered segment. Lowering it recovers faster "
            + "from a lost SYN and sends duplicates onto a path that was merely slow.",
            300, 3000),
        new(TcpSettingClass, "MinRto", "Minimum retransmission timeout (ms)", "TCP connection setup",
            ChangeRisk.High, TcpRestart,
            "Floors the retransmission timer. Below the real round-trip time this manufactures spurious "
            + "retransmissions, which is congestion the machine caused itself.",
            20, 300),
        new(TcpSettingClass, "InitialCongestionWindow", "Initial congestion window (MSS)", "TCP congestion control",
            ChangeRisk.Medium, TcpRestart,
            "How many segments may be sent before the first acknowledgement. A larger window loads short transfers "
            + "faster and bursts harder into a queue that is already full.",
            2, 64),
        new(TcpSettingClass, "DelayedAckFrequency", "Delayed ACK frequency", "TCP ACK behaviour",
            ChangeRisk.Medium, TcpRestart,
            "Acknowledging every segment removes up to 200 ms of delay from small request/response exchanges and "
            + "roughly doubles the acknowledgement traffic. It is a TCP control and does nothing for UDP traffic.",
            1, 255),
        new(TcpSettingClass, "DelayedAckTimeout", "Delayed ACK timeout (ms)", "TCP ACK behaviour",
            ChangeRisk.Medium, TcpRestart,
            "The upper bound on how long an acknowledgement may be held back. Same trade-off as the frequency, and "
            + "the same TCP-only caveat.",
            10, 600),
        new(TcpSettingClass, "DynamicPortRangeStartPort", "Dynamic port range start", "TCP resources",
            ChangeRisk.Medium, TcpRestart,
            "The first ephemeral port Windows hands to outbound connections. This is the modern replacement for the "
            + "MaxUserPort registry value. The floor is 1024 because that is what the stock templates ship with; "
            + "below it are the well-known ports.",
            1024, 65000),
        new(TcpSettingClass, "DynamicPortRangeNumberOfPorts", "Dynamic port range size", "TCP resources",
            ChangeRisk.Medium, TcpRestart,
            "How many ephemeral ports are available. It only matters on a machine that exhausts them — thousands of "
            + "short-lived connections — and a range that runs past 65535 is rejected.",
            255, 64511),

        new(OffloadGlobalClass, "ReceiveSideScaling", "Receive side scaling", "Global offload",
            ChangeRisk.Medium, "None",
            "Spreads receive processing across CPU cores. Disabling it pins every interrupt to one core and is a "
            + "diagnostic step, not an optimisation."),
        new(OffloadGlobalClass, "ReceiveSegmentCoalescing", "Receive segment coalescing", "Global offload",
            ChangeRisk.Medium, "None",
            "Merges received segments before handing them up, which raises throughput and lowers CPU use while adding "
            + "receive-side delay. This is the global switch; the per-adapter keywords are separate."),
        new(OffloadGlobalClass, "TaskOffload", "Task offload", "Global offload",
            ChangeRisk.High, "None",
            "The master switch for every hardware offload. Turning it off is how a suspected offload bug is confirmed; "
            + "it is never a tuning step, and it costs CPU on every packet.")
    ];

    public static IReadOnlyList<CimGlobalProperty> ForClass(string className) =>
        All.Where(item => string.Equals(item.ClassName, className, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static CimGlobalProperty? Find(string className, string property) => All.FirstOrDefault(item =>
        string.Equals(item.ClassName, className, StringComparison.OrdinalIgnoreCase)
        && string.Equals(item.Property, property, StringComparison.OrdinalIgnoreCase));
}
