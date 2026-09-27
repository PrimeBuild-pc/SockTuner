namespace SockTuner.Models;

public enum WifiBand
{
    Unknown,
    TwoPointFourGhz,
    FiveGhz,
    SixGhz
}

public enum WifiPhyKind
{
    Unknown,
    Fhss,
    Dsss,
    Infrared,
    Ofdm,
    HrDsss,
    Erp,
    Ht,
    Vht,
    Dmg,
    He,
    Eht
}

public enum WifiInterfaceState
{
    Unknown,
    NotReady,
    Connected,
    AdHocFormed,
    Disconnecting,
    Disconnected,
    Associating,
    Discovering,
    Authenticating
}

public enum WifiInventoryAvailability
{
    Available,
    UnsupportedPlatform,
    ServiceStopped,
    LocationPermissionDenied,
    Failed
}

public sealed record WifiSecurityInfo(
    string Authentication,
    string Cipher,
    bool SecurityEnabled,
    bool OneXEnabled,
    bool? PmfCapable = null,
    bool? PmfRequired = null)
{
    public string Display => !SecurityEnabled
        ? "Open"
        : $"{Authentication} · {Cipher}"
          + (PmfRequired == true ? " · PMF required" : PmfCapable == true ? " · PMF capable" : string.Empty);
}

/// <summary>
/// One beacon as the radio heard it. The span is the frequency range the BSS actually occupies,
/// taken from its operation elements rather than assumed from the primary channel.
/// </summary>
public sealed record WifiBssInfo(
    string Bssid,
    string Ssid,
    WifiBand Band,
    int Channel,
    int ChannelWidthMhz,
    int SpanLowMhz,
    int SpanHighMhz,
    int RssiDbm,
    int? ChannelUtilizationPercent = null,
    WifiSecurityInfo? Security = null,
    bool WpsAdvertised = false,
    WifiPhyKind Phy = WifiPhyKind.Unknown,
    bool HeAdvertised = false,
    bool EhtAdvertised = false)
{
    public bool Overlaps(WifiBssInfo other) =>
        Band == other.Band && SpanLowMhz < other.SpanHighMhz && other.SpanLowMhz < SpanHighMhz;

    public bool SameChannel(WifiBssInfo other) => Band == other.Band && Channel == other.Channel;

    public string SsidDisplay => Ssid.Length == 0 ? "(hidden)" : Ssid;

    public string Summary => $"{SsidDisplay} on channel {Channel} ({BandDisplay}, {ChannelWidthMhz} MHz) at {RssiDbm} dBm";

    public string BandDisplay => Band switch
    {
        WifiBand.TwoPointFourGhz => "2.4 GHz",
        WifiBand.FiveGhz => "5 GHz",
        WifiBand.SixGhz => "6 GHz",
        _ => "unknown band"
    };

    /// <summary>Builds a BSS from the beacon's centre frequency and advertised width.</summary>
    public static WifiBssInfo FromFrequency(
        string bssid, string ssid, int frequencyKhz, int widthMhz, int? widthCentreChannel, int rssiDbm,
        int? channelUtilizationPercent = null, WifiSecurityInfo? security = null, bool wpsAdvertised = false,
        WifiPhyKind phy = WifiPhyKind.Unknown, bool heAdvertised = false, bool ehtAdvertised = false)
    {
        var megahertz = frequencyKhz / 1000;
        var band = ClassifyBand(megahertz);
        var channel = ChannelFor(band, megahertz);
        var width = widthMhz <= 0 ? 20 : widthMhz;

        // A wide channel is not centred on its primary 20 MHz channel, so the span is built around
        // the advertised centre where the beacon gives one and around the primary otherwise.
        var centre = widthCentreChannel is { } centreChannel && band != WifiBand.Unknown
            ? FrequencyFor(band, centreChannel)
            : megahertz;
        return new WifiBssInfo(
            bssid, ssid, band, channel, width, centre - (width / 2), centre + (width / 2), rssiDbm,
            channelUtilizationPercent, security, wpsAdvertised, phy, heAdvertised, ehtAdvertised);
    }

    public static WifiBand ClassifyBand(int megahertz) => megahertz switch
    {
        >= 2400 and < 2500 => WifiBand.TwoPointFourGhz,
        >= 4900 and < 5925 => WifiBand.FiveGhz,
        >= 5925 and <= 7125 => WifiBand.SixGhz,
        _ => WifiBand.Unknown
    };

    public static int ChannelFor(WifiBand band, int megahertz) => band switch
    {
        WifiBand.TwoPointFourGhz => megahertz == 2484 ? 14 : (megahertz - 2407) / 5,
        WifiBand.FiveGhz => (megahertz - 5000) / 5,
        WifiBand.SixGhz => (megahertz - 5950) / 5,
        _ => 0
    };

    public static int FrequencyFor(WifiBand band, int channel) => band switch
    {
        WifiBand.TwoPointFourGhz => channel == 14 ? 2484 : 2407 + (channel * 5),
        WifiBand.FiveGhz => 5000 + (channel * 5),
        WifiBand.SixGhz => 5950 + (channel * 5),
        _ => 0
    };
}

