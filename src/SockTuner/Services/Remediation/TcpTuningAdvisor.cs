using System.Globalization;
using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services.Remediation;

/// <summary>What the path actually measured. Every proposal below is a function of these numbers.</summary>
public sealed record TcpPathMeasurement(
    double BitsPerSecond,
    double BaselineRttMs,
    BufferbloatGrade? DownloadGrade = null);

/// <summary>
/// Remediation layer: chooses the receive-window auto-tuning level from the measured path rather
/// than from a table.
/// </summary>
/// <remarks>
/// This is the one setting where a fixed recommendation is actively harmful in both directions. The
/// throughput ceiling of a TCP connection is window ÷ round-trip time, so the same value that costs
/// nothing on a short fast path throws most of the line away on a long one — and the level that is
/// safe for throughput leaves a download free to fill a bloated queue. Neither answer is knowable
/// without measuring, which is why it is derived here and never shipped as a preset value.
/// </remarks>
public static class TcpTuningAdvisor
{
    /// <summary>The largest window TCP can advertise without window scaling.</summary>
    public const int UnscaledWindowBytes = 65535;

    /// <summary>
    /// Used only when the transport filters could not be read. Callers should pass the template
    /// <see cref="WindowsTcpTemplateResolver"/> resolved from the live filters — on a stock machine
    /// a single filter sends all TCP to <c>Internet</c> while <c>InternetCustom</c> carries nothing,
    /// so assuming the Custom template writes into an empty room.
    /// </summary>
    public const string DefaultTcpTemplate = WindowsTcpTemplateResolver.FallbackTemplate;

    private const string AutoTuningProperty = "AutoTuningLevelLocal";

    // Values from the provider's own ValueMap on MSFT_NetTCPSetting.
    private const int HighlyRestricted = 1;
    private const int Restricted = 2;
    private const int Normal = 3;

    /// <summary>
    /// Bytes in flight needed to keep the link busy for one round trip. If this fits inside the
    /// unscaled window, restricting auto-tuning costs nothing at all.
    /// </summary>
    public static double BandwidthDelayProductBytes(double bitsPerSecond, double roundTripMs) =>
        bitsPerSecond <= 0 || roundTripMs <= 0 ? 0 : bitsPerSecond / 8 * (roundTripMs / 1000);

    /// <summary>The throughput a fixed window allows on a given round trip — the arithmetic that makes the trade-off concrete.</summary>
    public static double CeilingBitsPerSecond(int windowBytes, double roundTripMs) =>
        roundTripMs <= 0 ? 0 : windowBytes * 8 / (roundTripMs / 1000);

    public static RemediationAction? Advise(
        TcpPathMeasurement path,
        IReadOnlyList<GlobalSettingCapability> capabilities,
        string template)
    {
        var capability = capabilities.FirstOrDefault(item =>
            string.Equals(item.Property, AutoTuningProperty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.InstanceKey, template, StringComparison.OrdinalIgnoreCase));
        if (capability is null
            || !int.TryParse(capability.CurrentValue, NumberStyles.None, CultureInfo.InvariantCulture, out var current))
        {
            return null;
        }

        var product = BandwidthDelayProductBytes(path.BitsPerSecond, path.BaselineRttMs);
        var ceiling = CeilingBitsPerSecond(UnscaledWindowBytes, path.BaselineRttMs);

        if (path.DownloadGrade >= BufferbloatGrade.C)
        {
            var proposed = current > Restricted ? Restricted : current == Restricted ? HighlyRestricted : (int?)null;
            return proposed is { } target ? Restrict(capability, template, target, path, product, ceiling) : null;
        }

        return current <= HighlyRestricted && product > UnscaledWindowBytes ? Restore(capability, template, path, product, ceiling) : null;
    }

    private static RemediationAction? Restrict(
        GlobalSettingCapability capability,
        string template,
        int target,
        TcpPathMeasurement path,
        double product,
        double ceiling)
    {
        if (!Offers(capability, target))
        {
            return null;
        }

        return new RemediationAction(
            "tcp.autotuning.restrict",
            $"Hold the receive window down to shorten the download queue ({capability.DisplayFor(target.ToString())})",
            NetworkSegment.LocalNicDriver,
            ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, LocalControl.RequiresChoice),
            [Change(capability, template, target)],
            $"Latency under download load grades {LoadedLatencyAnalyzer.Display(path.DownloadGrade!.Value)} today. Less data "
                + "in flight means less of it sitting in the queue in front of the slow link, so latency under load falls.",
            $"This is a mitigation, not a fix: the queue is still on the router, and shaping it there (SQM) costs no "
                + $"throughput at all. Keeping the link busy on this path needs about {Kilobytes(product)} in flight, and a "
                + $"window pinned at the unscaled {UnscaledWindowBytes / 1024} KB would cap it near "
                + $"{ThroughputResult.FormatRate(ceiling)} against the {ThroughputResult.FormatRate(path.BitsPerSecond)} "
                + "measured. Expect to give up throughput for the latency.",
            "Re-run the loaded-latency measurement and the throughput test together. If throughput fell further than "
                + "latency improved, put the value back — it is exactly reversible.");
    }

    // The mirror case, and the one a fixed "optimizer" preset creates: a machine left throttling
    // itself long after whatever prompted the change.
    private static RemediationAction? Restore(
        GlobalSettingCapability capability,
        string template,
        TcpPathMeasurement path,
        double product,
        double ceiling)
    {
        if (!Offers(capability, Normal))
        {
            return null;
        }

        return new RemediationAction(
            "tcp.autotuning.restore",
            "Let the receive window grow again: this machine is capping its own downloads",
            NetworkSegment.LocalNicDriver,
            ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, LocalControl.RequiresChoice),
            [Change(capability, template, Normal)],
            $"Auto-tuning is set to {capability.CurrentDisplay}. Keeping this path busy needs about {Kilobytes(product)} "
                + $"in flight, well past the unscaled {UnscaledWindowBytes / 1024} KB, so a held-down window caps downloads "
                + $"near {ThroughputResult.FormatRate(ceiling)} whatever the line can do.",
            "Restoring it lets a download fill a bloated queue again. Where that queue is the problem, shape it on the "
                + "router instead of paying for it here.",
            "Re-run the throughput test. It should rise towards the line rate; if latency under load worsens with it, the "
                + "queue upstream is the real issue.");
    }

    private static bool Offers(GlobalSettingCapability capability, int value) =>
        capability.Choices.Any(choice => choice.RegistryValue == value.ToString(CultureInfo.InvariantCulture));

    private static ChangeRequest Change(GlobalSettingCapability capability, string template, int value) =>
        new(capability.SettingId, template, value.ToString(CultureInfo.InvariantCulture), ChangeSource.Profile);

    private static string Kilobytes(double bytes) => $"{bytes / 1024:0.#} KB";
}
