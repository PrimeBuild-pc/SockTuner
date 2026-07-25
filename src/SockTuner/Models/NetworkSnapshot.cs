using System.Net.NetworkInformation;

namespace SockTuner.Models;

public sealed record SystemOverview(
    string OperatingSystem,
    string Version,
    string MachineName,
    int LogicalProcessors,
    bool IsAdministrator,
    DateTimeOffset CapturedAt);

public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    OperationalStatus Status,
    long SpeedBitsPerSecond,
    string MacAddress,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    int Ipv4Index,
    int Ipv4Mtu,
    int Ipv6Index,
    int Ipv6Mtu,
    bool SupportsIPv4,
    bool SupportsIPv6,
    string? InventoryError,
    DriverInfo? Driver,
    IReadOnlyList<NdisAdvancedProperty> NdisProperties,
    bool NdisSupported,
    string? NdisInventoryError,
    AdapterCounters? Counters = null,
    IReadOnlyList<IpInterfaceInfo>? IpInterfaces = null)
{
    public string SpeedDisplay => FormatSpeed(SpeedBitsPerSecond);
    public string DriverDisplay => Driver is null ? "Unavailable" : $"{Driver.Provider} {Driver.Version}".Trim();
    public AdapterKind Kind => ClassifyAdapter(InterfaceType, Driver, SupportsIPv4, SupportsIPv6);
    public string AdapterKindDisplay => Kind.ToString();
    public string NdisPropertyCountDisplay => NdisInventoryError is not null
        ? "Partial"
        : NdisSupported ? NdisProperties.Count.ToString() : "Unsupported";
    public string InventoryStatus => string.Join("; ", new[] { InventoryError, NdisInventoryError }
        .Where(error => !string.IsNullOrWhiteSpace(error))
        .Select(error => $"Partial: {error}")) is { Length: > 0 } errors ? errors : "Complete";
    public string AddressesDisplay => Addresses.Count == 0 ? "—" : string.Join(", ", Addresses);
    public string GatewaysDisplay => Gateways.Count == 0 ? "—" : string.Join(", ", Gateways);
    public string DnsDisplay => DnsServers.Count == 0 ? "Automatic / unavailable" : string.Join(", ", DnsServers);
    public string ReceivedDisplay => Counters is null ? "Unavailable" : FormatByteCount(Counters.BytesReceived);
    public string SentDisplay => Counters is null ? "Unavailable" : FormatByteCount(Counters.BytesSent);
    public string ReceiveIssuesDisplay => Counters is null
        ? "Unavailable"
        : $"{Counters.IncomingPacketsWithErrors} errors / {Counters.IncomingPacketsDiscarded} discarded";
    public string SendIssuesDisplay => Counters is null
        ? "Unavailable"
        : $"{Counters.OutgoingPacketsWithErrors} errors / {Counters.OutgoingPacketsDiscarded} discarded";
    public string MetricDisplay => IpInterfaces is { Count: > 0 }
        ? string.Join(" / ", IpInterfaces.Select(item => $"{item.AddressFamily} {item.Metric} ({(item.AutomaticMetric ? "automatic" : "manual")})"))
        : "Unavailable";
    public string DefaultRoutePolicyDisplay => IpInterfaces is { Count: > 0 }
        ? string.Join(" / ", IpInterfaces.Select(item => $"{item.AddressFamily} {(item.DefaultRoutesDisabled ? "disabled" : "allowed")}"))
        : "Unavailable";
    public string MtuDisplay => (Ipv4Mtu, Ipv6Mtu) switch
    {
        ( > 0, > 0) when Ipv4Mtu == Ipv6Mtu => Ipv4Mtu.ToString(),
        ( > 0, > 0) => $"IPv4 {Ipv4Mtu} / IPv6 {Ipv6Mtu}",
        ( > 0, _) => $"IPv4 {Ipv4Mtu}",
        (_, > 0) => $"IPv6 {Ipv6Mtu}",
        _ => "Unknown"
    };
    public string ProtocolsDisplay => (SupportsIPv4, SupportsIPv6) switch
    {
        (true, true) => "IPv4 + IPv6",
        (true, false) => "IPv4",
        (false, true) => "IPv6",
        _ => "None"
    };

    public static AdapterKind ClassifyAdapter(
        NetworkInterfaceType interfaceType,
        DriverInfo? driver,
        bool supportsIPv4,
        bool supportsIPv6)
    {
        if (interfaceType == NetworkInterfaceType.Loopback)
        {
            return AdapterKind.Loopback;
        }

        if (interfaceType == NetworkInterfaceType.Tunnel)
        {
            return AdapterKind.Tunnel;
        }

        if (driver is not null)
        {
            var identities = new[] { driver.PnpInstanceId, driver.ComponentId };
            if (identities.Any(IsVirtualDeviceId))
            {
                return AdapterKind.Virtual;
            }

            if (identities.Any(IsPhysicalDeviceId))
            {
                return AdapterKind.Physical;
            }

            if ((driver.Characteristics & 0x1) != 0)
            {
                return AdapterKind.Virtual;
            }

            if ((driver.Characteristics & 0x4) != 0)
            {
                return AdapterKind.Physical;
            }

            return AdapterKind.DriverBacked;
        }

        return !supportsIPv4 && !supportsIPv6 ? AdapterKind.Filter : AdapterKind.System;
    }

    private static bool IsVirtualDeviceId(string value) =>
        value.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("VMBUS\\", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsPhysicalDeviceId(string value) =>
        value.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);

    public static string FormatByteCount(long bytes) => bytes switch
    {
        >= 1_000_000_000 => FormattableString.Invariant($"{bytes / 1_000_000_000d:0.##} GB"),
        >= 1_000_000 => FormattableString.Invariant($"{bytes / 1_000_000d:0.##} MB"),
        >= 1_000 => FormattableString.Invariant($"{bytes / 1_000d:0.##} KB"),
        >= 0 => $"{bytes} B",
        _ => "Unavailable"
    };

    public static string FormatSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => FormattableString.Invariant($"{bitsPerSecond / 1_000_000_000d:0.##} Gbps"),
        >= 1_000_000 => FormattableString.Invariant($"{bitsPerSecond / 1_000_000d:0.##} Mbps"),
        >= 1_000 => FormattableString.Invariant($"{bitsPerSecond / 1_000d:0.##} Kbps"),
        > 0 => $"{bitsPerSecond} bps",
        _ => "Unknown"
    };
}

