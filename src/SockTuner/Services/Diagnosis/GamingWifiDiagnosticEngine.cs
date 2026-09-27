using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

public sealed record GamingWifiDiagnosticInput(
    WifiRadioInfo? Radio,
    IReadOnlyList<WifiObservationSample>? Samples = null,
    ProbeStatistics? Gateway = null,
    AdapterCounterDelta? CounterDelta = null,
    DateTimeOffset? CapturedAt = null);

/// <summary>Pure correlation of passive WLAN facts with the already measured local gateway.</summary>
public static class GamingWifiDiagnosticEngine
{
    private const int SignalSwingDbm = 12;

    public static WifiDiagnosticReport Analyze(GamingWifiDiagnosticInput input)
    {
        var capturedAt = input.CapturedAt ?? DateTimeOffset.Now;
        var samples = input.Samples ?? [];
        if (input.Radio is not { } radio)
        {
            return new(capturedAt, WifiDiagnosticVerdict.NotEnoughData,
                "No Wi‑Fi interface was selected.", null, [], [], [], samples,
                ["Connection state", "Signal", "Gateway correlation"]);
        }

        var unknowns = new List<string>();
        if (!radio.Connected || radio.ConnectedBss is not { } connected)
        {
            return new(capturedAt, WifiDiagnosticVerdict.NotEnoughData,
                "The radio is present but it is not associated with an access point.", radio,
                [new("Interface state", radio.State.ToString(), "Windows WLAN API")], [],
                BuildChannels(radio), samples, ["Connected BSS", "Signal and channel", "Gateway correlation"]);
        }

        var findings = WifiRadioAnalyzer.Analyze(radio).ToList();
        var gatewayUnstable = IsGatewayUnstable(input.Gateway);
        var counterDelta = CounterMatches(radio, input.CounterDelta) ? input.CounterDelta : null;
        AddTemporalFindings(radio, samples, findings);
        AddCounterFinding(counterDelta, gatewayUnstable, findings);
        var corroboratingRadioEvidence = findings.Any(finding =>
            finding.Segment is NetworkSegment.Lan or NetworkSegment.LocalNicDriver);

        if (input.Gateway is null) unknowns.Add("Gateway correlation was not measured");
        if (connected.ChannelUtilizationPercent is null) unknowns.Add("The access point did not report channel utilization");
        if (radio.Security?.PmfCapable is null && connected.Security?.PmfCapable is null)
            unknowns.Add("PMF capability was not reported");
        if (samples.Count(sample => sample.Error is null && sample.RssiDbm.HasValue) < 3)
            unknowns.Add("Signal and link-rate stability were not observed over time");

        if (gatewayUnstable && findings.Count == 0)
        {
            findings.Add(new DiagnosticFinding(
                DiagnosticScope.Lan,
                DiagnosticConfidence.Low,
                "The gateway was unstable but current radio facts do not isolate the cause",
                $"Gateway: {input.Gateway!.Summary}. Signal, channel pressure and adapter counters do not currently explain it.",
                "Observe Wi‑Fi stability while the symptom is happening, then repeat the gaming diagnosis.",
                NetworkSegment.Lan,
                RemediationOwner.PresetOrManual));
        }

        var verdict = gatewayUnstable && corroboratingRadioEvidence
            ? WifiDiagnosticVerdict.Degraded
            : findings.Count > 0 ? WifiDiagnosticVerdict.AtRisk : WifiDiagnosticVerdict.Healthy;
        var summary = verdict switch
        {
            WifiDiagnosticVerdict.Degraded => "Gateway degradation coincides with local Wi‑Fi evidence.",
            WifiDiagnosticVerdict.AtRisk when gatewayUnstable => "The local path is unstable, but Wi‑Fi is not yet confirmed as the cause.",
            WifiDiagnosticVerdict.AtRisk => "The radio has one or more conditions that can affect latency or stability.",
            _ when input.Gateway is null => "The current radio facts look healthy; run Gaming diagnostics to correlate the gateway.",
            _ => "The current radio facts and gateway measurement show no local Wi‑Fi problem."
        };

        return new WifiDiagnosticReport(
            capturedAt,
            verdict,
            summary,
            radio,
            BuildEvidence(radio, input.Gateway, counterDelta),
            findings,
            BuildChannels(radio),
            samples,
            unknowns);
    }

    private static bool IsGatewayUnstable(ProbeStatistics? gateway) => gateway is not null
        && gateway.Sent > 0
        && (gateway.Received == 0 || gateway.LossPercent > 2 || gateway.P95Ms > 10 || gateway.JitterMs > 3);

