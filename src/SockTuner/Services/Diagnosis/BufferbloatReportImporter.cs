using System.Globalization;
using System.IO;
using System.Text.Json;
using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>Which online test produced the file.</summary>
public enum BufferbloatReportSource
{
    /// <summary>waveform.com/tools/bufferbloat, "Download raw data" — a sectioned CSV.</summary>
    Waveform,

    /// <summary>A bufferbloat test report with per-sample latency and phase names, as JSON.</summary>
    SampledJson
}

/// <summary>
/// An external bufferbloat run, restated in this app's own terms.
/// </summary>
/// <remarks>
/// <para>
/// The statistics here are recomputed from the raw per-sample latencies in the file rather than
/// copied from the tool's summary. Two reasons: the app grades on the median increase and the
/// summaries report a mean, so copying them would put a number on this app's scale that was never
/// measured on it; and recomputing means an imported run and a native run are graded by one piece
/// of code. The tool's own letter is kept in <see cref="ReportedGrade"/> so the two can be shown
/// side by side, and a disagreement is worth seeing rather than hiding.
/// </para>
/// <para>
/// Everything in the file is untrusted input: every field is optional, sizes are capped, and a
/// malformed report produces an error rather than a half-populated result.
/// </para>
/// </remarks>
public sealed record ImportedBufferbloatReport(
    BufferbloatReportSource Source,
    string TestId,
    DateTimeOffset CapturedAt,
    string? ReportedGrade,
    string? Provider,
    LoadedLatencyResult? Download,
    LoadedLatencyResult? Upload,
    IReadOnlyList<string> Notes)
{
    /// <summary>The grade this app derives from the file's own samples, worst of the directions.</summary>
    public BufferbloatGrade? DerivedGrade
    {
        get
        {
            var grades = new[] { Download, Upload }
                .Where(result => result?.LatencyIncreaseMs is not null)
                .Select(result => LoadedLatencyAnalyzer.Grade(result!.LatencyIncreaseMs!.Value))
                .ToArray();
            return grades.Length == 0 ? null : grades.Max();
        }
    }

    public string SourceDisplay => Source switch
    {
        BufferbloatReportSource.Waveform => "Waveform bufferbloat test",
        _ => "Bufferbloat test report (JSON)"
    };
}

/// <summary>
/// Reads a bufferbloat result produced by an online test and turns it into the same
/// <see cref="LoadedLatencyResult"/> a native run produces, so every downstream analyzer — the
/// grade, the router shaping advice, the receive-window advice — works on it unchanged.
/// </summary>
public static class BufferbloatReportImporter
{
    /// <summary>Reports larger than this are refused rather than read into memory.</summary>
    public const long MaximumBytes = 32 * 1024 * 1024;

    private const string WaveformMarker = "WAVEFORM.COM BUFFERBLOAT TEST RESULTS";

    public static ImportedBufferbloatReport Parse(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var trimmed = content.TrimStart('﻿', ' ', '\r', '\n', '\t');

        if (trimmed.StartsWith('{'))
        {
            return ParseJson(trimmed);
        }

        if (content.Contains(WaveformMarker, StringComparison.OrdinalIgnoreCase))
        {
            return ParseWaveform(content);
        }

        throw new InvalidDataException(
            "Unrecognised bufferbloat report. Expected the Waveform CSV export or a JSON report with latency samples.");
    }

    // ---- Waveform CSV ----------------------------------------------------------------------