public enum AdapterKind
{
    Physical,
    Virtual,
    DriverBacked,
    Loopback,
    Tunnel,
    Filter,
    System
}

public sealed record DriverInfo(
    string Provider,
    string Version,
    string Date,
    string InfPath,
    string ComponentId,
    string NdisVersion,
    string PnpInstanceId,
    uint Characteristics);

public sealed record NdisAdvancedProperty(
    string Keyword,
    string DisplayName,
    string CurrentValue,
    string DefaultValue,
    string Type,
    string ValidValues);

public sealed record NdisInventoryResult(
    DriverInfo? Driver,
    IReadOnlyList<NdisAdvancedProperty> Properties,
    bool IsSupported,
    string? Error);

public sealed record AdapterCounters(
    long BytesReceived,
    long BytesSent,
    long IncomingPacketsDiscarded,
    long IncomingPacketsWithErrors,
    long OutgoingPacketsDiscarded,
    long OutgoingPacketsWithErrors);

public sealed record IpInterfaceInfo(
    string AddressFamily,
    int InterfaceIndex,
    uint Metric,
    uint Mtu,
    bool AutomaticMetric,
    bool Connected,
    bool DefaultRoutesDisabled);

public sealed record TcpSettingInfo(
    string SettingName,
    byte? AutomaticUseCustom,
    byte? AutoTuningLevelEffective,
    byte? AutoTuningLevelGroupPolicy,
    byte? AutoTuningLevelLocal,
    byte? CongestionProvider,
    byte? CwndRestart,
    byte? DelayedAckFrequency,
    uint? DelayedAckTimeout,
    ushort? DynamicPortRangeStartPort,
    ushort? DynamicPortRangeNumberOfPorts,
    byte? EcnCapability,
    byte? ForceWindowScaling,
    uint? InitialCongestionWindow,
    uint? InitialRto,
    byte? MaxSynRetransmissions,
    byte? MemoryPressureProtection,
    uint? MinRto,
    byte? NonSackRttResiliency,
    byte? ScalingHeuristics,
    byte? Timestamps)
{
    public string AutoTuningDisplay =>
        $"{FormatAutoTuning(AutoTuningLevelLocal)} ({FormatAutoTuningSource(AutoTuningLevelEffective)}; policy {FormatAutoTuningPolicy(AutoTuningLevelGroupPolicy)})";
    public string CongestionDisplay => FormatCongestionProvider(CongestionProvider);
    public string AutomaticUseCustomDisplay => FormatSwitch(AutomaticUseCustom);
    public string EcnDisplay => FormatSwitch(EcnCapability);
    public string TimestampsDisplay => Timestamps switch
    {
        null => "Unavailable",
        0 => "Disabled",
        1 => "Enabled",
        2 => "Allowed",
        _ => $"Value {Timestamps}"
    };
    public string DynamicPortRangeDisplay => (DynamicPortRangeStartPort, DynamicPortRangeNumberOfPorts) switch
    {
        ({ } start, { } count) when count > 0 => $"{start}–{start + count - 1} ({count} ports)",
        ({ } start, 0) => $"No ports (start {start})",
        _ => "Unavailable"
    };
    public string TimingDisplay =>
        $"Initial RTO {FormatMilliseconds(InitialRto)}; minimum RTO {FormatMilliseconds(MinRto)}; delayed ACK timeout {FormatMilliseconds(DelayedAckTimeout)}; frequency {FormatNumber(DelayedAckFrequency)}";
    public string OtherFlagsDisplay =>
        $"Window scaling {FormatSwitch(ForceWindowScaling)}; heuristics {FormatSwitch(ScalingHeuristics)}; memory pressure {FormatMemoryPressure(MemoryPressureProtection)}; non-SACK RTT {FormatSwitch(NonSackRttResiliency)}; cwnd restart {FormatSwitch(CwndRestart)}";

    public static string FormatAutoTuning(byte? value) => value switch
    {
        null => "Unavailable",
        0 => "Disabled",
        1 => "Highly restricted",
        2 => "Restricted",
        3 => "Normal",
        4 => "Experimental",
        _ => $"Value {value}"
    };

    public static string FormatCongestionProvider(byte? value) => value switch
    {
        null => "Unavailable",
        0 => "Default",
        1 => "NewReno",
        2 => "CTCP",
        3 => "DCTCP",
        4 => "LEDBAT",
        5 => "CUBIC",
        6 => "BBR2",
        _ => $"Value {value}"
    };

    private static string FormatAutoTuningSource(byte? value) => value switch
    {
        null => "source unavailable",
        0 => "local",
        1 => "group policy",
        _ => $"source {value}"
    };

    private static string FormatAutoTuningPolicy(byte? value) => value switch
    {
        254 => "not configured",
        255 => "not changed",
        _ => FormatAutoTuning(value).ToLowerInvariant()
    };

    private static string FormatMemoryPressure(byte? value) => value switch
    {
        null => "Unavailable",
        0 => "Disabled",
        1 => "Enabled",
        2 => "Default",
        _ => $"Value {value}"
    };

    private static string FormatSwitch(byte? value) => value switch
    {
        null => "Unavailable",
        0 => "Disabled",
        1 => "Enabled",
        _ => $"Value {value}"
    };

    private static string FormatMilliseconds(uint? value) => value is null ? "unavailable" : $"{value} ms";
    private static string FormatNumber(byte? value) => value?.ToString() ?? "unavailable";
}

