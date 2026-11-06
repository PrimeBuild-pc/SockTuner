using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>Reading of existing evidence, shared by the beginner dashboard and exported reports.</summary>
public static class AutomaticDiagnosisSummary
{
    public static DiagnosticFinding? EnergyTrial(GamingDiagnosticReport report, AdapterInfo? adapter,
        IReadOnlyList<AdapterSettingCapability> capabilities)
    {
        if (adapter is null || adapter.Kind != AdapterKind.Physical
            || adapter.InterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
            || !Guid.TryParse(adapter.Id, out var id)
            || report.Gateway.Received < 20 || report.Gateway.JitterMs is not > 3
            || report.CounterDeltas?.Any(delta => delta.AdapterId.Trim('{', '}').Equals(adapter.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)
                && (delta.ReceiveErrors > 0 || delta.SendErrors > 0)) == true
            || !capabilities.Any(item => item.AdapterId == id && item.Keyword == "*EEE" && item.CanWrite
                && item.CurrentValue == "1" && item.Choices.Any(choice => choice.RegistryValue == "0"))) return null;
        return new(DiagnosticScope.LocalPc, DiagnosticConfidence.Low,
            "Energy saving is a possible local latency contributor",
            $"{adapter.Name}: EEE is enabled and gateway jitter measured {report.Gateway.JitterMs:0.0} ms. This does not establish causation.",
            "Optionally test EEE off, then compare and restore if no benefit is demonstrated.",
            NetworkSegment.LocalNicDriver, RemediationOwner.PresetOrManual, "nic.energy-latency-trial");
    }

    public static string Build(GamingDiagnosticReport report)
    {
        var lines = new List<string> { "Observed working in this run:" };
        if (report.Interface is { } selected) lines.Add($"• {selected.Detail} Installed driver: {selected.DriverDescription ?? "unavailable"}.");
        foreach (var probe in new[] { report.Gateway, report.Reference, report.GameTarget })
            lines.Add(probe.Received > 0
                ? $"• {probe.Label}: {probe.Received}/{probe.Sent} ICMP replies; median {probe.MedianMs:0.#} ms, P95 {probe.P95Ms:0.#} ms."
                : $"• {probe.Label}: insufficient ICMP replies; blocking or rate limiting is possible.");
        if (report.Dns.Duration > TimeSpan.Zero && report.Dns.Error is null) lines.Add("• DNS resolution completed.");
        if (report.Connection is { Error: null }) lines.Add("• The optional TCP connection succeeded.");
        lines.Add("Findings and hypotheses:");
        lines.AddRange(report.Findings.Select(item => $"• [{item.Confidence} confidence] {item.Title}\n  Evidence: {item.Evidence}\n  Next: {item.Action}"));
        lines.AddRange((report.Health ?? []).Select(item => $"• [{item.Confidence} confidence] {item.Title}\n  Evidence: {item.Evidence}\n  Next: {item.Action}"));
        if (report.InterruptSummary is { } interrupts) lines.Add(interrupts);
        lines.Add(report.DriverCleanup is { Count: > 0 } duplicates
            ? $"Driver store: {duplicates.Count} unused older package candidate(s). Optional disk cleanup only; removal has a separate confirmation and cannot be automatically undone. See Host diagnostics."
            : "Driver store: no unused older package candidates were reported. This does not establish that the installed driver is current; compare versions with the hardware vendor.");
        lines.Add("Limits: ICMP timeouts and a slow hop alone do not prove provider responsibility. This short run cannot rule out intermittent faults or establish in-game performance. Router queues and bandwidth require a separate, explicit traffic test. Privileged DPC/ISR capture is optional.");
        lines.Add("Recommended next step: review evidence-based actions. No change may be the correct choice. If the issue persists, repeat at the same time of day over Ethernet, compare another endpoint/network, and export this report with timestamps and targets for the provider. Ask them to check access-link errors and congestion; the report does not assign blame.");
        return string.Join(Environment.NewLine, lines);
    }
}
