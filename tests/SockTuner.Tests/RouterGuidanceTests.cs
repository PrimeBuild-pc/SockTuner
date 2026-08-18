using SockTuner.Models;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

/// <summary>
/// Router guidance has one acceptance test: a parameter, a value and a reason. Prose that tells
/// someone to "check their QoS settings" fails it.
/// </summary>
public sealed class RouterGuidanceTests
{
    [Fact]
    public void EveryInstructionNamesAParameterAValueAndAReason()
    {
        var guidance = RouterGuidance.For(new RouterGuidanceInput(
            Download: Loaded(TransferDirection.Download, idle: 14, loaded: 320, bitsPerSecond: 48_000_000),
            Wifi: CongestedRadio(),
            Topology: NatTopology.DoubleNat));

        Assert.All(guidance.SelectMany(item => item.Instructions), instruction =>
        {
            Assert.NotEmpty(instruction.Parameter);
            Assert.NotEmpty(instruction.Value);
            Assert.NotEmpty(instruction.Reason);
        });
        Assert.All(guidance, item => Assert.NotEmpty(item.Verification));
        Assert.All(guidance, item => Assert.Equal(RemediationOwner.Router, item.Owner));
    }

    [Fact]
    public void BufferbloatProducesShapedLimitsBelowTheMeasuredRate()
    {
        var guidance = RouterGuidance.For(new RouterGuidanceInput(
            Download: Loaded(TransferDirection.Download, 14, 320, 48_000_000),
            Upload: Loaded(TransferDirection.Upload, 14, 260, 10_000_000)));

        var item = Assert.Single(guidance);
        Assert.Equal("43200 kbit/s", Instruction(item, "SQM download limit").Value);
        Assert.Equal("9000 kbit/s", Instruction(item, "SQM upload limit").Value);
        Assert.Equal("cake", Instruction(item, "Queue discipline").Value);
        Assert.Equal("sqm.@queue[0].download", Instruction(item, "SQM download limit").UciPath);
        Assert.Contains("lower the limit by another 5%", item.Verification, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheMeasuredDirectionIsShaped()
    {
        var guidance = RouterGuidance.For(new RouterGuidanceInput(
            Download: Loaded(TransferDirection.Download, 14, 320, 48_000_000)));

        var item = Assert.Single(guidance);
        Assert.DoesNotContain(item.Instructions, instruction => instruction.Parameter.Contains("upload", StringComparison.Ordinal));
    }

    [Fact]
    public void AGoodGradeProducesNoShapingAdvice()
    {
        var guidance = RouterGuidance.For(new RouterGuidanceInput(
            Download: Loaded(TransferDirection.Download, 14, 26, 48_000_000)));

        Assert.Empty(guidance);
    }

    [Fact]
    public void ALoadThatNeverEstablishedProducesNoShapingAdvice()
    {
        var guidance = RouterGuidance.For(new RouterGuidanceInput(
            Download: Loaded(TransferDirection.Download, 14, 320, bitsPerSecond: 0)));

        Assert.Empty(guidance);
    }

    [Fact]
    public void TheChannelGivenToTheRouterIsTheOneTheReportRecommends()
    {
        var radio = CongestedRadio();

        var guidance = Assert.Single(RouterGuidance.For(new RouterGuidanceInput(Wifi: radio)));
        var recommended = WifiRadioAnalyzer.RecommendChannel(radio);

        Assert.Equal(recommended!.Value.Channel.ToString(), Instruction(guidance, "2.4 GHz channel").Value);
        Assert.Contains($"channel to {recommended.Value.Channel}",
            WifiRadioAnalyzer.Analyze(radio).Single(finding => finding.Title.Contains("share this channel", StringComparison.Ordinal)).Action,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AQuietRadioOnItsBestChannelNeedsNoRouterChange()
    {
        var radio = Radio(
            Bss("aa", "Home", 2462000, 20, -50),
            Bss("bb", "Other", 2412000, 20, -60));

        Assert.Empty(RouterGuidance.For(new RouterGuidanceInput(Wifi: radio)));
    }

    [Fact]
    public void FiveGigahertzIsNeverGivenAnExactChannel()
    {
        var radio = Radio(
            Bss("aa", "Home", 5180000, 20, -50),
            Bss("bb", "Other", 5180000, 20, -55));

        Assert.Empty(RouterGuidance.For(new RouterGuidanceInput(Wifi: radio)));
    }

    [Fact]
    public void DoubleNatNamesBothWaysOutAndDoesNotPretendItIsAnOpenWrtSetting()
    {
        var item = Assert.Single(RouterGuidance.For(new RouterGuidanceInput(Topology: NatTopology.DoubleNat)));

        Assert.Equal(2, item.Instructions.Count);
        Assert.All(item.Instructions, instruction => Assert.Null(instruction.UciPath));
        Assert.Contains("bridge", item.Instructions[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CarrierGradeNatIsNeverGivenRouterAdvice()
    {
        Assert.Empty(RouterGuidance.For(new RouterGuidanceInput(Topology: NatTopology.CarrierGradeNat)));
    }

    private static RouterInstruction Instruction(RouterGuidanceItem item, string parameter) =>
        Assert.Single(item.Instructions, instruction => instruction.Parameter == parameter);

    private static LoadedLatencyResult Loaded(TransferDirection direction, double idle, double loaded, double bitsPerSecond) =>
        new(direction,
            Stats(idle),
            Stats(loaded),
            new ThroughputResult("http://fake", direction, 4, (long)(bitsPerSecond * 10 / 8), TimeSpan.FromSeconds(10), true));

    private static ProbeStatistics Stats(double milliseconds) => ProbeStatistics.Calculate(
        "Loaded latency", "1.1.1.1",
        Enumerable.Range(0, 5).Select(index => new ProbeSample(DateTimeOffset.Now.AddSeconds(index), milliseconds)).ToArray());

    private static WifiRadioInfo CongestedRadio() => Radio(
        Bss("aa", "Home", 2412000, 40, -55),
        Bss("b1", "Neighbour1", 2412000, 20, -50),
        Bss("b2", "Neighbour2", 2417000, 20, -52));

    private static WifiBssInfo Bss(string bssid, string ssid, int frequencyKhz, int widthMhz, int rssi) =>
        WifiBssInfo.FromFrequency(bssid, ssid, frequencyKhz, widthMhz, widthMhz == 40 ? 3 : null, rssi);

    private static WifiRadioInfo Radio(params WifiBssInfo[] scan) =>
        new("guid", "Wi-Fi", scan[0].Ssid, scan[0].Bssid, 80, 400_000, 400_000, scan[0], scan);
}
