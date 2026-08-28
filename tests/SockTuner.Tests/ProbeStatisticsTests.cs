using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class ProbeStatisticsTests
{
    [Fact]
    public void WindowedJitter_DoesNotMoveWithHowFastTheProbeSent()
    {
        // The same line, sampled at 100 ms and at 50 ms. The consecutive-difference jitter reports
        // the same swing either way here, but the windowed figure is the one defined so that it
        // cannot drift with the send rate — which is what makes it comparable to a tick budget.
        var slow = Windowed(10, 100, index => index % 2 == 0 ? 20 : 30);
        var fast = Windowed(20, 50, index => index % 2 == 0 ? 20 : 30);

        Assert.Equal(5, slow.WindowedJitterMs!.Value, 3);
        Assert.Equal(5, fast.WindowedJitterMs!.Value, 3);
    }

    [Fact]
    public void WindowedJitter_IsNullWhenNoSecondHeldTwoReplies()
    {
        // At a one-second interval there is never more than one reply per window. A fabricated
        // figure would be worse than none.
        var sparse = Windowed(10, 1000, _ => 20);

        Assert.Null(sparse.WindowedJitterMs);
        Assert.NotNull(sparse.JitterMs);
    }

    [Fact]
    public void WindowedJitter_IsZeroOnAPerfectlySteadyPath()
    {
        Assert.Equal(0, Windowed(20, 100, _ => 18).WindowedJitterMs);
    }

    private static ProbeStatistics Windowed(int count, int intervalMs, Func<int, double> value) =>
        ProbeStatistics.Calculate("Game", "example.test", Enumerable.Range(0, count)
            .Select(index => new ProbeSample(DateTimeOffset.UnixEpoch.AddMilliseconds(index * intervalMs), value(index)))
            .ToArray());

    [Fact]
    public void Calculate_ReportsPercentilesLossAndJitter()
    {
        var samples = new ProbeSample[]
        {
            new(DateTimeOffset.UnixEpoch, 10),
            new(DateTimeOffset.UnixEpoch.AddSeconds(1), 12),
            new(DateTimeOffset.UnixEpoch.AddSeconds(2), null, "TimedOut"),
            new(DateTimeOffset.UnixEpoch.AddSeconds(3), 20)
        };

        var result = ProbeStatistics.Calculate("Game", "example.test", samples);

        Assert.Equal(4, result.Sent);
        Assert.Equal(3, result.Received);
        Assert.Equal(1, result.Lost);
        Assert.Equal(25, result.LossPercent);
        Assert.Equal(10, result.MinimumMs);
        Assert.Equal(12, result.MedianMs);
        Assert.Equal(14, result.AverageMs);
        Assert.Equal(19.2, result.P95Ms!.Value, 5);
        Assert.Equal(19.84, result.P99Ms!.Value, 5);
        Assert.Equal(20, result.MaximumMs);
        Assert.Equal(5, result.JitterMs);
        Assert.Equal("Mean absolute consecutive RTT difference", result.JitterMethod);
    }

    [Fact]
    public void Calculate_FlagsTimelineSpikes()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(index => new ProbeSample(DateTimeOffset.UnixEpoch.AddSeconds(index), index == 9 ? 50 : 10))
            .ToArray();

        var result = ProbeStatistics.Calculate("Game", "example.test", samples);

        Assert.Single(result.SpikeSamples);
        Assert.Equal(50, result.SpikeSamples[0].RoundTripTimeMs);
    }

    [Fact]
    public void Calculate_NoRepliesPreservesNote()
    {
        var result = ProbeStatistics.Calculate("Gateway", "none", [], "No gateway");

        Assert.Equal("No gateway", result.Summary);
        Assert.Null(result.AverageMs);
    }
}
