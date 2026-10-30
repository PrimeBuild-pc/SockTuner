using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Reads this machine's real CPU topology — which logical processors are P-cores vs. E-cores on a
/// hybrid chip, and which share an L3 cache (the same CCD/CCX on a multi-chiplet AMD part) — via
/// <c>GetLogicalProcessorInformationEx</c>. Used so an interrupt-affinity recommendation can avoid
/// an E-core when a P-core is free, and avoid hopping to a cold, cross-chiplet CCD.
/// </summary>
internal static class WindowsCpuTopologyInventory
{
    private const int ErrorInsufficientBuffer = 122;
    private const int RelationAll = 0xffff;
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int CacheLevel3 = 3;

    internal static CpuTopologyResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var size = 0;
        if (GetLogicalProcessorInformationEx(RelationAll, nint.Zero, ref size)
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            return new([], $"GetLogicalProcessorInformationEx failed with Windows error {Marshal.GetLastWin32Error()}.");
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref size))
            {
                return new([], $"GetLogicalProcessorInformationEx failed with Windows error {Marshal.GetLastWin32Error()}.");
            }

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return new(Parse(bytes), null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Walks the variable-length record stream <c>GetLogicalProcessorInformationEx</c> returns.
    /// Pure and byte-array based (no pointers) so the exact offsets below — matching the documented
    /// <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c>/<c>PROCESSOR_RELATIONSHIP</c>/
    /// <c>CACHE_RELATIONSHIP</c> layouts — can be exercised with a hand-built buffer in a unit test,
    /// without needing a real hybrid or multi-CCD machine to run on.
    /// </summary>
    internal static IReadOnlyList<CpuCoreTopology> Parse(byte[] data)
    {
        var efficiencyByCore = new Dictionary<int, int>();
        var l3GroupByCore = new Dictionary<int, int>();
        var l3MaskToGroupId = new Dictionary<ulong, int>();

        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var relationship = BitConverter.ToInt32(data, offset);
            var recordSize = BitConverter.ToInt32(data, offset + 4);
            if (recordSize < 8 || offset + recordSize > data.Length)
            {
                break; // Malformed or truncated record: stop rather than read past the buffer.
            }

            var payload = offset + 8;
            switch (relationship)
            {
                case RelationProcessorCore:
                    ParseProcessorCore(data, payload, offset + recordSize, efficiencyByCore);
                    break;
                case RelationCache:
                    ParseCache(data, payload, offset + recordSize, l3GroupByCore, l3MaskToGroupId);
                    break;
            }

            offset += recordSize;
        }

        var coreIndices = efficiencyByCore.Keys.Concat(l3GroupByCore.Keys).Distinct().OrderBy(index => index);
        return coreIndices
            .Select(index => new CpuCoreTopology(
                index,
                efficiencyByCore.TryGetValue(index, out var efficiency) ? efficiency : null,
                l3GroupByCore.TryGetValue(index, out var group) ? group : null))
            .ToArray();
    }

    // PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GROUP_AFFINITY[]
    private static void ParseProcessorCore(byte[] data, int start, int end, Dictionary<int, int> efficiencyByCore)
    {
        if (start + 24 > end)
        {
            return;
        }

        var efficiencyClass = data[start + 1];
        var groupCount = BitConverter.ToUInt16(data, start + 22);
        var groupAffinityOffset = start + 24;

        for (var group = 0; group < groupCount && groupAffinityOffset + 16 <= end; group++, groupAffinityOffset += 16)
        {
            var mask = BitConverter.ToUInt64(data, groupAffinityOffset);
            var groupNumber = BitConverter.ToUInt16(data, groupAffinityOffset + 8);
            if (groupNumber != 0)
            {
                continue; // Only the first processor group is modelled — see the >64 logical processors note below.
            }

            for (var bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0)
                {
                    efficiencyByCore[bit] = efficiencyClass;
                }
            }
        }
    }

    // CACHE_RELATIONSHIP: Level(1) Associativity(1) LineSize(2) CacheSize(4) Type(4) Reserved(18)
    // GroupCount(2) then the GroupMask union, starting at offset 32. Only GroupMasks[0] is read:
    // shortcut, a system with more than 64 logical processors (multiple groups) is out of scope for
    // the home/gaming hardware this heuristic targets; upgrade here if that ever changes.
    private static void ParseCache(
        byte[] data, int start, int end, Dictionary<int, int> l3GroupByCore, Dictionary<ulong, int> maskToGroupId)
    {
        if (start + 32 + 16 > end)
        {
            return;
        }

        var level = data[start];
        if (level != CacheLevel3)
        {
            return;
        }

        var mask = BitConverter.ToUInt64(data, start + 32);
        var groupNumber = BitConverter.ToUInt16(data, start + 32 + 8);
        if (groupNumber != 0 || mask == 0)
        {
            return;
        }

        if (!maskToGroupId.TryGetValue(mask, out var groupId))
        {
            groupId = maskToGroupId.Count;
            maskToGroupId[mask] = groupId;
        }

        for (var bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
            {
                l3GroupByCore[bit] = groupId;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, nint buffer, ref int returnedLength);
}
