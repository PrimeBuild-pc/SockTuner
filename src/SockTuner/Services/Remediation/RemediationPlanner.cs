using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services.Remediation;

/// <summary>
/// The levers that actually exist on this machine right now. Empty capabilities and a null adapter
/// are the normal case for a run that never selected one, and the planner degrades to guidance
/// rather than proposing a change it cannot address.
/// </summary>
public sealed record RemediationContext(
    Guid? AdapterId = null,
    IReadOnlyList<AdapterSettingCapability>? Capabilities = null,
    int? BlackHoledPathMtu = null,
    IReadOnlyList<GlobalSettingCapability>? GlobalCapabilities = null,
    TcpPathMeasurement? Path = null,
    string TcpTemplate = TcpTuningAdvisor.DefaultTcpTemplate)
{
    public IReadOnlyList<AdapterSettingCapability> Advertised => Capabilities ?? [];
    public IReadOnlyList<GlobalSettingCapability> Globals => GlobalCapabilities ?? [];
}

/// <summary>
/// Remediation layer: turns findings into actions. It never decides whether there is a problem —
/// diagnosis already did that — and it never invents a lever: a finding with nothing local to pull
/// becomes guidance carrying the diagnosis's own instruction, which is the honest output for
/// everything past the router.
/// </summary>
public static class RemediationPlanner
{
    public static IReadOnlyList<RemediationAction> Plan(
        IReadOnlyList<DiagnosticFinding> findings,
        RemediationContext context)
    {
        var actions = new List<RemediationAction>();
        var index = 0;
        foreach (var finding in findings)
        {
            actions.Add(For(finding, context, index++));

            // Bufferbloat stays router-owned: the queue is not on this machine and shaping it there
            // costs nothing. But when the router is not the user's to configure, holding the receive
            // window down is a real endpoint-side lever, so it is offered alongside — labelled a
            // mitigation, with the throughput it costs computed from this path rather than guessed.
            if (finding.Segment == NetworkSegment.RouterOrAccess
                && context.Path is { } path
                && TcpTuningAdvisor.Advise(path, context.Globals, context.TcpTemplate) is { } tuning)
            {
                actions.Add(tuning);
            }
        }

        return actions;
    }

    private static RemediationAction For(DiagnosticFinding finding, RemediationContext context, int index)
    {
        if (finding.Owner is RemediationOwner.Router or RemediationOwner.OutOfScope)
        {
            return Guidance(finding, index);
        }

        if (finding.Segment == NetworkSegment.LocalNicDriver
            && context is { BlackHoledPathMtu: { } mtu, AdapterId: { } adapter })
        {
            return new RemediationAction(
                $"remediation.{index}.interface-mtu",
                $"Set the interface MTU to {mtu} bytes",
                NetworkSegment.LocalNicDriver,
                ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, LocalControl.RequiresChoice),
                [new ChangeRequest("tcp.interface.mtu", adapter.ToString(), mtu.ToString(), ChangeSource.Manual)],
                $"Packets stop being emitted at a size the path discards without telling anyone. {finding.Evidence}",
                "The value is only correct for this path. A different network, or a route change on this one, can make it "
                    + "wrong in either direction; removing the value restores the link-derived default.",
                "Re-run path MTU discovery. It should report the same size and no longer report a black hole. Large "
                    + "transfers that stalled should complete.");
        }

        if (finding.Segment == NetworkSegment.LocalNicDriver && LatencyKeywords(context) is { Count: > 0 } changes)
        {
            return new RemediationAction(
                $"remediation.{index}.nic-idle-latency",
                "Switch off the driver's energy-saving and pause behaviour",
                NetworkSegment.LocalNicDriver,
                ResponsibilityAssigner.Assign(NetworkSegment.LocalNicDriver, LocalControl.RequiresChoice),
                changes,
                "Removes the two driver behaviours that add latency without appearing in any counter: the wake-up delay "
                    + $"after an idle gap, and pause frames that stall the whole queue. {finding.Evidence}",
                "Slightly higher idle power draw, and one congested moment now drops a packet instead of pausing the "
                    + "sender. Neither fixes a cable, a port or a driver fault — if the counters keep rising, the hardware "
                    + "is the problem.",
                "Compare the adapter's error and discard counters across an identical run. A change that leaves them "
                    + "climbing has not addressed the cause.");
        }

        return Guidance(finding, index);
    }

    /// <summary>
    /// The two standardised keywords worth proposing against a local fault, and only where the
    /// driver advertises both the keyword and the value.
    /// </summary>
    private static IReadOnlyList<ChangeRequest> LatencyKeywords(RemediationContext context)
    {
        if (context.AdapterId is not { } adapter)
        {
            return [];
        }

        return context.Advertised
            .Where(capability => capability.AdapterId == adapter && capability.IsStandardKeyword)
            .Where(capability => capability.Keyword is "*EEE" or "*FlowControl")
            .Where(capability => capability.Choices.Any(choice => choice.RegistryValue == "0"))
            .Where(capability => capability.CurrentValue != "0")
            .Select(capability => new ChangeRequest(capability.SettingId, adapter.ToString(), "0", ChangeSource.Manual))
            .ToArray();
    }

    private static RemediationAction Guidance(DiagnosticFinding finding, int index) => new(
        $"guidance.{index}.{finding.Segment}".ToLowerInvariant(),
        finding.Title,
        finding.Segment,
        finding.Owner,
        [],
        finding.Action,
        ResponsibilityAssigner.Explain(finding.Owner),
        finding.Owner == RemediationOwner.OutOfScope
            ? "Repeat the same measurement after the operator reports a change. Keep the runs to compare — that record is "
                + "the whole point of diagnosing something you cannot fix."
            : "Repeat the same measurement after the change. If nothing moves, the parameter was not the cause.");
}
