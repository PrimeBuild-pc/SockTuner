using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class SystemInventoryService
{
    public NetworkSnapshot Capture()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Select(ReadAdapter)
            .OrderByDescending(adapter => adapter.Status == OperationalStatus.Up)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var overview = new SystemOverview(
            RuntimeInformation.OSDescription.Trim(),
            Environment.OSVersion.Version.ToString(),
            Environment.MachineName,
            Environment.ProcessorCount,
            IsAdministrator(),
            DateTimeOffset.Now);

        var routes = WindowsRouteInventory.Read(adapters);
        return new NetworkSnapshot(overview, adapters, routes.Routes, routes.Error);
    }

    private static AdapterInfo ReadAdapter(NetworkInterface networkInterface)
    {
        IPInterfaceProperties? properties = null;
        IPv4InterfaceProperties? ipv4 = null;
        IPv6InterfaceProperties? ipv6 = null;
        var inventoryErrors = new List<string>();
        try
        {
            properties = networkInterface.GetIPProperties();
        }
        catch (NetworkInformationException exception)
        {
            inventoryErrors.Add($"IP properties: {exception.Message}");
        }

        if (properties is not null && networkInterface.Supports(NetworkInterfaceComponent.IPv4))
        {
            try
            {
                ipv4 = properties.GetIPv4Properties();
            }
            catch (NetworkInformationException exception)
            {
                inventoryErrors.Add($"IPv4 properties: {exception.Message}");
            }
        }

        if (properties is not null && networkInterface.Supports(NetworkInterfaceComponent.IPv6))
        {
            try
            {
                ipv6 = properties.GetIPv6Properties();
            }
            catch (NetworkInformationException exception)
            {
                inventoryErrors.Add($"IPv6 properties: {exception.Message}");
            }
        }

        var inventoryError = inventoryErrors.Count == 0 ? null : string.Join("; ", inventoryErrors);

        var ndis = WindowsNdisInventory.Read(networkInterface.Id);

        return new AdapterInfo(
            networkInterface.Id,
            networkInterface.Name,
            networkInterface.Description,
            networkInterface.NetworkInterfaceType,
            networkInterface.OperationalStatus,
            networkInterface.Speed,
            FormatMacAddress(networkInterface.GetPhysicalAddress()),
            properties?.UnicastAddresses.Select(item => item.Address.ToString()).ToArray() ?? [],
            properties?.GatewayAddresses.Select(item => item.Address.ToString()).ToArray() ?? [],
            properties?.DnsAddresses.Select(item => item.ToString()).ToArray() ?? [],
            ipv4?.Index ?? 0,
            ipv4?.Mtu ?? 0,
            ipv6?.Index ?? 0,
            ipv6?.Mtu ?? 0,
            networkInterface.Supports(NetworkInterfaceComponent.IPv4),
            networkInterface.Supports(NetworkInterfaceComponent.IPv6),
            inventoryError,
            ndis.Driver,
            ndis.Properties,
            ndis.IsSupported,
            ndis.Error);
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "—" : string.Join("-", bytes.Select(value => value.ToString("X2")));
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
