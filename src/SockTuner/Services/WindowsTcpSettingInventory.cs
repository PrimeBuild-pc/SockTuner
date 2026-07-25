using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsTcpSettingInventory
{
    private const string NamespacePath = @"\\.\root\StandardCimv2";
    private const string Query = """
        SELECT SettingName, AutomaticUseCustom, AutoTuningLevelEffective, AutoTuningLevelGroupPolicy,
               AutoTuningLevelLocal, CongestionProvider, CwndRestart, DelayedAckFrequency,
               DelayedAckTimeout, DynamicPortRangeNumberOfPorts, DynamicPortRangeStartPort,
               EcnCapability, ForceWS, InitialCongestionWindow, InitialRto, MaxSynRetransmissions,
               MemoryPressureProtection, MinRto, NonSackRttResiliency, ScalingHeuristics, Timestamps
        FROM MSFT_NetTCPSetting
        """;

    internal static TcpSettingInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(NamespacePath),
                new ObjectQuery(Query));
            using var results = searcher.Get();
            var settings = new List<TcpSettingInfo>(results.Count);
            var errors = new List<string>();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    try
                    {
                        settings.Add(new TcpSettingInfo(
                            ReadString(item, "SettingName"),
                            ReadByte(item, "AutomaticUseCustom"),
                            ReadByte(item, "AutoTuningLevelEffective"),
                            ReadByte(item, "AutoTuningLevelGroupPolicy"),
                            ReadByte(item, "AutoTuningLevelLocal"),
                            ReadByte(item, "CongestionProvider"),
                            ReadByte(item, "CwndRestart"),
                            ReadByte(item, "DelayedAckFrequency"),
                            ReadUInt32(item, "DelayedAckTimeout"),
                            ReadUInt16(item, "DynamicPortRangeStartPort"),
                            ReadUInt16(item, "DynamicPortRangeNumberOfPorts"),
                            ReadByte(item, "EcnCapability"),
                            ReadByte(item, "ForceWS"),
                            ReadUInt32(item, "InitialCongestionWindow"),
                            ReadUInt32(item, "InitialRto"),
                            ReadByte(item, "MaxSynRetransmissions"),
                            ReadByte(item, "MemoryPressureProtection"),
                            ReadUInt32(item, "MinRto"),
                            ReadByte(item, "NonSackRttResiliency"),
                            ReadByte(item, "ScalingHeuristics"),
                            ReadByte(item, "Timestamps")));
                    }
                    catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
                    {
                        errors.Add($"TCP setting row: {exception.Message}");
                    }
                }
            }

            return new(
                settings
                    .OrderBy(setting => setting.SettingName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                errors.Count == 0 ? null : string.Join(" ", errors));
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException
            or COMException)
        {
            return new([], exception.Message);
        }
    }

    private static string ReadString(ManagementBaseObject item, string name) =>
        Convert.ToString(item[name], CultureInfo.InvariantCulture) ?? "—";

    private static byte? ReadByte(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToByte(item[name], CultureInfo.InvariantCulture);

    private static ushort? ReadUInt16(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt16(item[name], CultureInfo.InvariantCulture);

    private static uint? ReadUInt32(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt32(item[name], CultureInfo.InvariantCulture);
}

internal sealed record TcpSettingInventoryResult(IReadOnlyList<TcpSettingInfo> Settings, string? Error);
