using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsOffloadInventory
{
    private const string NamespacePath = @"\\.\root\StandardCimv2";

    internal static OffloadInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], [], null);
        }

        var globals = new List<GlobalOffloadInfo>();
        var adapters = new List<AdapterOffloadInfo>();
        var errors = new List<string>();

        Query("Global offload", "SELECT * FROM MSFT_NetOffloadGlobalSetting", errors, item =>
        {
            AddGlobal("Receive-side scaling", "ReceiveSideScaling");
            AddGlobal("Receive segment coalescing", "ReceiveSegmentCoalescing");
            AddGlobal("TCP Chimney", "Chimney", automaticAllowed: true);
            AddGlobal("Task offload", "TaskOffload");
            AddGlobal("Network Direct", "NetworkDirect");
            AddGlobal("Network Direct across IP subnets", "NetworkDirectAcrossIPSubnets", blockedAllowed: true);
            AddGlobal("Packet coalescing filter", "PacketCoalescingFilter");

            void AddGlobal(string feature, string property, bool automaticAllowed = false, bool blockedAllowed = false)
            {
                var raw = ReadByte(item, property);
                globals.Add(new GlobalOffloadInfo(
                    feature,
                    FormatSwitch(raw, automaticAllowed, blockedAllowed),
                    raw));
            }
        });

        Query("RSS", """
            SELECT InstanceID, Name, InterfaceDescription, Enabled, Profile, NumberOfReceiveQueues,
                   BaseProcessorGroup, BaseProcessorNumber, MaxProcessorGroup, MaxProcessorNumber, MaxProcessors
            FROM MSFT_NetAdapterRssSettingData
            """, errors, item => AddAdapter(item, "RSS",
                FormatBoolean(ReadBoolean(item, "Enabled")),
                "—",
                "—",
                $"Profile {FormatRssProfile(ReadUInt32(item, "Profile"))}; queues {FormatNumber(ReadUInt32(item, "NumberOfReceiveQueues"))}; " +
                $"processors {FormatNumber(ReadUInt32(item, "MaxProcessors"))}; base {FormatProcessor(item, "Base")}; max {FormatProcessor(item, "Max")}"));

        Query("RSC", """
            SELECT InstanceID, Name, InterfaceDescription, IPv4Enabled, IPv4OperationalState, IPv4FailureReason,
                   IPv6Enabled, IPv6OperationalState, IPv6FailureReason
            FROM MSFT_NetAdapterRscSettingData
            """, errors, item => AddAdapter(item, "RSC", "Per protocol",
                FormatOperationalState(item, "IPv4"),
                FormatOperationalState(item, "IPv6"),
                "Receive segment coalescing"));

        Query("LSO", """
            SELECT InstanceID, Name, InterfaceDescription, IPv4Enabled, IPv6Enabled, MaximumLsoVersionSupported,
                   V1IPv4Enabled
            FROM MSFT_NetAdapterLsoSettingData
            """, errors, item => AddAdapter(item, "LSO", "Per protocol",
                FormatBoolean(ReadBoolean(item, "IPv4Enabled")),
                FormatBoolean(ReadBoolean(item, "IPv6Enabled")),
                $"Maximum version {FormatNumber(ReadUInt32(item, "MaximumLsoVersionSupported"))}; " +
                $"V1 IPv4 {FormatBoolean(ReadBoolean(item, "V1IPv4Enabled"))}"));

        Query("Checksum offload", """
            SELECT InstanceID, Name, InterfaceDescription, IpIPv4Enabled, TcpIPv4Enabled, TcpIPv6Enabled,
                   UdpIPv4Enabled, UdpIPv6Enabled
            FROM MSFT_NetAdapterChecksumOffloadSettingData
            """, errors, item => AddAdapter(item, "Checksum", "Per protocol",
                $"IP {FormatChecksum(ReadUInt32(item, "IpIPv4Enabled"))}; TCP {FormatChecksum(ReadUInt32(item, "TcpIPv4Enabled"))}; UDP {FormatChecksum(ReadUInt32(item, "UdpIPv4Enabled"))}",
                $"TCP {FormatChecksum(ReadUInt32(item, "TcpIPv6Enabled"))}; UDP {FormatChecksum(ReadUInt32(item, "UdpIPv6Enabled"))}",
                "Transmit/receive checksum direction"));

        Query("USO", """
            SELECT InstanceID, Name, InterfaceDescription, IPv4Enabled, IPv6Enabled
            FROM MSFT_NetAdapterUsoSettingData
            """, errors, item => AddAdapter(item, "USO", "Per protocol",
                FormatBoolean(ReadBoolean(item, "IPv4Enabled")),
                FormatBoolean(ReadBoolean(item, "IPv6Enabled")),
                "UDP segmentation offload"));

        Query("URO", """
            SELECT InstanceID, Name, InterfaceDescription, Enabled, Operational, FailureReason
            FROM MSFT_NetAdapterUroSettingData
            """, errors, item => AddAdapter(item, "URO",
                FormatBoolean(ReadBoolean(item, "Enabled")),
                "—",
                "—",
                $"Operational {FormatBoolean(ReadBoolean(item, "Operational"))}; failure {FormatUroFailure(ReadUInt32(item, "FailureReason"))}"));

        return new(
            globals.OrderBy(item => item.Feature, StringComparer.Ordinal).ToArray(),
            adapters
                .OrderBy(item => item.AdapterName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Feature, StringComparer.Ordinal)
                .ToArray(),
            errors.Count == 0 ? null : string.Join(" ", errors));

        void AddAdapter(
            ManagementBaseObject item,
            string feature,
            string state,
            string ipv4State,
            string ipv6State,
            string details)
        {
            var instanceId = ReadString(item, "InstanceID");
            if (!WindowsBindingInventory.TryParseAdapterId(instanceId, out var adapterId))
            {
                errors.Add($"{feature} instance '{instanceId}' has no adapter GUID.");
                return;
            }

            adapters.Add(new AdapterOffloadInfo(
                adapterId,
                ReadString(item, "Name"),
                ReadString(item, "InterfaceDescription"),
                feature,
                state,
                ipv4State,
                ipv6State,
                details));
        }
    }

    internal static string FormatSwitch(byte? value, bool automaticAllowed = false, bool blockedAllowed = false) => value switch
    {
        null => "Unavailable",
        0 when blockedAllowed => "Blocked",
        0 => "Disabled",
        1 when blockedAllowed => "Allowed",
        1 => "Enabled",
        2 when automaticAllowed => "Automatic",
        _ => $"Value {value}"
    };

    internal static string FormatChecksum(uint? value) => value switch
    {
        null => "Unavailable",
        0 => "Disabled",
        1 => "Transmit",
        2 => "Receive",
        3 => "Transmit + receive",
        _ => $"Value {value}"
    };

    internal static string FormatUroFailure(uint? value)
    {
        if (value is null)
        {
            return "Unavailable";
        }

        if (value == 0)
        {
            return "None";
        }

        var names = new List<string>();
        Add(1, "NIC property disabled");
        Add(2, "WFP compatibility");
        Add(4, "NDIS compatibility");
        Add(8, "Forwarding enabled");
        Add(16, "Global offload disabled");
        Add(32, "Capability");
        Add(64, "Teredo compatibility");
        Add(128, "IPsec compatibility");
        Add(256, "IPSNPI compatibility");
        Add(512, "Internal error");
        Add(1024, "Interface shutting down");
        Add(2048, "UDP unbound");
        Add(4096, "Unknown");
        var unknown = value.Value & ~8191u;
        if (unknown != 0)
        {
            names.Add($"Flags 0x{unknown:X}");
        }

        return string.Join(", ", names);

        void Add(uint flag, string name)
        {
            if ((value.Value & flag) != 0)
            {
                names.Add(name);
            }
        }
    }

    internal static string FormatRssProfile(uint? value) => value switch
    {
        null => "Unavailable",
        1 => "Closest",
        2 => "Closest static",
        3 => "NUMA scaling",
        4 => "NUMA scaling static",
        5 => "Conservative scaling",
        6 => "Balanced",
        _ => $"Value {value}"
    };

    private static string FormatOperationalState(ManagementBaseObject item, string prefix)
    {
        var enabled = ReadBoolean(item, $"{prefix}Enabled");
        var operational = ReadBoolean(item, $"{prefix}OperationalState");
        var reason = ReadUInt32(item, $"{prefix}FailureReason");
        return $"{FormatBoolean(enabled)}; " + (operational switch
        {
            true => "operational",
            false => $"not operational (reason {FormatNumber(reason)})",
            null => "operational state unavailable"
        });
    }

    private static string FormatProcessor(ManagementBaseObject item, string prefix)
    {
        var group = ReadUInt32(item, $"{prefix}ProcessorGroup");
        var number = ReadUInt32(item, $"{prefix}ProcessorNumber");
        return group is null || number is null ? "unavailable" : $"group {group}, CPU {number}";
    }

    private static string FormatBoolean(bool? value) => value switch
    {
        true => "Enabled",
        false => "Disabled",
        null => "Unavailable"
    };

    private static string FormatNumber(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    private static void Query(
        string surface,
        string query,
        ICollection<string> errors,
        Action<ManagementBaseObject> read)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(NamespacePath),
                new ObjectQuery(query));
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    read(item);
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException
            or COMException
            or InvalidCastException
            or FormatException
            or OverflowException)
        {
            errors.Add($"{surface}: {exception.Message}");
        }
    }

    private static string ReadString(ManagementBaseObject item, string name) =>
        Convert.ToString(item[name], CultureInfo.InvariantCulture) ?? "—";

    private static bool? ReadBoolean(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToBoolean(item[name], CultureInfo.InvariantCulture);

    private static uint? ReadUInt32(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt32(item[name], CultureInfo.InvariantCulture);

    private static byte? ReadByte(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToByte(item[name], CultureInfo.InvariantCulture);
}

internal sealed record OffloadInventoryResult(
    IReadOnlyList<GlobalOffloadInfo> GlobalSettings,
    IReadOnlyList<AdapterOffloadInfo> AdapterSettings,
    string? Error);
