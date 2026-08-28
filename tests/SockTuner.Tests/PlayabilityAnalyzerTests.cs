using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class PlayabilityAnalyzerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 13, 0, 0, TimeSpan.Zero);

    private static ProbeStatistics Probe(IEnumerable<double?> roundTrips, int intervalMs = 250)
    {
        var samples = roundTrips
            .Select((rtt, index) => new ProbeSample(Start.AddMilliseconds(index * intervalMs), rtt))
            .ToArray();
        return ProbeStatistics.Calculate("Game endpoint", "10.0.0.1", samples);
    }

    private static IEnumerable<double?> Flat(int count, double value) => Enumerable.Repeat<double?>(value, count);

    [Fact]
    public void TheSameJitterIsFineAtTwentyHertzAndFatalAtOneHundredAndTwentyEight()
    {
        // The whole point of the tick rate: one measurement, two verdicts. 10 ms of swing sits
        // inside half of a 50 ms update and is more than a whole 7.8 ms one.
        var swinging = Probe([15d, 35, 15, 35, 15, 35, 15, 35], intervalMs: 100);

        var relaxed = PlayabilityAnalyzer.Judge(swinging, GameProfiles.Get("apex-legends"));
        var competitive = PlayabilityAnalyzer.Judge(swinging, GameProfiles.Get("valorant"));

        Assert.Equal(PlayabilityGrade.Good, relaxed.Metrics.Single(metric => metric.Name == "jitter").Grade);
        Assert.Equal(PlayabilityGrade.Poor, competitive.Metrics.Single(metric => metric.Name == "jitter").Grade);
    }

    [Fact]
    public void TheJitterBudgetIsCappedSoASlowTickDoesNotExcuseAnything()
    {
        // A 10 Hz game's tick arithmetic alone would call 50 ms of jitter comfortable. True of the
        // tick, useless to a person, which is why the caps exist.
        var slow = GameProfile.Custom(10);

        Assert.Equal(15, slow.GoodJitterMs);
        Assert.Equal(30, slow.PlayableJitterMs);
    }

    [Fact]
    public void TheWorstMetricDecidesTheVerdictAndIsNamed()
    {
        // 18 ms is excellent for any of these games. Two per cent loss is not, and averaging the
        // three numbers would hide exactly that.
        var lossy = Probe(Flat(48, 18).Concat([null, null]));

        var verdict = PlayabilityAnalyzer.Judge(lossy, GameProfiles.Get("cs2"));

        Assert.Equal(PlayabilityGrade.Poor, verdict.Grade);
        Assert.Equal("packet loss", verdict.DecidedBy);
        Assert.Equal(PlayabilityGrade.Good, verdict.Metrics.Single(metric => metric.Name == "latency").Grade);
    }

    [Fact]
    public void AnySingleLostProbeCostsAnInputAndIsNotGraded_Good()
    {
        var oneLost = Probe(Flat(99, 15).Concat([null]));

        var loss = PlayabilityAnalyzer.Judge(oneLost, GameProfiles.Get("fortnite"))
            .Metrics.Single(metric => metric.Name == "packet loss");

        Assert.Equal(PlayabilityGrade.Playable, loss.Grade);
    }

    [Fact]
    public void AMetricThatCouldNotBeMeasuredNeverDecidesTheVerdict()
    {
        // One reply is enough for latency and loss and not enough for jitter. An unmeasurable
        // number must not turn a clean run into a bad verdict.
        var single = Probe([12d]);

        var verdict = PlayabilityAnalyzer.Judge(single, GameProfiles.Get("valorant"));

        Assert.Equal(PlayabilityGrade.Unmeasured, verdict.Metrics.Single(metric => metric.Name == "jitter").Grade);
        Assert.Equal(PlayabilityGrade.Good, verdict.Grade);
    }

    [Fact]
    public void NoRepliesIsReportedAsUnmeasuredRatherThanAsTheWorstPossibleConnection()
    {
        var silent = Probe(Flat(10, 0).Select(_ => (double?)null));

        var verdict = PlayabilityAnalyzer.Judge(silent, GameProfiles.Get("cs2"));

        Assert.Equal(PlayabilityGrade.Unmeasured, verdict.Grade);
        Assert.Empty(verdict.Metrics);
    }

    [Fact]
    public void AFastTickWarnsThatWindowsMeasuresRoundTripsInWholeMilliseconds()
    {
        // At 128 Hz the comfortable jitter budget is 3.9 ms and the measurement's own resolution is
        // 1 ms. Claiming a decimal there would be claiming precision the probe does not have.
        var jitter = PlayabilityAnalyzer.Judge(Probe(Flat(20, 18), intervalMs: 100), GameProfiles.Get("valorant"))
            .Metrics.Single(metric => metric.Name == "jitter");

        Assert.Contains("whole milliseconds", jitter.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyMetricsOutsideTheirBudgetBecomeFindings()
    {
        var clean = PlayabilityAnalyzer.Judge(Probe(Flat(20, 12), intervalMs: 100), GameProfiles.Get("apex-legends"));
        var bad = PlayabilityAnalyzer.Judge(Probe(Flat(20, 220), intervalMs: 100), GameProfiles.Get("apex-legends"));

        Assert.Empty(PlayabilityAnalyzer.Findings(clean));
        Assert.Contains(PlayabilityAnalyzer.Findings(bad), finding => finding.Section == "Gaming diagnostics");
    }

    [Theory]
    [InlineData(128, 30, 60)]
    [InlineData(64, 40, 80)]
    [InlineData(30, 60, 100)]
    [InlineData(20, 80, 150)]
    public void TheLatencyBudgetTightensAsTheTickRateRises(double hz, double good, double playable)
    {
        var (actualGood, actualPlayable) = GameProfile.Custom(hz).PingBudgetMs;

        Assert.Equal(good, actualGood);
        Assert.Equal(playable, actualPlayable);
    }

    [Fact]
    public void AnImportedTickRateLandsOnANamedTitleWhenOneMatches()
    {
        // 7.8125 ms is 128 Hz: the capture should reach a profile that carries evidence, not a
        // bare number.
        var matched = GameProfile.FromTickIntervalMs("Unknown", 1000d / 128);
        var unmatched = GameProfile.FromTickIntervalMs("Some game", 20);

        Assert.Equal("valorant", matched.Id);
        Assert.Equal("imported", unmatched.Id);
        Assert.Equal(50, unmatched.TickRateHz);
    }

    [Fact]
    public void AGenericBandIsNeverClaimedAsATitle()
    {
        // 128 Hz is both the "Pro esports" band and Valorant. Matching the band would put a title
        // on a capture that never named one.
        Assert.NotNull(GameProfiles.ClosestTo(128));
        Assert.NotEqual(TickRateSource.Generic, GameProfiles.ClosestTo(128)!.Source);
        Assert.Null(GameProfiles.ClosestTo(45));
    }

    [Fact]
    public void ProbePayloadFollowsTheTickBandAndStaysInsideAPathMtu()
    {
        foreach (var profile in GameProfiles.All)
        {
            Assert.InRange(profile.PayloadBytes, 1, DiagnosticProfile.MaximumPayloadBytes);
        }
    }
}
