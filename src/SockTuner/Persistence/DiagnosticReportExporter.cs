using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

namespace SockTuner.Persistence;

public static class DiagnosticReportExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeJson(GamingDiagnosticReport report, bool redact = false) => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        toolVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown",
        exportedAt = DateTimeOffset.Now,
        redacted = redact,
        report = redact ? Redact(report) : report
    }, Options);

    public static string SerializeHtml(GamingDiagnosticReport report, bool redact = false)
    {
        var safe = redact ? Redact(report) : report;
        var title = HtmlEncoder.Default.Encode($"SockTuner diagnostic — {safe.RequestedTarget}");
        var rows = new[] { safe.Gateway, safe.Reference, safe.GameTarget }
            .Concat(safe.FirstPublicBoundaryProbe is null ? [] : [safe.FirstPublicBoundaryProbe])
            .Select(item => $"<tr><td>{H(item.Label)}</td><td>{item.Sent}</td><td>{item.Received}</td><td>{item.Lost}</td><td>{F(item.MinimumMs)}</td><td>{F(item.MedianMs)}</td><td>{F(item.AverageMs)}</td><td>{F(item.P95Ms)}</td><td>{F(item.P99Ms)}</td><td>{F(item.MaximumMs)}</td><td>{F(item.JitterMs)}</td></tr>");
        var findings = safe.Findings.Select(item => $"<tr><td>{H(item.Scope.ToString())}</td><td>{H(item.Confidence.ToString())}</td><td>{H(item.Title)}</td><td>{H(item.Evidence)}</td><td>{H(item.Action)}</td></tr>");
        var json = H(SerializeJson(report, redact));
        return $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>{{title}}</title>
<style>body{font:14px Segoe UI,Arial;background:#111;color:#f2f2f2;margin:32px}h1,h2{font-weight:500}table{border-collapse:collapse;width:100%;margin:12px 0 28px}th,td{border:1px solid #444;padding:8px;text-align:left;vertical-align:top}th{background:#2b2b2b}pre{white-space:pre-wrap;background:#1f1f1f;border:1px solid #444;padding:16px}.muted{color:#b3b3b3}</style></head>
<body><h1>{{title}}</h1><p class="muted">Started {{H(safe.StartedAt.ToString("O"))}} · Profile {{H(safe.Profile.DisplayName)}} · Load {{H(safe.LoadCondition.ToString())}} · Duration {{safe.Duration.TotalSeconds:0.0}}s · Redacted {{redact}}</p>
<h2>Probe statistics</h2><table><thead><tr><th>Target</th><th>Sent</th><th>Received</th><th>Lost</th><th>Min</th><th>Median</th><th>Average</th><th>P95</th><th>P99</th><th>Max</th><th>Jitter</th></tr></thead><tbody>{{string.Join("", rows)}}</tbody></table>
<h2>Findings</h2><table><thead><tr><th>Scope</th><th>Confidence</th><th>Finding</th><th>Evidence</th><th>Action</th></tr></thead><tbody>{{string.Join("", findings)}}</tbody></table>
<h2>Raw report</h2><pre>{{json}}</pre></body></html>
""";
    }

    internal static GamingDiagnosticReport Redact(GamingDiagnosticReport report)
    {
        ProbeStatistics Probe(ProbeStatistics value) => value with
        {
            Target = "[redacted]",
            Samples = value.Samples.Select(sample => sample with { Error = sample.Error is null ? null : "[detail redacted]" }).ToArray()
        };
        return report with
        {
            RequestedTarget = "[redacted]",
            Gateway = Probe(report.Gateway),
            Reference = Probe(report.Reference),
            GameTarget = Probe(report.GameTarget),
            FirstPublicBoundaryProbe = report.FirstPublicBoundaryProbe is null ? null : Probe(report.FirstPublicBoundaryProbe),
            Dns = report.Dns with { Host = "[redacted]", Addresses = report.Dns.Addresses.Select(_ => "[redacted]").ToArray(), Error = report.Dns.Error is null ? null : "[detail redacted]" },
            Connection = report.Connection is null ? null : report.Connection with { Host = "[redacted]", Error = report.Connection.Error is null ? null : "[detail redacted]" },
            RouteSamples = report.RouteSamples?.Select(route => route with
            {
                Hops = route.Hops.Select(hop => hop with { Address = "[redacted]" }).ToArray(),
                Error = route.Error is null ? null : "[detail redacted]"
            }).ToArray(),
            FirstPublicBoundary = report.FirstPublicBoundary is null ? null : "[redacted]",
            PathMtu = report.PathMtu is null ? null : report.PathMtu with { Detail = "[detail redacted]" },
            Findings = report.Findings.Select(finding => finding with { Evidence = "[detail redacted]" }).ToArray(),
            CounterDeltas = report.CounterDeltas?.Select(delta => delta with { AdapterId = "[redacted]", AdapterName = "Adapter" }).ToArray()
        };
    }

    private static string H(string value) => HtmlEncoder.Default.Encode(value);
    private static string F(double? value) => value is null ? "—" : $"{value:0.0} ms";
}
