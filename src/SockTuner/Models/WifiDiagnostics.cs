namespace SockTuner.Models;

public enum WifiDiagnosticVerdict
{
    Healthy,
    AtRisk,
    Degraded,
    NotEnoughData
}

public sealed record WifiObservationSample(
    DateTimeOffset Timestamp,
    string InterfaceId,
    string Bssid,
    int? RssiDbm,
    uint TransmitRateKbps,
    uint ReceiveRateKbps,
    string? Error = null)
{
    public string SignalDisplay => RssiDbm is { } rssi ? $"{rssi} dBm" : "Not reported";
    public string RateDisplay => TransmitRateKbps == 0 && ReceiveRateKbps == 0
        ? "Not reported"
        : $"TX {TransmitRateKbps / 1000d:0.#} · RX {ReceiveRateKbps / 1000d:0.#} Mbit/s";
}

public sealed record WifiDiagnosticEvidence(string Name, string Value, string Source);

public sealed record WifiChannelPressure(
    WifiBand Band,
    int Channel,
    int BssCount,
    int StrongestRssiDbm,
    int OverlappingBssCount,
    int? AverageUtilizationPercent)
{
    public string BandDisplay => Band switch
    {
        WifiBand.TwoPointFourGhz => "2.4 GHz",
        WifiBand.FiveGhz => "5 GHz",
        WifiBand.SixGhz => "6 GHz",
        _ => "Unknown"
    };
    public string UtilizationDisplay => AverageUtilizationPercent is { } value ? $"{value}%" : "Not reported";
}

public sealed record WifiDiagnosticReport(
    DateTimeOffset CapturedAt,
    WifiDiagnosticVerdict Verdict,
    string Summary,
    WifiRadioInfo? Radio,
    IReadOnlyList<WifiDiagnosticEvidence> Evidence,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<WifiChannelPressure> Channels,
    IReadOnlyList<WifiObservationSample> Samples,
    IReadOnlyList<string> Unknowns)
{
    public string VerdictDisplay => Verdict switch
    {
        WifiDiagnosticVerdict.Healthy => "+ Healthy",
        WifiDiagnosticVerdict.AtRisk => "~ At risk",
        WifiDiagnosticVerdict.Degraded => "! Degraded",
        _ => "- Not enough data"
    };
}
