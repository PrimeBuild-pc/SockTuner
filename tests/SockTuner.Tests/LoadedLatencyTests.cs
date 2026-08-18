using SockTuner.Models;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Bufferbloat measurement and grading. The probe runs against fakes; the grader is a pure
/// function over the collected result.
/// </summary>
public sealed class LoadedLatencyTests
{
    private static readonly DiagnosticProfile Profile = new("quick", "Quick", 3, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task LoadRunsOnlyBetweenTheIdleAndLoadedMeasurements()
    {
        var phase = 0;
        var loadWasRunning = new List<bool>();
        var running = false;
        var probe = new LoadedLatencyProbe(
            (_, _) =>
            {
                loadWasRunning.Add(running);
                return Task.FromResult(Stats(phase++ == 0 ? 12 : 190));
            },
            async (direction, _, token) =>
            {
                running = true;
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // Stopped by the probe once the loaded measurement finished.
                }

                running = false;
                return new ThroughputResult("http://fake", direction, 4, 50_000_000, TimeSpan.FromSeconds(5), false);
            });

        var result = await probe.RunAsync(TransferDirection.Download, Profile, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new[] { false, true }, loadWasRunning);
        Assert.Equal(178, result.LatencyIncreaseMs);
        Assert.False(running);
    }

    [Fact]
    public async Task CancellationStopsTheLoadRatherThanLeavingItRunning()
    {
        using var cancellation = new CancellationTokenSource();
        var loadStopped = false;
        var idleDone = false;
        var probe = new LoadedLatencyProbe(
            (_, token) =>
            {
                // The idle phase completes; the cancellation lands while the load is already running.
                if (idleDone)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                idleDone = true;
                return Task.FromResult(Stats(10));
            },
            async (direction, _, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    loadStopped = true;
                }

                return new ThroughputResult("http://fake", direction, 1, 0, TimeSpan.Zero, false);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.RunAsync(TransferDirection.Upload, Profile, TimeSpan.Zero, cancellation.Token));

        Assert.True(loadStopped);
    }

    [Theory]
    [InlineData(2, BufferbloatGrade.APlus)]
    [InlineData(20, BufferbloatGrade.A)]
    [InlineData(45, BufferbloatGrade.B)]
    [InlineData(150, BufferbloatGrade.C)]
    [InlineData(300, BufferbloatGrade.D)]
    [InlineData(900, BufferbloatGrade.F)]
    public void GradeFollowsTheLatencyIncreaseScale(double increase, BufferbloatGrade expected) =>
        Assert.Equal(expected, LoadedLatencyAnalyzer.Grade(increase));

    [Fact]
    public void SevereBufferbloatIsOwnedByTheRouter()
    {
        var assessment = LoadedLatencyAnalyzer.Analyze(Result(idle: 14, loaded: 320));

        Assert.Equal(NetworkSegment.RouterOrAccess, assessment.Segment);
        Assert.Equal(RemediationOwner.Router, assessment.Owner);
        Assert.Equal(DiagnosticConfidence.High, assessment.Confidence);
        Assert.Contains(assessment.Supporting, item => item.Contains("grade D", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanLinkIsNotReportedAsABottleneck()
    {
        var assessment = LoadedLatencyAnalyzer.Analyze(Result(idle: 14, loaded: 17));

        Assert.Equal(NetworkSegment.Unknown, assessment.Segment);
        Assert.Contains("No meaningful bufferbloat", assessment.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadThatNeverEstablished_IsInconclusiveRatherThanAGoodGrade()
    {
        var result = Result(idle: 14, loaded: 15) with
        {
            Load = new ThroughputResult("http://fake", TransferDirection.Download, 4, 0, TimeSpan.FromSeconds(5), true)
        };

        var assessment = LoadedLatencyAnalyzer.Analyze(result);

        Assert.Equal(NetworkSegment.Unknown, assessment.Segment);
        Assert.Equal(DiagnosticConfidence.Low, assessment.Confidence);
        Assert.Contains("never established", assessment.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void UnansweredLatencyTarget_IsNotGraded()
    {
        var result = Result(idle: 14, loaded: 15) with
        {
            Loaded = ProbeStatistics.Calculate("Loaded latency", "1.1.1.1", [new ProbeSample(DateTimeOffset.Now, null)])
        };

        Assert.Equal(NetworkSegment.Unknown, LoadedLatencyAnalyzer.Analyze(result).Segment);
    }

    [Fact]
    public void BaselineTakenWhileTheMachineWasBusy_IsReportedInsteadOfGraded()
    {
        // 30 Mbit/s of local traffic against a 50 Mbit/s measured capacity: the "idle" phase was not idle.
        var utilization = new[] { new LinkUtilization("nic-1", "Ethernet", 30_000_000, 0, 1_000_000_000) };

        var assessment = LoadedLatencyAnalyzer.Analyze(Result(idle: 90, loaded: 400), utilization);

        Assert.Contains("not idle", assessment.Title, StringComparison.Ordinal);
        Assert.Equal(NetworkSegment.Lan, assessment.Segment);
        Assert.Equal(RemediationOwner.PresetOrManual, assessment.Owner);
    }

    [Fact]
    public void QuietMachineDoesNotSuppressTheGrade()
    {
        var utilization = new[] { new LinkUtilization("nic-1", "Ethernet", 200_000, 0, 1_000_000_000) };

        Assert.Equal(NetworkSegment.RouterOrAccess, LoadedLatencyAnalyzer.Analyze(Result(idle: 14, loaded: 320), utilization).Segment);
    }

    [Fact]
    public void UnknownLinkSpeedNeverReadsAsSaturation()
    {
        var utilization = new[] { new LinkUtilization("nic-1", "Ethernet", 900_000_000, 0, 0) };

        Assert.Null(LoadedLatencyAnalyzer.LocalSaturation(utilization, null));
    }

    [Fact]
    public void CounterResetIsReportedAsZeroRatherThanGuessed()
    {
        var delta = new AdapterCounterDelta("nic-1", "Ethernet", null, 1_000_000, null, null, null, null);

        var utilization = LinkUtilization.Calculate(delta, TimeSpan.FromSeconds(2), 1_000_000_000);

        Assert.Equal(0, utilization.ReceiveBitsPerSecond);
        Assert.Equal(4_000_000, utilization.SendBitsPerSecond);
        Assert.Equal(0.4, utilization.PeakPercentOfLink, 3);
    }

    private static LoadedLatencyResult Result(double idle, double loaded) => new(
        TransferDirection.Download,
        Stats(idle),
        Stats(loaded),
        new ThroughputResult("http://fake", TransferDirection.Download, 4, 60_000_000, TimeSpan.FromSeconds(10), true));

    private static ProbeStatistics Stats(double milliseconds, string label = "Loaded latency") =>
        ProbeStatistics.Calculate(label, "1.1.1.1", Enumerable.Range(0, 5)
            .Select(index => new ProbeSample(DateTimeOffset.Now.AddSeconds(index), milliseconds))
            .ToArray());
}
