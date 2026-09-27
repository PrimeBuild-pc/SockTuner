using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

namespace SockTuner.Persistence;

public sealed record DiagnosticHistoryEntry(Guid Id, DateTimeOffset SavedAt, GamingDiagnosticReport Report)
{
    public string Target => Report.RequestedTarget;
    public string Profile => Report.Profile.DisplayName;
    public string GameSummary => Report.GameTarget.Summary;
}

public sealed class DiagnosticHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _directory;

    public DiagnosticHistoryStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeBuild", "SockTuner", "History"))
    { }

    internal DiagnosticHistoryStore(string directory) => _directory = directory;

    public DiagnosticHistoryEntry Save(GamingDiagnosticReport report, int maximumEntries = 20)
    {
        maximumEntries = Math.Clamp(maximumEntries, 1, 200);
        if (!IsValidReport(report)) throw new ArgumentException("History report metadata is incomplete or invalid.", nameof(report));
        Directory.CreateDirectory(_directory);
        var entry = new DiagnosticHistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, MinimizeWifiHistory(report));
        File.WriteAllText(PathFor(entry.Id), JsonSerializer.Serialize(entry, Options));
        foreach (var old in Load().Skip(maximumEntries)) File.Delete(PathFor(old.Id));
        return entry;
    }

    public IReadOnlyList<DiagnosticHistoryEntry> Load()
    {
        if (!Directory.Exists(_directory)) return [];
        string[] paths;
        try
        {
            paths = Directory.GetFiles(_directory, "*.json");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return paths.Select(TryLoad)
            .Where(entry => entry is not null)
            .Cast<DiagnosticHistoryEntry>()
            .OrderByDescending(entry => entry.SavedAt)
            .ToArray();
    }

    private DiagnosticHistoryEntry? TryLoad(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<DiagnosticHistoryEntry>(File.ReadAllText(path), Options);
            var fileIdValid = Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var fileId)
                && entry is not null && entry.Id == fileId;
            if (!fileIdValid || entry!.Id == Guid.Empty || !IsValidReport(entry.Report))
            {
                File.Delete(path);
                return null;
            }
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            try { File.Delete(path); } catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    private static GamingDiagnosticReport MinimizeWifiHistory(GamingDiagnosticReport report)
    {
        if (report.Wifi is not { } wifiReport) return report;

        static WifiBssInfo Bss(WifiBssInfo value) => value with
        {
            Ssid = "[not retained in history]",
            Bssid = "[not retained in history]"
        };

        var wifiFindingKeys = wifiReport.Findings
            .Select(finding => (finding.Title, finding.Evidence))
            .ToHashSet();
        var radio = wifiReport.Radio;
        var wifi = wifiReport with
        {
            Radio = radio is null ? null : radio with
            {
                InterfaceId = "[not retained in history]",
                Description = "Wi-Fi adapter",
                Ssid = "[not retained in history]",
                Bssid = "[not retained in history]",
                ConnectedBss = radio.ConnectedBss is null ? null : Bss(radio.ConnectedBss),
                Neighbours = []
            },
            Findings = wifiReport.Findings.Select(finding =>
                finding with { Evidence = "[Wi-Fi detail not retained in history]" }).ToArray(),
            Samples = wifiReport.Samples.Select(sample => sample with
            {
                InterfaceId = "[not retained in history]",
                Bssid = "[not retained in history]",
                Error = sample.Error is null ? null : "[detail not retained in history]"
            }).ToArray()
        };

        return report with
        {
            Wifi = wifi,
            Findings = report.Findings.Select(finding => wifiFindingKeys.Contains((finding.Title, finding.Evidence))
                ? finding with { Evidence = "[Wi-Fi detail not retained in history]" }
                : finding).ToArray()
        };
    }

    private static bool IsValidReport(GamingDiagnosticReport? report) => report is not null
        && !string.IsNullOrWhiteSpace(report.RequestedTarget)
        && report.Profile is not null && report.Gateway is not null && report.Reference is not null
        && report.GameTarget is not null && report.Dns is not null && report.Findings is not null
        && Enum.IsDefined(report.LoadCondition) && (int)report.LoadCondition != 0;

    public void Delete(Guid id) => File.Delete(PathFor(id));
    private string PathFor(Guid id) => Path.Combine(_directory, $"{id:N}.json");
}
