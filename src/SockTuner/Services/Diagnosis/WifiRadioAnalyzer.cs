using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Diagnosis layer: separates a network problem from a radio problem. A weak or congested radio
/// produces loss and jitter that look exactly like a faulty line, and no TCP or NIC setting fixes
/// either. Pure over the collected scan — it never touches the radio.
/// </summary>
public static class WifiRadioAnalyzer
{
    /// <summary>Enough signal that the radio is not the limiting factor.</summary>
    private const int ComfortableRssiDbm = -60;

    /// <summary>Below this, retries and rate drops are expected rather than exceptional.</summary>
    private const int WeakRssiDbm = -70;

    /// <summary>Neighbours this far down are heard but do not meaningfully take airtime.</summary>
    private const int AudibleNeighbourRssiDbm = -85;

    /// <summary>The three 2.4 GHz channels whose 20 MHz spans do not overlap each other.</summary>
    private static readonly int[] NonOverlapping24 = [1, 6, 11];

    public static IReadOnlyList<DiagnosticFinding> Analyze(WifiRadioInfo radio)
    {
        if (!radio.Connected || radio.ConnectedBss is not { } bss)
        {
            return [];
        }

        var findings = new List<DiagnosticFinding>();
        if (SignalFinding(radio, bss) is { } signal)
        {
            findings.Add(signal);
        }

        if (BandFinding(radio, bss) is { } band)
        {
            findings.Add(band);
        }

        if (WidthFinding(bss) is { } width)
        {
            findings.Add(width);
        }

        if (CongestionFinding(radio, bss) is { } congestion)
        {
            findings.Add(congestion);
        }

        return findings;
    }

    private static DiagnosticFinding? SignalFinding(WifiRadioInfo radio, WifiBssInfo bss)
    {
        if (bss.RssiDbm >= ComfortableRssiDbm)
        {
            return null;
        }

        var weak = bss.RssiDbm < WeakRssiDbm;
        return new DiagnosticFinding(
            DiagnosticScope.Lan,
            weak ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
            weak ? "The wireless signal is too weak for a stable link" : "The wireless signal is marginal",
            $"{bss.Summary}. The negotiated rate is {radio.RateDisplay}. A radio at this level retries frames, and every "
                + "retry arrives as latency and jitter that no setting on this machine can remove.",
            weak
                ? "Move the machine or the access point, remove what sits between them, or add an access point. "
                    + "Wire the link if it carries anything latency-sensitive."
                : "Expect the link to degrade under load or interference. A shorter path to the access point, or a wire, "
                    + "removes the variable entirely.",
            NetworkSegment.Lan,
            ResponsibilityAssigner.Assign(NetworkSegment.Lan, LocalControl.None));
    }

    // The same network on a higher band is usually the single largest improvement available, and it
    // is invisible unless someone compares the scan against what the radio actually joined.
    private static DiagnosticFinding? BandFinding(WifiRadioInfo radio, WifiBssInfo bss)
    {
        if (bss.Band != WifiBand.TwoPointFourGhz || bss.Ssid.Length == 0)
        {
            return null;
        }

        var higher = radio.Neighbours
            .Where(neighbour => neighbour.Band is WifiBand.FiveGhz or WifiBand.SixGhz)
            .Where(neighbour => string.Equals(neighbour.Ssid, bss.Ssid, StringComparison.Ordinal))
            .Where(neighbour => neighbour.RssiDbm >= WeakRssiDbm)
            .MaxBy(neighbour => neighbour.RssiDbm);
        if (higher is null)
        {
            return null;
        }

        return new DiagnosticFinding(
            DiagnosticScope.Lan,
            DiagnosticConfidence.Medium,
            $"The same network is reachable on {higher.BandDisplay} but this radio joined 2.4 GHz",
            $"Connected to {bss.Summary}, while {higher.Summary} carries the same SSID. The 2.4 GHz band has three "
                + "non-overlapping channels and is shared with everything else in the building.",
            $"Join the {higher.BandDisplay} network — separate its SSID on the router if the client keeps choosing 2.4 GHz. "
                + "Check the signal first: the higher band carries less far.",
            NetworkSegment.Lan,
            ResponsibilityAssigner.Assign(NetworkSegment.Lan, LocalControl.RequiresChoice));
    }

