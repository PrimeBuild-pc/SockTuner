using System.Text.RegularExpressions;
using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services.Remediation;

/// <summary>Which kind of link a keyword is worth proposing on: a Wi-Fi radio and a wired NIC do not share power features.</summary>
public enum LinkKind { Any, Wired, Wireless }

/// <summary>
/// One keyword a profile wants at a given value, with the reason it wants it. The value is only
/// ever proposed when the installed driver advertises that exact value.
/// </summary>
/// <param name="DisplayMustMatch">
/// Set only for vendor keywords, whose values no standard defines. The driver's own label for the chosen value
/// must match this pattern, so a keyword where "0" means the opposite is skipped instead of inverted. Standard
/// <c>*</c> keywords carry documented 0/1 meanings and need no such check.
/// </param>
public sealed record KeywordTarget(
    string Keyword,
    string Value,
    string Reason,
    LinkKind Link = LinkKind.Any,
    string? DisplayMustMatch = null);

/// <summary>A Windows-wide CIM property (not a TCP template value) a profile wants, proposed only if the provider advertises the value.</summary>
public sealed record GlobalTarget(string ClassName, string Property, string Value, string Reason);

/// <summary>
/// A use case, not a "faster" button. Profiles weight different objectives against each other, and
/// they say so: the competitive-gaming profile trades throughput and CPU for latency, while the
/// streaming profile deliberately leaves the offloads that latency tuning would switch off.
/// </summary>
public sealed record UseCaseProfile(
    string Id,
    string DisplayName,
    string Weights,
    IReadOnlyList<KeywordTarget> Nic,
    IReadOnlyList<ChangeRequest> System,
    IReadOnlyList<GlobalTarget>? Global = null,
    bool DevicePowerManagementOff = false)
{
    public IReadOnlyList<GlobalTarget> GlobalTargets => Global ?? [];
}

public static class UseCaseProfiles
{
    // Standardised NDIS keywords carry documented values: 0 disabled, 1 enabled.
    private const string Off = "0";
    private const string On = "1";

    /// <summary>
    /// The driver's own wording for "off", in the languages the capability archive holds. Vendor keywords are
    /// only ever driven to 0 when the driver itself calls 0 by one of these names.
    /// </summary>
    internal const string OffWording =
        @"^(disabled?|disabilitat[oa]|disattivat[oa]|off|aus|deaktiviert|desactivad[oa]|d[ée]sactiv[ée]e?|d[ée]sactiv[ée]|отключ\w+|выключ\w+|已禁用|禁用|关闭|关)$";

    private static readonly KeywordTarget[] PowerSaving =
    [
        new("*EEE", Off, "Energy-efficient Ethernet adds wake-up latency to the first packet after idle and destabilises the link on some switches.", LinkKind.Wired),
        new("*SelectiveSuspend", Off, "A suspended adapter must wake before it can send, which shows up as a spike on the first packet after a pause.", LinkKind.Any),
        new("EnableGreenEthernet", Off, "Green Ethernet powers the PHY down on short or idle links and re-negotiates when traffic returns.", LinkKind.Wired, OffWording),
        new("GreenEthernet", Off, "Green Ethernet powers the PHY down on short or idle links and re-negotiates when traffic returns.", LinkKind.Wired, OffWording),
        new("PowerSavingMode", Off, "The driver's idle power-saving mode adds latency when traffic resumes.", LinkKind.Wired, OffWording),
        new("AdvancedEEE", Off, "Realtek's extended EEE modes add the same wake-up delay as standard EEE.", LinkKind.Wired, OffWording),
        new("GigaLite", Off, "Gigabit Lite lowers the link rate to save power and renegotiates under load.", LinkKind.Wired, OffWording),
        new("LowPowerEnable", Off, "Wi-Fi power save lets the radio doze between beacons, so a packet can wait for the next wake-up.", LinkKind.Wireless, OffWording),
        new("UAPSDSupport", Off, "U-APSD batches downstream frames for a sleeping radio; off, the access point sends them as they arrive.", LinkKind.Wireless, OffWording),
        new("uAPSDSupport", Off, "U-APSD batches downstream frames for a sleeping radio; off, the access point sends them as they arrive.", LinkKind.Wireless, OffWording),
        new("*NicAutoPowerSaver", Off, "Automatic NIC power saving lowers the link state between bursts and pays for it on the next packet.", LinkKind.Wired, OffWording),
        new("*EnableDynamicPowerGating", Off, "Dynamic power gating switches parts of the controller off while idle; they must power back up first.", LinkKind.Wired, OffWording),
        new("EnableConnectedPowerGating", Off, "Power gating while the link is up adds the same wake-up delay to the first packet after idle.", LinkKind.Wired, OffWording),
        new("AutoPowerSaveModeEnabled", Off, "Automatic power-save mode slows the controller when traffic is light.", LinkKind.Wired, OffWording),
        new("ReduceSpeedOnPowerDown", Off, "Dropping the link speed on power-down makes the adapter renegotiate when it wakes.", LinkKind.Wired, OffWording),
        new("MIMOPowerSaveMode", "3", "Spatial-multiplexing power save drops to one receive chain when idle and needs time to wake the others.", LinkKind.Wireless, @"^No SMPS$")
    ];

