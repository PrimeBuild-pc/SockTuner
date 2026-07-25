using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsIpInterfaceInventory
{
    private const ushort AddressFamilyUnspecified = 0;

    internal static IpInterfaceInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var result = GetIpInterfaceTable(AddressFamilyUnspecified, out var table);
        if (result != 0)
        {
            return new([], $"GetIpInterfaceTable failed with Windows error {result}.");
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibIpInterfaceRow>();
            var rowAddress = IntPtr.Add(table, NativeTableRowOffset);
            var interfaces = new List<IpInterfaceInfo>(count);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibIpInterfaceRow>(IntPtr.Add(rowAddress, index * rowSize));
                var family = row.Family switch
                {
                    2 => "IPv4",
                    23 => "IPv6",
                    _ => null
                };
                if (family is not null)
                {
                    interfaces.Add(new IpInterfaceInfo(
                        family,
                        checked((int)row.InterfaceIndex),
                        row.Metric,
                        row.NlMtu,
                        row.UseAutomaticMetric != 0,
                        row.Connected != 0,
                        row.DisableDefaultRoutes != 0));
                }
            }

            return new(interfaces
                .OrderBy(item => item.InterfaceIndex)
                .ThenBy(item => item.AddressFamily, StringComparer.Ordinal)
                .ToArray(), null);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    internal static int NativeRowSize => Marshal.SizeOf<MibIpInterfaceRow>();
    internal static int NativeTableRowOffset => checked((int)Marshal.OffsetOf<MibIpInterfaceTable>(nameof(MibIpInterfaceTable.Table)));

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpInterfaceTable(ushort addressFamily, out nint table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpInterfaceTable
    {
        public uint NumEntries;
        public MibIpInterfaceRow Table;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpInterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public uint RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public uint LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public uint[] ZoneIndices;
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }
}

internal sealed record IpInterfaceInventoryResult(IReadOnlyList<IpInterfaceInfo> Interfaces, string? Error);
