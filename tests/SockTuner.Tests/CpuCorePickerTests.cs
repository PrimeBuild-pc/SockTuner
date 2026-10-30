using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class CpuCorePickerTests
{
    private static readonly CpuTopologyResult FlatTopology = new([], null);

    [Fact]
    public void PickQuietest_WithNoTopologyDataJustPicksTheQuietestCore()
    {
        var usage = new[] { new CpuCoreUsage(0, 80), new CpuCoreUsage(1, 10), new CpuCoreUsage(2, 50) };

        var choice = CpuCorePicker.PickQuietest(usage, FlatTopology);

        Assert.NotNull(choice);
        Assert.Equal(1, choice.CoreIndex);
    }

    [Fact]
    public void PickQuietest_NeverPicksAnEcoreWhileAQuietPcoreExists()
    {
        // Core 0: P-core, busy. Core 1: E-core, idle. Core 2: P-core, idle.
        var usage = new[] { new CpuCoreUsage(0, 70), new CpuCoreUsage(1, 2), new CpuCoreUsage(2, 15) };
        var topology = new CpuTopologyResult(
            [new CpuCoreTopology(0, 1, 0), new CpuCoreTopology(1, 0, 0), new CpuCoreTopology(2, 1, 0)], null);

        var choice = CpuCorePicker.PickQuietest(usage, topology);

        Assert.NotNull(choice);
        Assert.Equal(2, choice.CoreIndex); // the quietest P-core, not the idler E-core
    }

    [Fact]
    public void PickQuietest_OnAHomogeneousChipTheEfficiencyClassIsIgnored()
    {
        // Every core reports the same EfficiencyClass: not actually hybrid, so no restriction applies.
        var usage = new[] { new CpuCoreUsage(0, 70), new CpuCoreUsage(1, 10) };
        var topology = new CpuTopologyResult([new CpuCoreTopology(0, 0, 0), new CpuCoreTopology(1, 0, 0)], null);

        var choice = CpuCorePicker.PickQuietest(usage, topology);

        Assert.NotNull(choice);
        Assert.Equal(1, choice.CoreIndex);
    }

    [Fact]
    public void PickQuietest_OnMultiCcdStaysOnTheBusierCcdInsteadOfHoppingToAnIdleOne()
    {
        // CCD0 (cores 0-1) is busy overall; CCD1 (cores 2-3) is almost idle. The pick should still
        // land inside CCD0 — its own quietest core — rather than jump to CCD1's idle cores.
        var usage = new[]
        {
            new CpuCoreUsage(0, 60), new CpuCoreUsage(1, 45),
            new CpuCoreUsage(2, 3), new CpuCoreUsage(3, 2)
        };
        var topology = new CpuTopologyResult(
            [
                new CpuCoreTopology(0, null, 0), new CpuCoreTopology(1, null, 0),
                new CpuCoreTopology(2, null, 1), new CpuCoreTopology(3, null, 1)
            ],
            null);

        var choice = CpuCorePicker.PickQuietest(usage, topology);

        Assert.NotNull(choice);
        Assert.Equal(1, choice.CoreIndex); // CCD0's own quietest core, not CCD1's idle ones
    }

    [Fact]
    public void PickQuietest_ReturnsNullWithoutAnyUsageData()
    {
        Assert.Null(CpuCorePicker.PickQuietest([], FlatTopology));
    }
}
