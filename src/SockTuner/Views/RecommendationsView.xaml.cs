using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;

namespace SockTuner.Views;

/// <summary>
/// Turns what the diagnosis, bufferbloat and Wi-Fi tabs established into actions: changes for
/// the tuning plan, router guidance and an exportable checklist. Nothing here measures or writes.
/// </summary>
public partial class RecommendationsView : UserControl
{
    private IReadOnlyList<AdapterInfo> _adapters = [];
    private IReadOnlyList<RouterGuidanceItem> _routerGuidance = [];

    public RecommendationsView()
    {
        InitializeComponent();
        UseCaseComboBox.ItemsSource = UseCaseProfiles.All;
        UseCaseComboBox.SelectedIndex = 0;
    }

    /// <summary>A line for the window's status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Changes were queued and the user should review them in the tuning plan.</summary>
    public event EventHandler? OpenPlanRequested;

    // The evidence comes from the other tabs; the window wires them in once.
    public Func<GamingDiagnosticReport?> LastDiagnosis { get; set; } = () => null;
    public ThroughputView? Throughput { get; set; }
    public WifiDiagnosticsView? Wifi { get; set; }
    public TuningPlanView? Plan { get; set; }

    public void SetAdapters(IReadOnlyList<AdapterInfo> adapters) => _adapters = adapters;

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private void RemediationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SendToPlanButton.IsEnabled = RemediationGrid.SelectedItem is RemediationAction { AppliesLocally: true };

    private async void BuildRecommendations_Click(object sender, RoutedEventArgs e) => await BuildAsync();

    /// <summary>
    /// Turns what the last runs established into actions. Everything here is derived from facts
    /// already collected — nothing is measured, and nothing is written.
    /// </summary>
    public async Task BuildAsync()
    {
        var report = LastDiagnosis();
        var download = Throughput?.LastDownload;
        var upload = Throughput?.LastUpload;
        var wifiReport = Wifi?.LatestReport;
        var measured = download is not null || upload is not null;
        var wifiMeasured = wifiReport is { Radio: not null };
        if (report is null && !measured && !wifiMeasured)
        {
            RecommendationSummaryText.Text =
                Loc.T("Nothing to derive from yet. Run a diagnosis, measure bufferbloat, or import an online "
                + "bufferbloat result — recommendations come from a measurement, never from a preset alone.");
            return;
        }

        try
        {
            var adapter = _adapters.FirstOrDefault(item =>
                item.Kind == AdapterKind.Physical
                && item.Status == System.Net.NetworkInformation.OperationalStatus.Up
                && Guid.TryParse(item.Id, out _));
            var adapterId = adapter is not null && Guid.TryParse(adapter.Id, out var parsed) ? parsed : (Guid?)null;

            // WMI and registry reads: slow enough to freeze the window if done on its thread.
            var (capabilities, globals, powerManagementOff) = await Task.Run(() => (
                WindowsAdapterCapabilityInventory.Read().Capabilities,
                WindowsGlobalSettingInventory.Read().Capabilities,
                adapterId is { } id && AdapterPowerSavingSpecification.IsPowerManagementOff(id)));

            // The receive-window advice is computed from this path, so it is only offered when a
            // throughput run actually measured one.
            TcpPathMeasurement? path = null;
            var loadedForPath = download ?? upload;
            if (loadedForPath is { } loaded && loaded.Load.BitsPerSecond > 0 && loaded.Idle.MedianMs is { } rtt)
            {
                path = new TcpPathMeasurement(
                    loaded.Load.BitsPerSecond,
                    rtt,
                    loaded.LatencyIncreaseMs is { } increase ? LoadedLatencyAnalyzer.Grade(increase) : null);
            }

            var context = new RemediationContext(
                adapterId,
                capabilities,
                report?.PathMtu is { State: PathMtuState.IcmpBlackHole, Mtu: { } mtu } ? mtu : null,
                globals,
                path);

            var findings = (report?.Findings ?? [])
                .Concat(wifiReport?.Findings ?? [])
                .DistinctBy(finding => (finding.Title, finding.Evidence))
                .ToArray();
            var actions = RemediationPlanner.Plan(findings, context).ToList();

            // The use-case profile is a deliberate preset rather than a finding, so it is added
            // explicitly and only when there is an adapter whose advertised keywords can carry it.
            if (UseCaseComboBox.SelectedItem is UseCaseProfile profile && adapterId is { } id)
            {
                actions.Insert(0, UseCaseProfiles.PlanFor(
                    profile, id, capabilities, globals, wireless: adapter?.InterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,
                    devicePowerIsOff: _ => powerManagementOff));
            }

            RemediationGrid.ItemsSource = actions;
            SendToPlanButton.IsEnabled = false;
            SendAllToPlanButton.IsEnabled = actions.Any(action => action.AppliesLocally);
            ExportChecklistButton.IsEnabled = actions.Count > 0;

            var wifi = ShowWifiRadio();
            ShowRouterGuidance(wifi);

            var local = actions.Count(action => action.AppliesLocally);
            var basis = report is null
                ? wifiMeasured && !measured
                    ? "passive Wi-Fi evidence"
                    : $"an imported {Throughput?.ImportedReport?.SourceDisplay ?? "bufferbloat"} result"
                : $"{findings.Length} finding(s)";
            RecommendationSummaryText.Text =
                Loc.F($"{actions.Count} action(s) from {basis}: {local} this machine can make, "
                + $"{actions.Count - local} belonging elsewhere. Adapter: {adapter?.Name ?? "none selected"}.");
            AppLog.Write("recommendations.built", $"Actions={actions.Count}; Local={local}; Imported={report is null}.");
        }
        catch (Exception exception)
        {
            RecommendationSummaryText.Text = Loc.F($"Recommendations could not be built: {exception.Message}");
            AppLog.Write("recommendations.failed", exception.Message);
        }
    }

