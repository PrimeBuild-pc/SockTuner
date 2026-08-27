using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Characterises the NDIS advanced keywords a driver advertises.
/// </summary>
/// <remarks>
/// <para>
/// This catalog never decides <em>whether</em> a keyword exists — <see cref="WindowsNdisInventory"/>
/// already answers that by reading <c>Ndi\Params</c>, so the driver remains the allowlist. It only
/// annotates what the driver reports, so the inventory can say what is known about a keyword
/// instead of presenting ~80 raw names as equally trustworthy.
/// </para>
/// <para>
/// Classification is one rule plus a small exception table. The rule is Microsoft's standardized
/// keyword convention: a leading <c>*</c> means Windows defines the keyword and the driver
/// publishes a documented default/enum for it; no <c>*</c> means a private vendor keyword with no
/// public specification. The exception table carries only the keywords the research corpus
/// actually characterised — see <c>docs/JACKPOTS_ZENIT_NDIS_CANDIDATES.md</c> §C and its
/// "safe-ish latency levers" list. Keywords absent from the table fall back to the rule, which is
/// the honest default in both directions.
/// </para>
/// <para>
/// A <see cref="NicKeywordDisposition.Rejected"/> or <see cref="NicKeywordDisposition.Uncharacterised"/>
/// entry is an annotation, not an enforcement point: writes are gated by
/// <see cref="SettingCatalog"/>, which lists no NIC keyword at all today.
/// </para>
/// </remarks>
public static class NicKeywordCatalog
{
    private const string StandardizedFallbackNote =
        "Standardized Windows keyword advertised by this driver. Only the driver-advertised default and range apply; no value is assumed.";

    private const string VendorFallbackNote =
        "Private vendor keyword with no public specification. Recognised but not characterised; its meaning and safe range are unknown.";

