using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Whether a change helped. The interesting cases are the ones where the honest answer is "this
/// measurement cannot tell", which is what the noise floor exists to say.
/// </summary>
public sealed class ApplyVerificationAnalyzerTests
{
    private static ProbeStatistics Probe(
        double median, double p95, double jitter, double lossPercent, int sent = 100) =>
        new("Game", "game.example", sent, sent - (int)Math.Round(sent * lossPercent / 100), lossPercent,
            MinimumMs: median - 2, MedianMs: median, AverageMs: median, P95Ms: p95, P99Ms: p95 + 2,
            MaximumMs: p95 + 5, JitterMs: jitter, Samples: [], Note: null);

    private static GamingDiagnosticReport Report(
        ProbeStatistics game,
        string target = "game.example",
        DiagnosticLoadCondition load = DiagnosticLoadCondition.Unspecified)
    {
        var neutral = Probe(10, 12, 1, 0);
        return new(
            target, DateTimeOffset.Now, TimeSpan.FromSeconds(20),
            DiagnosticProfiles.All[0], load,
            neutral, neutral, game,
            new DnsMeasurement("game.example", TimeSpan.Zero, [], null),
            Connection: null,
            Findings: []);
    }

    [Fact]
    public void RunsWithDifferentParametersAreRefusedRatherThanCompared()
    {
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(40, 60, 5, 0)),
            Report(Probe(20, 30, 2, 0), target: "other.example"));

        Assert.Equal(ApplyOutcome.NotComparable, verification.Outcome);
        Assert.Empty(verification.Metrics);
    }

    [Fact]
    public void ADifferenceSmallerThanTheInstrumentIsNotAnImprovement()
    {
        // Windows times an ICMP round trip to the millisecond. Half of one is not a result.
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(20.0, 30.0, 0.5, 0)),
            Report(Probe(19.6, 29.7, 0.4, 0)));

        Assert.Equal(ApplyOutcome.NoMeasurableChange, verification.Outcome);
        Assert.All(verification.Metrics, metric => Assert.False(metric.IsSignificant));
    }

    [Fact]
    public void ADifferenceSmallerThanThePathsOwnJitterIsNotAnImprovementEither()
    {
        // A path that swings 8 ms between packets swings about that much between runs. A 5 ms
        // "gain" on top of that is the same measurement twice.
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(40, 60, 8, 0)),
            Report(Probe(35, 55, 8, 0)));

        Assert.Equal(ApplyOutcome.NoMeasurableChange, verification.Outcome);
    }

    [Fact]
    public void AGenuineImprovementIsReportedAsOne()
    {
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(40, 90, 8, 0)),
            Report(Probe(22, 30, 3, 0)));

        Assert.Equal(ApplyOutcome.Improved, verification.Outcome);
        Assert.Contains("median round trip", verification.Headline, StringComparison.OrdinalIgnoreCase);

        // One pair of runs is one pair of runs, and the text has to say so.
        Assert.Contains("once more", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuineRegressionSuggestsRollingBack()
    {
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(20, 30, 2, 0)),
            Report(Probe(45, 80, 9, 0)));

        Assert.Equal(ApplyOutcome.Worse, verification.Outcome);
        Assert.True(verification.SuggestsRollback);
        Assert.Contains("rolling back", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BetterInOnePlaceAndWorseInAnotherIsATradeRatherThanAWin()
    {
        // Latency down, jitter up: a real outcome of turning interrupt moderation off on some
        // drivers, and not something to resolve into a single winner on the user's behalf.
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(median: 40, p95: 50, jitter: 2, lossPercent: 0)),
            Report(Probe(median: 25, p95: 70, jitter: 9, lossPercent: 0)));

        Assert.Equal(ApplyOutcome.Mixed, verification.Outcome);
        Assert.False(verification.SuggestsRollback);
    }

    [Fact]
    public void LossIsJudgedAgainstWhatOneLostPacketWouldRepresent()
    {
        // 100 probes: one packet is 1%. A 1% move is exactly one packet, so it is not a result.
        var oneProbe = ApplyVerificationAnalyzer.Verify(
            Report(Probe(20, 30, 1, 0, sent: 100)),
            Report(Probe(20, 30, 1, 1, sent: 100)));
        Assert.Equal(ApplyOutcome.NoMeasurableChange, oneProbe.Outcome);

        // Five packets out of a hundred is.
        var fiveProbes = ApplyVerificationAnalyzer.Verify(
            Report(Probe(20, 30, 1, 0, sent: 100)),
            Report(Probe(20, 30, 1, 5, sent: 100)));
        Assert.Equal(ApplyOutcome.Worse, fiveProbes.Outcome);
    }

    [Fact]
    public void EveryMetricReportsBothItsNumbersAndWhetherTheyMeanAnything()
    {
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(40, 90, 8, 0)),
            Report(Probe(22, 30, 3, 0)));

        Assert.Equal(4, verification.Metrics.Count);
        Assert.All(verification.Metrics, metric => Assert.False(string.IsNullOrWhiteSpace(metric.Display)));
        Assert.Contains(verification.Metrics, metric => metric.VerdictBadge.StartsWith(Badges.Good, StringComparison.Ordinal));
    }

    [Fact]
    public void AnInsignificantMovementSaysWhatTheRunCouldResolve()
    {
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(20.0, 30.0, 0.5, 0)),
            Report(Probe(19.6, 29.7, 0.4, 0)));

        Assert.Contains(
            verification.Metrics,
            metric => metric.Display.Contains("this run can resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNoiseFloorIsNeverBelowTheInstrumentsResolution()
    {
        // Even a perfectly steady path cannot resolve below a millisecond.
        var verification = ApplyVerificationAnalyzer.Verify(
            Report(Probe(20.0, 20.0, 0.0, 0)),
            Report(Probe(19.5, 19.5, 0.0, 0)));

        Assert.Equal(ApplyOutcome.NoMeasurableChange, verification.Outcome);
        Assert.All(
            verification.Metrics.Where(metric => metric.Unit == "ms"),
            metric => Assert.True(metric.NoiseFloor >= ApplyVerificationAnalyzer.IcmpResolutionMs));
    }
}
