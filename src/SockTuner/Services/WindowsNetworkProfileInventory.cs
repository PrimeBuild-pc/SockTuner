using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsNetworkProfileInventory
{
    private static readonly Guid NetworkListManagerClassId = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    internal static NetworkProfileInventoryResult Read(IReadOnlyList<AdapterInfo> adapters)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var profiles = new List<NetworkProfileInfo>();
        object? managerObject = null;
        try
        {
            var managerType = Type.GetTypeFromCLSID(NetworkListManagerClassId)
                ?? throw new COMException("Network List Manager is unavailable.");
            managerObject = Activator.CreateInstance(managerType)
                ?? throw new COMException("Network List Manager could not be created.");
            dynamic manager = managerObject;
            object? networksObject = null;
            try
            {
                networksObject = manager.GetNetworks(3);
                foreach (dynamic networkObject in (dynamic)networksObject)
                {
                    try
                    {
                        var network = (INetwork)networkObject;
                        object? connectionsObject = null;
                        try
                        {
                            connectionsObject = network.GetNetworkConnections();
                            foreach (dynamic connectionObject in (dynamic)connectionsObject)
                            {
                                try
                                {
                                    var connection = (INetworkConnection)connectionObject;
                                    var adapterId = connection.GetAdapterId();
                                    profiles.Add(new NetworkProfileInfo(
                                        network.GetNetworkId(),
                                        network.GetName(),
                                        CategoryName(network.GetCategory()),
                                        DomainTypeName(network.GetDomainType()),
                                        network.GetConnectivity(),
                                        network.GetIsConnected(),
                                        network.GetIsConnectedToInternet(),
                                        adapterId,
                                        FindAdapterName(adapters, adapterId)));
                                }
                                finally
                                {
                                    ReleaseComObject(connectionObject);
                                }
                            }
                        }
                        finally
                        {
                            ReleaseComObject(connectionsObject);
                        }
                    }
                    finally
                    {
                        ReleaseComObject(networkObject);
                    }
                }
            }
            finally
            {
                ReleaseComObject(networksObject);
            }

            return new(profiles
                .OrderByDescending(profile => profile.IsConnected)
                .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(profile => profile.AdapterName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(), null);
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidCastException)
        {
            return new(profiles, exception.Message);
        }
        finally
        {
            ReleaseComObject(managerObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    internal static string CategoryName(int category) => category switch
    {
        0 => "Public",
        1 => "Private",
        2 => "Domain authenticated",
        _ => $"Category {category}"
    };

    internal static string DomainTypeName(int domainType) => domainType switch
    {
        0 => "Non-domain",
        1 => "Domain",
        2 => "Domain authenticated",
        _ => $"Domain type {domainType}"
    };

    private static string FindAdapterName(IReadOnlyList<AdapterInfo> adapters, Guid adapterId) =>
        adapters.FirstOrDefault(adapter => Guid.TryParse(adapter.Id, out var id) && id == adapterId)?.Name
        ?? "Unknown interface";

    [ComImport]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
        uint GetConnectivity();
        int GetCategory();
        void SetCategory(int category);
    }

    [ComImport]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
        uint GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }
}

internal sealed record NetworkProfileInventoryResult(IReadOnlyList<NetworkProfileInfo> Profiles, string? Error);