    private static DiagnosticFinding? WidthFinding(WifiBssInfo bss)
    {
        if (bss.Band != WifiBand.TwoPointFourGhz || bss.ChannelWidthMhz < 40)
        {
            return null;
        }

        return new DiagnosticFinding(
            DiagnosticScope.Lan,
            DiagnosticConfidence.Medium,
            "The access point uses a 40 MHz channel on 2.4 GHz",
            $"{bss.Summary} occupies {bss.SpanLowMhz}–{bss.SpanHighMhz} MHz, which is most of a band that only has room for "
                + "three non-overlapping channels. It collides with more neighbours and gains little throughput in return.",
            "Set the 2.4 GHz channel width to 20 MHz on the router. Leave 40 MHz and wider to the 5 and 6 GHz bands.",
            NetworkSegment.RouterOrAccess,
            ResponsibilityAssigner.Assign(NetworkSegment.RouterOrAccess, LocalControl.None));
    }

    private static DiagnosticFinding? CongestionFinding(WifiRadioInfo radio, WifiBssInfo bss)
    {
        var audible = radio.OverlappingNeighbours.Where(neighbour => neighbour.RssiDbm >= AudibleNeighbourRssiDbm).ToArray();
        if (audible.Length == 0)
        {
            return null;
        }

        var coChannel = audible.Count(bss.SameChannel);
        var partial = audible.Length - coChannel;

        // Partial overlap is the worse case: two radios on the same channel take turns, two radios
        // half a channel apart cannot hear each other well enough to take turns at all.
        var severe = audible.Length >= 4 || partial >= 2;
        var action = bss.Band == WifiBand.TwoPointFourGhz
            ? Recommend24(radio, bss)
            : "Move the access point to a channel with no overlapping neighbour in this band. Which channels are legal "
                + "depends on the regulatory domain and on DFS, so pick from what the router offers.";

        return new DiagnosticFinding(
            DiagnosticScope.Lan,
            severe ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
            $"{audible.Length} other network(s) share this channel's spectrum",
            $"Connected to {bss.Summary}. {coChannel} neighbour(s) on the same channel and {partial} partially overlapping, "
                + "all above -85 dBm: " + string.Join("; ", audible.OrderByDescending(item => item.RssiDbm).Take(5).Select(item => item.Summary))
                + ". Shared airtime shows up as jitter and retries, not as lost link.",
            action,
            NetworkSegment.RouterOrAccess,
            ResponsibilityAssigner.Assign(NetworkSegment.RouterOrAccess, LocalControl.None));
    }

    /// <summary>
    /// Names the exact channel to move to. 1, 6 and 11 are the only 2.4 GHz channels that do not
    /// overlap each other, and they are legal in every regulatory domain, so an exact number is
    /// safe to give here in a way it is not on 5 or 6 GHz.
    /// </summary>
    private static string Recommend24(WifiRadioInfo radio, WifiBssInfo bss)
    {
        var others = radio.Neighbours
            .Where(neighbour => neighbour.Band == WifiBand.TwoPointFourGhz)
            .Where(neighbour => !string.Equals(neighbour.Bssid, bss.Bssid, StringComparison.OrdinalIgnoreCase))
            .Where(neighbour => neighbour.RssiDbm >= AudibleNeighbourRssiDbm)
            .ToArray();

        var best = NonOverlapping24
            .Select(channel => (Channel: channel, Load: Load(others, channel)))
            .OrderBy(candidate => candidate.Load)
            .ThenBy(candidate => candidate.Channel)
            .First();

        return best.Channel == bss.Channel
            ? $"Channel {bss.Channel} is already the least congested of 1, 6 and 11 here; the band itself is full. "
                + "A 5 GHz or wired link is the only real improvement."
            : $"Set the 2.4 GHz channel to {best.Channel} on the router, at 20 MHz width. Channels 1, 6 and 11 are the only "
                + $"ones that do not overlap each other, and {best.Channel} carries the least traffic this radio can hear.";
    }

    /// <summary>
    /// Weighted by how loudly each neighbour is heard: a distant access point on the same channel
    /// costs far less airtime than a loud one, so counting BSSIDs alone picks the wrong channel.
    /// </summary>
    private static int Load(IReadOnlyList<WifiBssInfo> neighbours, int candidateChannel)
    {
        var centre = WifiBssInfo.FrequencyFor(WifiBand.TwoPointFourGhz, candidateChannel);
        return neighbours
            .Where(neighbour => neighbour.SpanLowMhz < centre + 10 && centre - 10 < neighbour.SpanHighMhz)
            .Sum(neighbour => Math.Max(0, neighbour.RssiDbm + 95));
    }
}
