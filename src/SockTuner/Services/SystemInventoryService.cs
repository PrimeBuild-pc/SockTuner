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

        return new NetworkSnapshot(overview, adapters);
    }

    private static AdapterInfo ReadAdapter(NetworkInterface networkInterface)
    {
        IPInterfaceProperties? properties = null;
        string? inventoryError = null;
        try
        {
            properties = networkInterface.GetIPProperties();
        }
        catch (NetworkInformationException exception)
        {
            inventoryError = exception.Message;
        }

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
            networkInterface.Supports(NetworkInterfaceComponent.IPv4),
            networkInterface.Supports(NetworkInterfaceComponent.IPv6),
            inventoryError);
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
