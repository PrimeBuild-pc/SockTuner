using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services.Remediation;

/// <summary>
/// One change on the router, named the way the router names it. "Check your QoS settings" is not
/// guidance; a parameter, a value and the reason for that value is.
/// </summary>
/// <param name="UciPath">
/// The OpenWrt configuration path where one exists, so the instruction can be typed as-is and, once
/// the SSH transport lands, written by the same value that is shown here.
/// </param>
public sealed record RouterInstruction(string Parameter, string Value, string Reason, string? UciPath = null)
{
    public string Summary => UciPath is null
        ? $"{Parameter} = {Value} — {Reason}"
        : $"{Parameter} = {Value} (OpenWrt: {UciPath}) — {Reason}";
}

public sealed record RouterGuidanceItem(
    string Title,
    NetworkSegment Segment,
    IReadOnlyList<RouterInstruction> Instructions,
    string Verification)
{
    /// <summary>Router work is router-owned by construction; the owner is still derived, never written by hand.</summary>
    public RemediationOwner Owner => ResponsibilityAssigner.Assign(Segment, LocalControl.None);
}

/// <summary>
/// Facts the guidance is derived from. Everything is optional: a run that measured only one
/// direction produces guidance for that direction rather than guessing the other.
/// </summary>
public sealed record RouterGuidanceInput(
    LoadedLatencyResult? Download = null,
    LoadedLatencyResult? Upload = null,
    WifiRadioInfo? Wifi = null,
    NatTopology Topology = NatTopology.Unknown);

/// <summary>
/// Remediation layer: what to change on the router. These are the fixes that genuinely cannot be
/// made from the endpoint — the queue that causes bufferbloat lives on the device in front of the
/// slow link, and no amount of local tuning drains it.
/// </summary>
public static class RouterGuidance
{
    /// <summary>
    /// Shaping has to sit just below the real capacity, or the bottleneck queue stays in the modem
    /// where the router cannot manage it. 90% is the usual starting point; on a link whose rate
    /// varies — cable and DSL in particular — it has to come down further, which is why the
    /// verification step says what to do when the grade does not move.
    /// </summary>
    public const double ShapedShareOfMeasured = 0.9;

    public static IReadOnlyList<RouterGuidanceItem> For(RouterGuidanceInput input)
    {
        var items = new List<RouterGuidanceItem>();
        if (Sqm(input) is { } sqm)
        {
            items.Add(sqm);
        }

        if (Wifi(input.Wifi) is { } wifi)
        {
            items.Add(wifi);
        }

        if (input.Topology == NatTopology.DoubleNat)
        {
            items.Add(new RouterGuidanceItem(
                "Stop one of the two devices translating",
                NetworkSegment.RouterOrAccess,
                [
                    new RouterInstruction(
                        "Upstream modem or ONT mode", "bridge",
                        "With the upstream device bridging, the router holds the single public address and inbound "
                        + "connections have one translation to cross instead of two."),
                    new RouterInstruction(
                        "Inner router NAT (if the upstream device must keep routing)", "disabled",
                        "The alternative: leave the upstream device routing and turn the inner one into an access point. "
                        + "Exactly one device should translate — which one matters far less than the count.")
                ],
                "Re-run the topology check. The router's WAN address should be public, or at least not private, and the "
                    + "path should show one private hop rather than two."));
        }

        return items;
    }

    private static RouterGuidanceItem? Sqm(RouterGuidanceInput input)
    {
        var directions = new[] { input.Download, input.Upload }
            .Where(result => result is not null)
            .Select(result => result!)
            .Where(result => result.LatencyIncreaseMs is { } increase
                && LoadedLatencyAnalyzer.Grade(increase) >= BufferbloatGrade.C
                && result.Load.BitsPerSecond > 0)
            .ToArray();
        if (directions.Length == 0)
        {
            return null;
        }

        var instructions = new List<RouterInstruction>
        {
            new("Queue discipline", "cake",
                "CAKE keeps the queue short instead of large, which is what turns a full link into an unusable one.",
                "sqm.@queue[0].qdisc"),
            new("Queue setup script", "piece_of_cake.qos",
                "The simplest script that works. Move to layer_cake.qos only if traffic is actually DSCP-marked.",
                "sqm.@queue[0].script"),
            new("SQM enabled", "1", "Shaping does nothing until the queue is switched on.", "sqm.@queue[0].enabled")
        };

        foreach (var direction in directions)
        {
            var measured = direction.Load.BitsPerSecond;
            var shaped = (long)(measured * ShapedShareOfMeasured / 1000);
            var name = direction.Direction == TransferDirection.Download ? "download" : "upload";
            instructions.Add(new RouterInstruction(
                $"SQM {name} limit",
                $"{shaped} kbit/s",
                $"{ShapedShareOfMeasured:P0} of the {ThroughputResult.FormatRate(measured)} this connection measured. "
                    + $"Latency rises {direction.LatencyIncreaseMs:0} ms under {name} load today "
                    + $"(grade {LoadedLatencyAnalyzer.Display(LoadedLatencyAnalyzer.Grade(direction.LatencyIncreaseMs!.Value))}); "
                    + "shaping below the real rate moves the queue onto the router, which is the only device that can manage it.",
                $"sqm.@queue[0].{name}"));
        }

        instructions.Add(new RouterInstruction(
            "Link layer adaptation", "depends on the access technology",
            "Ethernet and fibre need none; ATM-based DSL and PPPoE carry per-packet overhead that has to be declared or the "
                + "shaper runs over its own limit. Set it to what the line actually is rather than to a default.",
            "sqm.@queue[0].linklayer"));

        return new RouterGuidanceItem(
            "Shape the link on the router so the queue is somewhere it can be managed",
            NetworkSegment.RouterOrAccess,
            instructions,
            "Re-run the loaded-latency measurement in the same direction. The grade should move to A or B. If it barely "
                + "moves, lower the limit by another 5% and repeat — a variable-rate line needs more headroom than a fixed one.");
    }

    private static RouterGuidanceItem? Wifi(WifiRadioInfo? radio)
    {
        if (radio?.ConnectedBss is not { Band: WifiBand.TwoPointFourGhz } bss)
        {
            return null;
        }

        var instructions = new List<RouterInstruction>();
        if (WifiRadioAnalyzer.RecommendChannel(radio) is { AlreadyBest: false } best)
        {
            instructions.Add(new RouterInstruction(
                "2.4 GHz channel", best.Channel.ToString(),
                $"Channel {bss.Channel} overlaps more of what this radio can hear than {best.Channel} does. 1, 6 and 11 are "
                    + "the only 2.4 GHz channels that do not overlap each other, and all three are legal everywhere.",
                "wireless.radio0.channel"));
        }

        if (bss.ChannelWidthMhz >= 40)
        {
            instructions.Add(new RouterInstruction(
                "2.4 GHz channel width", "20 MHz (HT20)",
                "A 40 MHz channel takes most of a band with room for three, collides with more neighbours, and returns "
                    + "little throughput for it.",
                "wireless.radio0.htmode"));
        }

        if (instructions.Count == 0)
        {
            return null;
        }

        return new RouterGuidanceItem(
            "Move the 2.4 GHz radio off the congested spectrum",
            NetworkSegment.RouterOrAccess,
            instructions,
            "Reconnect and re-read the radio. The neighbour count overlapping this channel should drop, and jitter on the "
                + "gateway probe should follow it down.");
    }
}
