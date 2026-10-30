using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsCpuTopologyInventoryTests
{
    // Hand-built SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record streams, matching the documented
    // layout byte-for-byte, so hybrid and multi-CCD topologies can be exercised without needing
    // real hardware of either kind to run the test on.
    private static byte[] ProcessorCoreRecord(byte efficiencyClass, ulong coreMask)
    {
        var bytes = new byte[8 + 24 + 16];
        BitConverter.GetBytes(0).CopyTo(bytes, 0); // RelationProcessorCore
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 4);
        bytes[8 + 1] = efficiencyClass;
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 8 + 22); // GroupCount
        BitConverter.GetBytes(coreMask).CopyTo(bytes, 8 + 24); // GROUP_AFFINITY.Mask
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 8 + 24 + 8); // GROUP_AFFINITY.Group
        return bytes;
    }

    private static byte[] CacheRecord(byte level, ulong groupMask)
    {
        var bytes = new byte[8 + 32 + 16];
        BitConverter.GetBytes(2).CopyTo(bytes, 0); // RelationCache
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 4);
        bytes[8] = level;
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 8 + 30); // GroupCount
        BitConverter.GetBytes(groupMask).CopyTo(bytes, 8 + 32); // GroupMasks[0].Mask
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 8 + 32 + 8); // GroupMasks[0].Group
        return bytes;
    }

    private static byte[] Concat(params byte[][] records) => records.SelectMany(record => record).ToArray();

    [Fact]
    public void Parse_AUniformQuadCoreIsNeitherHybridNorMultiCcd()
    {
        var data = Concat(
            ProcessorCoreRecord(0, 1UL << 0), ProcessorCoreRecord(0, 1UL << 1),
            ProcessorCoreRecord(0, 1UL << 2), ProcessorCoreRecord(0, 1UL << 3),
            CacheRecord(3, 0b1111)); // one shared L3 across all four cores

        var result = new CpuTopologyResult(WindowsCpuTopologyInventory.Parse(data), null);

        Assert.False(result.IsHybrid);
        Assert.False(result.IsMultiCcd);
        Assert.Equal(4, result.Cores.Count);
    }

    [Fact]
    public void Parse_DistinctEfficiencyClassesAreDetectedAsHybrid()
    {
        var data = Concat(
            ProcessorCoreRecord(1, 1UL << 0), // P-core
            ProcessorCoreRecord(1, 1UL << 1), // P-core
            ProcessorCoreRecord(0, 1UL << 2), // E-core
            ProcessorCoreRecord(0, 1UL << 3), // E-core
            CacheRecord(3, 0b1111));

        var result = new CpuTopologyResult(WindowsCpuTopologyInventory.Parse(data), null);

        Assert.True(result.IsHybrid);
        Assert.Equal(1, result.MaxEfficiencyClass);
        Assert.Equal([1, 1, 0, 0], result.Cores.OrderBy(core => core.CoreIndex).Select(core => core.EfficiencyClass));
    }

    [Fact]
    public void Parse_TwoDistinctL3MasksAreDetectedAsMultiCcd()
    {
        var data = Concat(
            ProcessorCoreRecord(0, 0b1111),
            CacheRecord(3, 0b0011), // CCD0: cores 0-1 share this L3
            CacheRecord(3, 0b1100)); // CCD1: cores 2-3 share a different L3

        var result = new CpuTopologyResult(WindowsCpuTopologyInventory.Parse(data), null);

        Assert.True(result.IsMultiCcd);
        var groups = result.Cores.OrderBy(core => core.CoreIndex).Select(core => core.L3GroupId).ToArray();
        Assert.Equal(groups[0], groups[1]);
        Assert.Equal(groups[2], groups[3]);
        Assert.NotEqual(groups[0], groups[2]);
    }

    [Fact]
    public void Parse_L2CacheRecordsAreIgnored()
    {
        var data = Concat(ProcessorCoreRecord(0, 0b11), CacheRecord(2, 0b11));

        var result = new CpuTopologyResult(WindowsCpuTopologyInventory.Parse(data), null);

        Assert.All(result.Cores, core => Assert.Null(core.L3GroupId));
    }

    [Fact]
    public void Parse_ATruncatedTrailingRecordIsIgnoredRatherThanReadingPastTheBuffer()
    {
        var full = ProcessorCoreRecord(0, 1);
        var truncated = full[..^4]; // cut the record short

        var result = WindowsCpuTopologyInventory.Parse(truncated);

        Assert.Empty(result);
    }
}

/// <summary>Read-only live check of GetLogicalProcessorInformationEx; skipped unless explicitly enabled.</summary>
public sealed class WindowsCpuTopologyInventoryLiveTests
{
    [LiveWindowsFact]
    public void Read_ReportsAtLeastOneCorePerLogicalProcessor()
    {
        var result = WindowsCpuTopologyInventory.Read();

        Assert.Null(result.Error);
        Assert.Equal(Environment.ProcessorCount, result.Cores.Count);
    }
}
