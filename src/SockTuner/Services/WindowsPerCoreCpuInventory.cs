using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Live per-core CPU utilisation, read once (not polled), so an interrupt-affinity recommendation
/// can target a core that is not already busy instead of guessing. No per-core reader existed in
/// this codebase before — only manual core <em>selection</em> UI in <c>InterruptAffinityView</c>.
/// </summary>
internal static class WindowsPerCoreCpuInventory
{
    internal static CpuCoreUsageResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor");
            var cores = new List<CpuCoreUsage>();
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    // "_Total" is the all-core aggregate row; only numbered instances are real cores.
                    if (item["Name"] is not string name || !int.TryParse(name, out var coreIndex))
                    {
                        continue;
                    }

                    var percent = item["PercentProcessorTime"] switch
                    {
                        ulong value => (double)value,
                        uint value => value,
                        _ => 0d
                    };
                    cores.Add(new CpuCoreUsage(coreIndex, percent));
                }
            }

            return new(cores.OrderBy(core => core.CoreIndex).ToArray(), null);
        }
        catch (Exception exception) when (exception is System.Management.ManagementException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new([], $"Could not read per-core CPU usage: {exception.Message}");
        }
    }
}
