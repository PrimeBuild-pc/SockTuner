using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsRouteInventory
{
    private const int ErrorInsufficientBuffer = 122;
    private const ushort AddressFamilyInterNetworkV6 = 23;

    internal static RouteInventoryResult Read(IReadOnlyList<AdapterInfo> adapters)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var ipv4 = ReadIpv4(adapters);
        var ipv6 = ReadIpv6(adapters);
        var errors = new[] { ipv4.Error, ipv6.Error }.Where(error => error is not null).ToArray();
        return new(
            ipv4.Routes.Concat(ipv6.Routes)
                .OrderBy(route => route.Destination is "0.0.0.0/0" or "::/0" ? 0 : 1)
                .ThenBy(route => route.AddressFamily, StringComparer.Ordinal)
                .ThenBy(route => route.Destination, StringComparer.Ordinal)
                .ThenBy(route => route.Metric)
                .ToArray(),
            errors.Length == 0 ? null : string.Join("; ", errors));
    }

    private static RouteInventoryResult ReadIpv4(IReadOnlyList<AdapterInfo> adapters)
    {
        var size = 0;
        var result = GetIpForwardTable(nint.Zero, ref size, false);
        if (result is not 0 and not ErrorInsufficientBuffer)
        {
            return new([], $"GetIpForwardTable failed with Windows error {result}.");
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetIpForwardTable(buffer, ref size, false);
            if (result != 0)
            {
                return new([], $"GetIpForwardTable failed with Windows error {result}.");
            }

            var interfaceNames = InterfaceNames(adapters, adapter => adapter.Ipv4Index);
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpForwardRow>();
            var routes = new RouteInfo[count];
            var rowAddress = IntPtr.Add(buffer, sizeof(int));

            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(rowAddress, index * rowSize));
                routes[index] = new RouteInfo(
                    "IPv4",
                    $"{FormatAddress(row.Destination)}/{PrefixLength(row.Mask)}",
                    row.NextHop == 0 ? "On-link" : FormatAddress(row.NextHop),
                    checked((int)row.InterfaceIndex),
                    interfaceNames.GetValueOrDefault(checked((int)row.InterfaceIndex), "Unknown interface"),
                    row.Metric1,
                    ProtocolName(row.Protocol),
                    RouteTypeName(row.Type));
            }

            return new(routes, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RouteInventoryResult ReadIpv6(IReadOnlyList<AdapterInfo> adapters)
    {
        var result = GetIpForwardTable2(AddressFamilyInterNetworkV6, out var table);
        if (result != 0)
        {
            return new([], $"GetIpForwardTable2(IPv6) failed with Windows error {result}.");
        }

        try
        {
            var interfaceNames = InterfaceNames(adapters, adapter => adapter.Ipv6Index);
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibIpForwardRow2>();
            var rowAddress = IntPtr.Add(table, checked((int)Marshal.OffsetOf<MibIpForwardTable2>(nameof(MibIpForwardTable2.Table))));
            var routes = new RouteInfo[count];

            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow2>(IntPtr.Add(rowAddress, index * rowSize));
                var nextHop = FormatIpv6Address(row.NextHop);
                var onLink = IPAddress.Parse(nextHop).Equals(IPAddress.IPv6Any);
                routes[index] = new RouteInfo(
                    "IPv6",
                    $"{FormatIpv6Address(row.DestinationPrefix.Prefix)}/{row.DestinationPrefix.PrefixLength}",
                    onLink ? "On-link" : nextHop,
                    checked((int)row.InterfaceIndex),
                    interfaceNames.GetValueOrDefault(checked((int)row.InterfaceIndex), "Unknown interface"),
                    row.Metric,
                    ProtocolName(row.Protocol),
                    onLink ? "Direct" : "Indirect");
            }

            return new(routes, null);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static IReadOnlyDictionary<int, string> InterfaceNames(
        IReadOnlyList<AdapterInfo> adapters,
        Func<AdapterInfo, int> indexSelector) => adapters
            .Where(adapter => indexSelector(adapter) > 0)
            .GroupBy(indexSelector)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(adapter => adapter.NdisSupported).First().Name);

    internal static string FormatAddress(uint address) => new IPAddress(BitConverter.GetBytes(address)).ToString();

    private static string FormatIpv6Address(SockaddrInet address) => FormatIpv6Address(
        address.Ipv6Part0, address.Ipv6Part1, address.Ipv6Part2, address.Ipv6Part3, address.Ipv6ScopeId);

    internal static string FormatIpv6Address(uint part0, uint part1, uint part2, uint part3, uint scopeId)
    {
        var bytes = new byte[16];
        Buffer.BlockCopy(new[] { part0, part1, part2, part3 }, 0, bytes, 0, bytes.Length);
        return new IPAddress(bytes, scopeId).ToString();
    }

    internal static int PrefixLength(uint mask) => BitOperations.PopCount(mask);

    internal static int NativeRowSize => Marshal.SizeOf<MibIpForwardRow>();
    internal static int NativeRow2Size => Marshal.SizeOf<MibIpForwardRow2>();
    internal static int NativeTable2RowOffset => checked((int)Marshal.OffsetOf<MibIpForwardTable2>(nameof(MibIpForwardTable2.Table)));

    private static string ProtocolName(uint protocol) => protocol switch
    {
        2 => "Local",
        3 => "Management",
        4 => "ICMP",
        8 => "RIP",
        13 => "OSPF",
        14 => "BGP",
        19 => "DHCP",
        10002 => "Auto-static",
        10006 => "Static",
        10007 => "Static non-DOD",
        _ => $"Protocol {protocol}"
    };

    private static string RouteTypeName(uint type) => type switch
    {
        2 => "Invalid",
        3 => "Direct",
        4 => "Indirect",
        _ => $"Type {type}"
    };

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable2(ushort addressFamily, out nint table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint Destination;
        public uint Mask;
        public uint Policy;
        public uint NextHop;
        public uint InterfaceIndex;
        public uint Type;
        public uint Protocol;
        public uint Age;
        public uint NextHopAutonomousSystem;
        public uint Metric1;
        public uint Metric2;
        public uint Metric3;
        public uint Metric4;
        public uint Metric5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardTable2
    {
        public uint NumEntries;
        public MibIpForwardRow2 Table;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public uint Ipv6Part0;
        [FieldOffset(12)] public uint Ipv6Part1;
        [FieldOffset(16)] public uint Ipv6Part2;
        [FieldOffset(20)] public uint Ipv6Part3;
        [FieldOffset(24)] public uint Ipv6ScopeId;
    }
}

internal sealed record RouteInventoryResult(IReadOnlyList<RouteInfo> Routes, string? Error);
