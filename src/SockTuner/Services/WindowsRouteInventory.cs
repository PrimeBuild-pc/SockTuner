using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsRouteInventory
{
    private const int ErrorInsufficientBuffer = 122;

    internal static RouteInventoryResult Read(IReadOnlyList<AdapterInfo> adapters)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

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

            var interfaceNames = adapters
                .Where(adapter => adapter.Ipv4Index > 0)
                .GroupBy(adapter => adapter.Ipv4Index)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(adapter => adapter.NdisSupported).First().Name);
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpForwardRow>();
            var routes = new RouteInfo[count];
            var rowAddress = IntPtr.Add(buffer, sizeof(int));

            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(rowAddress, index * rowSize));
                routes[index] = new RouteInfo(
                    $"{FormatAddress(row.Destination)}/{PrefixLength(row.Mask)}",
                    row.NextHop == 0 ? "On-link" : FormatAddress(row.NextHop),
                    checked((int)row.InterfaceIndex),
                    interfaceNames.GetValueOrDefault(checked((int)row.InterfaceIndex), "Unknown interface"),
                    row.Metric1,
                    ProtocolName(row.Protocol),
                    RouteTypeName(row.Type));
            }

            return new(routes
                .OrderBy(route => route.Destination == "0.0.0.0/0" ? 0 : 1)
                .ThenBy(route => route.Destination, StringComparer.Ordinal)
                .ThenBy(route => route.Metric)
                .ToArray(), null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string FormatAddress(uint address) => new IPAddress(BitConverter.GetBytes(address)).ToString();

    internal static int PrefixLength(uint mask) => BitOperations.PopCount(mask);

    internal static int NativeRowSize => Marshal.SizeOf<MibIpForwardRow>();

    private static string ProtocolName(uint protocol) => protocol switch
    {
        2 => "Local",
        3 => "Management",
        8 => "RIP",
        13 => "OSPF",
        14 => "BGP",
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
}

internal sealed record RouteInventoryResult(IReadOnlyList<RouteInfo> Routes, string? Error);
