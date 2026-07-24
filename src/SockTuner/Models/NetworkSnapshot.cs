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
    bool SupportsIPv4,
    bool SupportsIPv6,
    string? InventoryError)
{
    public string SpeedDisplay => FormatSpeed(SpeedBitsPerSecond);
    public string InventoryStatus => InventoryError is null ? "Complete" : $"Partial: {InventoryError}";
    public string AddressesDisplay => Addresses.Count == 0 ? "—" : string.Join(", ", Addresses);
    public string GatewaysDisplay => Gateways.Count == 0 ? "—" : string.Join(", ", Gateways);
    public string DnsDisplay => DnsServers.Count == 0 ? "Automatic / unavailable" : string.Join(", ", DnsServers);
    public string ProtocolsDisplay => (SupportsIPv4, SupportsIPv6) switch
    {
        (true, true) => "IPv4 + IPv6",
        (true, false) => "IPv4",
        (false, true) => "IPv6",
        _ => "None"
    };

    public static string FormatSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000d:0.##} Mbps",
        >= 1_000 => $"{bitsPerSecond / 1_000d:0.##} Kbps",
        > 0 => $"{bitsPerSecond} bps",
        _ => "Unknown"
    };
}

public sealed record NetworkSnapshot(SystemOverview System, IReadOnlyList<AdapterInfo> Adapters)
{
    public int ActiveAdapterCount => Adapters.Count(adapter => adapter.Status == OperationalStatus.Up);
    public int PhysicalLikeAdapterCount => Adapters.Count(adapter => adapter.InterfaceType is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211);
}