    public static IReadOnlyList<UseCaseProfile> All { get; } =
    [
        new("competitive-gaming", "Competitive gaming",
            "Latency and jitter first. Costs CPU, some peak throughput and idle power, and none of it moves the base RTT "
            + "set by distance and route. Changes only things Windows or the driver reads: interrupt batching, receive "
            + "coalescing, every adapter power-saving feature the driver advertises, the multimedia scheduler's network "
            + "throttle, and the device's own power management. Deliberately untouched, because they do nothing useful on "
            + "current Windows: TCP auto-tuning, timestamps, ECN, the 'reservable bandwidth' limit, Tcp1323Opts and "
            + "GlobalMaxTcpWindowSize.",
            [
                new KeywordTarget("*InterruptModeration", Off,
                    "Coalescing interrupts holds received packets back to batch them; off, each arrives as it lands."),
                new KeywordTarget("*RscIPv4", Off, "Receive segment coalescing adds receive-side delay to raise throughput."),
                new KeywordTarget("*RscIPv6", Off, "Receive segment coalescing adds receive-side delay to raise throughput."),
                new KeywordTarget("*FlowControl", Off,
                    "Pause frames stop the sender rather than dropping one packet, which stalls everything behind it."),
                // RoamAggressiveness is an ordinal "how soon to look for a better AP" level, confirmed
                // 0-indexed ("0" = "1. Lowest") against a real archived Intel AC-3168 capture. The other
                // cataloged aliases (NicKeywordCatalog, TuningArea.WiFiRadio) do not hold up the same
                // way: RegRoamLevel is a plain on/off switch on a real archived Realtek 8852BE ("0" =
                // "1. Disable", not a level), and RoamNeedIndicateTh/RoamIndicateTh are raw signal
                // thresholds, not a level enum — none of them get a target here.
                new KeywordTarget("RoamAggressiveness", "0",
                    "At the lowest setting the radio stays on the access point it already has instead of scanning for a "
                    + "better one; a scan is a gap in data flow, and mid-match is the worst time for one.",
                    LinkKind.Wireless, @"^\d*\.?\s*low(est)?$"),
                ..PowerSaving
            ],
            [
                // Documented MMCSS values: throttling off, and the smallest share reserved for
                // non-multimedia work. Both are reboot-scoped and exactly reversible.
                new ChangeRequest("mmcss.network-throttling-index", null, "4294967295", ChangeSource.Profile),
                new ChangeRequest("mmcss.system-responsiveness", null, "10", ChangeSource.Profile)
            ],
            Global:
            [
                new GlobalTarget(CimGlobalPropertyCatalog.OffloadGlobalClass, "ReceiveSegmentCoalescing", Off,
                    "The Windows-level switch for receive coalescing, so it holds even when the driver exposes no keyword.")
            ],
            DevicePowerManagementOff: true),

        new("streaming-and-upload", "Streaming and upload",
            "Sustained upload capacity first. Ping matters less than a queue that does not collapse, so the offloads that "
            + "latency tuning switches off are deliberately left on.",
            [
                new KeywordTarget("*RscIPv4", On, "Coalescing raises sustained throughput and lowers CPU use on long transfers."),
                new KeywordTarget("*RscIPv6", On, "Coalescing raises sustained throughput and lowers CPU use on long transfers."),
                new KeywordTarget("*LsoV2IPv4", On, "Large-send offload moves segmentation to the adapter on sustained uploads."),
                new KeywordTarget("*LsoV2IPv6", On, "Large-send offload moves segmentation to the adapter on sustained uploads.")
            ],
            []),

        new("calls-and-remote-work", "Calls and remote work",
            "Consistency first. A steady 40 ms beats an average of 20 ms that spikes, so this profile removes the idle "
            + "power transitions (Ethernet and Wi-Fi) and leaves interrupt moderation adaptive.",
            [
                new KeywordTarget("*FlowControl", Off, "Pause frames turn one congested moment into a stall for every stream."),
                ..PowerSaving
            ],
            [],
            DevicePowerManagementOff: true)
    ];

