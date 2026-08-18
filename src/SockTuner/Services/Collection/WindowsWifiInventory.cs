using System.Runtime.InteropServices;
using System.Text;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Collection layer: reads the wireless radio state through the native WLAN API. Read-only — it
/// takes the cached scan results and never asks the radio to scan, which would interrupt the
/// connection it is measuring.
///
/// A warm read costs tens of milliseconds, but the first one can block while the WLAN AutoConfig
/// service starts, so callers must not run it on the UI thread.
/// </summary>
internal static class WindowsWifiInventory
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ClientVersionVistaOrLater = 2;
    private const uint CurrentConnectionOpcode = 7;
    private const uint BssTypeAny = 3;

    internal static WifiInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], false, null);
        }

        var opened = WlanOpenHandle(ClientVersionVistaOrLater, nint.Zero, out _, out var handle);
        if (opened == ErrorServiceNotActive)
        {
            return new([], false, "The WLAN AutoConfig service is not running, so wireless state cannot be read.");
        }

        if (opened != ErrorSuccess)
        {
            return new([], false, $"WlanOpenHandle failed with Windows error {opened}.");
        }

        try
        {
            var enumerated = WlanEnumInterfaces(handle, nint.Zero, out var listPointer);
            if (enumerated != ErrorSuccess)
            {
                return new([], false, $"WlanEnumInterfaces failed with Windows error {enumerated}.");
            }

            try
            {
                var count = Marshal.ReadInt32(listPointer);
                var radios = new List<WifiRadioInfo>(count);
                for (var index = 0; index < count; index++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(listPointer + 8 + (index * InterfaceInfoSize));
                    radios.Add(ReadRadio(handle, info));
                }

                return new WifiInventoryResult(radios, true, null);
            }
            finally
            {
                WlanFreeMemory(listPointer);
            }
        }
        finally
        {
            WlanCloseHandle(handle, nint.Zero);
        }
    }

    private static WifiRadioInfo ReadRadio(nint handle, WlanInterfaceInfo info)
    {
        var id = info.InterfaceGuid.ToString();
        var (ssid, bssid, quality, transmit, receive, connectionError) = ReadConnection(handle, info.InterfaceGuid);
        var (neighbours, scanError) = ReadBssList(handle, info.InterfaceGuid);
        var connected = neighbours.FirstOrDefault(entry =>
            string.Equals(entry.Bssid, bssid, StringComparison.OrdinalIgnoreCase));

        return new WifiRadioInfo(
            id, info.Description, ssid, bssid, quality, transmit, receive, connected, neighbours,
            string.Join(" ", new[] { connectionError, scanError }.Where(item => item is not null)) is { Length: > 0 } error
                ? error
                : null);
    }

    private static (string Ssid, string Bssid, int Quality, uint Transmit, uint Receive, string? Error) ReadConnection(
        nint handle, Guid interfaceGuid)
    {
        var queried = WlanQueryInterface(
            handle, ref interfaceGuid, CurrentConnectionOpcode, nint.Zero, out _, out var data, nint.Zero);
        if (queried != ErrorSuccess)
        {
            // The documented result for an interface that is simply not associated.
            return ("", "", 0, 0, 0, null);
        }

        try
        {
            var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            var association = attributes.Association;
            return (
                Encoding.UTF8.GetString(association.Ssid.Value, 0, (int)Math.Min(association.Ssid.Length, 32u)),
                FormatMac(association.Bssid),
                (int)association.SignalQuality,
                association.TransmitRateKbps,
                association.ReceiveRateKbps,
                null);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static (IReadOnlyList<WifiBssInfo> Neighbours, string? Error) ReadBssList(nint handle, Guid interfaceGuid)
    {
        var queried = WlanGetNetworkBssList(
            handle, ref interfaceGuid, nint.Zero, BssTypeAny, false, nint.Zero, out var listPointer);
        if (queried != ErrorSuccess)
        {
            return ([], $"WlanGetNetworkBssList failed with Windows error {queried}.");
        }

        try
        {
            var count = Marshal.ReadInt32(listPointer + 4);
            var entries = new List<WifiBssInfo>(Math.Max(count, 0));
            for (var index = 0; index < count; index++)
            {
                var entryPointer = listPointer + 8 + (index * BssEntrySize);
                var entry = Marshal.PtrToStructure<WlanBssEntry>(entryPointer);
                var (width, centreChannel) = ReadWidth(entryPointer, entry);
                entries.Add(WifiBssInfo.FromFrequency(
                    FormatMac(entry.Bssid),
                    Encoding.UTF8.GetString(entry.Ssid.Value, 0, (int)Math.Min(entry.Ssid.Length, 32u)),
                    (int)entry.ChannelCentreFrequencyKhz,
                    width,
                    centreChannel,
                    entry.Rssi));
            }

            return (entries, null);
        }
        finally
        {
            WlanFreeMemory(listPointer);
        }
    }

    private const byte HtOperationElement = 61;
    private const byte VhtOperationElement = 192;

    /// <summary>
    /// Reads the occupied width out of the beacon's information elements. HT gives 20 or 40 MHz and
    /// the side the second half sits on; VHT gives 80 or 160 MHz and the exact centre channel.
    /// A 6 GHz radio advertising only HE reads as 20 MHz — the HE operation element is not parsed.
    /// </summary>
    private static (int WidthMhz, int? CentreChannel) ReadWidth(nint entryPointer, WlanBssEntry entry)
    {
        if (entry.InformationElementSize == 0 || entry.InformationElementOffset == 0)
        {
            return (20, null);
        }

        var elements = new byte[entry.InformationElementSize];
        Marshal.Copy(entryPointer + (int)entry.InformationElementOffset, elements, 0, elements.Length);

        var width = 20;
        int? centre = null;
        var position = 0;
        while (position + 2 <= elements.Length)
        {
            var id = elements[position];
            var length = elements[position + 1];
            var payload = position + 2;
            if (payload + length > elements.Length)
            {
                break;
            }

            if (id == HtOperationElement && length >= 2)
            {
                // Bits 0-1 of the second byte carry the secondary channel offset: 1 above, 3 below.
                var offset = elements[payload + 1] & 0x03;
                if (offset is 1 or 3)
                {
                    width = 40;
                    centre = elements[payload] + (offset == 1 ? 2 : -2);
                }
            }
            else if (id == VhtOperationElement && length >= 3)
            {
                var vhtWidth = elements[payload] switch { 1 => 80, 2 => 160, 3 => 160, _ => 0 };
                if (vhtWidth > 0 && elements[payload + 1] > 0)
                {
                    width = vhtWidth;
                    centre = elements[payload + 1];
                }
            }

            position = payload + length;
        }

        return (width, centre);
    }

    private static string FormatMac(byte[] address) => string.Join(":", address.Select(item => item.ToString("x2")));

    internal static readonly int InterfaceInfoSize = Marshal.SizeOf<WlanInterfaceInfo>();
    internal static readonly int BssEntrySize = Marshal.SizeOf<WlanBssEntry>();

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint handle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        nint handle, ref Guid interfaceGuid, uint opCode, nint reserved,
        out uint dataSize, out nint data, nint valueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(
        nint handle, ref Guid interfaceGuid, nint ssid, uint bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, nint reserved, out nint bssList);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public uint BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public uint PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint ReceiveRateKbps;
        public uint TransmitRateKbps;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        public uint State;
        public uint ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public WlanAssociationAttributes Association;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanBssEntry
    {
        public Dot11Ssid Ssid;
        public uint PhyId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public uint BssType;
        public uint PhyType;
        public int Rssi;
        public uint LinkQuality;
        [MarshalAs(UnmanagedType.U1)] public bool InRegulatoryDomain;
        public ushort BeaconPeriod;
        public ulong Timestamp;
        public ulong HostTimestamp;
        public ushort CapabilityInformation;
        public uint ChannelCentreFrequencyKhz;
        public uint RateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] RateSet;
        public uint InformationElementOffset;
        public uint InformationElementSize;
    }
}