    /// <summary>
    /// The export is a sequence of <c>====== SECTION ======</c> headers. Key/value sections hold
    /// <c>Name,Value</c> lines; the measurement sections hold one latency per line. Section names
    /// repeat in the file, so the parser keys on the first occurrence and ignores duplicates.
    /// </summary>
    private static ImportedBufferbloatReport ParseWaveform(string content)
    {
        var sections = SplitWaveformSections(content);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, lines) in sections)
        {
            foreach (var line in lines)
            {
                var separator = line.IndexOf(',');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                if (key.Length > 0 && !values.ContainsKey(key))
                {
                    values[key] = line[(separator + 1)..].Trim();
                }
            }
        }

        var idle = Latencies(sections, "UNLOADED LATENCY MEASUREMENTS");
        var download = Latencies(sections, "DOWNLOAD STAGE LATENCY MEASUREMENTS");
        var upload = Latencies(sections, "UPLOAD STAGE LATENCY MEASUREMENTS");
        if (idle.Count == 0)
        {
            throw new InvalidDataException(
                "The Waveform export has no unloaded latency measurements, so there is no idle baseline to compare against.");
        }

        var captured = values.TryGetValue("Unix Time", out var unix)
            && long.TryParse(unix, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.Now;

        var notes = new List<string>();
        var idleStats = Statistics("Idle", "waveform.com", idle);
        return new ImportedBufferbloatReport(
            BufferbloatReportSource.Waveform,
            values.GetValueOrDefault("Test ID", "unknown"),
            captured,
            values.GetValueOrDefault("Bufferbloat Grade"),
            Provider: null,
            Direction(TransferDirection.Download, idleStats, download, Mbps(values, "Download speed (Mbps)"), notes),
            Direction(TransferDirection.Upload, idleStats, upload, Mbps(values, "Upload speed (Mbps)"), notes),
            notes);
    }

    private static List<(string Name, List<string> Lines)> SplitWaveformSections(string content)
    {
        var sections = new List<(string, List<string>)>();
        var current = new List<string>();
        var name = "preamble";
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("=====", StringComparison.Ordinal))
            {
                sections.Add((name, current));
                name = line.Trim('=', ' ', '\t');
                current = [];
                continue;
            }

            if (line.Length > 0) current.Add(line);
        }

        sections.Add((name, current));
        return sections;
    }

    private static List<double> Latencies(List<(string Name, List<string> Lines)> sections, string prefix)
    {
        var values = new List<double>();
        foreach (var (name, lines) in sections)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in lines)
            {
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value is > 0 and < 60_000)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static double? Mbps(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps)
        && mbps > 0
            ? mbps
            : null;

    // ---- Sampled JSON ----------------------------------------------------------------------

    /// <summary>
    /// A report carrying every latency sample with the phase it was taken in. The phase names are
    /// what makes it usable: <c>baseline</c> is the idle reference and <c>download</c>/<c>upload</c>
    /// are the saturated ones. Warm-up and recovery phases are deliberately ignored — the queue is
    /// still filling during a warm-up, so including it would flatter the result.
    /// </summary>
    private static ImportedBufferbloatReport ParseJson(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A bufferbloat report must be a JSON object.");
        }

        if (!root.TryGetProperty("latencySamples", out var samples) || samples.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "This JSON has no latencySamples array, so there are no measurements to grade.");
        }

        var byPhase = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object) continue;
            if (ReadString(sample, "phase") is not { } phase) continue;

            // A sample flagged as lost has no round trip to add; it is counted as loss instead.
            var lost = sample.TryGetProperty("loss", out var loss) && loss.ValueKind == JsonValueKind.True;
            var rtt = ReadDouble(sample, "rttMs");
            if (!byPhase.TryGetValue(phase, out var list)) byPhase[phase] = list = [];
            if (!lost && rtt is > 0 and < 60_000) list.Add(rtt.Value);
        }

        if (!byPhase.TryGetValue("baseline", out var baseline) || baseline.Count == 0)
        {
            throw new InvalidDataException(
                "The report has no baseline phase, so there is no idle latency to compare the loaded phases against.");
        }

        var summary = root.TryGetProperty("summary", out var element) && element.ValueKind == JsonValueKind.Object
            ? element
            : default;

        var notes = new List<string>();
        if (byPhase.TryGetValue("bidirectional", out var both) && both.Count > 0)
        {
            var idleMedian = Median(baseline);
            var bothMedian = Median(both).ToString("0.0", CultureInfo.InvariantCulture);
            var idleText = idleMedian.ToString("0.0", CultureInfo.InvariantCulture);
            notes.Add(
                $"The report also measured both directions at once: median {bothMedian} ms against an idle "
                + $"{idleText} ms. That phase is not graded here, because the app's scale is per direction, but a "
                + "bidirectional figure much worse than either single direction points at the upstream queue.");
        }

        var idleStats = Statistics("Idle", ReadString(summary, "edgeLocation") ?? "report", baseline);
        return new ImportedBufferbloatReport(
            BufferbloatReportSource.SampledJson,
            ReadString(root, "sessionId") ?? "unknown",
            ReadTimestamp(root),
            ReadString(summary, "totalGrade"),
            ReadProvider(root),
            Direction(TransferDirection.Download, idleStats, byPhase.GetValueOrDefault("download") ?? [],
                ReadDouble(summary, "downloadThroughputMbps"), notes),
            Direction(TransferDirection.Upload, idleStats, byPhase.GetValueOrDefault("upload") ?? [],
                ReadDouble(summary, "uploadThroughputMbps"), notes),
            notes);
    }

    private static string? ReadProvider(JsonElement root) =>
        root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("sessionMeta", out var session) && session.ValueKind == JsonValueKind.Object
            ? ReadString(session, "asOrganization")
            : null;

    private static DateTimeOffset ReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && ReadString(metadata, "generatedAt") is { } generated
            && DateTimeOffset.TryParse(generated, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return ReadDouble(root, "startedAt") is { } started and > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)started)
            : DateTimeOffset.Now;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    // ---- shared -----------------------------------------------------------------------------

    /// <summary>
    /// One direction, or nothing when the file did not measure it. A direction with latency but no
    /// throughput still grades — the queue growth is the measurement — and the missing rate only
    /// costs the shaping advice, which says so rather than inventing a number.
    /// </summary>
    private static LoadedLatencyResult? Direction(
        TransferDirection direction,
        ProbeStatistics idle,
        IReadOnlyList<double> loaded,
        double? mbps,
        List<string> notes)
    {
        if (loaded.Count == 0)
        {
            notes.Add($"No {direction.ToString().ToLowerInvariant()} phase in this report; that direction is not graded.");
            return null;
        }

        if (mbps is null)
        {
            notes.Add(
                $"The {direction.ToString().ToLowerInvariant()} rate is missing from the report, so shaping advice "
                + "cannot be computed for it: the shaped rate has to sit just below a rate that was actually reached.");
        }

        var stats = Statistics($"Loaded {direction}", idle.Target, loaded);
        return new LoadedLatencyResult(direction, idle, stats, Throughput(direction, mbps, loaded.Count));
    }

    /// <summary>
    /// A throughput record standing in for the transfer the online test ran. The byte count is
    /// derived from the reported rate over an assumed one-second window purely so that
    /// <see cref="ThroughputResult.BitsPerSecond"/> reports the rate the file stated; nothing reads
    /// the byte count itself.
    /// </summary>
    private static ThroughputResult Throughput(TransferDirection direction, double? mbps, int samples)
    {
        var duration = TimeSpan.FromSeconds(1);
        var bytes = mbps is { } rate ? (long)(rate * 1_000_000d / 8d) : 0L;
        return new ThroughputResult(
            "imported report", direction, Streams: 1, bytes, duration, Completed: true,
            Error: mbps is null ? $"No {direction.ToString().ToLowerInvariant()} rate in the report" : null,
            FailureKind: null);
    }

    private static ProbeStatistics Statistics(string label, string target, IReadOnlyList<double> latencies)
    {
        var start = DateTimeOffset.Now;
        var samples = latencies
            .Select((value, index) => new ProbeSample(start.AddMilliseconds(index), value))
            .ToArray();
        return ProbeStatistics.Calculate(label, target, samples, "Imported from an external bufferbloat report.");
    }

    private static double Median(List<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
    }

    public static ImportedBufferbloatReport Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes)
        {
            throw new InvalidDataException(
                $"The report is {info.Length / (1024 * 1024)} MB; the limit is {MaximumBytes / (1024 * 1024)} MB.");
        }

        return Parse(File.ReadAllText(path));
    }
}