public sealed record GlobalOffloadInfo(string Feature, string State, byte? RawValue);

public sealed record AdapterOffloadInfo(
    Guid AdapterId,
    string AdapterName,
    string InterfaceDescription,
    string Feature,
    string State,
    string Ipv4State,
    string Ipv6State,
    string Details);

public sealed record NetworkBindingInfo(
    Guid AdapterId,
    string AdapterName,
    string InterfaceDescription,
    string ComponentId,
    string DisplayName,
    string BindName,
    bool Enabled,
    uint Characteristics,
    string ComponentClassGuid,
    string ComponentClassName,
    uint Source)
{
    public string StateDisplay => Enabled ? "Enabled" : "Disabled";
    public string CharacteristicsDisplay => $"0x{Characteristics:X8}";
}

public sealed record NetworkProfileInfo(
    Guid NetworkId,
    string Name,
    string Category,
    string DomainType,
    uint Connectivity,
    bool IsConnected,
    bool IsInternetConnected,
    Guid AdapterId,
    string AdapterName)
{
    public string ConnectivityDisplay => FormatConnectivity(Connectivity);
    public string StatusDisplay => IsInternetConnected ? "Internet" : IsConnected ? "Connected" : "Disconnected";

    public static string FormatConnectivity(uint connectivity)
    {
        if (connectivity == 0)
        {
            return "Disconnected";
        }

        var values = new List<string>();
        AddFlag(0x1, "IPv4 no traffic");
        AddFlag(0x2, "IPv6 no traffic");
        AddFlag(0x10, "IPv4 subnet");
        AddFlag(0x20, "IPv4 local");
        AddFlag(0x40, "IPv4 internet");
        AddFlag(0x100, "IPv6 subnet");
        AddFlag(0x200, "IPv6 local");
        AddFlag(0x400, "IPv6 internet");
        var unknown = connectivity & ~0x773u;
        if (unknown != 0)
        {
            values.Add($"Flags 0x{unknown:X}");
        }

        return string.Join(", ", values);

        void AddFlag(uint flag, string name)
        {
            if ((connectivity & flag) != 0)
            {
                values.Add(name);
            }
        }
    }
}

