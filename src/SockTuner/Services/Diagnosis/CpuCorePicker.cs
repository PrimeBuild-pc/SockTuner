using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>Which core to pin a device's interrupts to, and why — topology-aware, never a flat "quietest of all" guess.</summary>
public sealed record CpuCoreChoice(int CoreIndex, double PercentProcessorTime, string Reason);

/// <summary>
/// Chooses a target core for an interrupt-affinity recommendation from real CPU topology, not a
/// single "quietest core overall" guess: on a hybrid chip (Intel P-core/E-core) an E-core is never
/// picked while a P-core exists, and on a multi-chiplet AMD part the core comes from whichever L3
/// group (CCD/CCX) is already carrying the most load, rather than hopping to an idle but distant one.
/// </summary>
public static class CpuCorePicker
{
    public static CpuCoreChoice? PickQuietest(IReadOnlyList<CpuCoreUsage> usage, CpuTopologyResult topology)
    {
        if (usage.Count == 0)
        {
            return null;
        }

        var topologyByCore = topology.Cores.ToDictionary(core => core.CoreIndex);
        var candidates = usage;
        var notes = new List<string>();

        if (topology.IsHybrid && topology.MaxEfficiencyClass is { } maxEfficiency)
        {
            var performanceCores = candidates
                .Where(core => topologyByCore.TryGetValue(core.CoreIndex, out var info) && info.EfficiencyClass == maxEfficiency)
                .ToArray();
            if (performanceCores.Length > 0)
            {
                candidates = performanceCores;
                notes.Add("restricted to performance cores on this hybrid chip");
            }
        }

        if (topology.IsMultiCcd)
        {
            var groups = candidates
                .Where(core => topologyByCore.TryGetValue(core.CoreIndex, out var info) && info.L3GroupId is not null)
                .GroupBy(core => topologyByCore[core.CoreIndex].L3GroupId!.Value)
                .ToArray();
            if (groups.Length > 1)
            {
                var busiestGroup = groups.OrderByDescending(group => group.Average(core => core.PercentProcessorTime)).First();
                candidates = busiestGroup.ToArray();
                notes.Add("kept on the CCD that is already carrying the most load, instead of an idle but distant one");
            }
        }

        if (candidates.Count == 0)
        {
            candidates = usage; // The topology filters found nothing to work with; fall back to a plain quietest-core pick.
        }

        var quietest = candidates.OrderBy(core => core.PercentProcessorTime).First();
        var reason = notes.Count == 0
            ? "the quietest core on this machine right now"
            : $"the quietest core after this machine's own topology was taken into account ({string.Join(", ", notes)})";
        return new CpuCoreChoice(quietest.CoreIndex, quietest.PercentProcessorTime, reason);
    }
}