    public static UseCaseProfile Get(string id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown use-case profile: {id}");

    /// <summary>
    /// Turns a profile into an action for one adapter. A keyword is only proposed when the installed
    /// driver advertises it <em>and</em> advertises the exact value, so a profile can never create a
    /// property or push a value the driver does not accept. A vendor keyword is only proposed when its
    /// target says which wording means "off" and the driver's own label for that value says so too; a
    /// keyword the catalog refuses is never proposed at all.
    /// </summary>
    public static RemediationAction PlanFor(
        UseCaseProfile profile,
        Guid adapterId,
        IReadOnlyList<AdapterSettingCapability> capabilities,
        IReadOnlyList<GlobalSettingCapability>? globals = null,
        bool wireless = false,
        Func<Guid, bool>? devicePowerIsOff = null)
    {
        var changes = new List<ChangeRequest>();
        var applied = new List<string>();
        var skipped = new List<string>();
        var link = wireless ? LinkKind.Wireless : LinkKind.Wired;
        var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in profile.Nic)
        {
            if (target.Link != LinkKind.Any && target.Link != link) continue;

            var capability = capabilities.FirstOrDefault(item =>
                item.AdapterId == adapterId
                && string.Equals(item.Keyword, target.Keyword, StringComparison.OrdinalIgnoreCase));
            if (capability is null)
            {
                // A vendor keyword is expected to be absent on most drivers; only a standard one is worth reporting.
                if (target.DisplayMustMatch is null) skipped.Add($"{target.Keyword}: not advertised by this driver");
                continue;
            }

            if (target.DisplayMustMatch is null && !capability.IsStandardKeyword)
            {
                skipped.Add($"{target.Keyword}: not advertised by this driver");
                continue;
            }

            if (NicKeywordCatalog.IsRejected(capability.Keyword)) continue;

            var choice = capability.Choices.FirstOrDefault(item => string.Equals(item.RegistryValue, target.Value, StringComparison.Ordinal));
            if (choice is null)
            {
                skipped.Add($"{target.Keyword}: driver does not offer the value {target.Value}");
                continue;
            }

            if (target.DisplayMustMatch is { } wording
                && !Regex.IsMatch(choice.DisplayValue.Trim(), wording, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                skipped.Add($"{target.Keyword}: driver labels {target.Value} as \"{choice.DisplayValue}\", which is not a wording this profile trusts");
                continue;
            }

            if (string.Equals(capability.CurrentValue, target.Value, StringComparison.Ordinal)) continue;
            if (!already.Add(capability.Keyword)) continue;

            changes.Add(new ChangeRequest(capability.SettingId, adapterId.ToString(), target.Value, ChangeSource.Profile));
            applied.Add($"{capability.DisplayName} → {choice.DisplayValue}: {target.Reason}");
        }

        foreach (var target in profile.GlobalTargets)
        {
            var global = globals?.FirstOrDefault(item =>
                string.Equals(item.ClassName, target.ClassName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Property, target.Property, StringComparison.OrdinalIgnoreCase));
            if (global is null || !global.Choices.Any(item => string.Equals(item.RegistryValue, target.Value, StringComparison.Ordinal)))
            {
                skipped.Add($"{target.Property}: not offered by this Windows build");
                continue;
            }

            if (string.Equals(global.CurrentValue, target.Value, StringComparison.Ordinal)) continue;
            changes.Add(new ChangeRequest(global.SettingId, global.InstanceKey, target.Value, ChangeSource.Profile));
            applied.Add($"{global.DisplayName} → {global.DisplayFor(target.Value)}: {target.Reason}");
        }

        if (profile.DevicePowerManagementOff && !(devicePowerIsOff?.Invoke(adapterId) ?? false))
        {
            changes.Add(new ChangeRequest(
                AdapterPowerSavingSpecification.SettingId, adapterId.ToString(),
                AdapterPowerSavingSpecification.PowerManagementOff.ToString(), ChangeSource.Profile));
            applied.Add("Device power management → off: Windows no longer powers this adapter down to save energy "
                + "(this also stops it waking the PC, so wake-on-LAN for this adapter ends).");
        }

        foreach (var change in profile.System) applied.Add($"{change.SettingId} → {change.ProposedValue}");
        changes.AddRange(profile.System);

        return new RemediationAction(
            $"profile.{profile.Id}",
            $"Apply the {profile.DisplayName} profile",
            NetworkSegment.LocalNicDriver,
            // The values are a judgement about what this machine is for, so the profile is never
            // applied without the user choosing it.
            ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, LocalControl.RequiresChoice),
            changes,
            applied.Count == 0
                ? "Nothing to change: this adapter already matches the profile, or advertises none of the properties it uses."
                : string.Join(" ", applied),
            profile.Weights + (skipped.Count == 0 ? string.Empty : " Skipped — " + string.Join("; ", skipped) + "."),
            "Re-run the same diagnostic profile against the same endpoint under the same load condition and compare the "
                + "two runs. A profile that does not show up in the comparison did nothing worth keeping.");
    }
}