    private static void AddTemporalFindings(
        WifiRadioInfo radio, IReadOnlyList<WifiObservationSample> samples, ICollection<DiagnosticFinding> findings)
    {
        var valid = samples.Where(sample => sample.Error is null && sample.RssiDbm.HasValue).ToArray();
        if (valid.Length < 3) return;

        var signalRange = valid.Max(sample => sample.RssiDbm!.Value) - valid.Min(sample => sample.RssiDbm!.Value);
        var bssids = valid.Select(sample => sample.Bssid).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rates = valid
            .Where(sample => sample.TransmitRateKbps > 0 && sample.ReceiveRateKbps > 0)
            .Select(sample => Math.Min(sample.TransmitRateKbps, sample.ReceiveRateKbps))
            .ToArray();
        var peakRate = rates.Length == 0 ? 0 : rates.Max();
        var lowRate = rates.Length == 0 ? 0 : rates.Min();
        var rateCollapsed = rates.Length >= 3 && lowRate * 2 < peakRate;

        if (signalRange < SignalSwingDbm && bssids.Length <= 1 && !rateCollapsed) return;

        var evidence = new List<string>();
        if (signalRange >= SignalSwingDbm) evidence.Add($"RSSI moved across a {signalRange} dB range");
        if (bssids.Length > 1) evidence.Add($"the client used {bssids.Length} BSSIDs");
        if (rateCollapsed) evidence.Add($"the lower negotiated rate fell below half the peak ({lowRate / 1000d:0.#} vs {peakRate / 1000d:0.#} Mbit/s)");
        findings.Add(new DiagnosticFinding(
            DiagnosticScope.Lan,
            bssids.Length > 1 || signalRange >= 18 ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
            "The wireless link changed materially during observation",
            $"{valid.Length} passive samples from {radio.Description}: {string.Join("; ", evidence)}.",
            "Keep the observation running while the game stutters. Move closer to the access point or use Ethernet if the same change repeats with gateway spikes.",
            NetworkSegment.Lan,
            RemediationOwner.PresetOrManual));
    }

    private static bool CounterMatches(WifiRadioInfo radio, AdapterCounterDelta? delta)
    {
        if (delta is null) return false;
        return Guid.TryParse(radio.InterfaceId, out var radioId)
               && Guid.TryParse(delta.AdapterId, out var adapterId)
            ? radioId == adapterId
            : string.Equals(radio.InterfaceId, delta.AdapterId, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCounterFinding(
        AdapterCounterDelta? delta, bool gatewayUnstable, ICollection<DiagnosticFinding> findings)
    {
        if (!gatewayUnstable || delta is null) return;
        var issues = new[] { delta.ReceiveErrors, delta.ReceiveDiscards, delta.SendErrors, delta.SendDiscards };
        if (issues.Any(value => value is null) || issues.Sum(value => value!.Value) <= 0) return;

        findings.Add(new DiagnosticFinding(
            DiagnosticScope.LocalPc,
            DiagnosticConfidence.High,
            "Adapter errors or discards increased while the gateway degraded",
            delta.Summary,
            "Check driver state and radio stability before changing Internet or game settings.",
            NetworkSegment.LocalNicDriver,
            RemediationOwner.PresetOrManual));
    }

    private static IReadOnlyList<WifiDiagnosticEvidence> BuildEvidence(
        WifiRadioInfo radio, ProbeStatistics? gateway, AdapterCounterDelta? delta)
    {
        var connected = radio.ConnectedBss!;
        var evidence = new List<WifiDiagnosticEvidence>
        {
            new("Signal", radio.SignalDisplay, "Windows WLAN API"),
            new("Band / channel", $"{connected.BandDisplay} · {connected.Channel} · {connected.ChannelWidthMhz} MHz", "Cached BSS entry"),
            new("Negotiated rate", radio.RateDisplay, "Windows WLAN API"),
            new("PHY", radio.PhyDisplay, "Windows WLAN API"),
            new("Security", (radio.Security ?? connected.Security)?.Display ?? "Not reported", "Windows WLAN API / RSN element"),
            new("Overlapping BSS", radio.OverlappingNeighbours.Count.ToString(), "Cached BSS list")
        };
        if (connected.ChannelUtilizationPercent is { } utilization)
            evidence.Add(new("AP channel utilization", $"{utilization}%", "BSS Load element"));
        if (gateway is not null) evidence.Add(new("Gateway", gateway.Summary, "Gaming diagnostics"));
        if (delta is not null) evidence.Add(new("Adapter counters", delta.Summary, "Windows adapter counters"));
        return evidence;
    }

    private static IReadOnlyList<WifiChannelPressure> BuildChannels(WifiRadioInfo radio)
    {
        var connected = radio.ConnectedBss;
        return radio.Neighbours
            .Where(item => item.Channel > 0)
            .GroupBy(item => (item.Band, item.Channel))
            .Select(group => new WifiChannelPressure(
                group.Key.Band,
                group.Key.Channel,
                group.Count(),
                group.Max(item => item.RssiDbm),
                connected is null ? 0 : group.Count(item =>
                    !string.Equals(item.Bssid, connected.Bssid, StringComparison.OrdinalIgnoreCase)
                    && connected.Overlaps(item)),
                group.Any(item => item.ChannelUtilizationPercent.HasValue)
                    ? (int)Math.Round(group.Where(item => item.ChannelUtilizationPercent.HasValue)
                        .Average(item => item.ChannelUtilizationPercent!.Value))
                    : null))
            .OrderBy(item => item.Band)
            .ThenBy(item => item.Channel)
            .ToArray();
    }
}
