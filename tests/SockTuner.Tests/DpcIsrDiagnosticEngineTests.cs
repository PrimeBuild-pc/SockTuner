using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class DpcIsrDiagnosticEngineTests
{
    [Fact]
    public void Analyze_NamesTheWorstCombinedDriverAsTheCulprit()
    {
        var capture = new DpcIsrCaptureResult(
            [
                new DpcIsrDriverSample("quiet.sys", 1, 1, 2),
                new DpcIsrDriverSample("loud.sys", 40, 10, 8)
            ],
            TimeSpan.FromSeconds(10), null);

        var finding = DpcIsrDiagnosticEngine.Analyze(capture, jitterMsInSameWindow: 15);

        Assert.Equal("loud.sys", finding.CulpritDriver);
        Assert.Equal(["loud.sys", "quiet.sys"], finding.Ranking.Select(sample => sample.DriverName));
        Assert.Equal(15, finding.JitterMsInSameWindow);
    }

    [Fact]
    public void Analyze_SharePercentIsDerivedFromTheActualCaptureWindowNotAFixedThreshold()
    {
        var capture = new DpcIsrCaptureResult([new DpcIsrDriverSample("x.sys", 500, 0, 10)], TimeSpan.FromSeconds(10), null);

        var finding = DpcIsrDiagnosticEngine.Analyze(capture, null);

        // 500ms of a 10 000ms window is 5%.
        Assert.Equal(5, finding.CulpritSharePercent, precision: 3);
    }

    [Fact]
    public void Analyze_NoDriversReportsNothingRatherThanAFakeCulprit()
    {
        var finding = DpcIsrDiagnosticEngine.Analyze(new DpcIsrCaptureResult([], TimeSpan.FromSeconds(10), null), null);

        Assert.Null(finding.CulpritDriver);
        Assert.Empty(finding.Ranking);
    }

    [Fact]
    public void Analyze_ACaptureErrorIsCarriedAsTheReasonRatherThanThrown()
    {
        var finding = DpcIsrDiagnosticEngine.Analyze(
            new DpcIsrCaptureResult([], TimeSpan.Zero, "Administrator rights are required."), null);

        Assert.Null(finding.CulpritDriver);
        Assert.Equal("Administrator rights are required.", finding.Reason);
    }
}
