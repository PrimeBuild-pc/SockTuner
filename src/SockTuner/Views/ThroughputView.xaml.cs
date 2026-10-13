using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Views;

/// <summary>
/// Loaded latency, throughput and imported online bufferbloat results. The results it holds feed
/// the window's recommendations, router guidance and checklist.
/// </summary>
public partial class ThroughputView : UserControl
{
    private readonly ThroughputProbe _throughput = new();
    private readonly SystemInventoryService _inventory = new();
    private CancellationTokenSource? _throughputCancellation;
    private IReadOnlyList<AdapterInfo> _adapters = [];

    public ThroughputView()
    {
        InitializeComponent();
        ThroughputDirectionComboBox.ItemsSource = Enum.GetValues<TransferDirection>();
        ThroughputDirectionComboBox.SelectedIndex = 0;
    }

    /// <summary>A line for the window's status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>True while a transfer runs, for the window's activity bar.</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>The user asked for recommendations built from an imported result.</summary>
    public event EventHandler? RecommendationsRequested;

    /// <summary>The game the diagnostics tab is set to; a grade is judged against its tick rate.</summary>
    public Func<GameProfile?> CurrentGame { get; set; } = () => null;

    public LoadedLatencyResult? LastDownload { get; private set; }
    public LoadedLatencyResult? LastUpload { get; private set; }
    public ImportedBufferbloatReport? ImportedReport { get; private set; }

    /// <summary>Link speeds from the latest inventory, so counter deltas become utilization.</summary>
    public void SetAdapters(IReadOnlyList<AdapterInfo> adapters) => _adapters = adapters;

    public void UseLatencyTarget(string target) => LoadedLatencyTargetText.Text = target;

    public void CancelActiveWork() => _throughputCancellation?.Cancel();

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private void CancelThroughput_Click(object sender, RoutedEventArgs e)
    {
        _throughputCancellation?.Cancel();
        SetStatus(Loc.T("Stopping the transfer…"));
    }

