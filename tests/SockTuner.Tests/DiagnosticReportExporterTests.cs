using System.Text.Json;
using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Tests;

public sealed class DiagnosticReportExporterTests
{
    [Fact]
    public void SerializeJson_WritesVersionedEnvelopeWithRawSamples()
    {
        using var document = JsonDocument.Parse(DiagnosticReportExporter.SerializeJson(Report()));

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("redacted").GetBoolean());
        Assert.Equal(10, document.RootElement.GetProperty("report").GetProperty("gameTarget").GetProperty("samples")[0].GetProperty("roundTripTimeMs").GetDouble());
    }

    [Fact]
    public void SerializeHtml_IsSelfContainedAndEscapesContent()
    {
        var html = DiagnosticReportExporter.SerializeHtml(Report());

        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" href=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedExports_RemoveTargetsAddressesAndAdapterIdentity()
    {
        var json = DiagnosticReportExporter.SerializeJson(Report(), redact: true);
        var html = DiagnosticReportExporter.SerializeHtml(Report(), redact: true);

        foreach (var secret in new[] { "secret.example", "203.0.113.8", "SECRET-ADAPTER", "PRIVATE-ERROR" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static GamingDiagnosticReport Report()
    {
        var sample = new ProbeSample(DateTimeOffset.UnixEpoch, 10);
        var probe = ProbeStatistics.Calculate("Game", "secret.example", [sample]);
        return new GamingDiagnosticReport(
            "secret.example", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
            new DiagnosticProfile("quick", "Quick", 12, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            probe with { Label = "Gateway" }, probe with { Label = "Reference" }, probe,
            new DnsMeasurement("secret.example", TimeSpan.FromMilliseconds(2), ["203.0.113.8"], null),
            new ConnectionMeasurement("secret.example", 443, TimeSpan.FromMilliseconds(3), null),
            [new DiagnosticFinding(DiagnosticScope.General, DiagnosticConfidence.Low, "<script>", "PRIVATE-ERROR evidence", "Action")],
            [new RouteSample(DateTimeOffset.UnixEpoch, [new RouteHop(1, "203.0.113.8", 1, "TtlExpired")], null)],
            "203.0.113.8", new PathMtuResult(PathMtuState.Discovered, 1500, "PRIVATE-ERROR mtu"),
            [new AdapterCounterDelta("SECRET-ADAPTER", "SECRET-ADAPTER", 1, 1, 0, 0, 0, 0)]);
    }
}
