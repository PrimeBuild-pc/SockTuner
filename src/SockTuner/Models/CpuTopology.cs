namespace SockTuner.Models;

/// <summary>
/// One logical processor's place in the CPU's own topology: its efficiency class on a hybrid chip
/// (Intel P-core/E-core — higher is more performant, per Microsoft's own definition), and which L3
/// cache group it shares with other cores — the practical proxy for "which CCD/CCX" on a multi-chiplet
/// AMD part, since L3 is not shared across chiplets.
/// </summary>
public sealed record CpuCoreTopology(int CoreIndex, int? EfficiencyClass, int? L3GroupId);

public sealed record CpuTopologyResult(IReadOnlyList<CpuCoreTopology> Cores, string? Error)
{
    /// <summary>More than one distinct efficiency class means this is a hybrid chip (P-cores + E-cores).</summary>
    public bool IsHybrid => Cores.Select(core => core.EfficiencyClass).Where(value => value is not null).Distinct().Count() > 1;

    /// <summary>More than one distinct L3 group means interrupts on one group pay a cross-chiplet latency to reach the other.</summary>
    public bool IsMultiCcd => Cores.Select(core => core.L3GroupId).Where(value => value is not null).Distinct().Count() > 1;

    public int? MaxEfficiencyClass => Cores.Select(core => core.EfficiencyClass).Where(value => value is not null).DefaultIfEmpty((int?)null).Max();
}
