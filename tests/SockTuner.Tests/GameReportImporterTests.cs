using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class GameReportImporterTests
{
    // Shaped after a real GameNetAnalyzer capture: a 20 ms tick game against an AWS endpoint.
    private const string Report = """
    {
      "ToolVersion": "1.0.0",
      "Timestamp": "2026-01-08T21:07:52.9095772+01:00",
      "Game": "Fortnite",
      "GameProfile": { "Notes": "Performance mode", "TargetRegions": ["AWS FRA"], "ExpectedTickMs": 20.0 },
      "LocalIP": "192.168.1.2",
      "RemoteIP": "18.157.42.2",
      "RemotePort": "9060",
      "RemoteHost": "ec2-18-157-42-2.eu-central-1.compute.amazonaws.com",
      "RegionHint": "AWS Frankfurt (eu-central-1)",
      "Flow": {
        "PacketCount": 4754, "DurationSec": 76.642, "PktPerSec": 62.0,
        "AvgDeltaMs": 16.125, "MinDeltaMs": 0.0, "MaxDeltaMs": 238.684,
        "AvgJitterMs": 3.4, "MaxJitterMs": 222.559, "BurstRatio": 0.09, "SpikeRatio": 0.009
      },
      "Scores": { "AvgJitter": "A", "Spike": "S", "Overall": "A", "Burst": "B" }
    }
    """;

    [Fact]
    public void TheServerTheGameActuallyUsedBecomesTheDiagnosticTarget()
    {
        // This is the one thing SockTuner cannot work out on its own, and the reason to import at all.
        var report = GameReportImporter.Parse(Report);

        Assert.Equal("ec2-18-157-42-2.eu-central-1.compute.amazonaws.com", report.DiagnosticTarget);
        Assert.Equal("AWS Frankfurt (eu-central-1)", report.RegionHint);
        Assert.Equal(20.0, report.ExpectedTickMs);
    }

    [Fact]
    public void TheAddressIsUsedWhenNoHostnameWasResolved()
    {
        var report = GameReportImporter.Parse(Report.Replace("\"RemoteHost\": \"ec2-18-157-42-2.eu-central-1.compute.amazonaws.com\",", string.Empty));

        Assert.Equal("18.157.42.2", report.DiagnosticTarget);
    }

    [Fact]
    public void FlowStatisticsAreRead()
    {
        var flow = GameReportImporter.Parse(Report).Flow;

        Assert.NotNull(flow);
        Assert.Equal(4754, flow.PacketCount);
        Assert.Equal(3.4, flow.AverageJitterMs);
        Assert.Equal(238.684, flow.MaximumDeltaMs);
    }

    [Fact]
    public void ALongStallIsReportedAsAFreezeRatherThanLag()
    {
        // 238 ms against a 20 ms tick is twelve missed ticks: the player sees a freeze, not lag.
        var findings = GameReportImporter.Analyze(GameReportImporter.Parse(Report));

        var stall = Assert.Single(findings, finding => finding.Title.Contains("stalled", StringComparison.Ordinal));
        Assert.Equal(ChangeRisk.High, stall.Severity);
        Assert.Equal("Gaming diagnostics", stall.Section);
    }

    [Fact]
    public void JitterIsJudgedAgainstTheGamesOwnTickNotAFixedThreshold()
    {
        // 12 ms of jitter is well inside a 50 ms tick and past half the budget on a 20 ms one, so the same
        // number has to produce different findings.
        var slow = Report.Replace("\"ExpectedTickMs\": 20.0", "\"ExpectedTickMs\": 50.0")
                         .Replace("\"AvgJitterMs\": 3.4", "\"AvgJitterMs\": 12.0")
                         .Replace("\"MaxDeltaMs\": 238.684", "\"MaxDeltaMs\": 60.0");
        var fast = Report.Replace("\"AvgJitterMs\": 3.4", "\"AvgJitterMs\": 12.0")
                         .Replace("\"MaxDeltaMs\": 238.684", "\"MaxDeltaMs\": 60.0");

        Assert.DoesNotContain(
            GameReportImporter.Analyze(GameReportImporter.Parse(slow)),
            finding => finding.Title.Contains("jitter", StringComparison.Ordinal));
        Assert.Contains(
            GameReportImporter.Analyze(GameReportImporter.Parse(fast)),
            finding => finding.Title.Contains("jitter", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanCaptureSaysSoRatherThanInventingAProblem()
    {
        var clean = Report.Replace("\"MaxDeltaMs\": 238.684", "\"MaxDeltaMs\": 40.0")
                          .Replace("\"SpikeRatio\": 0.009", "\"SpikeRatio\": 0.001");

        var finding = Assert.Single(GameReportImporter.Analyze(GameReportImporter.Parse(clean)));

        Assert.Contains("looks clean", finding.Title, StringComparison.Ordinal);
        Assert.Equal(ChangeRisk.Low, finding.Severity);
    }

    [Fact]
    public void AReportWithoutFlowStatisticsIsHandledRatherThanCrashing()
    {
        var finding = Assert.Single(GameReportImporter.Analyze(
            GameReportImporter.Parse("""{ "Game": "Valorant" }""")));

        Assert.Contains("no flow statistics", finding.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void MalformedInputIsRefused(string json) =>
        Assert.ThrowsAny<Exception>(() => GameReportImporter.Parse(json));

    [Fact]
    public void UnknownFieldsAndMissingProfilesDoNotBreakTheImport()
    {
        // The format belongs to another tool and will change; an unexpected field must not be fatal.
        var report = GameReportImporter.Parse("""
        { "Game": "Apex", "SomethingNew": { "a": 1 }, "RemoteIP": "1.2.3.4",
          "Flow": { "PacketCount": 10, "AvgJitterMs": 1.0 } }
        """);

        Assert.Equal("Apex", report.Game);
        Assert.Equal("1.2.3.4", report.DiagnosticTarget);
        Assert.Null(report.ExpectedTickMs);
        Assert.Equal(10, report.Flow?.PacketCount);
    }

    [Fact]
    public void EveryFindingRoutesSomewhereAndSaysWhatToDo() =>
        Assert.All(GameReportImporter.Analyze(GameReportImporter.Parse(Report)), finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Section));
            Assert.False(string.IsNullOrWhiteSpace(finding.Action));
            Assert.False(string.IsNullOrWhiteSpace(finding.Evidence));
        });
}
