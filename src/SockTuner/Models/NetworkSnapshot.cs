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
    string? NdisInventoryError)
{
    public string SpeedDisplay => FormatSpeed(SpeedBitsPerSecond);
    public string DriverDisplay => Driver is null ? "Unavailable" : $"{Driver.Provider} {Driver.Version}".Trim();
    public string AdapterKindDisplay => ClassifyAdapter(InterfaceType, Driver, SupportsIPv4, SupportsIPv6).ToString();
    public string NdisPropertyCountDisplay => NdisInventoryError is not null
        ? "Partial"
        : NdisSupported ? NdisProperties.Count.ToString() : "Unsupported";
    public string InventoryStatus => string.Join("; ", new[] { InventoryError, NdisInventoryError }
        .Where(error => !string.IsNullOrWhiteSpace(error))
        .Select(error => $"Partial: {error}")) is { Length: > 0 } errors ? errors : "Complete";
    public string AddressesDisplay => Addresses.Count == 0 ? "—" : string.Join(", ", Addresses);
    public string GatewaysDisplay => Gateways.Count == 0 ? "—" : string.Join(", ", Gateways);
    public string DnsDisplay => DnsServers.Count == 0 ? "Automatic / unavailable" : string.Join(", ", DnsServers);
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
            if ((driver.Characteristics & 0x4) != 0
                || driver.PnpInstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                || driver.PnpInstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                return AdapterKind.Physical;
            }

            if ((driver.Characteristics & 0x1) != 0
                || driver.PnpInstanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
                || driver.PnpInstanceId.StartsWith("VMBUS\\", StringComparison.OrdinalIgnoreCase)
                || driver.PnpInstanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase))
            {
                return AdapterKind.Virtual;
            }

            return AdapterKind.DriverBacked;
        }

        return !supportsIPv4 && !supportsIPv6 ? AdapterKind.Filter : AdapterKind.System;
    }

    public static string FormatSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000d:0.##} Mbps",
        >= 1_000 => $"{bitsPerSecond / 1_000d:0.##} Kbps",
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

public sealed record RouteInfo(
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
    string? RouteInventoryError)
{
    public int ActiveAdapterCount => Adapters.Count(adapter => adapter.Status == OperationalStatus.Up);
    public int PhysicalLikeAdapterCount => Adapters.Count(adapter => adapter.InterfaceType is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211);
}
