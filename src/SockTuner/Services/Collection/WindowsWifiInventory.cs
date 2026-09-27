using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Reads passive wireless state through the native WLAN API. BSS data comes only from the cache
/// Windows already owns: this type deliberately does not import or call WlanScan.
/// </summary>
internal static class WindowsWifiInventory
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ErrorInvalidState = 5023;
    private const uint ClientVersionVistaOrLater = 2;
    private const uint RadioStateOpcode = 4;
    private const uint CurrentConnectionOpcode = 7;
    private const uint BssTypeAny = 3;

    internal static WifiInventoryResult Read()
    {
        var capturedAt = DateTimeOffset.Now;
        if (!OperatingSystem.IsWindows())
        {
            return new([], false, null, WifiInventoryAvailability.UnsupportedPlatform, capturedAt);
        }

        var opened = WlanOpenHandle(ClientVersionVistaOrLater, nint.Zero, out _, out var handle);
        if (opened == ErrorServiceNotActive)
        {
            return new([], false,
                "The WLAN AutoConfig service is not running, so wireless state cannot be read.",
                WifiInventoryAvailability.ServiceStopped, capturedAt);
        }

        if (opened != ErrorSuccess)
        {
            return new([], false, DescribeError("WlanOpenHandle", opened), Availability(opened), capturedAt);
        }

        try
        {
            var enumerated = WlanEnumInterfaces(handle, nint.Zero, out var listPointer);
            if (enumerated != ErrorSuccess)
            {
                return new([], false, DescribeError("WlanEnumInterfaces", enumerated), Availability(enumerated), capturedAt);
            }

            try
            {
                var count = Marshal.ReadInt32(listPointer);
                var radios = new List<WifiRadioInfo>(count);
                var accessDenied = false;
                for (var index = 0; index < count; index++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(listPointer + 8 + (index * InterfaceInfoSize));
                    var read = ReadRadio(handle, info);
                    radios.Add(read.Radio);
                    accessDenied |= read.AccessDenied;
                }

                return new WifiInventoryResult(
                    radios,
                    true,
                    accessDenied ? DescribeError("WLAN API", ErrorAccessDenied) : null,
                    accessDenied ? WifiInventoryAvailability.LocationPermissionDenied : WifiInventoryAvailability.Available,
                    capturedAt);
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

    internal static WifiObservationSample ReadConnectionSample(string interfaceId)
    {
        var timestamp = DateTimeOffset.Now;
        if (!Guid.TryParse(interfaceId, out var interfaceGuid))
            return new(timestamp, interfaceId, "", null, 0, 0, "The Wi‑Fi interface ID is invalid.");
        if (!OperatingSystem.IsWindows())
            return new(timestamp, interfaceId, "", null, 0, 0, "Windows WLAN API is unavailable.");

        var opened = WlanOpenHandle(ClientVersionVistaOrLater, nint.Zero, out _, out var handle);
        if (opened != ErrorSuccess)
            return new(timestamp, interfaceId, "", null, 0, 0, DescribeError("WlanOpenHandle", opened));

        try
        {
            var connection = ReadConnection(handle, interfaceGuid);
            if (connection.Bssid.Length == 0)
                return new(timestamp, interfaceId, "", null, 0, 0, connection.Error ?? "The radio is not associated.");
            var bsses = ReadBssList(handle, interfaceGuid);
            var connected = bsses.Bsses.FirstOrDefault(item =>
                string.Equals(item.Bssid, connection.Bssid, StringComparison.OrdinalIgnoreCase));
            return new(timestamp, interfaceId, connection.Bssid, connected?.RssiDbm,
                connection.Transmit, connection.Receive, connection.Error ?? bsses.Error);
        }
        finally
        {
            WlanCloseHandle(handle, nint.Zero);
        }
    }

    private static RadioRead ReadRadio(nint handle, WlanInterfaceInfo info)
    {
        var connection = ReadConnection(handle, info.InterfaceGuid);
        var bssRead = ReadBssList(handle, info.InterfaceGuid);
        var capability = ReadCapability(handle, info.InterfaceGuid);
        var radioState = ReadRadioState(handle, info.InterfaceGuid, connection.PhyIndex);
        var connected = bssRead.Bsses.FirstOrDefault(entry =>
            string.Equals(entry.Bssid, connection.Bssid, StringComparison.OrdinalIgnoreCase));
        var security = MergeSecurity(connection.Security, connected?.Security);
        var errors = new[] { connection.Error, bssRead.Error, capability.Error, radioState.Error }
            .Where(item => item is not null);

        return new RadioRead(
            new WifiRadioInfo(
                info.InterfaceGuid.ToString(),
                info.Description,
                connection.Ssid,
                connection.Bssid,
                connection.Quality,
                connection.Transmit,
                connection.Receive,
                connected,
                bssRead.Bsses,
                string.Join(" ", errors) is { Length: > 0 } error ? error : null,
                MapInterfaceState(info.State),
                connection.Phy,
                connection.Mode,
                security,
                capability.Phys,
                radioState.SoftwareOn,
                radioState.HardwareOn),
            connection.AccessDenied || bssRead.AccessDenied || capability.AccessDenied || radioState.AccessDenied);
    }

    private static ConnectionRead ReadConnection(nint handle, Guid interfaceGuid)
    {
        var queried = WlanQueryInterface(
            handle, ref interfaceGuid, CurrentConnectionOpcode, nint.Zero, out _, out var data, nint.Zero);
        if (queried == ErrorInvalidState)
        {
            return ConnectionRead.Empty;
        }
        if (queried != ErrorSuccess)
        {
            return ConnectionRead.Empty with
            {
                Error = DescribeError("WlanQueryInterface(current connection)", queried),
                AccessDenied = queried == ErrorAccessDenied
            };
        }

        try
        {
            var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            var association = attributes.Association;
            var security = attributes.Security;
            return new ConnectionRead(
                DecodeSsid(association.Ssid),
                FormatMac(association.Bssid),
                (int)association.SignalQuality,
                association.TransmitRateKbps,
                association.ReceiveRateKbps,
                MapPhy(association.PhyType),
                association.PhyIndex,
                MapConnectionMode(attributes.ConnectionMode),
                new WifiSecurityInfo(
                    MapAuthentication(security.Authentication),
                    MapCipher(security.Cipher),
                    security.SecurityEnabled,
                    security.OneXEnabled),
                null,
                false);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static BssRead ReadBssList(nint handle, Guid interfaceGuid)
    {
        var queried = WlanGetNetworkBssList(
            handle, ref interfaceGuid, nint.Zero, BssTypeAny, false, nint.Zero, out var listPointer);
        if (queried != ErrorSuccess)
        {
            return new([], DescribeError("WlanGetNetworkBssList", queried), queried == ErrorAccessDenied);
        }

        try
        {
            var count = Marshal.ReadInt32(listPointer + 4);
            var entries = new List<WifiBssInfo>(Math.Max(count, 0));
            for (var index = 0; index < count; index++)
            {
                var entryPointer = listPointer + 8 + (index * BssEntrySize);
                var entry = Marshal.PtrToStructure<WlanBssEntry>(entryPointer);
                var band = WifiBssInfo.ClassifyBand((int)entry.ChannelCentreFrequencyKhz / 1000);
                var elements = ReadInformationElementBytes(entryPointer, entry);
                var parsed = ParseInformationElements(elements, band);
                entries.Add(WifiBssInfo.FromFrequency(
                    FormatMac(entry.Bssid),
                    DecodeSsid(entry.Ssid),
                    (int)entry.ChannelCentreFrequencyKhz,
                    parsed.WidthMhz,
                    parsed.CentreChannel,
                    entry.Rssi,
                    parsed.ChannelUtilizationPercent,
                    parsed.Security,
                    parsed.WpsAdvertised,
                    MapPhy(entry.PhyType),
                    parsed.HeAdvertised,
                    parsed.EhtAdvertised));
            }

            return new(entries, null, false);
        }
        finally
        {
            WlanFreeMemory(listPointer);
        }
    }

    private static CapabilityRead ReadCapability(nint handle, Guid interfaceGuid)
    {
        var queried = WlanGetInterfaceCapability(handle, ref interfaceGuid, nint.Zero, out var pointer);
        if (queried != ErrorSuccess)
        {
            return new([], DescribeError("WlanGetInterfaceCapability", queried), queried == ErrorAccessDenied);
        }

        try
        {
            var capability = Marshal.PtrToStructure<WlanInterfaceCapability>(pointer);
            var count = Math.Min((int)capability.SupportedPhyCount, capability.SupportedPhys?.Length ?? 0);
            var phys = (capability.SupportedPhys ?? [])
                .Take(count)
                .Select(MapPhy)
                .Where(phy => phy != WifiPhyKind.Unknown)
                .Distinct()
                .ToArray();
            return new(phys, null, false);
        }
        finally
        {
            WlanFreeMemory(pointer);
        }
    }

    private static RadioStateRead ReadRadioState(nint handle, Guid interfaceGuid, uint? selectedPhy)
    {
        var queried = WlanQueryInterface(
            handle, ref interfaceGuid, RadioStateOpcode, nint.Zero, out _, out var pointer, nint.Zero);
        if (queried != ErrorSuccess)
        {
            return new(null, null, DescribeError("WlanQueryInterface(radio state)", queried), queried == ErrorAccessDenied);
        }

        try
        {
            var state = Marshal.PtrToStructure<WlanRadioState>(pointer);
            var count = Math.Min((int)state.NumberOfPhys, state.PhyStates?.Length ?? 0);
            var candidates = (state.PhyStates ?? []).Take(count).ToArray();
            var selected = selectedPhy is { } phy
                ? candidates.Where(item => item.PhyIndex == phy).ToArray()
                : candidates;
            if (selected.Length == 0) selected = candidates;
            return new(
                selected.Length == 0 ? null : selected.All(item => item.SoftwareRadioState == 1),
                selected.Length == 0 ? null : selected.All(item => item.HardwareRadioState == 1),
                null,
                false);
        }
        finally
        {
            WlanFreeMemory(pointer);
        }
    }

    internal static WifiSecurityInfo? MergeSecurity(WifiSecurityInfo? connection, WifiSecurityInfo? beacon) =>
        connection is null
            ? beacon
            : connection with
            {
                PmfCapable = beacon?.PmfCapable,
                PmfRequired = beacon?.PmfRequired
            };

    private const byte BssLoadElement = 11;
    private const byte RsnElement = 48;
    private const byte HtOperationElement = 61;
    private const byte VhtOperationElement = 192;
    private const byte VendorElement = 221;
    private const byte ExtensionElement = 255;
    private const byte HeOperationExtension = 36;
    private const byte EhtOperationExtension = 106;

    /// <summary>Bounds-checked parser over untrusted beacon information elements.</summary>
    internal static WifiInformationElements ParseInformationElements(ReadOnlySpan<byte> elements, WifiBand band)
    {
        var width = 20;
        int? centre = null;
        int? utilization = null;
        WifiSecurityInfo? security = null;
        var wps = false;
        var he = false;
        var eht = false;
        var position = 0;

        while (position + 2 <= elements.Length)
        {
            var id = elements[position];
            var length = elements[position + 1];
            var payloadStart = position + 2;
            if (payloadStart + length > elements.Length) break;
            var payload = elements.Slice(payloadStart, length);

            if (id == HtOperationElement && payload.Length >= 2)
            {
                var offset = payload[1] & 0x03;
                if (offset is 1 or 3)
                {
                    width = Math.Max(width, 40);
                    centre = payload[0] + (offset == 1 ? 2 : -2);
                }
            }
            else if (id == VhtOperationElement && payload.Length >= 3)
            {
                var vhtWidth = payload[0] switch { 1 => 80, 2 or 3 => 160, _ => 0 };
                if (vhtWidth > 0 && payload[1] > 0)
                {
                    width = vhtWidth;
                    centre = payload[1];
                }
            }
            else if (id == BssLoadElement && payload.Length >= 3)
            {
                utilization = (int)Math.Round(payload[2] * 100d / byte.MaxValue);
            }
            else if (id == RsnElement)
            {
                security = ParseRsn(payload);
            }
            else if (id == VendorElement && payload.StartsWith(new byte[] { 0x00, 0x50, 0xF2, 0x04 }))
            {
                wps = true;
            }
            else if (id == ExtensionElement && payload.Length > 0)
            {
                if (payload[0] == HeOperationExtension)
                {
                    he = true;
                    if (band == WifiBand.SixGhz && ParseHeSixGhzOperation(payload) is { } operation)
                    {
                        width = operation.WidthMhz;
                        centre = operation.CentreChannel;
                    }
                }
                else if (payload[0] == EhtOperationExtension)
                {
                    // Presence is useful capability evidence. Width remains HE-derived until the
                    // EHT operation layout is available from a driver-independent Windows contract.
                    eht = true;
                }
            }

            position = payloadStart + length;
        }

        return new(width, centre, utilization, security, wps, he, eht);
    }

    private static (int WidthMhz, int CentreChannel)? ParseHeSixGhzOperation(ReadOnlySpan<byte> payload)
    {
        // Extension ID + 3-byte HE parameters + BSS colour + basic MCS/NSS.
        if (payload.Length < 7) return null;
        var parameters = payload[1] | (payload[2] << 8) | (payload[3] << 16);
        if ((parameters & (1 << 17)) == 0) return null;

        var offset = 7;
        if ((parameters & (1 << 14)) != 0) offset += 3; // VHT operation info
        if ((parameters & (1 << 15)) != 0) offset += 1; // max co-hosted BSSID
        if (payload.Length < offset + 5) return null;

        var width = (payload[offset + 1] & 0x03) switch { 0 => 20, 1 => 40, 2 => 80, _ => 160 };
        var centre = payload[offset + 2];
        return centre == 0 ? null : (width, centre);
    }

    private static WifiSecurityInfo? ParseRsn(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 1) return null;
        var position = 2;
        var groupCipher = SuiteName(payload.Slice(position, 4), cipher: true);
        position += 4;
        if (position + 2 > payload.Length) return null;
        var pairwiseCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(position, 2));
        position += 2;
        if (pairwiseCount > 64 || position + (pairwiseCount * 4) > payload.Length) return null;
        var pairwise = pairwiseCount > 0 ? SuiteName(payload.Slice(position, 4), cipher: true) : groupCipher;
        position += pairwiseCount * 4;
        if (position + 2 > payload.Length) return null;
        var akmCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(position, 2));
        position += 2;
        if (akmCount > 64 || position + (akmCount * 4) > payload.Length) return null;
        var authentication = akmCount > 0 ? SuiteName(payload.Slice(position, 4), cipher: false) : "RSN";
        position += akmCount * 4;
        bool? capable = null;
        bool? required = null;
        if (position + 2 <= payload.Length)
        {
            var capabilities = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(position, 2));
            capable = (capabilities & (1 << 7)) != 0;
            required = (capabilities & (1 << 6)) != 0;
        }

        return new WifiSecurityInfo(authentication, pairwise, true, false, capable, required);
    }

    private static string SuiteName(ReadOnlySpan<byte> suite, bool cipher)
    {
        if (suite.Length < 4 || suite[0] != 0x00 || suite[1] != 0x0F || suite[2] != 0xAC)
            return cipher ? "Vendor cipher" : "Vendor authentication";

        return cipher
            ? suite[3] switch
            {
                0 => "Group cipher",
                1 => "WEP-40",
                2 => "TKIP",
                4 => "CCMP-128",
                5 => "WEP-104",
                6 => "BIP-CMAC-128",
                8 => "GCMP-128",
                9 => "GCMP-256",
                10 => "CCMP-256",
                11 => "BIP-GMAC-128",
                12 => "BIP-GMAC-256",
                13 => "BIP-CMAC-256",
                _ => "RSN cipher"
            }
            : suite[3] switch
            {
                1 => "WPA2-Enterprise",
                2 => "WPA2-Personal",
                5 => "WPA2-Enterprise SHA-256",
                6 => "WPA2-Personal SHA-256",
                8 => "WPA3-SAE",
                11 or 12 => "WPA3-Enterprise",
                18 => "OWE",
                _ => "RSN"
            };
    }

    private static byte[] ReadInformationElementBytes(nint entryPointer, WlanBssEntry entry)
    {
        if (entry.InformationElementSize == 0 || entry.InformationElementOffset == 0) return [];
        var elements = new byte[entry.InformationElementSize];
        Marshal.Copy(entryPointer + (int)entry.InformationElementOffset, elements, 0, elements.Length);
        return elements;
    }

    private static string DecodeSsid(Dot11Ssid ssid) =>
        Encoding.UTF8.GetString(ssid.Value, 0, (int)Math.Min(ssid.Length, 32u));

    private static string FormatMac(byte[] address) => string.Join(":", address.Select(item => item.ToString("x2")));

    internal static WifiInventoryAvailability Availability(uint error) => error switch
    {
        ErrorAccessDenied => WifiInventoryAvailability.LocationPermissionDenied,
        ErrorServiceNotActive => WifiInventoryAvailability.ServiceStopped,
        _ => WifiInventoryAvailability.Failed
    };

    internal static string DescribeError(string operation, uint error) => error == ErrorAccessDenied
        ? $"{operation} was denied by Windows. Allow desktop apps to use Location in Windows Settings, then refresh."
        : $"{operation} failed with Windows error {error}.";

    private static WifiInterfaceState MapInterfaceState(uint state) => state switch
    {
        0 => WifiInterfaceState.NotReady,
        1 => WifiInterfaceState.Connected,
        2 => WifiInterfaceState.AdHocFormed,
        3 => WifiInterfaceState.Disconnecting,
        4 => WifiInterfaceState.Disconnected,
        5 => WifiInterfaceState.Associating,
        6 => WifiInterfaceState.Discovering,
        7 => WifiInterfaceState.Authenticating,
        _ => WifiInterfaceState.Unknown
    };

    private static WifiPhyKind MapPhy(uint phy) => phy switch
    {
        1 => WifiPhyKind.Fhss,
        2 => WifiPhyKind.Dsss,
        3 => WifiPhyKind.Infrared,
        4 => WifiPhyKind.Ofdm,
        5 => WifiPhyKind.HrDsss,
        6 => WifiPhyKind.Erp,
        7 => WifiPhyKind.Ht,
        8 => WifiPhyKind.Vht,
        9 => WifiPhyKind.Dmg,
        10 => WifiPhyKind.He,
        11 => WifiPhyKind.Eht,
        _ => WifiPhyKind.Unknown
    };

    private static string MapConnectionMode(uint mode) => mode switch
    {
        0 => "Profile",
        1 => "Temporary profile",
        2 => "Secure discovery",
        3 => "Unsecure discovery",
        4 => "Automatic",
        5 => "Invalid",
        _ => "Unknown"
    };

    private static string MapAuthentication(uint authentication) => authentication switch
    {
        1 => "Open",
        2 => "Shared key",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        5 => "WPA-None",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3",
        9 => "WPA3-SAE",
        10 => "OWE",
        11 => "WPA3-Enterprise 192-bit",
        _ => $"Authentication {authentication}"
    };

    private static string MapCipher(uint cipher) => cipher switch
    {
        0 => "None",
        1 => "WEP-40",
        2 => "TKIP",
        4 => "CCMP-128",
        5 => "WEP-104",
        6 => "BIP",
        8 => "GCMP-128",
        9 => "GCMP-256",
        10 => "CCMP-256",
        0x100 => "Use group cipher",
        0x101 => "WEP",
        _ => $"Cipher {cipher}"
    };

    internal static readonly int InterfaceInfoSize = Marshal.SizeOf<WlanInterfaceInfo>();
    internal static readonly int BssEntrySize = Marshal.SizeOf<WlanBssEntry>();

    private sealed record RadioRead(WifiRadioInfo Radio, bool AccessDenied);
    private sealed record ConnectionRead(
        string Ssid, string Bssid, int Quality, uint Transmit, uint Receive, WifiPhyKind Phy,
        uint? PhyIndex, string Mode, WifiSecurityInfo? Security, string? Error, bool AccessDenied)
    {
        public static readonly ConnectionRead Empty = new("", "", 0, 0, 0, WifiPhyKind.Unknown, null, "Unknown", null, null, false);
    }
    private sealed record BssRead(IReadOnlyList<WifiBssInfo> Bsses, string? Error, bool AccessDenied);
    private sealed record CapabilityRead(IReadOnlyList<WifiPhyKind> Phys, string? Error, bool AccessDenied);
    private sealed record RadioStateRead(bool? SoftwareOn, bool? HardwareOn, string? Error, bool AccessDenied);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint handle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetInterfaceCapability(nint handle, ref Guid interfaceGuid, nint reserved, out nint capability);

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool OneXEnabled;
        public uint Authentication;
        public uint Cipher;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        public uint State;
        public uint ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceCapability
    {
        public uint InterfaceType;
        [MarshalAs(UnmanagedType.Bool)] public bool Dot11dSupported;
        public uint MaximumDesiredSsidListSize;
        public uint MaximumDesiredBssidListSize;
        public uint SupportedPhyCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public uint[] SupportedPhys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanPhyRadioState
    {
        public uint PhyIndex;
        public uint SoftwareRadioState;
        public uint HardwareRadioState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanRadioState
    {
        public uint NumberOfPhys;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public WlanPhyRadioState[] PhyStates;
    }
}

internal sealed record WifiInformationElements(
    int WidthMhz,
    int? CentreChannel,
    int? ChannelUtilizationPercent,
    WifiSecurityInfo? Security,
    bool WpsAdvertised,
    bool HeAdvertised,
    bool EhtAdvertised);