    private static readonly IReadOnlyDictionary<string, (NicKeywordDisposition Disposition, string Note)> Characterised =
        new Dictionary<string, (NicKeywordDisposition, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // Documented low-latency levers worth exposing at the driver-advertised range.
            ["*InterruptModeration"] =
                (NicKeywordDisposition.DriverAdvertised,
                    "Primary interrupt-moderation control. The documented way to trade interrupt rate against latency; supersedes any below-driver ITR register poke."),
            ["*EEE"] =
                (NicKeywordDisposition.DriverAdvertised,
                    "Energy-Efficient Ethernet. Disabling removes a documented link power-saving latency source."),
            ["*RSS"] =
                (NicKeywordDisposition.DriverAdvertised,
                    "Receive Side Scaling on/off. Standardized; spreads receive processing across CPUs."),
            ["*RSSProfile"] =
                (NicKeywordDisposition.DriverAdvertised,
                    "RSS load-balancing profile. Standardized enum; the useful choice is machine-specific."),
            ["*NumRssQueues"] =
                (NicKeywordDisposition.DriverAdvertised,
                    "RSS queue count. Advertised range varies by adapter; never exceed what the driver reports."),
            ["*MaxRssProcessors"] =
                (NicKeywordDisposition.DriverAdvertised,
                    "Cap on CPUs used for RSS. Machine-specific; depends on the actual core layout."),
            ["*RssBaseProcNumber"] =
                (NicKeywordDisposition.Situational,
                    "First CPU used for RSS queues. Strictly machine-specific — a fixed value copied from another system is a guess, not a tuning."),

            // Documented, but with a real trade-off that must be stated before any write.
            ["*FlowControl"] =
                (NicKeywordDisposition.Situational,
                    "802.3x pause frames. Disabling can cause receive drops on a congested or slower link — a real regression, not a free win."),
            ["*RscIPv4"] =
                (NicKeywordDisposition.Situational,
                    "Receive Segment Coalescing. Coalescing raises receive latency; enabling it contradicts a low-latency goal."),
            ["*RscIPv6"] =
                (NicKeywordDisposition.Situational,
                    "Receive Segment Coalescing. Coalescing raises receive latency; enabling it contradicts a low-latency goal."),
            ["*UdpRsc"] =
                (NicKeywordDisposition.Situational,
                    "UDP Receive Segment Coalescing. Same latency trade-off as the TCP variants."),
            ["*PacketCoalescing"] =
                (NicKeywordDisposition.Situational,
                    "Receive coalescing control. A genuine latency lever, but it interacts with the RSC keywords; changing them in opposite directions is incoherent."),
            ["*NdisPoll"] =
                (NicKeywordDisposition.Situational,
                    "NDIS polling mode. Safe on its own, but pairing it with a vendor busy-poll interval can pin a CPU core; never enable the pair implicitly."),
            ["*ReceiveBuffers"] =
                (NicKeywordDisposition.Situational,
                    "Receive descriptor ring size. Must be clamped to the driver-advertised maximum; larger rings trade memory and can add buffering latency."),
            ["*TransmitBuffers"] =
                (NicKeywordDisposition.Situational,
                    "Transmit descriptor ring size. Must be clamped to the driver-advertised maximum."),
            ["*JumboPacket"] =
                (NicKeywordDisposition.Situational,
                    "Jumbo frame size. Path-dependent and behavioural: a frame size the path cannot carry breaks connectivity rather than improving it."),

            // Research-flagged unsafe. Recognised so the inventory can say why, never offered.
            ["ThreadPoll"] =
                (NicKeywordDisposition.Rejected,
                    "Vendor busy/spin-poll interval. Can hold a CPU core at full utilisation, raising power, heat and DPC latency for other devices — it trades a whole core for receive latency."),
            ["DisablePhyReset"] =
                (NicKeywordDisposition.Rejected,
                    "Suppresses PHY reset. Can wedge link renegotiation after a cable or speed change, with driver reinstall as the recovery path."),
            ["PnPCapabilities"] =
                (NicKeywordDisposition.Rejected,
                    "Opaque bitmask covering the Device Manager power-management and wake checkboxes. Coarse and illegible; the individual standardized power keywords express the same intent reversibly."),
            ["DropHighlyFragmentedPacket"] =
                (NicKeywordDisposition.Rejected,
                    "Silently drops legitimate fragmented traffic. A correctness risk, not a latency control."),
            ["HwOption"] =
                (NicKeywordDisposition.Rejected,
                    "Undocumented vendor bitmask with no public meaning. A value valid on one silicon revision is undefined behaviour on another."),
            ["HwOptionV2"] =
                (NicKeywordDisposition.Rejected,
                    "Undocumented vendor bitmask with no public meaning. A value valid on one silicon revision is undefined behaviour on another."),
            ["HwOptionV3"] =
                (NicKeywordDisposition.Rejected,
                    "Undocumented vendor bitmask with no public meaning. A value valid on one silicon revision is undefined behaviour on another.")
        };

    /// <summary>
    /// Describes a keyword exactly as advertised. Unknown keywords are classified by the
    /// standardized-prefix rule rather than being dropped or guessed at.
    /// </summary>
    public static NicKeywordInfo Describe(string? keyword)
    {
        var normalized = (keyword ?? string.Empty).Trim();
        var keywordClass = IsStandardized(normalized) ? NicKeywordClass.Standardized : NicKeywordClass.Vendor;

        if (Characterised.TryGetValue(normalized, out var known))
        {
            return new NicKeywordInfo(normalized, keywordClass, known.Disposition, known.Note);
        }

        return keywordClass == NicKeywordClass.Standardized
            ? new NicKeywordInfo(normalized, keywordClass, NicKeywordDisposition.DriverAdvertised, StandardizedFallbackNote)
            : new NicKeywordInfo(normalized, keywordClass, NicKeywordDisposition.Uncharacterised, VendorFallbackNote);
    }

    internal static bool IsStandardized(string keyword) => keyword.StartsWith('*');

    /// <summary>Keywords carrying a curated characterisation, for tests and documentation.</summary>
    internal static IEnumerable<string> CharacterisedKeywords => Characterised.Keys;
}
