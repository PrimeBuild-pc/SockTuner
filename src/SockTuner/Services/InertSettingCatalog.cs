using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// A setting other tuning tools still write that modern Windows does not act on, or does not act on
/// in the way the claim says.
/// </summary>
/// <param name="Confidence">
/// How sure SockTuner is. <see cref="DiagnosticConfidence.High"/> means the behaviour is documented
/// or the feature was removed from the OS; anything lower means the entry is shown as a caution and
/// still needs confirming against the capability archive before it is stated as fact.
/// </param>
public sealed record InertSetting(
    string Name,
    string Location,
    string Claim,
    string Reality,
    DiagnosticConfidence Confidence)
{
    public string Summary => $"{Name} ({Location}) — claimed: {Claim} Actually: {Reality}";
}

/// <summary>
/// The settings SockTuner deliberately will not write, with the reason for each.
/// </summary>
/// <remarks>
/// This exists because the alternative is worse than doing nothing. A tool that writes a value
/// Windows stopped reading in 2007 produces a placebo the user then credits for every later
/// improvement, and the real cause never gets found. Showing the value with the reason it does
/// nothing is more useful than changing it — the whole catalog is read-only by construction.
/// </remarks>
public static class InertSettingCatalog
{
    private const string TcpipParameters = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    public static IReadOnlyList<InertSetting> All { get; } =
    [
        new("TcpWindowSize", TcpipParameters,
            "sets the TCP receive window.",
            "Receive window auto-tuning replaced it in Windows Vista and the stack does not read it. "
            + "The equivalent modern control is the auto-tuning level, which SockTuner does expose.",
            DiagnosticConfidence.High),
        new("GlobalMaxTcpWindowSize", TcpipParameters,
            "raises the maximum TCP receive window.",
            "Same as TcpWindowSize: superseded by auto-tuning and not read.",
            DiagnosticConfidence.High),
        new("Tcp1323Opts", TcpipParameters,
            "enables window scaling and RFC 1323 timestamps.",
            "Applied to Windows Server 2003 and earlier. Scaling and timestamps are now stack settings, exposed here "
            + "as the auto-tuning level, scaling heuristics and the timestamps switch.",
            DiagnosticConfidence.High),
        new("TCP Chimney Offload", "netsh int tcp set global chimney",
            "offloads the whole TCP connection to the adapter.",
            "Removed from Windows 10 and later. The command is still accepted and does nothing.",
            DiagnosticConfidence.High),
        new("NetDMA / Direct Cache Access", TcpipParameters + @"\EnableTCPA",
            "lets the adapter copy received data with the DMA engine.",
            "Removed in Windows 8. Nothing reads the value.",
            DiagnosticConfidence.High),
        new("DefaultTTL", TcpipParameters,
            "improves latency or throughput.",
            "It does change the outgoing time-to-live, and that is all it does. TTL bounds how many hops a packet may "
            + "cross; it has no effect on the speed of the ones it does cross.",
            DiagnosticConfidence.High),
        new("SackOpts = 0", TcpipParameters,
            "reduces overhead by disabling selective acknowledgement.",
            "Selective acknowledgement is on by default and lets a sender retransmit only what was actually lost. "
            + "Disabling it makes every loss more expensive, not less.",
            DiagnosticConfidence.High),
        new("MaxConnectionsPerServer, MaxConnectionsPer1_0Server", @"HKLM\...\Internet Settings",
            "raises the number of simultaneous connections.",
            "A WinINet limit, so it affects Internet Explorer and applications built on it. It is not a TCP/IP stack "
            + "setting and modern browsers do not consult it.",
            DiagnosticConfidence.High),
        new("MaxUserPort", TcpipParameters,
            "raises the number of usable outbound ports.",
            "Superseded in Windows Vista by the dynamic port range, which is what the stack actually consults. "
            + "SockTuner exposes that range through the TCP settings provider instead.",
            DiagnosticConfidence.High),
        new("IRPStackSize", @"HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters",
            "improves network performance.",
            "Sizes the I/O stack for the file and printer sharing server. It matters when file sharing fails to start; "
            + "it has nothing to do with internet latency or throughput.",
            DiagnosticConfidence.High),

        // Below: SockTuner's reading, not settled fact. Shown as a caution rather than a verdict
        // until the capability archive confirms them across builds.
        new("EnablePMTUDiscovery, EnablePMTUBHDetect", TcpipParameters,
            "controls path MTU discovery and black-hole detection.",
            "Documented for Windows 2000 and 2003. Path MTU discovery is not optional on modern Windows, and SockTuner "
            + "detects black-holing by measuring the path rather than by trusting a registry flag. Not confirmed "
            + "across every current build.",
            DiagnosticConfidence.Medium),
        new("Host resolution priority (LocalPriority, HostsPriority, DnsPriority, NetbtPriority)",
            @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider",
            "makes name resolution faster by reordering the providers.",
            "These ordered the legacy Winsock name-space providers. The modern resolver does not appear to consult "
            + "them, and any gain would be in lookup time rather than in the latency of an established session. "
            + "Not confirmed across every current build.",
            DiagnosticConfidence.Medium),
        new("AFD DefaultReceiveWindow, DefaultSendWindow, FastSendDatagramThreshold",
            @"HKLM\SYSTEM\CurrentControlSet\Services\AFD\Parameters",
            "enlarges socket buffers for more throughput.",
            "Undocumented, and Windows sizes socket buffers dynamically. Plausible but unverified, so SockTuner reads "
            + "these and does not write them until the behaviour is documented or measured.",
            DiagnosticConfidence.Low)
    ];

    /// <summary>
    /// Whether a registry value name appears in this catalog. The suite asserts that no writable
    /// catalog entry names one, so a future entry cannot quietly reopen a setting documented here as
    /// doing nothing.
    /// </summary>
    public static bool IsInert(string name) => All.Any(item =>
        item.Name.Split(',').Any(part => string.Equals(part.Trim(), name, StringComparison.OrdinalIgnoreCase)));
}
