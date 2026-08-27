using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services.Remediation;

/// <summary>
/// One keyword a profile wants at a given value, with the reason it wants it. The value is only
/// ever a standardised NDIS value, and it is dropped unless the installed driver advertises it.
/// </summary>
public sealed record KeywordTarget(string Keyword, string Value, string Reason);

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
    IReadOnlyList<ChangeRequest> System);

public static class UseCaseProfiles
{
    // Standardised NDIS keywords carry documented values: 0 disabled, 1 enabled. Vendor keywords do
    // not, which is why no profile ever proposes one — see PlanFor.
    private const string Off = "0";
    private const string On = "1";

    public static IReadOnlyList<UseCaseProfile> All { get; } =
    [
        new("competitive-gaming", "Competitive gaming",
            "Latency and jitter first. Costs CPU and some peak throughput, and none of it moves the base RTT set by "
            + "distance and route. No global TCP state is touched: on current Windows the stack defaults are already "
            + "right, and the one setting worth moving — the receive window auto-tuning level — depends on the measured "
            + "bandwidth-delay product, so it is derived per path instead of shipped as a value.",
            [
                new KeywordTarget("*InterruptModeration", Off,
                    "Coalescing interrupts holds received packets back to batch them; off, each arrives as it lands."),
                new KeywordTarget("*RscIPv4", Off, "Receive segment coalescing adds receive-side delay to raise throughput."),
                new KeywordTarget("*RscIPv6", Off, "Receive segment coalescing adds receive-side delay to raise throughput."),
                new KeywordTarget("*EEE", Off, "Energy-efficient Ethernet adds wake-up latency to the first packet after idle."),
                new KeywordTarget("*FlowControl", Off,
                    "Pause frames stop the sender rather than dropping one packet, which stalls everything behind it.")
            ],
            [
                // Documented MMCSS values: throttling off, and the smallest share reserved for
                // non-multimedia work. Both are reboot-scoped and exactly reversible.
                new ChangeRequest("mmcss.network-throttling-index", null, "4294967295", ChangeSource.Profile),
                new ChangeRequest("mmcss.system-responsiveness", null, "10", ChangeSource.Profile)
            ]),

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
            + "power transitions and leaves interrupt moderation adaptive.",
            [
                new KeywordTarget("*EEE", Off, "Energy-efficient Ethernet adds wake-up latency after every idle gap in a call."),
                new KeywordTarget("*FlowControl", Off, "Pause frames turn one congested moment into a stall for every stream.")
            ],
            [])
    ];

    public static UseCaseProfile Get(string id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown use-case profile: {id}");

    /// <summary>
    /// Turns a profile into an action for one adapter. A keyword is only proposed when the installed
    /// driver advertises it <em>and</em> advertises the exact value, so a profile can never create a
    /// property or push a value the driver does not accept. Vendor keywords are excluded outright:
    /// their values are not standardised, so no profile can know what a value means.
    /// </summary>
    public static RemediationAction PlanFor(
        UseCaseProfile profile,
        Guid adapterId,
        IReadOnlyList<AdapterSettingCapability> capabilities)
    {
        var changes = new List<ChangeRequest>();
        var applied = new List<string>();
        var skipped = new List<string>();

        foreach (var target in profile.Nic)
        {
            var capability = capabilities.FirstOrDefault(item =>
                item.AdapterId == adapterId
                && string.Equals(item.Keyword, target.Keyword, StringComparison.OrdinalIgnoreCase));
            if (capability is null || !capability.IsStandardKeyword)
            {
                skipped.Add($"{target.Keyword}: not advertised by this driver");
                continue;
            }

            if (!capability.Choices.Any(choice => string.Equals(choice.RegistryValue, target.Value, StringComparison.Ordinal)))
            {
                skipped.Add($"{target.Keyword}: driver does not offer the value {target.Value}");
                continue;
            }

            if (string.Equals(capability.CurrentValue, target.Value, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(new ChangeRequest(capability.SettingId, adapterId.ToString(), target.Value, ChangeSource.Profile));
            applied.Add($"{capability.DisplayName} → {target.Value}: {target.Reason}");
        }

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
