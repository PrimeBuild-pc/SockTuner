using SockTuner.Models;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Radio diagnosis over a fixed scan. No radio is touched and no scan is triggered; the analyzer is
/// a pure function over what the collection layer already read.
/// </summary>
public sealed class WifiRadioAnalyzerTests
{
    [Fact]
    public void NativeStructSizes_MatchTheWlanApiAbi()
    {
        Assert.Equal(532, WindowsWifiInventory.InterfaceInfoSize);
        Assert.Equal(360, WindowsWifiInventory.BssEntrySize);
    }

    [Fact]
    public void ChannelsAndBandsAreDerivedFromTheBeaconFrequency()
    {
        Assert.Equal((WifiBand.TwoPointFourGhz, 6), Channel(2437000));
        Assert.Equal((WifiBand.TwoPointFourGhz, 14), Channel(2484000));
        Assert.Equal((WifiBand.FiveGhz, 36), Channel(5180000));
        Assert.Equal((WifiBand.SixGhz, 37), Channel(6135000));
    }

    [Fact]
    public void WideChannelsOccupyTheSpectrumTheyAdvertise()
    {
        // An 80 MHz VHT BSS centred on channel 42 spans 5170–5250 MHz whatever its primary channel is.
        var wide = WifiBssInfo.FromFrequency("aa", "Wide", 5180000, 80, 42, -55);

        Assert.Equal((5170, 5250), (wide.SpanLowMhz, wide.SpanHighMhz));
        Assert.True(wide.Overlaps(WifiBssInfo.FromFrequency("bb", "Narrow", 5240000, 20, null, -60)));
    }

    [Fact]
    public void HtAndVhtOperationElementsSupplyChannelWidthAndCentre()
    {
        byte[] elements =
        [
            61, 2, 36, 1,
            192, 3, 1, 42, 0
        ];

        var parsed = WindowsWifiInventory.ParseInformationElements(elements, WifiBand.FiveGhz);

        Assert.Equal(80, parsed.WidthMhz);
        Assert.Equal(42, parsed.CentreChannel);
    }

    [Fact]
    public void PartialEhtIsReportedWithoutGuessingAWidth()
    {
        var parsed = WindowsWifiInventory.ParseInformationElements([255, 1, 106], WifiBand.SixGhz);

        Assert.True(parsed.EhtAdvertised);
        Assert.Equal(20, parsed.WidthMhz);
        Assert.Null(parsed.CentreChannel);
    }

    [Fact]
    public void InformationElementsExposeLoadSecurityWpsAndPmf()
    {
        byte[] elements =
        [
            11, 5, 1, 0, 128, 0, 0,
            48, 20,
            1, 0, 0, 15, 172, 4,
            1, 0, 0, 15, 172, 4,
            1, 0, 0, 15, 172, 8,
            192, 0,
            221, 4, 0, 80, 242, 4
        ];

        var parsed = WindowsWifiInventory.ParseInformationElements(elements, WifiBand.FiveGhz);

        Assert.Equal(50, parsed.ChannelUtilizationPercent);
        Assert.Equal("WPA3-SAE", parsed.Security?.Authentication);
        Assert.Equal("CCMP-128", parsed.Security?.Cipher);
        Assert.True(parsed.Security?.PmfCapable);
        Assert.True(parsed.Security?.PmfRequired);
        Assert.True(parsed.WpsAdvertised);
    }

