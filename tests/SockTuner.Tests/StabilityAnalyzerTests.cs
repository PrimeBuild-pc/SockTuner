using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Episode detection over a long monitoring window. Pure over collected samples: no network, no
/// host access, deterministic timestamps.
/// </summary>
public sealed class StabilityAnalyzerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SteadyRunReportsNoEpisodes()
    {
        var report = Report(Enumerable.Range(0, 40).Select(index => Reply(index, 20 + (index % 3))));

        var result = StabilityAnalyzer.Analyze(report);

        Assert.True(result.Stable);
        Assert.Contains("No loss burst", result.Verdict, StringComparison.Ordinal);
        Assert.Contains("does not rule out", result.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsecutiveNoReplies_BecomeOneLossBurstWithItsWindow()
    {
        var samples = Enumerable.Range(0, 30).Select(index => index is >= 10 and <= 13
            ? NoReply(index)
            : Reply(index, 20));

        var episodes = StabilityAnalyzer.Analyze(Report(samples)).Episodes;

        var episode = Assert.Single(episodes);
        Assert.Equal(StabilityEventKind.LossBurst, episode.Kind);
        Assert.Equal(4, episode.Samples);
        Assert.Equal(Start.AddSeconds(10), episode.StartedAt);
        Assert.Equal(Start.AddSeconds(13), episode.EndedAt);
    }

    [Fact]
    public void SingleBadSampleIsNoiseRatherThanAnEpisode()
    {
        var samples = Enumerable.Range(0, 30).Select(index => index == 7 ? NoReply(index) : Reply(index, 20));

        Assert.Empty(StabilityAnalyzer.Analyze(Report(samples)).Episodes);
    }

    [Fact]
    public void SpikesAreJudgedAgainstTheRunsOwnBaseline()
    {
        // A 90 ms path is not a spike; the same path jumping to 400 ms is.
        var samples = Enumerable.Range(0, 30).Select(index => index is 20 or 21 ? Reply(index, 400) : Reply(index, 90));

        var episode = Assert.Single(StabilityAnalyzer.Analyze(Report(samples)).Episodes);

        Assert.Equal(StabilityEventKind.LatencySpike, episode.Kind);
        Assert.Equal(400, episode.PeakMs);
    }

    [Fact]
    public void SmallVariationOnAFastLinkIsNotASpike()
    {
        var samples = Enumerable.Range(0, 30).Select(index => Reply(index, index is 12 or 13 ? 8 : 1));

        Assert.Empty(StabilityAnalyzer.Analyze(Report(samples)).Episodes);
    }

    [Fact]
    public void LocalApiFailuresAreNotCountedAsPathLoss()
    {
        var samples = Enumerable.Range(0, 30).Select(index => index is >= 5 and <= 9
            ? new MonitorSample(Start.AddSeconds(index), "Gateway", "192.168.1.1", null, MonitorSampleKind.LocalError, "Ping API failure")
            : Reply(index, 20));

        Assert.Empty(StabilityAnalyzer.Analyze(Report(samples)).Episodes);
    }

    [Fact]
    public void EachTargetIsAnalysedAgainstItsOwnBaseline()
    {
        var samples = Enumerable.Range(0, 20).SelectMany(index => new[]
        {
            new MonitorSample(Start.AddSeconds(index), "Gateway", "192.168.1.1", 1, MonitorSampleKind.Reply, null),
            new MonitorSample(Start.AddSeconds(index), "Endpoint", "9.9.9.9", index is 8 or 9 ? 500 : 60, MonitorSampleKind.Reply, null)
        });

        var episode = Assert.Single(StabilityAnalyzer.Analyze(Report(samples)).Episodes);

        Assert.Equal("Endpoint", episode.Label);
    }

    [Fact]
    public void TruncatedWindowIsDeclaredInTheVerdict()
    {
        var samples = Enumerable.Range(0, 10).Select(index => Reply(index, 20)).ToArray();
        var report = new MonitorReport(Start, TimeSpan.FromMinutes(30), samples, samples.Length + 500);

        var result = StabilityAnalyzer.Analyze(report);

        Assert.True(result.SamplesTruncated);
        Assert.Contains("Older samples were dropped", result.Verdict, StringComparison.Ordinal);
    }

    private static MonitorReport Report(IEnumerable<MonitorSample> samples)
    {
        var values = samples.ToArray();
        return new MonitorReport(Start, TimeSpan.FromMinutes(5), values, values.Length);
    }

    private static MonitorSample Reply(int second, double milliseconds) =>
        new(Start.AddSeconds(second), "Gateway", "192.168.1.1", milliseconds, MonitorSampleKind.Reply, null);

    private static MonitorSample NoReply(int second) =>
        new(Start.AddSeconds(second), "Gateway", "192.168.1.1", null, MonitorSampleKind.NoReply, "TimedOut");
}