/// <summary>
/// One wireless interface: its current association plus the cached BSS list supplied by Windows.
/// SockTuner never triggers a scan to populate this model.
/// </summary>
public sealed record WifiRadioInfo(
    string InterfaceId,
    string Description,
    string Ssid,
    string Bssid,
    int SignalQualityPercent,
    uint TransmitRateKbps,
    uint ReceiveRateKbps,
    WifiBssInfo? ConnectedBss,
    IReadOnlyList<WifiBssInfo> Neighbours,
    string? Error = null,
    WifiInterfaceState State = WifiInterfaceState.Unknown,
    WifiPhyKind Phy = WifiPhyKind.Unknown,
    string ConnectionMode = "Unknown",
    WifiSecurityInfo? Security = null,
    IReadOnlyList<WifiPhyKind>? SupportedPhys = null,
    bool? SoftwareRadioOn = null,
    bool? HardwareRadioOn = null)
{
    public bool Connected => Bssid.Length > 0;

    public string RateDisplay => TransmitRateKbps == 0 && ReceiveRateKbps == 0
        ? "Unavailable"
        : $"TX {TransmitRateKbps / 1000d:0.#} Mbit/s · RX {ReceiveRateKbps / 1000d:0.#} Mbit/s";

    public string SignalDisplay => ConnectedBss is { } bss
        ? $"{bss.RssiDbm} dBm ({SignalQualityPercent}%)"
        : $"{SignalQualityPercent}%";

    public string PhyDisplay => Phy switch
    {
        WifiPhyKind.Ht => "Wi‑Fi 4 (802.11n)",
        WifiPhyKind.Vht => "Wi‑Fi 5 (802.11ac)",
        WifiPhyKind.He => "Wi‑Fi 6/6E (802.11ax)",
        WifiPhyKind.Eht => "Wi‑Fi 7 (802.11be)",
        WifiPhyKind.Unknown => "Not reported",
        _ => Phy.ToString()
    };

    public string RadioStateDisplay => (SoftwareRadioOn, HardwareRadioOn) switch
    {
        (false, _) => "Off in software",
        (_, false) => "Off in hardware",
        (true, true) => "On",
        _ => "Not reported"
    };

    public IReadOnlyList<WifiBssInfo> OverlappingNeighbours => ConnectedBss is not { } bss
        ? []
        : Neighbours
            .Where(neighbour => !string.Equals(neighbour.Bssid, bss.Bssid, StringComparison.OrdinalIgnoreCase))
            .Where(bss.Overlaps)
            .ToArray();
}

public sealed record WifiInventoryResult(
    IReadOnlyList<WifiRadioInfo> Radios,
    bool Supported,
    string? Error,
    WifiInventoryAvailability Availability = WifiInventoryAvailability.Available,
    DateTimeOffset? CapturedAt = null)
{
    public bool LocationPermissionRequired => Availability == WifiInventoryAvailability.LocationPermissionDenied;
}