    private async void RunThroughput_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = ThroughputEndpointText.Text.Trim();
        var latencyTarget = LoadedLatencyTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(latencyTarget))
        {
            SetStatus(Loc.T("Enter both a throughput endpoint and a latency target."));
            return;
        }

        if (!int.TryParse(ThroughputStreamsText.Text.Trim(), out var streams)
            || streams < 1 || streams > ThroughputProbe.MaximumStreams)
        {
            SetStatus(Loc.F($"Streams must be between 1 and {ThroughputProbe.MaximumStreams}."));
            return;
        }

        if (!int.TryParse(ThroughputSecondsText.Text.Trim(), out var seconds)
            || seconds < 1 || seconds > ThroughputProbe.MaximumDuration.TotalSeconds)
        {
            SetStatus(Loc.F($"Seconds per phase must be between 1 and {ThroughputProbe.MaximumDuration.TotalSeconds:0}."));
            return;
        }

        if (ThroughputDirectionComboBox.SelectedItem is not TransferDirection direction)
        {
            SetStatus(Loc.T("Select a transfer direction."));
            return;
        }

        _throughputCancellation?.Dispose();
        _throughputCancellation = new CancellationTokenSource();
        SetThroughputBusy(true);
        BufferbloatGradeText.Text = Loc.T("Measuring…");
        ThroughputResultText.Text = Loc.T("Running…");
        LoadedLatencyResultText.Text = Loc.T("Running…");
        BufferbloatAssessmentText.Text = Loc.T("Running…");
        SetStatus(Loc.F($"Measuring {direction.ToString().ToLowerInvariant()} throughput and loaded latency…"));
        AppLog.Write("throughput.started", $"Endpoint={endpoint}; Latency={latencyTarget}; Direction={direction}; Streams={streams}; Seconds={seconds}.");

        try
        {
            // The same adapter counters the diagnostics tab uses, so an idle baseline taken while
            // something else was already filling the link is reported as such instead of graded.
            var beforeCounters = await Task.Run(_inventory.CaptureCounters, _throughputCancellation.Token);
            var startedAt = DateTimeOffset.Now;

            var profile = new DiagnosticProfile(
                "loaded-latency", "Loaded latency", Math.Max(seconds * 2, 10),
                TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1));

            var probe = LoadedLatencyProbe.For(
                latencyTarget, endpoint, streams, NetworkDiagnosticService.ProbeAsync, _throughput);

            var result = await probe.RunAsync(
                direction, profile, LoadedLatencyProbe.DefaultWarmUp, _throughputCancellation.Token);

            var afterCounters = await Task.Run(_inventory.CaptureCounters, _throughputCancellation.Token);
            var elapsed = DateTimeOffset.Now - startedAt;
            var utilization = BuildUtilization(beforeCounters, afterCounters, elapsed);

            if (result.Direction == TransferDirection.Download) LastDownload = result;
            else LastUpload = result;
            ShowLoadedLatency(result, utilization);
            SetStatus(Loc.T("Measurement complete."));
            AppLog.Write("throughput.completed", result.Summary);
        }
        catch (OperationCanceledException)
        {
            BufferbloatGradeText.Text = Loc.T("Cancelled");
            BufferbloatAssessmentText.Text = Loc.T("The run was cancelled before it could be graded.");
            SetStatus(Loc.T("Measurement cancelled."));
            AppLog.Write("throughput.cancelled", "Cancelled by the operator.");
        }
        catch (Exception exception)
        {
            BufferbloatGradeText.Text = Loc.T("Failed");
            BufferbloatAssessmentText.Text = exception.Message;
            SetStatus(Loc.T("Measurement failed."));
            AppLog.Write("throughput.failed", exception.Message);
        }
        finally
        {
            SetThroughputBusy(false);
        }
    }

    private IReadOnlyList<LinkUtilization> BuildUtilization(
        IReadOnlyList<AdapterCounterSample> before, IReadOnlyList<AdapterCounterSample> after, TimeSpan elapsed)
    {
        var speeds = _adapters
            .ToDictionary(adapter => adapter.Id, adapter => adapter.SpeedBitsPerSecond, StringComparer.OrdinalIgnoreCase);
        return AdapterCounterDeltaCalculator.Calculate(before, after)
            .Select(delta => LinkUtilization.Calculate(
                delta, elapsed, speeds.TryGetValue(delta.AdapterId, out var speed) && speed > 0 ? speed : 0))
            .ToArray();
    }

    private void ShowLoadedLatency(LoadedLatencyResult result, IReadOnlyList<LinkUtilization> utilization)
    {
        ThroughputResultText.Text = result.Load.Summary;
        LoadedLatencyResultText.Text = result.Summary;

        BufferbloatGradeText.Text = result.LatencyIncreaseMs is { } increase
            ? LoadedLatencyAnalyzer.Display(LoadedLatencyAnalyzer.Grade(increase))
            : Loc.T("Not gradable");

        var assessment = LoadedLatencyAnalyzer.Analyze(result, utilization);
        var lines = new List<string> { assessment.Title, $"Confidence: {assessment.Confidence}. Owner: {assessment.Owner}." };
        lines.AddRange(assessment.Supporting.Select(item => $"• {item}"));
        if (assessment.Contradicting.Count > 0)
        {
            lines.Add("Against this reading:");
            lines.AddRange(assessment.Contradicting.Select(item => $"• {item}"));
        }

        // A letter grade says how far the queue grew. It does not say whether the game survives it,
        // and that answer depends on the tick rate the diagnostics tab is already set to.
        if (CurrentGame() is { } game)
        {
            var idle = PlayabilityAnalyzer.Judge(result.Idle, game);
            var loaded = PlayabilityAnalyzer.Judge(result.Loaded, game);
            lines.Add(idle.Grade == loaded.Grade
                ? $"For {game.DisplayName}: {loaded.GradeDisplay.ToLowerInvariant()} both idle and under load."
                : $"For {game.DisplayName}: {idle.GradeDisplay.ToLowerInvariant()} idle, "
                    + $"{loaded.GradeDisplay.ToLowerInvariant()} under load — decided by {loaded.DecidedBy}. "
                    + "A game that only breaks while something else is downloading is a queue, and the queue is the "
                    + "router's to fix.");
        }

        BufferbloatAssessmentText.Text = string.Join(Environment.NewLine, lines);
    }

    private void SetThroughputBusy(bool isBusy)
    {
        BusyChanged?.Invoke(this, isBusy);
        RunThroughputButton.IsEnabled = !isBusy;
        CancelThroughputButton.IsEnabled = isBusy;
    }

    /// <summary>
    /// Reads an online bufferbloat export and restates it as this app's own measurement, so a
    /// result measured from outside the machine drives the same recommendations as a local run.
    /// </summary>
    private void ImportBufferbloatReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Import an online bufferbloat result"),
            Filter = Loc.T("Bufferbloat results (*.csv;*.json)|*.csv;*.json|All files (*.*)|*.*"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var report = BufferbloatReportImporter.Load(dialog.FileName);
            ImportedReport = report;
            LastDownload = report.Download ?? LastDownload;
            LastUpload = report.Upload ?? LastUpload;

            ShowImportedBufferbloat(report);
            ImportedReportRecommendButton.IsEnabled = true;
            SetStatus(Loc.F($"Imported a {report.SourceDisplay} result."));
            AppLog.Write("bufferbloat.imported",
                $"Source={report.Source}; Test={report.TestId}; Derived={report.DerivedGrade}; Reported={report.ReportedGrade}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                          or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            ImportedBufferbloatText.Text = Loc.F($"Import refused: {exception.Message}");
            ImportedReportRecommendButton.IsEnabled = false;
            SetStatus(Loc.T("Bufferbloat import failed."));
            AppLog.Write("bufferbloat.import_failed", exception.Message);
        }
    }

    private void ShowImportedBufferbloat(ImportedBufferbloatReport report)
    {
        var lines = new List<string>
        {
            $"{report.SourceDisplay} · test {report.TestId} · captured {report.CapturedAt:yyyy-MM-dd HH:mm}"
                + (report.Provider is null ? string.Empty : $" · {report.Provider}")
        };

        foreach (var direction in new[] { report.Download, report.Upload })
        {
            if (direction is null) continue;
            lines.Add($"• {direction.Summary}");
        }

        // The two grades are shown together on purpose. They are computed differently — this app
        // takes the median increase, the sites quote a mean — so agreement is worth seeing, and a
        // disagreement is worth investigating rather than papering over.
        var derived = report.DerivedGrade is { } grade ? LoadedLatencyAnalyzer.Display(grade) : "not graded";
        lines.Add(report.ReportedGrade is { } reported
            ? $"• Grade: {derived} derived here from the file's own samples; the test itself reported {reported}."
            : $"• Grade: {derived}, derived here from the file's own samples.");

        lines.AddRange(report.Notes.Select(note => $"• {note}"));
        ImportedBufferbloatText.Text = string.Join(Environment.NewLine, lines);
        BufferbloatGradeText.Text = derived;
        BufferbloatSummaryText.Text =
            Loc.F($"Imported from {report.SourceDisplay}. Latency increase under load is graded on the Waveform scale; "
            + $"it describes queue growth in front of the slowest link, not link speed.");
    }

    private void RecommendFromImport_Click(object sender, RoutedEventArgs e) =>
        RecommendationsRequested?.Invoke(this, EventArgs.Empty);
}
