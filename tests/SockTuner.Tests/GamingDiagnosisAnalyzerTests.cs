using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class GamingDiagnosisAnalyzerTests
{
    private readonly GamingDiagnosisAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_FindsLocalNetworkInstabilityFirst()
    {
        var findings = _analyzer.Analyze(
            Probe("Gateway", 8, 40, 15, 15),
            Probe("Reference", 20, 35, 5),
            Probe("Game", 35, 50, 6),
            Dns(20));

        Assert.Contains(findings, finding => finding.Scope == DiagnosticScope.Lan && finding.Confidence == DiagnosticConfidence.High);
    }

    [Fact]
    public void Analyze_DoesNotTreatMarginalGatewayAsStableForUpstreamDiagnosis()
    {
        var findings = _analyzer.Analyze(
            Probe("Gateway", 5, 12, 4),
            Probe("Reference", 90, 120, 20),
            Probe("Game", 100, 130, 20),
            Dns(20));

        Assert.Contains(findings, finding => finding.Scope == DiagnosticScope.Lan);
        Assert.DoesNotContain(findings, finding => finding.Title.Contains("after the local gateway", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DistinguishesDistanceFromHealthyReference()
    {
        var findings = _analyzer.Analyze(
            Probe("Gateway", 1, 2, 0.2),
            Probe("Reference", 10, 14, 1),
            Probe("Game", 75, 85, 2),
            Dns(15));

        Assert.Contains(findings, finding => finding.Title.Contains("Distance", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ExplainsSlowDnsWithoutCallingItGameRtt()
    {
        var findings = _analyzer.Analyze(
            Probe("Gateway", 1, 2, 0.2),
            Probe("Reference", 10, 14, 1),
            Probe("Game", 20, 24, 1),
            Dns(400));

        var finding = Assert.Single(findings, item => item.Scope == DiagnosticScope.Dns);
        Assert.Contains("not expect", finding.Action, StringComparison.OrdinalIgnoreCase);
    }

    private static ProbeStatistics Probe(string label, double minimum, double p95, double jitter, double loss = 0) =>
        new(label, label, 20, loss == 0 ? 20 : 19, loss, minimum, minimum + 1, minimum + 2, p95, p95, p95 + 2, jitter, []);

    private static DnsMeasurement Dns(double milliseconds) =>
        new("game.test", TimeSpan.FromMilliseconds(milliseconds), ["192.0.2.1"], null);
}
