using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class ProbeStatisticsTests
{
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