public sealed record WinsockProviderInfo(
    uint CatalogEntryId,
    Guid ProviderId,
    string Name,
    int AddressFamily,
    int SocketType,
    int Protocol,
    int ChainLength,
    IReadOnlyList<uint> ChainEntries,
    uint ProviderFlags,
    uint ServiceFlags1,
    uint ServiceFlags2,
    uint ServiceFlags3,
    uint ServiceFlags4)
{
    public string AddressFamilyDisplay => AddressFamily switch
    {
        1 => "Unix",
        2 => "IPv4",
        23 => "IPv6",
        32 => "Bluetooth",
        34 => "Hyper-V",
        _ => $"Family {AddressFamily}"
    };
    public string SocketTypeDisplay => SocketType switch
    {
        1 => "Stream",
        2 => "Datagram",
        3 => "Raw",
        4 => "RDM",
        5 => "SeqPacket",
        _ => $"Type {SocketType}"
    };
    public string ProtocolDisplay => Protocol switch
    {
        6 => "TCP",
        17 => "UDP",
        _ => $"Protocol {Protocol}"
    };
    public string ChainDisplay => ChainLength switch
    {
        0 => "Layered",
        1 => "Base",
        _ => $"Chain ({ChainLength}): {string.Join(" → ", ChainEntries)}"
    };
    public string FlagsDisplay =>
        $"Provider 0x{ProviderFlags:X8} / Services 0x{ServiceFlags1:X8}, 0x{ServiceFlags2:X8}, 0x{ServiceFlags3:X8}, 0x{ServiceFlags4:X8}";
}

public sealed record RouteInfo(
    string AddressFamily,
    string Destination,
    string NextHop,
    int InterfaceIndex,
    string InterfaceName,
    uint Metric,
    string Protocol,
    string Type);

public sealed record NetworkSnapshot(
    SystemOverview System,
    IReadOnlyList<AdapterInfo> Adapters,
    IReadOnlyList<RouteInfo> Routes,
    string? RouteInventoryError,
    string? IpInterfaceInventoryError = null,
    IReadOnlyList<NetworkProfileInfo>? NetworkProfiles = null,
    string? NetworkProfileInventoryError = null,
    IReadOnlyList<WinsockProviderInfo>? WinsockProviders = null,
    string? WinsockInventoryError = null,
    IReadOnlyList<NetworkBindingInfo>? NetworkBindings = null,
    string? NetworkBindingInventoryError = null,
    IReadOnlyList<GlobalOffloadInfo>? GlobalOffloads = null,
    IReadOnlyList<AdapterOffloadInfo>? AdapterOffloads = null,
    string? OffloadInventoryError = null,
    IReadOnlyList<TcpSettingInfo>? TcpSettings = null,
    string? TcpSettingInventoryError = null)
{
    public int ActiveAdapterCount => Adapters.Count(adapter =>
        adapter.Status == OperationalStatus.Up
        && (adapter.SupportsIPv4 || adapter.SupportsIPv6)
        && adapter.Kind is not AdapterKind.Filter
            and not AdapterKind.Loopback
            and not AdapterKind.Tunnel);
    public int PhysicalLikeAdapterCount => Adapters.Count(adapter => adapter.InterfaceType is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211);
}
