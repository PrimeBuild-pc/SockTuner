using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Drift over history and thresholds over a live stream. Both run on fixtures with fixed
/// timestamps: no clock, no network, no host state.
/// </summary>
public sealed class BaselineAndWatchdogTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SustainedDegradationIsReportedAgainstTheOlderRuns()
    {
        var entries = Entries(
            (Now.AddDays(-14), 20), (Now.AddDays(-13), 22), (Now.AddDays(-12), 21),
            (Now.AddDays(-2), 34), (Now.AddDays(-1), 36));

        var report = BaselineAnalyzer.Compare(entries, TimeSpan.FromDays(7), Now);

        Assert.True(report.Comparable);
        Assert.Equal((3, 2), (report.BaselineRuns, report.RecentRuns));
        var change = Assert.Single(report.Degraded, item => item.Metric == "Median ping");
        Assert.Equal(21, change.Baseline);
        Assert.Equal(35, change.Recent);
        Assert.Contains("Median ping degraded 67%", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ImprovementIsReportedInItsOwnRight()
    {
        var entries = Entries(
            (Now.AddDays(-14), 60), (Now.AddDays(-13), 62), (Now.AddDays(-2), 20), (Now.AddDays(-1), 22));

        var report = BaselineAnalyzer.Compare(entries, TimeSpan.FromDays(7), Now);

        Assert.Empty(report.Degraded);
        Assert.Contains("Improved against the earlier baseline", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargePercentageOnATinyNumberIsNotADegradation()
    {
        // 2 ms to 3 ms is 50% and means nothing on a LAN path.
        var entries = Entries(
            (Now.AddDays(-14), 2), (Now.AddDays(-13), 2), (Now.AddDays(-2), 3), (Now.AddDays(-1), 3));

        var report = BaselineAnalyzer.Compare(entries, TimeSpan.FromDays(7), Now);

        Assert.Empty(report.Degraded);
        Assert.Equal("No significant change against the earlier baseline.", report.Verdict);
    }

    [Fact]
    public void OneBadRunDoesNotBecomeTheVerdict()
    {
        // The median of each side ignores the outlier a mean would follow.
        var entries = Entries(
            (Now.AddDays(-14), 20), (Now.AddDays(-13), 21), (Now.AddDays(-12), 20),
            (Now.AddDays(-3), 21), (Now.AddDays(-2), 400), (Now.AddDays(-1), 20));

        var report = BaselineAnalyzer.Compare(entries, TimeSpan.FromDays(7), Now);

        Assert.Empty(report.Degraded);
    }

    [Fact]
    public void RunsWithDifferentParametersAreRefusedRatherThanCompared()
    {
        var entries = Entries(
            (Now.AddDays(-14), 20), (Now.AddDays(-13), 21), (Now.AddDays(-2), 40), (Now.AddDays(-1), 41)).ToList();
        entries[3] = entries[3] with { Report = entries[3].Report with { RequestedTarget = "other.example" } };

        var report = BaselineAnalyzer.Compare(entries, TimeSpan.FromDays(7), Now);

        Assert.False(report.Comparable);
        Assert.Contains("same target", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void TooFewRunsOnOneSideIsSaidPlainly()
    {
        var entries = Entries(
            (Now.AddDays(-14), 20), (Now.AddDays(-13), 21), (Now.AddDays(-12), 22), (Now.AddDays(-1), 40));

        var report = BaselineAnalyzer.Compare(entries, TimeSpan.FromDays(7), Now);

        Assert.False(report.Comparable);
        Assert.Contains("1 recent run(s)", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchdogWaitsForAFullBadWindowRatherThanOneSample()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLatencyMs: 100, WindowSamples: 5));

        for (var index = 0; index < 4; index++)
        {
            Assert.Null(watchdog.Observe(Reply(index, 20)));
        }

        Assert.Null(watchdog.Observe(Reply(4, 900)));
        Assert.Empty(watchdog.OpenAlerts);
    }

    [Fact]
    public void AlertRecordsWhenTheProblemStarted_NotWhenItWasRaised()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLatencyMs: 100, WindowSamples: 3));
        watchdog.Observe(Reply(0, 20));
        watchdog.Observe(Reply(1, 400));

        // The window fills at second 2 and its median already breaches, so the alert fires here...
        var alert = watchdog.Observe(Reply(2, 420));

        Assert.NotNull(alert);
        Assert.Equal(WatchdogAlertKind.Latency, alert.Kind);
        // ...but it is dated from second 1, the first sample that was itself bad.
        Assert.Equal(Now.AddSeconds(1), alert.StartedAt);
        Assert.True(alert.Open);
    }

    [Fact]
    public void RecoveryClosesTheAlertAndRecordsItsDuration()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLatencyMs: 100, WindowSamples: 3));
        for (var index = 0; index < 3; index++)
        {
            watchdog.Observe(Reply(index, 400));
        }

        for (var index = 3; index < 6; index++)
        {
            watchdog.Observe(Reply(index, 20));
        }

        // Seconds 0 to 2 were bad; the alert spans those rather than the time taken to notice.
        var alert = Assert.Single(watchdog.Alerts);
        Assert.False(alert.Open);
        Assert.Equal(Now, alert.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(2), alert.Duration);
    }

    [Fact]
    public void OneOpenAlertPerTargetRatherThanOnePerBadSample()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLatencyMs: 100, WindowSamples: 3));
        for (var index = 0; index < 20; index++)
        {
            watchdog.Observe(Reply(index, 400));
        }

        Assert.Single(watchdog.Alerts);
    }

    [Fact]
    public void LossCrossesItsOwnThresholdIndependently()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLatencyMs: 1000, MaximumLossPercent: 20, WindowSamples: 4));
        watchdog.Observe(Reply(0, 5));
        watchdog.Observe(NoReply(1));
        watchdog.Observe(NoReply(2));
        var alert = watchdog.Observe(Reply(3, 5));

        Assert.NotNull(alert);
        Assert.Equal(WatchdogAlertKind.Loss, alert.Kind);
        Assert.Equal(50, alert.Measured);
        Assert.Equal(Now.AddSeconds(1), alert.StartedAt);
    }

    [Fact]
    public void LocalApiFailuresNeverRaiseAnAlertAboutThePath()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLossPercent: 1, WindowSamples: 3));
        for (var index = 0; index < 10; index++)
        {
            watchdog.Observe(new MonitorSample(
                Now.AddSeconds(index), "Gateway", "192.168.1.1", null, MonitorSampleKind.LocalError, "Ping API failure"));
        }

        Assert.Empty(watchdog.Alerts);
    }

    [Fact]
    public void EachTargetIsWatchedSeparately()
    {
        var watchdog = new Watchdog(new WatchdogThresholds(MaximumLatencyMs: 100, WindowSamples: 3));
        for (var index = 0; index < 3; index++)
        {
            watchdog.Observe(Reply(index, 20));
            watchdog.Observe(new MonitorSample(Now.AddSeconds(index), "Endpoint", "9.9.9.9", 500, MonitorSampleKind.Reply, null));
        }

        var alert = Assert.Single(watchdog.Alerts);
        Assert.Equal("Endpoint", alert.Label);
    }

    [Theory]
    [InlineData(0, 5, 20)]
    [InlineData(100, 101, 20)]
    [InlineData(100, 5, 2)]
    public void ThresholdsAreValidatedBeforeAnythingIsWatched(double latency, double loss, int window) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Watchdog(new WatchdogThresholds(latency, loss, window)));

    [Fact]
    public void BeforeAndAfterReportNamesTheChangeAndRefusesAnIncomparablePair()
    {
        var baseline = Report(Now.AddHours(-1), 40);
        var after = Report(Now, 20) with { Profile = new DiagnosticProfile("other", "Other", 30, TimeSpan.Zero, TimeSpan.FromSeconds(1)) };
        var applied = new PlannedChange(
            SettingCatalog.Get("mmcss.system-responsiveness"),
            SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null),
            new StoredSettingValue(true, "20"),
            new StoredSettingValue(true, "10"),
            ChangeSource.Profile);

        var html = DiagnosticReportExporter.SerializeComparisonHtml(
            baseline, after, DiagnosticComparisonService.Compare(baseline, after), [applied]);

        Assert.Contains("Not comparable", html, StringComparison.Ordinal);
        Assert.Contains("the runs cannot be compared", html, StringComparison.Ordinal);
        Assert.Contains("SystemResponsiveness", html, StringComparison.Ordinal);
        Assert.Contains("<td>20</td><td>10</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeAndAfterReportRedactsTheAdapterTargetOnRequest()
    {
        var baseline = Report(Now.AddHours(-1), 40);
        var after = Report(Now, 20);
        var definition = SettingCatalog.Get("tcp.interface.mtu");
        var applied = new PlannedChange(
            definition,
            definition.ResolveAddress(Guid.NewGuid().ToString()),
            StoredSettingValue.Missing,
            new StoredSettingValue(true, "1420"));

        var html = DiagnosticReportExporter.SerializeComparisonHtml(
            baseline, after, DiagnosticComparisonService.Compare(baseline, after), [applied], redact: true);

        Assert.Contains("[redacted]", html, StringComparison.Ordinal);
        Assert.DoesNotContain(applied.Address.TargetId!, html, StringComparison.Ordinal);
        Assert.Contains("Game average ms", html, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DiagnosticHistoryEntry> Entries(params (DateTimeOffset SavedAt, double Ms)[] runs) =>
        runs.Select(run => new DiagnosticHistoryEntry(Guid.NewGuid(), run.SavedAt, Report(run.SavedAt, run.Ms))).ToArray();

    private static GamingDiagnosticReport Report(DateTimeOffset startedAt, double milliseconds)
    {
        var stats = ProbeStatistics.Calculate("Game endpoint", "198.51.100.10", Enumerable.Range(0, 5)
            .Select(index => new ProbeSample(startedAt.AddSeconds(index), milliseconds))
            .ToArray());
        return new GamingDiagnosticReport(
            "game.example", startedAt, TimeSpan.FromSeconds(10), DiagnosticProfiles.All[1],
            DiagnosticLoadCondition.Idle, stats, stats, stats,
            new DnsMeasurement("game.example", TimeSpan.FromMilliseconds(5), ["198.51.100.10"], null),
            null, []);
    }

    private static MonitorSample Reply(int second, double milliseconds) =>
        new(Now.AddSeconds(second), "Gateway", "192.168.1.1", milliseconds, MonitorSampleKind.Reply, null);

    private static MonitorSample NoReply(int second) =>
        new(Now.AddSeconds(second), "Gateway", "192.168.1.1", null, MonitorSampleKind.NoReply, "TimedOut");
}
