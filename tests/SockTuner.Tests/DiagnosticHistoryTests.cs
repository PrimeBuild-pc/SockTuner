using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class DiagnosticHistoryTests
{
    [Fact]
    public void Store_BoundsLoadsAndDeletesEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-History-{Guid.NewGuid():N}");
        try
        {
            var store = new DiagnosticHistoryStore(directory);
            store.Save(Report("one", 10), 2);
            Thread.Sleep(2);
            store.Save(Report("two", 20), 2);
            Thread.Sleep(2);
            var newest = store.Save(Report("three", 30), 2);
            File.WriteAllText(Path.Combine(directory, "invalid.json"), "invalid");
            File.WriteAllText(Path.Combine(directory, $"{Guid.NewGuid():N}.json"), "{}");
            var validPath = Directory.GetFiles(directory, $"{newest.Id:N}.json").Single();
            File.WriteAllText(Path.Combine(directory, $"{Guid.NewGuid():N}.json"), File.ReadAllText(validPath));

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
            Assert.Equal("three", loaded[0].Target);
            store.Delete(newest.Id);
            Assert.Single(store.Load());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Compare_RequiresIdenticalTargetAndEveryProfileParameter()
    {
        var baseline = Report("game", 20);
        var after = Report("game", 15);

        var comparison = DiagnosticComparisonService.Compare(baseline, after);
        var mismatch = DiagnosticComparisonService.Compare(baseline, after with
        {
            Profile = after.Profile with { Interval = TimeSpan.FromSeconds(2) }
        });
        var portMismatch = DiagnosticComparisonService.Compare(baseline, after with
        {
            Connection = new ConnectionMeasurement("game", 443, TimeSpan.FromMilliseconds(10), null)
        });

        Assert.True(comparison.Comparable);
        Assert.Contains(comparison.Metrics, metric => metric.Metric == "Game average ms" && metric.Delta == -5);
        Assert.False(mismatch.Comparable);
        Assert.False(portMismatch.Comparable);
    }

    private static GamingDiagnosticReport Report(string target, double rtt)
    {
        var probe = ProbeStatistics.Calculate("Game", target, [new ProbeSample(DateTimeOffset.UnixEpoch, rtt)]);
        return new(target, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
            new DiagnosticProfile("quick", "Quick", 12, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            probe with { Label = "Gateway" }, probe with { Label = "Reference" }, probe,
            new DnsMeasurement(target, TimeSpan.Zero, [], null), null, []);
    }
}