    private WifiRadioInfo? ShowWifiRadio()
    {
        if (Wifi?.LatestReport is { Radio: { } latest } latestReport)
        {
            ShowWifiRadio(latest, latestReport.Findings);
            return latest;
        }

        var inventory = WindowsWifiInventory.Read();
        if (!inventory.Supported || inventory.Radios.Count == 0)
        {
            WifiRadioText.Text = inventory.Error is { } error
                ? Loc.F($"Wi-Fi radio inventory unavailable: {error}")
                : Loc.T("No Wi-Fi radio is connected; this connection is wired.");
            return null;
        }

        var radio = inventory.Radios.FirstOrDefault(item => item.Connected) ?? inventory.Radios[0];
        ShowWifiRadio(radio, WifiRadioAnalyzer.Analyze(radio));
        return radio;
    }

    private void ShowWifiRadio(WifiRadioInfo radio, IReadOnlyList<DiagnosticFinding> findings)
    {
        var lines = new List<string>
        {
            $"{radio.Description}: SSID {radio.Ssid} · {radio.SignalDisplay} · {radio.RateDisplay}"
        };
        lines.AddRange(findings.Select(finding => $"• {finding.Title} — {finding.Evidence} {finding.Action}".TrimEnd()));
        if (WifiRadioAnalyzer.RecommendChannel(radio) is { } channel)
        {
            lines.Add(channel.AlreadyBest
                ? $"• Channel {channel.Channel} is already the least congested of those seen."
                : $"• Channel {channel.Channel} is less congested than the one in use.");
        }

        if (findings.Count == 0) lines.Add("• Nothing about the radio stood out.");
        WifiRadioText.Text = string.Join(Environment.NewLine, lines);
    }

    private void ShowRouterGuidance(WifiRadioInfo? wifi)
    {
        var download = Throughput?.LastDownload;
        var upload = Throughput?.LastUpload;
        var items = RouterGuidance.For(new RouterGuidanceInput(download, upload, wifi));
        _routerGuidance = items;
        if (items.Count == 0)
        {
            RouterGuidanceText.Text = download is null && upload is null
                ? Loc.T("Run a bufferbloat measurement first: shaping advice is computed from the rate this connection actually reached, never from the advertised one.")
                : Loc.T("Nothing measured here needs a router change.");
            return;
        }

        var lines = new List<string>();
        foreach (var item in items)
        {
            lines.Add($"{item.Title}  [{item.Owner}]");
            lines.AddRange(item.Instructions.Select(instruction => $"    • {instruction.Summary}"));
            lines.Add($"    Verify: {item.Verification}");
        }

        RouterGuidanceText.Text = string.Join(Environment.NewLine, lines);
    }

