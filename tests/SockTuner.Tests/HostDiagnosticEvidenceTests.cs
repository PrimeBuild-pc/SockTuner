using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class HostDiagnosticEvidenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");

    private static DpcIsrWindow Window(int index, double jitter, string driver = "audio.sys")
    {
        var start = Start.AddSeconds(index);
        var samples = new[] { 10d, 10 + jitter, 10, 10 + jitter }
            .Select((value, i) => new ProbeSample(start.AddMilliseconds(i * 100), value)).ToArray();
        return new(start, start.AddSeconds(1), [new(driver, 5, 5, 4)],
            ProbeStatistics.Calculate("Concurrent", "127.0.0.1", samples));
    }

    private static DpcIsrFinding Analyze(DpcIsrWindow[] windows, double? baseline = 1) =>
        DpcIsrDiagnosticEngine.Analyze(new([new("audio.sys", 25, 25, 20)], TimeSpan.FromSeconds(5), null, windows), baseline);

    [Fact]
    public void RepeatedConcurrentDominanceAndHigherJitterEnablesEvidence()
    {
        var finding = Analyze(Enumerable.Range(0, 5).Select(i => Window(i, 8)).ToArray());
        Assert.True(finding.HasRepeatedEvidence);
        Assert.Equal(8, finding.JitterMsInSameWindow);
        Assert.Contains("not proof", finding.Reason);
    }

    [Fact]
    public void OneSpikeCannotEnableEvidence()
    {
        Assert.False(Analyze(Enumerable.Range(0, 5).Select(i => Window(i, i == 2 ? 8 : 1)).ToArray()).HasRepeatedEvidence);
    }

    [Fact]
    public void DifferentDriversWinningEachWindowDoesNotEnableEvidence()
    {
        Assert.False(Analyze(Enumerable.Range(0, 5).Select(i => Window(i, 8, i == 0 ? "audio.sys" : $"other{i}.sys")).ToArray()).HasRepeatedEvidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8d)]
    [InlineData(7.5)]
    public void MissingBaselineOrChangesWithinIcmpResolutionAreInconclusive(double? baseline)
    {
        Assert.False(Analyze(Enumerable.Range(0, 5).Select(i => Window(i, 8)).ToArray(), baseline).HasRepeatedEvidence);
    }

    [Fact]
    public void MissingRepliesAndNonOverlappingSamplesAreInconclusive()
    {
        var windows = Enumerable.Range(0, 5).Select(i => Window(i, 8)).ToArray();
        var missing = windows.Select(window => window with { Latency = window.Latency with { Received = 0 } }).ToArray();
        Assert.False(Analyze(missing).HasRepeatedEvidence);
        var sequential = windows.Select(window => window with
        {
            Latency = window.Latency with { Samples = window.Latency.Samples.Select(sample =>
                sample with { Timestamp = sample.Timestamp.AddSeconds(-10) }).ToArray() }
        }).ToArray();
        Assert.False(Analyze(sequential).HasRepeatedEvidence);
    }

    [Fact]
    public void EtwAggregationUsesExclusiveWindowEndsAndIgnoresOutsideEvents()
    {
        var windows = new[] { Window(0, 1), Window(1, 1) };
        var sample = new DpcIsrDriverSample("driver.sys", 1, 2, 1);
        var capture = EtwDpcIsrCapture.AggregateWindows(
            [(Start.AddTicks(-1), sample), (Start, sample), (Start.AddSeconds(1), sample), (Start.AddSeconds(2), sample)], windows);
        Assert.Equal(2, Assert.Single(capture.Drivers).EventCount);
        Assert.All(capture.Windows!, window => Assert.Equal(1, Assert.Single(window.Drivers).EventCount));
        Assert.Equal(TimeSpan.FromSeconds(2), capture.Duration);
    }

    private static DriverStorePackage Package(int id, int version, string provider = "Vendor", string deviceClass = "MEDIA") =>
        new($"oem{id}.inf", "audio.inf", provider, deviceClass, $"01/01/2026 {version}.0.0.0");

    [Fact]
    public void CleanupProtectsEveryBoundVersionAndNewestEvenWhenNewestIsNotActive()
    {
        var packages = new[] { Package(1, 1), Package(2, 2), Package(3, 3), Package(4, 4) };
        var duplicates = DriverStoreDuplicateFinder.FindOutdatedDuplicates(packages,
            new HashSet<string> { "OEM1.INF", "oem3.inf" });
        var duplicate = Assert.Single(duplicates);
        Assert.Equal("oem2.inf", duplicate.Outdated.PublishedName);
        Assert.Equal("oem4.inf", duplicate.Retained.PublishedName);
    }

    [Fact]
    public void CleanupCannotCrossProviderOrClassAndDoesNotGuessUnknownVersions()
    {
        var packages = new[] { Package(1, 1), Package(2, 2, "Other vendor"), Package(3, 3, deviceClass: "Display") };
        Assert.Empty(DriverStoreDuplicateFinder.FindOutdatedDuplicates(packages, new HashSet<string> { "oem99.inf" }));
        var ambiguous = new[] { Package(1, 1), Package(2, 2), Package(3, 3) with { DriverVersionRaw = "unknown" } };
        Assert.Empty(DriverStoreDuplicateFinder.FindOutdatedDuplicates(ambiguous, new HashSet<string> { "oem99.inf" }));
    }

    [Fact]
    public void MissingBindingsNeverEnableCleanup()
    {
        var packages = new[] { Package(1, 1), Package(2, 2) };
        Assert.Empty(DriverStoreDuplicateFinder.FindOutdatedDuplicates(packages, null));
        Assert.Empty(DriverStoreDuplicateFinder.FindOutdatedDuplicates(packages, new HashSet<string>()));
    }

    [Fact]
    public void CleanupDoesNotProposeEqualVersionsOrNonOemTargets()
    {
        var packages = new[] { Package(1, 1), Package(2, 1), Package(3, 2) with { PublishedName = "arbitrary.inf" } };
        Assert.Empty(DriverStoreDuplicateFinder.FindOutdatedDuplicates(packages.Take(2).ToArray(), new HashSet<string> { "oem99.inf" }));
        var invalid = new[] { Package(1, 1) with { PublishedName = "arbitrary.inf" }, Package(2, 2) };
        Assert.Empty(DriverStoreDuplicateFinder.FindOutdatedDuplicates(invalid, new HashSet<string> { "oem99.inf" }));
    }
}
