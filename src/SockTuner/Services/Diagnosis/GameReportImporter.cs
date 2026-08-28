using System.IO;
using System.Text.Json;
using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Reads a capture-derived game report and turns it into findings SockTuner can act on.
/// </summary>
/// <remarks>
/// <para>
/// Imported data is treated as untrusted input: it is parsed defensively, every field is optional,
/// and nothing in it is executed or used to build a path. A report that is malformed produces an
/// error, never a partially-applied state.
/// </para>
/// <para>
/// The value of an external capture is the one thing SockTuner cannot obtain by itself: which
/// server the game actually used, and how its packets were spaced. Everything else it can measure
/// directly, so nothing here is taken on faith — the report supplies the target, and SockTuner then
/// measures that target itself.
/// </para>
/// </remarks>
public static class GameReportImporter
{
    /// <summary>Reports larger than this are refused rather than loaded into memory.</summary>
    public const long MaximumBytes = 64 * 1024 * 1024;

    public static GameFlowReport Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A game report must be a JSON object.");
        }

        var flow = root.TryGetProperty("Flow", out var flowElement) && flowElement.ValueKind == JsonValueKind.Object
            ? ReadFlow(flowElement)
            : null;

        return new GameFlowReport(
            ReadString(root, "Game") ?? "Unknown game",
            ReadTimestamp(root),
            ReadString(root, "RemoteIP"),
            ReadString(root, "RemoteHost"),
            ReadString(root, "RemotePort"),
            ReadString(root, "RegionHint"),
            root.TryGetProperty("GameProfile", out var profile) && profile.ValueKind == JsonValueKind.Object
                ? ReadDouble(profile, "ExpectedTickMs")
                : null,
            flow,
            ReadScores(root));
    }

    /// <summary>
    /// Judges the flow against the game's own tick rate rather than a fixed threshold: 8 ms of
    /// jitter is nothing on a 50 ms tick and is most of the budget on a 20 ms one.
    /// </summary>
    public static IReadOnlyList<HealthFinding> Analyze(GameFlowReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var findings = new List<HealthFinding>();

        if (report.Flow is not { } flow)
        {
            return
            [
                new HealthFinding(
                    $"{report.Game}: the report carries no flow statistics",
                    "Nothing to judge.",
                    "Re-capture with the analyzer, or measure the endpoint directly from the diagnostics tab.",
                    "Gaming diagnostics",
                    DiagnosticConfidence.Low,
                    ChangeRisk.Low)
            ];
        }

        var tick = report.ExpectedTickMs;

        // The same thresholds the live measurement is judged against, so a capture and a probe
        // cannot disagree about the same line: half a tick is comfortable, one tick is the edge.
        var game = tick is { } expected and > 0 ? GameProfile.FromTickIntervalMs(report.Game, expected) : null;

        if (game is not null && flow.AverageJitterMs > game.GoodJitterMs)
        {
            var past = flow.AverageJitterMs > game.PlayableJitterMs;
            findings.Add(new HealthFinding(
                $"{report.Game}: jitter is {(past ? "past" : "at")} what the game's own tick allows",
                $"Average jitter {flow.AverageJitterMs:0.0} ms against a {game.TickIntervalMs:0.#} ms tick — "
                + $"{game.GoodJitterMs:0.0} ms is comfortable, {game.PlayableJitterMs:0.0} ms is the edge.",
                "Packets arriving unevenly relative to the tick is what is felt as inconsistent hit registration. "
                + "Measure the same endpoint under load: if jitter only appears under load, the queue is the cause "
                + "and it is the router's to fix.",
                "Throughput & bufferbloat",
                past ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                past ? ChangeRisk.High : ChangeRisk.Medium));
        }

        if (tick is { } tickMs && flow.MaximumDeltaMs > tickMs * 5)
        {
            findings.Add(new HealthFinding(
                $"{report.Game}: the flow stalled for {flow.MaximumDeltaMs:0} ms at its worst",
                $"Largest gap between packets was {flow.MaximumDeltaMs:0} ms, against a {tickMs:0.#} ms tick.",
                "A gap that long is a freeze rather than lag. Run the continuous monitor against this endpoint to see "
                + "whether it recurs, and check the stability episodes it reports.",
                "Gaming diagnostics",
                DiagnosticConfidence.Medium,
                ChangeRisk.High));
        }

        if (flow.SpikeRatio > 0.02)
        {
            findings.Add(new HealthFinding(
                $"{report.Game}: {flow.SpikeRatio:P1} of packets arrived late",
                $"Spike ratio {flow.SpikeRatio:0.###} over {flow.PacketCount} packets.",
                "Occasional late packets with otherwise clean spacing usually point at something sharing the link "
                + "rather than at the path itself. The health check lists what is bound to this adapter.",
                "Network bindings",
                DiagnosticConfidence.Low,
                ChangeRisk.Medium));
        }

        if (findings.Count == 0)
        {
            findings.Add(new HealthFinding(
                $"{report.Game}: the captured flow looks clean",
                flow.Summary,
                "Nothing in this capture points at the network. If the game still felt wrong, the cause is more likely "
                + "on the machine or at the server than on the path.",
                "Dashboard",
                DiagnosticConfidence.Medium,
                ChangeRisk.Low));
        }

        return findings;
    }

    private static GameFlowStatistics ReadFlow(JsonElement flow) => new(
        (int)(ReadDouble(flow, "PacketCount") ?? 0),
        ReadDouble(flow, "DurationSec") ?? 0,
        ReadDouble(flow, "PktPerSec") ?? 0,
        ReadDouble(flow, "AvgDeltaMs") ?? 0,
        ReadDouble(flow, "MaxDeltaMs") ?? 0,
        ReadDouble(flow, "AvgJitterMs") ?? 0,
        ReadDouble(flow, "MaxJitterMs") ?? 0,
        ReadDouble(flow, "BurstRatio") ?? 0,
        ReadDouble(flow, "SpikeRatio") ?? 0);

    private static IReadOnlyDictionary<string, string> ReadScores(JsonElement root)
    {
        if (!root.TryGetProperty("Scores", out var scores) || scores.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var score in scores.EnumerateObject())
        {
            if (score.Value.ValueKind == JsonValueKind.String)
            {
                result[score.Name] = score.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateTimeOffset ReadTimestamp(JsonElement root) =>
        ReadString(root, "Timestamp") is { } text
        && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
