using SockTuner.Views;

namespace SockTuner.Tests;

public sealed class SparklineGeometryTests
{
    [Fact]
    public void Build_SplitsMissingSamplesWithoutClosingTheGap()
    {
        var segments = SparklineGeometry.Build([10, 20, null, 30, 40]);

        Assert.Equal(2, segments.Count);
        Assert.Equal(2, segments[0].Count);
        Assert.Equal(2, segments[1].Count);
        Assert.True(segments[0][0].Y > segments[1][1].Y);
    }

    [Fact]
    public void Build_CentresAConstantSeries()
    {
        var segment = Assert.Single(SparklineGeometry.Build([5, 5, 5]));

        Assert.All(segment, point => Assert.Equal(60, point.Y));
    }
}
