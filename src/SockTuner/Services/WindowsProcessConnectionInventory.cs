using System.Net;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Which process owns each active TCP connection, read from the same owner-PID table Resource
/// Monitor uses (<c>GetExtendedTcpTable</c>) — no packet capture, matching SockTuner's existing
/// non-capture diagnostic model.
/// </summary>
/// <remarks>
/// shortcut: reports a connection count per process, not bytes-in-flight or retransmits.
/// Per-connection TCP ESTATS (<c>GetPerTcpConnectionEStats</c>) would add that, but collection must be
/// switched on per connection before data accumulates (<c>SetPerTcpConnectionEStats</c>), which is a
/// bigger follow-up than this slice. Upgrade when "which process is saturating the link" needs more
/// than a connection count.
/// </remarks>
internal static class WindowsProcessConnectionInventory
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;

    internal static ProcessConnectionInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var processNames = new Dictionary<int, string>();
        var ipv4 = ReadFamily(AfInet, processNames);
        var ipv6 = ReadFamily(AfInet6, processNames);
        var errors = new[] { ipv4.Error, ipv6.Error }.Where(error => error is not null).ToArray();
        return new(
            ipv4.Connections.Concat(ipv6.Connections).ToArray(),
            errors.Length == 0 ? null : string.Join("; ", errors));
    }

    /// <summary>Attempts to re-size and re-fetch when the table grows between the sizing call and the data call.</summary>
    private const int MaxAttempts = 3;

    private static ProcessConnectionInventoryResult ReadFamily(int addressFamily, Dictionary<int, string> processNames)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var size = 0;
            var sizingResult = GetExtendedTcpTable(nint.Zero, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
            if (sizingResult is not 0 and not ErrorInsufficientBuffer)
            {
                return new([], $"GetExtendedTcpTable failed with Windows error {sizingResult}.");
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
                if (result == ErrorInsufficientBuffer && attempt < MaxAttempts)
                {
                    // The table grew between the sizing call above and this one; ask for a fresh size.
                    continue;
                }

                if (result != 0)
                {
                    return new([], $"GetExtendedTcpTable failed with Windows error {result}.");
                }

                return new(ParseTable(buffer, addressFamily, processNames), null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new([], "GetExtendedTcpTable: the connection table kept growing faster than it could be read.");
    }

    private static ProcessConnectionInfo[] ParseTable(nint buffer, int addressFamily, Dictionary<int, string> processNames)
    {
        var count = Marshal.ReadInt32(buffer);
        var rowAddress = IntPtr.Add(buffer, sizeof(int));
        var connections = new ProcessConnectionInfo[count];

        if (addressFamily == AfInet)
        {
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(IntPtr.Add(rowAddress, index * rowSize));
                var processId = checked((int)row.OwningPid);
                connections[index] = new ProcessConnectionInfo(
                    "IPv4", FormatIpv4(row.LocalAddr), Port(row.LocalPort),
                    FormatIpv4(row.RemoteAddr), Port(row.RemotePort),
                    (TcpConnectionState)row.State, processId, ProcessName(processId, processNames));
            }
        }
        else
        {
            var rowSize = Marshal.SizeOf<Tcp6RowOwnerPid>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<Tcp6RowOwnerPid>(IntPtr.Add(rowAddress, index * rowSize));
                var processId = checked((int)row.OwningPid);
                connections[index] = new ProcessConnectionInfo(
                    "IPv6", FormatIpv6(row.LocalAddr, row.LocalScopeId), Port(row.LocalPort),
                    FormatIpv6(row.RemoteAddr, row.RemoteScopeId), Port(row.RemotePort),
                    (TcpConnectionState)row.State, processId, ProcessName(processId, processNames));
            }
        }

        return connections;
    }

    private static string ProcessName(int processId, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // The process exited between the table snapshot and this lookup.
            name = "(exited)";
        }

        cache[processId] = name;
        return name;
    }

    /// <summary>
    /// The port fields in <c>MIB_TCPROW_OWNER_PID</c>/<c>MIB_TCP6ROW_OWNER_PID</c> hold the port in
    /// network byte order inside the low 16 bits of the DWORD; this swaps those two bytes back to a
    /// host-order port number.
    /// </summary>
    internal static int Port(uint raw) => ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);

    private static string FormatIpv4(uint address) => new IPAddress(BitConverter.GetBytes(address)).ToString();

    private static string FormatIpv6(byte[] address, uint scopeId) => new IPAddress(address, scopeId).ToString();

    internal static int NativeTcpRowSize => Marshal.SizeOf<TcpRowOwnerPid>();
    internal static int NativeTcp6RowSize => Marshal.SizeOf<Tcp6RowOwnerPid>();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        nint tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }
}
