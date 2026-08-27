namespace SockTuner.Models;

public enum WifiBand
{
    Unknown,
    TwoPointFourGhz,
    FiveGhz,
    SixGhz
}

/// <summary>
/// One beacon as the radio heard it. The span is the frequency range the BSS actually occupies,
/// taken from its HT/VHT operation elements rather than assumed from the primary channel — a
/// neighbour on a 40 or 80 MHz channel interferes far beyond the channel number it advertises.
/// </summary>
public sealed record WifiBssInfo(
    string Bssid,
    string Ssid,
    WifiBand Band,
    int Channel,
    int ChannelWidthMhz,
    int SpanLowMhz,
    int SpanHighMhz,
    int RssiDbm)
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

    /// <summary>
    /// Builds a BSS from the beacon's centre frequency and its advertised width. Frequencies come
    /// from Windows in kHz.
    /// </summary>
    public static WifiBssInfo FromFrequency(
        string bssid, string ssid, int frequencyKhz, int widthMhz, int? widthCentreChannel, int rssiDbm)
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
        return new WifiBssInfo(bssid, ssid, band, channel, width, centre - (width / 2), centre + (width / 2), rssiDbm);
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
/// One wireless interface: what it is associated with, and every other BSS its radio can currently
/// hear. The neighbour list is read from the cached scan results — SockTuner never triggers a scan,
/// which would interrupt the connection.
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
    string? Error = null)
{
    public bool Connected => Bssid.Length > 0;

    public string RateDisplay => TransmitRateKbps == 0 && ReceiveRateKbps == 0
        ? "Unavailable"
        : $"TX {TransmitRateKbps / 1000d:0.#} Mbit/s · RX {ReceiveRateKbps / 1000d:0.#} Mbit/s";

    public string SignalDisplay => ConnectedBss is { } bss
        ? $"{bss.RssiDbm} dBm ({SignalQualityPercent}%)"
        : $"{SignalQualityPercent}%";

    /// <summary>Other BSSs whose spectrum overlaps the one this radio is using.</summary>
    public IReadOnlyList<WifiBssInfo> OverlappingNeighbours => ConnectedBss is not { } bss
        ? []
        : Neighbours
            .Where(neighbour => !string.Equals(neighbour.Bssid, bss.Bssid, StringComparison.OrdinalIgnoreCase))
            .Where(bss.Overlaps)
            .ToArray();
}

public sealed record WifiInventoryResult(IReadOnlyList<WifiRadioInfo> Radios, bool Supported, string? Error);
