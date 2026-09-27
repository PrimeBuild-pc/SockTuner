using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class GamingWifiDiagnosticEngineTests
{
    [Fact]
    public void MissingRadioIsNotEnoughData()
    {
        var report = GamingWifiDiagnosticEngine.Analyze(new(null));

        Assert.Equal(WifiDiagnosticVerdict.NotEnoughData, report.Verdict);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void StrongRadioAndStableGatewayAreHealthy()
    {
        var report = GamingWifiDiagnosticEngine.Analyze(new(Radio(-48), Gateway: Gateway(3, 4, 5)));

        Assert.Equal(WifiDiagnosticVerdict.Healthy, report.Verdict);
        Assert.Empty(report.Findings);
        Assert.Contains(report.Evidence, item => item.Name == "Gateway");
    }

    [Fact]
    public void WeakSignalAndUnstableGatewayConfirmLocalDegradation()
    {
        var report = GamingWifiDiagnosticEngine.Analyze(new(Radio(-78), Gateway: Gateway(4, null, 38)));

        Assert.Equal(WifiDiagnosticVerdict.Degraded, report.Verdict);
        Assert.Contains(report.Findings, item => item.Title.Contains("signal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnstableGatewayWithoutRadioEvidenceDoesNotBlameWifi()
    {
        var report = GamingWifiDiagnosticEngine.Analyze(new(Radio(-48), Gateway: Gateway(4, null, 35)));

        Assert.Equal(WifiDiagnosticVerdict.AtRisk, report.Verdict);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(DiagnosticConfidence.Low, finding.Confidence);
        Assert.Contains("do not isolate", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PassiveSignalSwingIsReported()
    {
        var radio = Radio(-55);
        WifiObservationSample[] samples =
        [
            new(DateTimeOffset.Now, radio.InterfaceId, radio.Bssid, -50, 600_000, 600_000),
            new(DateTimeOffset.Now.AddSeconds(1), radio.InterfaceId, radio.Bssid, -65, 250_000, 250_000),
            new(DateTimeOffset.Now.AddSeconds(2), radio.InterfaceId, radio.Bssid, -53, 600_000, 600_000)
        ];

        var report = GamingWifiDiagnosticEngine.Analyze(new(radio, samples));

        Assert.Equal(WifiDiagnosticVerdict.AtRisk, report.Verdict);
        Assert.Contains(report.Findings, item => item.Title.Contains("changed materially", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityMetadataAloneDoesNotCreateAPerformanceFinding()
    {
        var radio = Radio(-48) with
        {
            Security = new WifiSecurityInfo("WPA3-SAE", "CCMP-128", true, false, true, true)
        };

        var report = GamingWifiDiagnosticEngine.Analyze(new(radio));

        Assert.Equal(WifiDiagnosticVerdict.Healthy, report.Verdict);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void AdapterIssuesRaiseConfidenceOnlyWhenGatewayAlsoDegrades()
    {
        var delta = new AdapterCounterDelta("guid", "Wi-Fi", 1, 1, 2, 0, 0, 0);

        var stable = GamingWifiDiagnosticEngine.Analyze(new(Radio(-48), Gateway: Gateway(3, 4, 5), CounterDelta: delta));
        var unstable = GamingWifiDiagnosticEngine.Analyze(new(Radio(-48), Gateway: Gateway(3, null, 30), CounterDelta: delta));
        var anotherAdapter = GamingWifiDiagnosticEngine.Analyze(new(
            Radio(-48), Gateway: Gateway(3, null, 30), CounterDelta: delta with { AdapterId = "another" }));

        Assert.DoesNotContain(stable.Findings, item => item.Segment == NetworkSegment.LocalNicDriver);
        Assert.Contains(unstable.Findings, item => item.Segment == NetworkSegment.LocalNicDriver);
        Assert.DoesNotContain(anotherAdapter.Findings, item => item.Segment == NetworkSegment.LocalNicDriver);
    }

    [Fact]
    public void MissingRatesAreNotReportedAsARateCollapse()
    {
        var radio = Radio(-55);
        WifiObservationSample[] samples =
        [
            new(DateTimeOffset.Now, radio.InterfaceId, radio.Bssid, -55, 600_000, 600_000),
            new(DateTimeOffset.Now.AddSeconds(1), radio.InterfaceId, radio.Bssid, -55, 0, 0),
            new(DateTimeOffset.Now.AddSeconds(2), radio.InterfaceId, radio.Bssid, -55, 600_000, 600_000)
        ];

        var report = GamingWifiDiagnosticEngine.Analyze(new(radio, samples));

        Assert.DoesNotContain(report.Findings, item => item.Title.Contains("changed materially", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObserverRejectsActiveOrUnboundedSamplingParameters()
    {
        var observer = new WifiStabilityObserver();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => observer.ObserveAsync(
            "guid", TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(2), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => observer.ObserveAsync(
            "guid", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), null, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.ObserveAsync(
            "guid", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), null, cancellation.Token));
    }

    private static WifiRadioInfo Radio(int rssi)
    {
        var bss = WifiBssInfo.FromFrequency("aa", "Home", 5180000, 80, 42, rssi,
            security: new WifiSecurityInfo("WPA2-Personal", "CCMP-128", true, false));
        return new WifiRadioInfo(
            "guid", "Wi-Fi", bss.Ssid, bss.Bssid, 80, 600_000, 600_000, bss, [bss],
            State: WifiInterfaceState.Connected, Phy: WifiPhyKind.Vht);
    }

    private static ProbeStatistics Gateway(params double?[] values) => ProbeStatistics.Calculate(
        "Gateway",
        "192.168.1.1",
        values.Select((value, index) => new ProbeSample(
            DateTimeOffset.UnixEpoch.AddMilliseconds(index * 100), value)).ToArray());
}
