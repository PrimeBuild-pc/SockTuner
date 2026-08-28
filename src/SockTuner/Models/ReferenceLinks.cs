namespace SockTuner.Models;

public sealed record ReferenceLink(string Title, string Url, string Why);

/// <summary>
/// External references, opened in the browser and never downloaded or bundled.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a short list of things SockTuner does <em>not</em> replace, not a software
/// catalogue. A curated list of pinned installer URLs is a supply-chain liability — links rot, and a
/// hijacked host serves malware from behind a trusted button — which is why nothing here downloads,
/// installs, or runs anything.
/// </para>
/// <para>
/// Every entry says why it is here. A reference that cannot justify itself is one more thing to
/// maintain.
/// </para>
/// </remarks>
public static class ReferenceLinks
{
    public static IReadOnlyList<ReferenceLink> All { get; } =
    [
        new("Interrupt affinity policy",
            "https://learn.microsoft.com/windows-hardware/drivers/kernel/interrupt-affinity-and-priority",
            "The Microsoft documentation behind the interrupt affinity tab, including what each policy value means."),
        new("NDIS standardized keywords",
            "https://learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-network-devices",
            "What the asterisk-prefixed adapter properties are, and which values a driver is expected to accept."),
        new("Windows TCP settings",
            "https://learn.microsoft.com/powershell/module/nettcpip/set-nettcpsetting",
            "The supported surface behind the TCP settings tab, for checking a value against its documentation."),
        new("Bufferbloat and latency under load",
            "https://www.bufferbloat.net/projects/",
            "Background on why latency rises under load and why the fix belongs on the router rather than here."),
        new("Waveform bufferbloat test",
            "https://www.waveform.com/tools/bufferbloat",
            "An independent measurement to check the bufferbloat grade against; a second opinion on a number matters."),
        new("Cloudflare speed test",
            "https://speed.cloudflare.com",
            "Throughput and loaded latency from a different vantage point than the endpoint you chose."),
        new("Packet loss and jitter test",
            "https://packetlosstest.com",
            "A browser-side loss and jitter measurement to compare against a SockTuner run before blaming the path."),
        new("Ping packet test (esports)",
            "https://pingpackettest.com/game/pro-esports",
            "Per-game endpoint latency from an external vantage point, useful for deciding which region to play on."),
        new("Cloudflare AIM scores",
            "https://speed.cloudflare.com/aim",
            "Scores a connection for gaming, streaming and calls, including loaded latency, from outside this machine."),
        new("WinMTR",
            "https://sourceforge.net/projects/winmtr/",
            "Continuous per-hop route measurement. SockTuner samples the path during a run; WinMTR is the tool to leave running for hours."),
        new("Wireshark",
            "https://www.wireshark.org",
            "Packet capture. SockTuner deliberately does not capture traffic, so this is where that job goes.")
    ];
}