    [Fact]
    public void ConnectionSecurityKeepsPmfFlagsFromTheBeacon()
    {
        var connection = new WifiSecurityInfo("WPA3-SAE", "CCMP-128", true, false);
        var beacon = new WifiSecurityInfo("WPA3-SAE", "CCMP-128", true, false, true, true);

        var merged = WindowsWifiInventory.MergeSecurity(connection, beacon);

        Assert.True(merged?.PmfCapable);
        Assert.True(merged?.PmfRequired);
        Assert.Contains("PMF required", merged?.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void HeSixGhzOperationSuppliesTheAdvertisedWidth()
    {
        byte[] elements =
        [
            255, 12,
            36, 0, 0, 2, 0, 0, 0,
            5, 2, 7, 0, 0
        ];

        var parsed = WindowsWifiInventory.ParseInformationElements(elements, WifiBand.SixGhz);

        Assert.True(parsed.HeAdvertised);
        Assert.Equal(80, parsed.WidthMhz);
        Assert.Equal(7, parsed.CentreChannel);
    }

    [Fact]
    public void TruncatedInformationElementIsIgnoredRatherThanReadPastTheBuffer()
    {
        var parsed = WindowsWifiInventory.ParseInformationElements([48, 20, 1, 0], WifiBand.FiveGhz);

        Assert.Equal(20, parsed.WidthMhz);
        Assert.Null(parsed.Security);
    }

    [Fact]
    public void AccessDeniedIsReportedAsLocationPermissionRatherThanGenericFailure()
    {
        Assert.Equal(WifiInventoryAvailability.LocationPermissionDenied, WindowsWifiInventory.Availability(5));
        Assert.Contains("Location", WindowsWifiInventory.DescribeError("test", 5), StringComparison.Ordinal);
        Assert.Equal(WifiInventoryAvailability.ServiceStopped, WindowsWifiInventory.Availability(1062));
        Assert.Equal(WifiInventoryAvailability.Failed, WindowsWifiInventory.Availability(1234));
    }

    [Fact]
    public void StrongUncongestedLinkProducesNoFindings()
    {
        var radio = Radio(Bss("aa", "Home", 5180000, 80, 42, -45));

        Assert.Empty(WifiRadioAnalyzer.Analyze(radio));
    }

    [Fact]
    public void UnassociatedRadioIsNotDiagnosed()
    {
        var radio = new WifiRadioInfo("guid", "Wi-Fi", "", "", 0, 0, 0, null, []);

        Assert.Empty(WifiRadioAnalyzer.Analyze(radio));
    }

    [Fact]
    public void WeakSignalIsAPlacementProblem_NotANetworkFault()
    {
        var radio = Radio(Bss("aa", "Home", 5180000, 80, 42, -78));

        var finding = Assert.Single(WifiRadioAnalyzer.Analyze(radio));

        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Equal(NetworkSegment.Lan, finding.Segment);
        Assert.Contains("no setting on this machine can remove", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void SameNetworkOnFiveGhz_IsSurfacedWhenTheRadioJoinedTwoPointFour()
    {
        var radio = Radio(
            Bss("aa", "Home", 2437000, 20, null, -58),
            Bss("bb", "Home", 5180000, 80, 42, -60));

        var findings = WifiRadioAnalyzer.Analyze(radio);

        var band = Assert.Single(findings, item => item.Title.Contains("5 GHz", StringComparison.Ordinal));
        Assert.Equal(RemediationOwner.PresetOrManual, band.Owner);
    }

    [Fact]
    public void FortyMegahertzOnTwoPointFour_IsOwnedByTheRouterWithAnExactValue()
    {
        var radio = Radio(Bss("aa", "Home", 2412000, 40, 3, -50));

        var finding = Assert.Single(WifiRadioAnalyzer.Analyze(radio), item => item.Title.Contains("40 MHz", StringComparison.Ordinal));

        Assert.Equal(RemediationOwner.Router, finding.Owner);
        Assert.Contains("20 MHz", finding.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void CongestionRecommendsTheQuietestNonOverlappingChannel()
    {
        // Channel 1 is crowded and loud, channel 6 has one distant neighbour, channel 11 is clear.
        var radio = Radio(
            Bss("aa", "Home", 2412000, 20, null, -55),
            Bss("b1", "Neighbour1", 2412000, 20, null, -50),
            Bss("b2", "Neighbour2", 2417000, 20, null, -52),
            Bss("b3", "Neighbour3", 2422000, 20, null, -58),
            Bss("b4", "Neighbour4", 2437000, 20, null, -80));

        var finding = Assert.Single(WifiRadioAnalyzer.Analyze(radio), item => item.Title.Contains("share this channel", StringComparison.Ordinal));

        Assert.Equal(RemediationOwner.Router, finding.Owner);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains("channel to 11", finding.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyOnTheQuietestChannel_IsSaidPlainlyRatherThanRecommendingAMove()
    {
        var radio = Radio(
            Bss("aa", "Home", 2462000, 20, null, -55),
            Bss("b1", "Neighbour1", 2462000, 20, null, -60),
            Bss("b2", "Neighbour2", 2412000, 20, null, -50),
            Bss("b3", "Neighbour3", 2437000, 20, null, -50));

        var finding = Assert.Single(WifiRadioAnalyzer.Analyze(radio), item => item.Title.Contains("share this channel", StringComparison.Ordinal));

        Assert.Contains("already the least congested", finding.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void DistantNeighboursDoNotCountAsCongestion()
    {
        var radio = Radio(
            Bss("aa", "Home", 2412000, 20, null, -55),
            Bss("b1", "FarAway", 2412000, 20, null, -90));

        Assert.Empty(WifiRadioAnalyzer.Analyze(radio));
    }

    private static (WifiBand Band, int Channel) Channel(int frequencyKhz)
    {
        var bss = WifiBssInfo.FromFrequency("aa", "x", frequencyKhz, 20, null, -50);
        return (bss.Band, bss.Channel);
    }

    private static WifiBssInfo Bss(string bssid, string ssid, int frequencyKhz, int widthMhz, int? centreChannel, int rssi) =>
        WifiBssInfo.FromFrequency(bssid, ssid, frequencyKhz, widthMhz, centreChannel, rssi);

    /// <summary>The first BSS is the one the radio is associated with; the rest are what it can hear.</summary>
    private static WifiRadioInfo Radio(params WifiBssInfo[] scan) => new(
        "guid", "Wi-Fi", scan[0].Ssid, scan[0].Bssid, 80, 400_000, 400_000, scan[0], scan);
}

/// <summary>Read-only live check of the native WLAN surface; skipped unless explicitly enabled.</summary>
public sealed class WindowsWifiInventoryLiveTests
{
    [LiveWindowsFact]
    public void Read_ReportsRadiosOrExplainsWhyItCannot()
    {
        var result = WindowsWifiInventory.Read();

        Assert.NotNull(result.CapturedAt);
        Assert.True(result.Supported || result.Error is not null || result.Radios.Count == 0);
        Assert.All(result.Radios, radio => Assert.All(radio.Neighbours, bss =>
            Assert.InRange(bss.ChannelWidthMhz, 20, 160)));
    }
}
