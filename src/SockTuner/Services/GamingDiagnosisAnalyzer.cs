using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services;

public sealed class GamingDiagnosisAnalyzer
{
    public IReadOnlyList<DiagnosticFinding> Analyze(
        ProbeStatistics gateway,
        ProbeStatistics reference,
        ProbeStatistics gameTarget,
        DnsMeasurement dns)
    {
        var findings = new List<DiagnosticFinding>();

        if (gateway.Sent == 0)
        {
            findings.Add(new(
                DiagnosticScope.Lan,
                DiagnosticConfidence.Low,
                "Gateway could not be identified",
                gateway.Note ?? "No active default gateway was available.",
                "Verify the active adapter and default route."));
        }
        else if (gateway.Received == 0)
        {
            findings.Add(new(
                DiagnosticScope.Lan,
                DiagnosticConfidence.Low,
                "Gateway did not answer ICMP",
                $"No gateway ICMP replies ({string.Join(", ", gateway.Samples.Select(sample => sample.FailureKind).Where(kind => kind is not null).Distinct())}). Blocking or deprioritization may be involved, so this does not prove LAN loss.",
                "Check local connectivity and router ICMP policy before drawing a conclusion."));
        }
        else if (!IsLocalStable(gateway))
        {
            var confidence = gateway.LossPercent >= 10 || gateway.P95Ms > 25 || gateway.JitterMs > 10
                ? DiagnosticConfidence.High
                : DiagnosticConfidence.Medium;
            findings.Add(new(
                DiagnosticScope.Lan,
                confidence,
                "Possible instability inside the local network",
                $"Gateway: {gateway.Summary}. A short ICMP run is indicative, not conclusive.",
                "Repeat a longer gateway test, then inspect Wi-Fi signal/interference, Ethernet cabling, adapter errors, power saving, and router load."));
        }

        if (reference.Received == 0)
        {
            findings.Add(new(
                DiagnosticScope.IspOrRouting,
                DiagnosticConfidence.Low,
                "Internet reference did not answer",
                "The neutral reference target returned no ICMP replies; filtering is possible.",
                "Repeat against another reference target and inspect the route."));
        }
        else if (IsLocalStable(gateway) && (reference.LossPercent > 1 || reference.P95Ms > 80 || reference.JitterMs > 15))
        {
            findings.Add(new(
                DiagnosticScope.RouterOrAccess,
                DiagnosticConfidence.Medium,
                "Degradation appears after the local gateway",
                $"Gateway is stable while the internet reference is {reference.Summary}.",
                "Test idle versus loaded latency, then inspect the access link, router queues, and ISP first mile."));
        }

        if (gameTarget.Received == 0)
        {
            findings.Add(new(
                DiagnosticScope.GameEndpoint,
                DiagnosticConfidence.Low,
                "Game endpoint could not be measured with ICMP",
                "No game-target probe replied. Many game servers intentionally block ICMP.",
                "Use an endpoint/profile that permits measurement or add native flow diagnostics; do not treat this as proven packet loss."));
        }
        else if (IsReferenceStable(reference) && gameTarget.LossPercent > Math.Max(1, reference.LossPercent + 1))
        {
            findings.Add(new(
                DiagnosticScope.IspOrRouting,
                DiagnosticConfidence.Medium,
                "Loss is specific to the game path",
                $"Reference loss is {reference.LossPercent:0.#}% while game-path loss is {gameTarget.LossPercent:0.#}%.",
                "Repeat at different times and compare routes; collect a support report for the ISP or game provider."));
        }
        else if (IsReferenceStable(reference) && gameTarget.MinimumMs > 60 && gameTarget.MinimumMs > reference.MinimumMs + 25)
        {
            findings.Add(new(
                DiagnosticScope.GameEndpoint,
                DiagnosticConfidence.Medium,
                "Distance, region, or routing likely dominates base ping",
                $"Best game RTT is {gameTarget.MinimumMs:0.0} ms versus {reference.MinimumMs:0.0} ms to the neutral reference.",
                "Verify the selected game region and compare alternate regional endpoints; a local tweak cannot remove propagation delay."));
        }
        else if (gameTarget.JitterMs > 8 || gameTarget.P95Ms > gameTarget.MedianMs + 25)
        {
            findings.Add(new(
                DiagnosticScope.GameEndpoint,
                DiagnosticConfidence.Medium,
                "Game path has latency spikes",
                $"Game endpoint: {gameTarget.Summary}.",
                "Run a longer idle/loaded comparison and repeated route sampling to separate queueing from remote-server behavior."));
        }

        if (dns.Error is not null)
        {
            findings.Add(new(
                DiagnosticScope.Dns,
                DiagnosticConfidence.High,
                "DNS lookup failed",
                dns.Error,
                "Verify configured resolvers and retry. DNS affects discovery/connection setup, not established-session RTT."));
        }
        else if (dns.Duration.TotalMilliseconds > 250)
        {
            findings.Add(new(
                DiagnosticScope.Dns,
                DiagnosticConfidence.Medium,
                "DNS lookup is slow",
                $"Resolution took {dns.Duration.TotalMilliseconds:0.0} ms.",
                "Compare resolvers. Do not expect a DNS change to reduce steady in-match packet latency."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new(
                DiagnosticScope.General,
                DiagnosticConfidence.Low,
                "No clear fault isolated by this short run",
                $"Gateway: {gateway.Summary}; reference: {reference.Summary}; game target: {gameTarget.Summary}.",
                "Run a longer test during the actual problem and add loaded-latency and route evidence."));
        }

        // Segment and owner are derived here rather than written into each finding, so two
        // findings about the same part of the chain cannot disagree about who owns the fix.
        return findings.Select(finding => ResponsibilityAssigner.Attribute(finding, ControlFor(finding.Scope))).ToArray();
    }

    // How much of the lever SockTuner holds for each area it reports on.
    private static LocalControl ControlFor(DiagnosticScope scope) => scope switch
    {
        // Resolver choice is a reversible local setting, but which resolver to trust is the
        // user's call, so it is never applied without asking.
        DiagnosticScope.Dns => LocalControl.RequiresChoice,
        DiagnosticScope.LocalPc => LocalControl.RequiresChoice,
        DiagnosticScope.Lan => LocalControl.RequiresChoice,
        _ => LocalControl.None
    };

    private static bool IsLocalStable(ProbeStatistics statistics) =>
        statistics.Received > 0 && statistics.LossPercent == 0 && statistics.P95Ms <= 10 && statistics.JitterMs <= 3;

    private static bool IsReferenceStable(ProbeStatistics statistics) =>
        statistics.Received > 0 && statistics.LossPercent <= 1 && statistics.P95Ms <= 30 && statistics.JitterMs <= 5;
}
