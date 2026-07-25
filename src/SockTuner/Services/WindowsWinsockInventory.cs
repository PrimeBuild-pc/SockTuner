using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsWinsockInventory
{
    private const int SocketError = -1;
    private const int WsaNoBuffers = 10055;

    internal static WinsockInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var wsaData = Marshal.AllocHGlobal(512);
        try
        {
            var startup = WSAStartup(0x0202, wsaData);
            if (startup != 0)
            {
                return new([], $"WSAStartup failed with Windows error {startup}.");
            }

            try
            {
                var size = 0;
                var count = WSAEnumProtocolsW(nint.Zero, nint.Zero, ref size);
                if (count == SocketError)
                {
                    var error = WSAGetLastError();
                    if (error != WsaNoBuffers)
                    {
                        return new([], $"WSAEnumProtocolsW failed with Windows error {error}.");
                    }
                }

                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    count = WSAEnumProtocolsW(nint.Zero, buffer, ref size);
                    if (count == SocketError)
                    {
                        return new([], $"WSAEnumProtocolsW failed with Windows error {WSAGetLastError()}.");
                    }

                    var rowSize = Marshal.SizeOf<WsaProtocolInfo>();
                    var providers = new WinsockProviderInfo[count];
                    for (var index = 0; index < count; index++)
                    {
                        var row = Marshal.PtrToStructure<WsaProtocolInfo>(IntPtr.Add(buffer, index * rowSize));
                        providers[index] = new WinsockProviderInfo(
                            row.CatalogEntryId,
                            row.ProviderId,
                            row.ProtocolName,
                            row.AddressFamily,
                            row.SocketType,
                            row.Protocol,
                            row.ProtocolChain.ChainLength,
                            row.ProtocolChain.ChainEntries?
                                .Take(Math.Clamp(row.ProtocolChain.ChainLength, 0, 7))
                                .ToArray() ?? [],
                            row.ProviderFlags,
                            row.ServiceFlags1,
                            row.ServiceFlags2,
                            row.ServiceFlags3,
                            row.ServiceFlags4);
                    }

                    return new(providers
                        .OrderBy(provider => provider.CatalogEntryId)
                        .ToArray(), null);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                WSACleanup();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(wsaData);
        }
    }

    internal static int NativeRowSize => Marshal.SizeOf<WsaProtocolInfo>();

    [DllImport("ws2_32.dll")]
    private static extern int WSAStartup(ushort versionRequested, nint wsaData);

    [DllImport("ws2_32.dll")]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode)]
    private static extern int WSAEnumProtocolsW(nint protocols, nint protocolBuffer, ref int bufferLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct WsaProtocolChain
    {
        public int ChainLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public uint[] ChainEntries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WsaProtocolInfo
    {
        public uint ServiceFlags1;
        public uint ServiceFlags2;
        public uint ServiceFlags3;
        public uint ServiceFlags4;
        public uint ProviderFlags;
        public Guid ProviderId;
        public uint CatalogEntryId;
        public WsaProtocolChain ProtocolChain;
        public int Version;
        public int AddressFamily;
        public int MaxSockAddr;
        public int MinSockAddr;
        public int SocketType;
        public int Protocol;
        public int ProtocolMaxOffset;
        public int NetworkByteOrder;
        public int SecurityScheme;
        public uint MessageSize;
        public uint ProviderReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProtocolName;
    }
}

internal sealed record WinsockInventoryResult(IReadOnlyList<WinsockProviderInfo> Providers, string? Error);