    private void SendRecommendationToPlan_Click(object sender, RoutedEventArgs e)
    {
        if (RemediationGrid.SelectedItem is not RemediationAction { AppliesLocally: true } action)
        {
            SetStatus(Loc.T("Select an action that this machine can make."));
            return;
        }

        if (Plan is not { } plan) return;
        var accepted = plan.Propose(action.Changes);
        SetStatus(accepted == action.Changes.Count
            ? Loc.F($"Sent {accepted} change(s) to the tuning plan. Nothing is applied until you review and confirm there.")
            : Loc.F($"Sent {accepted} of {action.Changes.Count} change(s); the rest are not advertised for the selected adapter."));
        AppLog.Write("recommendations.sent_to_plan", $"Action={action.Id}; Accepted={accepted}/{action.Changes.Count}.");
    }

    /// <summary>
    /// The automatic path: every action this machine can make, queued in one click. It stops where
    /// the manual path stops — on the tuning plan — because the preview, the read-back check and
    /// the typed confirmation are what make any of this reversible, and no button skips them.
    /// </summary>
    private void SendAllRecommendationsToPlan_Click(object sender, RoutedEventArgs e) => QueueAll();

    /// <summary>Chooses a use-case profile by id, so another surface can ask for one without reaching into the combo box.</summary>
    public void SelectProfile(string id) =>
        UseCaseComboBox.SelectedItem = UseCaseProfiles.All.FirstOrDefault(profile => profile.Id == id) ?? UseCaseComboBox.SelectedItem;

    /// <summary>Queues every action this machine can make and returns how many changes the plan accepted.</summary>
    public int QueueAll(Func<RemediationAction, bool>? only = null)
    {
        if (RemediationGrid.ItemsSource is not IEnumerable<RemediationAction> actions)
        {
            SetStatus(Loc.T("Build recommendations first."));
            return 0;
        }

        var applicable = actions.Where(action => action.AppliesLocally && (only?.Invoke(action) ?? true)).ToArray();
        if (applicable.Length == 0)
        {
            SetStatus(Loc.T("Nothing here is a change this machine can make."));
            return 0;
        }

        if (Plan is not { } plan) return 0;
        var requested = 0;
        var accepted = 0;
        foreach (var action in applicable)
        {
            requested += action.Changes.Count;
            accepted += plan.Propose(action.Changes);
        }

        SetStatus(accepted == requested
            ? Loc.F($"Sent {accepted} change(s) from {applicable.Length} action(s) to the tuning plan. "
              + $"Nothing is applied until you preview and confirm it there.")
            : Loc.F($"Sent {accepted} of {requested} change(s); the rest are not advertised by the selected adapter's driver."));
        AppLog.Write("recommendations.sent_all", $"Actions={applicable.Length}; Accepted={accepted}/{requested}.");
        if (accepted > 0) OpenPlanRequested?.Invoke(this, EventArgs.Empty);
        return accepted;
    }

    /// <summary>
    /// Writes the recommendations out as a checklist. The router half is the point: those changes
    /// are made in another device's web interface or over SSH, where a panel inside this window is
    /// no help at all.
    /// </summary>
    private void ExportChecklist_Click(object sender, RoutedEventArgs e)
    {
        if (RemediationGrid.ItemsSource is not IEnumerable<RemediationAction> actions)
        {
            SetStatus(Loc.T("Build recommendations first."));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Export the action checklist"),
            Filter = Loc.T("Markdown (*.md)|*.md|All files (*.*)|*.*"),
            FileName = $"socktuner-checklist-{DateTime.Now:yyyyMMdd-HHmmss}.md"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var basis = new List<string>();
            if (Throughput?.ImportedReport is { } imported)
            {
                basis.Add($"Imported {imported.SourceDisplay}, test {imported.TestId}, captured {imported.CapturedAt:yyyy-MM-dd HH:mm}.");
            }

            foreach (var direction in new[] { Throughput?.LastDownload, Throughput?.LastUpload })
            {
                if (direction is not null) basis.Add(direction.Summary);
            }

            if (LastDiagnosis() is { } report) basis.Add($"Diagnosis of {report.RequestedTarget} at {report.StartedAt:yyyy-MM-dd HH:mm}.");

            File.WriteAllText(dialog.FileName, ActionChecklistExporter.ToMarkdown(
                [.. actions], _routerGuidance, string.Join(Environment.NewLine + Environment.NewLine, basis)));
            SetStatus(Loc.F($"Checklist exported to {dialog.FileName}."));
            AppLog.Write("checklist.exported", $"Actions={actions.Count()}; Router={_routerGuidance.Count}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus(Loc.F($"Checklist export failed: {exception.Message}"));
            AppLog.Write("checklist.export_failed", exception.Message);
        }
    }
}
