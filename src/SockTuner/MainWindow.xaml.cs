using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;

namespace SockTuner;

public partial class MainWindow : Window
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int MonitorMaximumSamples = 1000;

    /// <summary>The catalogue, plus whatever tick rate an imported capture brought with it.</summary>
    private readonly ObservableCollection<GameProfile> _gameProfiles = new(GameProfiles.All);

    private readonly SystemInventoryService _inventory = new();
    private readonly NetworkDiagnosticService _diagnostics = new();
    private readonly NetworkMonitorService _monitor = new();
    private readonly RouteGatewayResolver _routeGatewayResolver = new();
    private readonly DiagnosticHistoryStore _historyStore = new();
    private readonly ObservableCollection<DiagnosticHistoryEntry> _history = [];
    private NetworkSnapshot? _snapshot;
    private GamingDiagnosticReport? _lastReport;
    private CancellationTokenSource? _diagnosticCancellation;
    private CancellationTokenSource? _monitorCancellation;
    private readonly ThroughputProbe _throughput = new();
    private readonly RouteQualityProbe _routeQuality = new();
    private readonly DnsBenchmarkProbe _dnsBenchmark = new();
    private CancellationTokenSource? _dnsBenchmarkCancellation;
    private readonly ElevatedWorkerClient _dnsWorker = new();
    private DnsBenchmarkReport? _lastDnsReport;
    private GameFlowReport? _importedReport;
    private readonly ElevatedWorkerClient _irqWorker = new();
    private InterruptAffinityInventoryResult? _interrupts;
    private readonly ObservableCollection<CoreChoice> _coreChoices = [];
    private readonly BottleneckLocator _bottleneck = new();
    private CancellationTokenSource? _throughputCancellation;
    private LoadedLatencyResult? _lastLoadedLatency;
    private LoadedLatencyResult? _lastDownload;
    private LoadedLatencyResult? _lastUpload;
    private ImportedBufferbloatReport? _importedBufferbloat;
    private readonly ObservableCollection<MonitorSample> _monitorSamples = [];
    private IReadOnlyList<AdapterInfo> _adapterRows = [];
    private readonly ObservableCollection<InterfaceAdvice> _interfaceAdvice = [];
    private IReadOnlyList<NdisPropertyRow> _ndisRows = [];
    private UserPreferences _preferences = new();
    private int _busyCount;
    private object? _selectedInventoryItem;
    private System.Windows.Controls.DataGrid? _selectedInventoryGrid;

    public MainWindow()
    {
        InitializeComponent();
        _preferences = AppPreferences.Load();
        AppLog.ConfigureRetention(_preferences.LogFileMegabytes);
        LogRetentionComboBox.ItemsSource = Enumerable.Range(1, 64);
        LogRetentionComboBox.SelectedItem = _preferences.LogFileMegabytes;
        DiagnosticProfileComboBox.ItemsSource = DiagnosticProfiles.All;
        DiagnosticProfileComboBox.SelectedItem = DiagnosticProfiles.All[0];
        DiagnosticGameComboBox.ItemsSource = _gameProfiles;
        DiagnosticGameComboBox.SelectedItem = _gameProfiles[0];
        DiagnosticLoadComboBox.ItemsSource = Enum.GetValues<DiagnosticLoadCondition>();
        DiagnosticLoadComboBox.SelectedIndex = 0;
        ThroughputDirectionComboBox.ItemsSource = Enum.GetValues<TransferDirection>();
        ThroughputDirectionComboBox.SelectedIndex = 0;
        UseCaseComboBox.ItemsSource = UseCaseProfiles.All;
        UseCaseComboBox.SelectedIndex = 0;
        IrqPolicyComboBox.ItemsSource = new[]
        {
            InterruptPolicy.SpecifiedProcessors,
            InterruptPolicy.AllCloseProcessors,
            InterruptPolicy.OneCloseProcessor,
            InterruptPolicy.AllProcessorsInMachine,
            InterruptPolicy.SpreadMessagesAcrossAllProcessors
        }.Select(policy => new PolicyChoice(policy)).ToArray();
        IrqPolicyComboBox.SelectedIndex = 0;
        IrqPriorityComboBox.ItemsSource = Enum.GetValues<InterruptPriority>();
        IrqPriorityComboBox.SelectedItem = InterruptPriority.Undefined;
        IrqCoreList.ItemsSource = _coreChoices;
        InterfaceAdviceGrid.ItemsSource = _interfaceAdvice;
        InterfaceProfileComboBox.ItemsSource = InterfaceProfiles;
        InterfaceProfileComboBox.SelectedIndex = 0;
        ReferenceLinkList.ItemsSource = ReferenceLinks.All;
        MonitorSamplesGrid.ItemsSource = _monitorSamples;
        foreach (var entry in _historyStore.Load()) _history.Add(entry);
        HistoryGrid.ItemsSource = _history;
        TuningPlan.Applied += async (_, _) =>
        {
            // Consent is accepted inside the plan view, so the badge is re-read after it acts.
            _preferences = AppPreferences.Load();
            ShowWriteState();
            await RefreshInventoryAsync();
        };
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        RestoreWindowGeometry();
        Closing += (_, _) => SaveWindowGeometry();
        Loaded += async (_, _) =>
        {
            await RefreshInventoryAsync();
            LoadInterruptAffinity();
        };
        ShowWriteState();
        WriteLog("app.started", "SockTuner UI started.");
    }

    /// <summary>
    /// States what the app can actually do right now. The badge used to read "read-only preview"
    /// permanently, which stopped being true once the transaction path was unlocked — a label that
    /// understates what a tool can change is as misleading as one that overstates it.
    /// </summary>
    private void ShowWriteState()
    {
        var accepted = WriteConsent.IsAccepted(_preferences);
        WriteStateText.Text = accepted ? "CHANGES ARMED" : "INVENTORY ONLY";
        WriteStateText.ToolTip = accepted
            ? "Change consent accepted. Applying still needs elevation, a preview, and a typed confirmation, and every change is snapshotted and reversible."
            : "Nothing can be written until you accept the change consent in the tuning plan. Everything else is read-only.";
        // The dashboard used to state flatly that mutations were disabled. That stopped being true
        // when the transaction path was unlocked, and a stale reassurance is worse than none.
        WorkflowWriteStateText.Text = accepted
            ? "Changes are armed. Applying still needs elevation and a typed confirmation in the tuning plan."
            : "Nothing is written until you accept the change consent in the tuning plan.";
    }

    /// <summary>
    /// Puts the window back where it was left. A saved position is only honoured when it still
    /// lands on a monitor that exists: a geometry from a display that has since been unplugged
    /// would otherwise open the app somewhere the user cannot reach it, with no way to recover
    /// short of editing the preferences file.
    /// </summary>
    private void RestoreWindowGeometry()
    {
        if (_preferences.Window is not { } geometry) return;

        if (!geometry.FitsWithin(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight))
        {
            WriteLog("window.geometry_discarded", "Saved window geometry lands off every current monitor.");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = geometry.Left;
        Top = geometry.Top;
        Width = geometry.Width;
        Height = geometry.Height;
        if (geometry.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Saves the restored size rather than the current one, so closing while maximised remembers
    /// both the maximised state and the size to return to when it is un-maximised.
    /// </summary>
    private void SaveWindowGeometry()
    {
        try
        {
            var bounds = RestoreBounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Width)) return;

            _preferences = _preferences with
            {
                Window = new WindowGeometry(
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    WindowState == WindowState.Maximized)
            };
            AppPreferences.Save(_preferences);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is not worth blocking a clean shutdown over.
            WriteLog("window.geometry_save_failed", exception.Message);
        }
    }

    private async void RefreshInventory_Click(object sender, RoutedEventArgs e) => await RefreshInventoryAsync();

    /// <summary>
    /// Shows the status-bar activity bar while anything long is running. A counter rather than a
    /// flag because a diagnosis and a throughput run are separate surfaces that can overlap, and a
    /// flag would clear the bar the moment the first of them finished. Clamped, so a
    /// <c>finally</c> that unwinds a run which never started cannot drive it negative.
    /// </summary>
    private void SetBusy(bool isBusy)
    {
        _busyCount = Math.Max(0, _busyCount + (isBusy ? 1 : -1));
        BusyBar.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// F5 re-reads the inventory, Ctrl+F goes to the global search, Ctrl+K jumps to a section by
    /// name, and Ctrl+1..9 select the first tab of each navigation group. Twenty sections is more
    /// than a mouse should have to carry.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var control = e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control;

        if (e.Key == System.Windows.Input.Key.F5)
        {
            e.Handled = true;
            _ = RefreshInventoryAsync();
            return;
        }

        if (control && e.Key == System.Windows.Input.Key.F)
        {
            e.Handled = true;
            InventoryFilterText.SelectAll();
            InventoryFilterText.Focus();
            return;
        }

        if (control && e.Key == System.Windows.Input.Key.K)
        {
            e.Handled = true;
            ShowSectionJump();
            return;
        }

        // Ctrl+1..9 over the selectable tabs, in the order the navigation shows them.
        if (control && e.Key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9)
        {
            var index = e.Key - System.Windows.Input.Key.D1;
            var selectable = SelectableTabs();
            if (index < selectable.Count)
            {
                e.Handled = true;
                InventoryTabs.SelectedItem = selectable[index];
            }
        }
    }

    private List<System.Windows.Controls.TabItem> SelectableTabs() =>
        [.. InventoryTabs.Items.OfType<System.Windows.Controls.TabItem>().Where(item => item.IsEnabled)];

    /// <summary>
    /// Jump to a section by typing part of its name. The global search box doubles as the input:
    /// typing there and pressing Ctrl+K again moves to the first section whose name matches, which
    /// keeps one text field rather than introducing a modal palette over a modal-free app.
    /// </summary>
    private void ShowSectionJump()
    {
        var typed = InventoryFilterText.Text.Trim();
        if (typed.Length == 0)
        {
            InventoryFilterText.Focus();
            StatusText.Text = "Type part of a section name, then press Ctrl+K again to jump to it.";
            return;
        }

        var match = SelectableTabs().FirstOrDefault(item =>
            item.Header?.ToString()?.Contains(typed, StringComparison.OrdinalIgnoreCase) == true);
        if (match is null)
        {
            StatusText.Text = $"No section matching \u201c{typed}\u201d.";
            return;
        }

        InventoryTabs.SelectedItem = match;
        StatusText.Text = $"Jumped to {match.Header}.";
    }

    /// <summary>
    /// One button for the three exports. WPF gives a Button no dropdown of its own, so its own
    /// context menu is opened under it — no menu bar, no popup plumbing.
    /// </summary>
    private void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ExportMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ExportMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void HealthGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenHealthSection_Click(sender, e);

    private async Task RefreshInventoryAsync()
    {
        StatusText.Text = "Reading Windows network inventory…";
        SetBusy(true);

        try
        {
            _snapshot = await Task.Run(_inventory.Capture);
            ShowSnapshot(_snapshot);
            StatusText.Text = $"Inventory refreshed at {_snapshot.System.CapturedAt:HH:mm:ss}";
            WriteLog("inventory.completed", $"Captured {_snapshot.Adapters.Count} interfaces, {_snapshot.Routes.Count} IP routes, {_snapshot.NetworkProfiles?.Count ?? 0} network profiles, {_snapshot.NetworkBindings?.Count ?? 0} bindings, {_snapshot.AdapterOffloads?.Count ?? 0} adapter offload rows, {_snapshot.TcpSettings?.Count ?? 0} TCP templates, {_snapshot.QosPolicies?.Count ?? 0} QoS policies, {_snapshot.WinsockProviders?.Count ?? 0} Winsock providers, and {_snapshot.Adapters.Sum(adapter => adapter.NdisProperties.Count)} NDIS properties.");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Inventory failed: {exception.Message}";
            WriteLog("inventory.failed", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowSnapshot(NetworkSnapshot snapshot)
    {
        ActiveAdaptersText.Text = snapshot.ActiveAdapterCount.ToString();
        AdapterCountText.Text = snapshot.Adapters.Count.ToString();
        ProcessorCountText.Text = snapshot.System.LogicalProcessors.ToString();
        NdisPropertyCountText.Text = snapshot.Adapters.Sum(adapter => adapter.NdisProperties.Count).ToString();
        PrivilegeText.Text = snapshot.System.IsAdministrator ? "Elevated" : "Standard user";
        OsText.Text = snapshot.System.OperatingSystem;
        BuildText.Text = snapshot.System.Version;
        MachineText.Text = snapshot.System.MachineName;
        CapturedText.Text = snapshot.System.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz");
        ShowHealthCheck(snapshot);
        _adapterRows = snapshot.Adapters;
        TuningPlan.SetAdapters(snapshot.Adapters);
        ShowInterfaceAdvice();
        var dnsCandidates = snapshot.Adapters
            .Where(item => Guid.TryParse(item.Id, out _) && item.Kind == AdapterKind.Physical)
            .ToArray();
        var previouslySelected = (DnsAdapterComboBox.SelectedItem as AdapterInfo)?.Id;
        DnsAdapterComboBox.ItemsSource = dnsCandidates;
        DnsAdapterComboBox.SelectedItem = dnsCandidates.FirstOrDefault(item => item.Id == previouslySelected)
            ?? dnsCandidates.FirstOrDefault(item => item.Status == System.Net.NetworkInformation.OperationalStatus.Up)
            ?? dnsCandidates.FirstOrDefault();
        _ndisRows = snapshot.Adapters
            .SelectMany(adapter => adapter.NdisProperties.Select(property => new NdisPropertyRow(
                adapter.Name,
                adapter.DriverDisplay,
                property.DisplayName,
                property.Keyword,
                property.CurrentValue,
                property.DefaultValue,
                property.Type,
                property.ValidValues)))
            .ToArray();
        ApplyAdapterFilter();
        ApplyNdisFilter();
        NdisSummaryText.Text = $"{_ndisRows.Count} advanced properties advertised across {snapshot.Adapters.Count(adapter => adapter.NdisSupported)} supported adapter(s). Raw keywords remain visible and no values are changed.";
        RoutesGrid.ItemsSource = snapshot.Routes;
        DnsInterfacesGrid.ItemsSource = snapshot.Adapters.Where(adapter => adapter.Ipv4Index > 0 || adapter.Ipv6Index > 0);
        InterfaceSummaryText.Text = snapshot.IpInterfaceInventoryError is null
            ? $"{snapshot.Adapters.Sum(adapter => adapter.IpInterfaces?.Count ?? 0)} native IPv4/IPv6 interface row(s)."
            : $"Interface metric inventory partial: {snapshot.IpInterfaceInventoryError}";
        var ipv4RouteCount = snapshot.Routes.Count(route => route.AddressFamily == "IPv4");
        var ipv6RouteCount = snapshot.Routes.Count(route => route.AddressFamily == "IPv6");
        RouteSummaryText.Text = snapshot.RouteInventoryError is null
            ? $"{ipv4RouteCount} native IPv4 and {ipv6RouteCount} native IPv6 route(s)."
            : $"Route inventory partial: {snapshot.RouteInventoryError}";
        NetworkProfilesGrid.ItemsSource = snapshot.NetworkProfiles ?? [];
        NetworkProfileSummaryText.Text = snapshot.NetworkProfileInventoryError is null
            ? $"{snapshot.NetworkProfiles?.Count ?? 0} profile connection(s) from Windows Network List Manager."
            : $"Network profile inventory partial: {snapshot.NetworkProfileInventoryError}";
        BindingsGrid.ItemsSource = snapshot.NetworkBindings ?? [];
        BindingSummaryText.Text = snapshot.NetworkBindingInventoryError is null
            ? $"{snapshot.NetworkBindings?.Count ?? 0} binding(s) from root/StandardCimv2. Inspection only; no protocol or filter is changed."
            : $"Network binding inventory partial: {snapshot.NetworkBindingInventoryError}";
        GlobalOffloadsGrid.ItemsSource = snapshot.GlobalOffloads ?? [];
        AdapterOffloadsGrid.ItemsSource = snapshot.AdapterOffloads ?? [];
        OffloadSummaryText.Text = snapshot.OffloadInventoryError is null
            ? $"{snapshot.GlobalOffloads?.Count ?? 0} global and {snapshot.AdapterOffloads?.Count ?? 0} adapter setting row(s). Inspection only; no offload is changed."
            : $"Offload inventory partial: {snapshot.OffloadInventoryError}";
        TcpSettingsGrid.ItemsSource = snapshot.TcpSettings ?? [];
        TcpSettingSummaryText.Text = snapshot.TcpSettingInventoryError is null
            ? $"{snapshot.TcpSettings?.Count ?? 0} TCP template row(s). Read-only values include local/group-policy sources and raw export fields."
            : $"TCP setting inventory partial: {snapshot.TcpSettingInventoryError}";
        QosPoliciesGrid.ItemsSource = snapshot.QosPolicies ?? [];
        QosPolicySummaryText.Text = snapshot.QosPolicyInventoryError is null
            ? $"{snapshot.QosPolicies?.Count ?? 0} QoS policy row(s). Inspection only; no policy is created or changed."
            : $"QoS policy inventory partial: {snapshot.QosPolicyInventoryError}";
        WinsockProvidersGrid.ItemsSource = snapshot.WinsockProviders ?? [];
        WinsockSummaryText.Text = snapshot.WinsockInventoryError is null
            ? $"{snapshot.WinsockProviders?.Count ?? 0} native protocol provider(s). Inspection only; repair remains separately gated."
            : $"Winsock inventory partial: {snapshot.WinsockInventoryError}";
        ApplyInventoryFilter();
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        var target = DiagnosticTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText.Text = "Enter a game server, regional endpoint, or IP address.";
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(DiagnosticPortText.Text))
        {
            if (!int.TryParse(DiagnosticPortText.Text, out var parsedPort) || parsedPort is <= 0 or > 65535)
            {
                StatusText.Text = "TCP port must be between 1 and 65535.";
                return;
            }

            port = parsedPort;
        }

        if (DiagnosticProfileComboBox.SelectedItem is not DiagnosticProfile profile
            || DiagnosticLoadComboBox.SelectedItem is not DiagnosticLoadCondition loadCondition)
        {
            StatusText.Text = "Select a diagnostic profile.";
            return;
        }

        if (DiagnosticGameComboBox.SelectedItem is not GameProfile game)
        {
            StatusText.Text = "Select a game tick rate.";
            return;
        }

        // The game decides how large each probe is — a live game packet is not 32 bytes — but not
        // how fast they are sent. A serial ICMP probe cannot reach 128 packets a second against a
        // real path, and sending at a game's rate to somebody else's server uninvited is not this
        // app's business. The rate-independent jitter figure is what keeps the comparison honest.
        profile = profile with { PayloadBytes = game.PayloadBytes };

        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        _lastReport = null;
        SetDiagnosticBusy(true);
        ClearDiagnosticResults("Running…");
        DiagnosticRunSummaryText.Text = $"{profile.DisplayName}: resolving {target}, then collecting {profile.SampleCount} concurrent samples per endpoint…";
        StatusText.Text = $"Diagnosing {target}…";
        WriteLog("diagnostic.started", $"Target={target}; Port={port?.ToString() ?? "none"}; Profile={profile.Id}; Load={loadCondition}.");

        try
        {
            var beforeSnapshot = await Task.Run(_inventory.Capture, _diagnosticCancellation.Token);
            _diagnosticCancellation.Token.ThrowIfCancellationRequested();
            var gateway = await _routeGatewayResolver.ResolveAsync(target, beforeSnapshot, _diagnosticCancellation.Token);
            var beforeCounters = await Task.Run(_inventory.CaptureCounters, _diagnosticCancellation.Token);
            _diagnosticCancellation.Token.ThrowIfCancellationRequested();
            var report = await _diagnostics.RunAsync(target, gateway, port, profile, loadCondition, _diagnosticCancellation.Token);
            var afterCounters = await Task.Run(_inventory.CaptureCounters, _diagnosticCancellation.Token);
            _diagnosticCancellation.Token.ThrowIfCancellationRequested();
            var afterSnapshot = await Task.Run(_inventory.Capture, _diagnosticCancellation.Token);
            _diagnosticCancellation.Token.ThrowIfCancellationRequested();
            _snapshot = afterSnapshot;
            ShowSnapshot(afterSnapshot);
            report = report with
            {
                CounterDeltas = AdapterCounterDeltaCalculator.Calculate(beforeCounters, afterCounters),
                Game = game
            };
            _lastReport = report;
            try
            {
                _history.Insert(0, _historyStore.Save(report));
                while (_history.Count > 20) _history.RemoveAt(_history.Count - 1);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WriteLog("history.save_failed", exception.Message);
            }
            GatewayResultText.Text = report.Gateway.Summary;
            ReferenceResultText.Text = report.Reference.Summary;
            GameResultText.Text = report.GameTarget.Summary;
            var pathSummary = $"Boundary: {report.FirstPublicBoundary ?? "not identified"}\nMTU: {report.PathMtu?.Detail ?? "not measured"}";
            DnsResultText.Text = report.Connection is null
                ? $"{report.Dns.Summary}\n{pathSummary}"
                : $"{report.Dns.Summary}\n{report.Connection.Summary}\n{pathSummary}";
            var statistics = new[] { report.Gateway, report.Reference, report.GameTarget }
                .Concat(report.FirstPublicBoundaryProbe is null ? [] : [report.FirstPublicBoundaryProbe])
                .ToArray();
            DiagnosticStatisticsGrid.ItemsSource = statistics;
            DiagnosticSamplesGrid.ItemsSource = statistics.SelectMany(item => item.Samples.Select(sample => new DiagnosticTimelineSample(
                item.Label, sample.Timestamp, sample.RoundTripTimeMs, item.SpikeSamples.Contains(sample), sample.FailureKind, sample.Error))).ToArray();
            RouteSamplesGrid.ItemsSource = report.RouteSamples ?? [];
            await ShowRoutingAnalysisAsync(report, afterSnapshot, _diagnosticCancellation.Token);
            FindingsGrid.ItemsSource = report.Findings;
            ShowPlayability(report.GameTarget, game);
            var counterSummary = report.CounterDeltas is { Count: > 0 }
                ? $" Counter deltas: {string.Join(" · ", report.CounterDeltas.Select(delta => delta.Summary))}"
                : " Counter deltas unavailable.";
            DiagnosticRunSummaryText.Text = $"{report.Findings.Count} finding(s) for {report.RequestedTarget} from the {profile.DisplayName} profile ({report.Duration.TotalSeconds:0.0}s).{counterSummary}";
            StatusText.Text = $"Diagnosis completed at {DateTimeOffset.Now:HH:mm:ss}";
            WriteLog("diagnostic.completed", $"Target={report.RequestedTarget}; Duration={report.Duration.TotalSeconds:0.0}s; Findings={report.Findings.Count}.");
        }
        catch (OperationCanceledException)
        {
            ClearDiagnosticResults("Canceled");
            DiagnosticRunSummaryText.Text = $"Diagnosis for {target} canceled. Partial samples were discarded.";
            StatusText.Text = "Diagnosis canceled";
            WriteLog("diagnostic.canceled", $"Target={target}.");
        }
        catch (Exception exception)
        {
            ClearDiagnosticResults("Failed");
            DiagnosticRunSummaryText.Text = $"Diagnosis for {target} failed: {exception.Message}";
            StatusText.Text = "Diagnosis failed";
            WriteLog("diagnostic.failed", $"Target={target}; Error={exception.Message}");
        }
        finally
        {
            SetDiagnosticBusy(false);
        }
    }

    private void ExportSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            StatusText.Text = "Refresh inventory before exporting a snapshot.";
            return;
        }

        if (MessageBox.Show(
                "The snapshot contains machine, network profile names/IDs, adapter, binding component, address, route, DNS, driver, and Winsock provider identifiers. Export it anyway?",
                "Export diagnostic snapshot",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SaveSnapshot(redact: false);
    }

    private void ExportSupportSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            StatusText.Text = "Refresh inventory before exporting a support snapshot.";
            return;
        }

        SaveSnapshot(redact: true);
    }

    private void SaveSnapshot(bool redact)
    {
        var kind = redact ? "support" : "snapshot";
        var dialog = new SaveFileDialog
        {
            Filter = "SockTuner JSON snapshot (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"SockTuner-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, SnapshotExporter.Serialize(_snapshot!, redact));
            StatusText.Text = $"{(redact ? "Redacted support snapshot" : "Snapshot")} exported to {dialog.FileName}.";
            WriteLog(redact ? "support_snapshot.exported" : "snapshot.exported", dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Snapshot export failed: {exception.Message}";
            WriteLog("snapshot.export_failed", exception.Message);
        }
    }

    private void ExportReportJson_Click(object sender, RoutedEventArgs e) => SaveReport(html: false, redact: false);
    private void ExportReportHtml_Click(object sender, RoutedEventArgs e) => SaveReport(html: true, redact: false);
    private void ExportRedactedReportHtml_Click(object sender, RoutedEventArgs e) => SaveReport(html: true, redact: true);

    private void SaveReport(bool html, bool redact)
    {
        if (_lastReport is null)
        {
            StatusText.Text = "Run a completed diagnosis before exporting a report.";
            return;
        }

        SaveReport(_lastReport, html, redact);
    }

    private void SaveReport(GamingDiagnosticReport report, bool html, bool redact)
    {
        if (!redact && MessageBox.Show(
                "The full report contains diagnostic targets, addresses, routes, adapter identifiers, and error details. Export it anyway?",
                "Export full diagnostic report",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var extension = html ? ".html" : ".json";
        var dialog = new SaveFileDialog
        {
            Filter = html ? "Self-contained HTML report (*.html)|*.html" : "SockTuner JSON report (*.json)|*.json",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"SockTuner-report-{DateTime.Now:yyyyMMdd-HHmmss}{extension}"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var content = html
                ? DiagnosticReportExporter.SerializeHtml(report, redact)
                : DiagnosticReportExporter.SerializeJson(report, redact);
            File.WriteAllText(dialog.FileName, content);
            StatusText.Text = $"Report exported to {dialog.FileName}.";
            WriteLog("report.exported", $"Format={(html ? "html" : "json")}; Redacted={redact}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Report export failed: {exception.Message}";
            WriteLog("report.export_failed", exception.Message);
        }
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Logs can contain diagnostic targets and local file paths. Export them anyway?",
                "Export application logs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "SockTuner JSON Lines log (*.jsonl)|*.jsonl",
            DefaultExt = ".jsonl",
            AddExtension = true,
            FileName = $"SockTuner-log-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            AppLog.Export(dialog.FileName);
            StatusText.Text = $"Log exported to {dialog.FileName}.";
            WriteLog("log.exported", dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Log export failed: {exception.Message}";
        }
    }

    /// <summary>
    /// A single spot test cannot answer "has this got worse". Only the history can, and only when
    /// there are enough comparable runs on each side of the window.
    /// </summary>
    private void CheckDrift_Click(object sender, RoutedEventArgs e)
    {
        var report = BaselineAnalyzer.Compare(_history.ToArray(), TimeSpan.FromDays(7), DateTimeOffset.Now);
        var lines = new List<string> { report.Verdict };
        if (report.Comparable)
        {
            lines.Add($"{report.RecentRuns} recent run(s) against {report.BaselineRuns} earlier one(s).");
            lines.AddRange(report.Changes.Select(change => (change.Significant ? "• " : "  ") + change.Summary));
        }

        ComparisonText.Text = string.Join(Environment.NewLine, lines);
        WriteLog("baseline.checked", report.Verdict);
    }

    private void CompareHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().OrderBy(entry => entry.SavedAt).ToArray();
        if (selected.Length != 2)
        {
            ComparisonText.Text = "Select exactly two runs.";
            return;
        }

        var comparison = DiagnosticComparisonService.Compare(selected[0].Report, selected[1].Report);
        ComparisonText.Text = comparison.Comparable
            ? comparison.Reason + "\n" + string.Join("\n", comparison.Metrics.Select(metric => metric.Summary))
            : $"Not comparable: {comparison.Reason}";
    }

    private void TrendHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().ToArray();
        var trend = DiagnosticComparisonService.Trend(selected);
        ComparisonText.Text = trend.Comparable
            ? trend.Reason + "\n" + string.Join("\n", trend.Points.Select(point => point.Summary))
            : $"Not comparable: {trend.Reason}";
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().ToArray();
        if (selected.Length != 1)
        {
            ComparisonText.Text = "Select exactly one run to export.";
            return;
        }
        SaveReport(selected[0].Report, html: true, redact: true);
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().ToArray();
        try
        {
            foreach (var entry in selected)
            {
                _historyStore.Delete(entry.Id);
                _history.Remove(entry);
            }
            ComparisonText.Text = $"Deleted {selected.Length} run(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ComparisonText.Text = $"Delete failed: {exception.Message}";
        }
    }

    private void LogRetention_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LogRetentionComboBox.SelectedItem is not int megabytes)
        {
            return;
        }

        try
        {
            _preferences = _preferences with { LogFileMegabytes = megabytes };
            AppPreferences.Save(_preferences);
            var error = AppLog.ConfigureRetention(megabytes);
            PreferenceStatusText.Text = error is null
                ? $"Retention saved: two files of up to {megabytes} MB each."
                : $"Retention saved, but existing logs could not be trimmed: {error}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PreferenceStatusText.Text = $"Preference save failed: {exception.Message}";
        }
    }

    private void InventoryFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyInventoryFilter();

    private void InventoryTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, InventoryTabs))
        {
            _selectedInventoryItem = null;
            _selectedInventoryGrid = null;
        }
    }

    private void InventoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid)
        {
            return;
        }

        if (grid.SelectedItem is { } item)
        {
            _selectedInventoryItem = item;
            _selectedInventoryGrid = grid;
        }
        else if (ReferenceEquals(grid, _selectedInventoryGrid))
        {
            _selectedInventoryItem = null;
            _selectedInventoryGrid = null;
        }
    }

    private void CopyInventorySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInventoryItem is null)
        {
            StatusText.Text = "Select an inventory row to copy.";
            return;
        }

        CopyToClipboard(
            JsonSerializer.Serialize(_selectedInventoryItem, _selectedInventoryItem.GetType(), new JsonSerializerOptions { WriteIndented = true }),
            "Copied selected inventory row.");
    }

    private void ApplyInventoryFilter()
    {
        if (_snapshot is null)
        {
            return;
        }

        var query = InventoryFilterText.Text;
        RoutesGrid.ItemsSource = Filter(_snapshot.Routes, query);
        DnsInterfacesGrid.ItemsSource = Filter(_snapshot.Adapters.Where(adapter => adapter.Ipv4Index > 0 || adapter.Ipv6Index > 0), query);
        NetworkProfilesGrid.ItemsSource = Filter(_snapshot.NetworkProfiles ?? [], query);
        BindingsGrid.ItemsSource = Filter(_snapshot.NetworkBindings ?? [], query);
        GlobalOffloadsGrid.ItemsSource = Filter(_snapshot.GlobalOffloads ?? [], query);
        AdapterOffloadsGrid.ItemsSource = Filter(_snapshot.AdapterOffloads ?? [], query);
        TcpSettingsGrid.ItemsSource = Filter(_snapshot.TcpSettings ?? [], query);
        QosPoliciesGrid.ItemsSource = Filter(_snapshot.QosPolicies ?? [], query);
        WinsockProvidersGrid.ItemsSource = Filter(_snapshot.WinsockProviders ?? [], query);
        ApplyAdapterFilter();
        ApplyNdisFilter();
    }

    private static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string query) =>
        items.Where(item => item is not null && InventorySearch.Matches(item, query)).ToArray();

    private void AdapterFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyAdapterFilter();

    private void NdisFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyNdisFilter();

    // Both per-grid boxes reuse InventorySearch, which already walks every public property
    // (including the computed *Display ones) and so cannot fall behind when a field is added.
    private void ApplyAdapterFilter() =>
        AdaptersGrid.ItemsSource = Filter(
            Filter(_adapterRows, AdapterFilterText.Text), InventoryFilterText.Text);

    private void ApplyNdisFilter() =>
        NdisPropertiesGrid.ItemsSource = Filter(
            Filter(_ndisRows, NdisFilterText.Text), InventoryFilterText.Text);

    private void CopyAdapter_Click(object sender, RoutedEventArgs e)
    {
        if (AdaptersGrid.SelectedItem is not AdapterInfo adapter)
        {
            StatusText.Text = "Select an adapter to copy.";
            return;
        }

        CopyToClipboard(
            $"Name\t{adapter.Name}\nDescription\t{adapter.Description}\nID\t{adapter.Id}\nStatus\t{adapter.Status}\nKind\t{adapter.AdapterKindDisplay}\nType\t{adapter.InterfaceType}\nSpeed\t{adapter.SpeedDisplay}\nMTU\t{adapter.MtuDisplay}\nIPv4 index\t{adapter.Ipv4Index}\nIPv6 index\t{adapter.Ipv6Index}\nIP metrics\t{adapter.MetricDisplay}\nDefault routes\t{adapter.DefaultRoutePolicyDisplay}\nReceived\t{adapter.ReceivedDisplay}\nSent\t{adapter.SentDisplay}\nReceive issues\t{adapter.ReceiveIssuesDisplay}\nSend issues\t{adapter.SendIssuesDisplay}\nDriver\t{adapter.DriverDisplay}\nNDIS\t{adapter.NdisPropertyCountDisplay}\nProtocols\t{adapter.ProtocolsDisplay}\nInventory\t{adapter.InventoryStatus}\nAddresses\t{adapter.AddressesDisplay}\nGateways\t{adapter.GatewaysDisplay}\nDNS\t{adapter.DnsDisplay}",
            $"Copied adapter {adapter.Name}.");
    }

    private void CopyNdisProperty_Click(object sender, RoutedEventArgs e)
    {
        if (NdisPropertiesGrid.SelectedItem is not NdisPropertyRow property)
        {
            StatusText.Text = "Select an NDIS property to copy.";
            return;
        }

        CopyToClipboard(
            $"Adapter\t{property.Adapter}\nDriver\t{property.Driver}\nProperty\t{property.Property}\nKeyword\t{property.Keyword}\nCurrent\t{property.Current}\nDefault\t{property.Default}\nType\t{property.Type}\nValid values\t{property.ValidValues}",
            $"Copied {property.Keyword}.");
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = successMessage;
        }
        catch (ExternalException exception)
        {
            StatusText.Text = $"Clipboard unavailable: {exception.Message}";
        }
    }

    private async void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        var target = DiagnosticTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)
            || !int.TryParse(MonitorDurationText.Text, out var durationSeconds) || durationSeconds is < 1 or > 3600
            || !int.TryParse(MonitorIntervalText.Text, out var intervalMilliseconds) || intervalMilliseconds is < 100 or > 60_000)
        {
            SetMonitorStatus("Enter a target, duration 1–3600 seconds, and interval 100–60000 ms.");
            return;
        }

        _monitorCancellation?.Dispose();
        _monitorCancellation = new CancellationTokenSource();
        MonitorStartButton.IsEnabled = false;
        MonitorStopButton.IsEnabled = true;
        _monitorSamples.Clear();
        SetMonitorStatus("Monitoring…");
        try
        {
            _snapshot ??= await Task.Run(_inventory.Capture, _monitorCancellation.Token);
            var gateway = await _routeGatewayResolver.ResolveAsync(target, _snapshot, _monitorCancellation.Token);
            var resolvedTarget = IPAddress.TryParse(target, out _)
                ? target
                : (await Dns.GetHostAddressesAsync(target, _monitorCancellation.Token)).First().ToString();
            var targets = new List<MonitorTarget>
            {
                new("Reference", "1.1.1.1"),
                new("Game endpoint", resolvedTarget)
            };
            if (!string.IsNullOrWhiteSpace(gateway)) targets.Insert(0, new("Gateway", gateway));
            // Judged on a rolling window, not on single samples: one lost probe is not an outage,
            // and a user alerted on every one of them stops reading the alerts.
            var watchdog = new Watchdog(new WatchdogThresholds());
            var progress = new Progress<MonitorSample>(sample =>
            {
                if (_monitorSamples.Count == MonitorMaximumSamples) _monitorSamples.RemoveAt(0);
                _monitorSamples.Add(sample);
                if (watchdog.Observe(sample) is { } alert)
                {
                    SetMonitorStatus(alert.Summary);
                    WriteLog(alert.Open ? "watchdog.opened" : "watchdog.closed", alert.Summary);
                }
            });
            var report = await _monitor.RunAsync(
                targets,
                TimeSpan.FromSeconds(durationSeconds),
                TimeSpan.FromMilliseconds(intervalMilliseconds),
                TimeSpan.FromSeconds(1),
                MonitorMaximumSamples,
                progress,
                _monitorCancellation.Token);
            var window = report.SamplesTruncated ? $"Newest {report.Samples.Count}/{report.TotalSampleCount} samples: " : string.Empty;
            // A run that averages well can still have dropped out entirely for two seconds, which is
            // what a player actually feels. The episode list is the part worth reading.
            var stability = StabilityAnalyzer.Analyze(report);
            if (watchdog.Alerts.Count > 0)
            {
                WriteLog("watchdog.summary", $"{watchdog.Alerts.Count} alert(s); {watchdog.OpenAlerts.Count} still open at the end of the run.");
            }
            SetMonitorStatus(
                window
                + string.Join(" · ", report.Summaries.Select(summary => $"{summary.Label}: {summary.Summary}"))
                + Environment.NewLine + stability.Verdict
                + Environment.NewLine + PlayabilityAnalyzer.Availability(stability)
                + (stability.Episodes.Count == 0
                    ? string.Empty
                    : Environment.NewLine + string.Join(Environment.NewLine, stability.Episodes.Select(episode => "• " + episode.Summary))));
            WriteLog("monitor.completed", $"Target={target}; Duration={report.Duration.TotalSeconds:0.0}s; Samples={report.Samples.Count}; {stability.Verdict}");
        }
        catch (OperationCanceledException)
        {
            SetMonitorStatus($"Stopped; {_monitorSamples.Count} visible sample(s) retained.");
            WriteLog("monitor.canceled", $"Target={target}; Samples={_monitorSamples.Count}.");
        }
        catch (Exception exception)
        {
            SetMonitorStatus($"Monitoring failed: {exception.Message}");
            WriteLog("monitor.failed", exception.Message);
        }
        finally
        {
            MonitorStartButton.IsEnabled = true;
            MonitorStopButton.IsEnabled = false;
        }
    }

    private void SetMonitorStatus(string value)
    {
        MonitorStatusText.Text = value;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(MonitorStatusText)
            ?? new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(MonitorStatusText);
        peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void StopMonitor_Click(object sender, RoutedEventArgs e) => _monitorCancellation?.Cancel();

    private void CancelDiagnostic_Click(object sender, RoutedEventArgs e) => _diagnosticCancellation?.Cancel();

    private void SetDiagnosticBusy(bool isBusy)
    {
        SetBusy(isBusy);
        RunDiagnosticButton.IsEnabled = !isBusy;
        CancelDiagnosticButton.IsEnabled = isBusy;
        DiagnosticTargetText.IsEnabled = !isBusy;
        DiagnosticPortText.IsEnabled = !isBusy;
        DiagnosticProfileComboBox.IsEnabled = !isBusy;
        DiagnosticGameComboBox.IsEnabled = !isBusy;
        DiagnosticLoadComboBox.IsEnabled = !isBusy;
    }

    private void ClearDiagnosticResults(string value)
    {
        GatewayResultText.Text = value;
        ReferenceResultText.Text = value;
        GameResultText.Text = value;
        DnsResultText.Text = value;
        FindingsGrid.ItemsSource = null;
        DiagnosticStatisticsGrid.ItemsSource = null;
        DiagnosticSamplesGrid.ItemsSource = null;
        RouteSamplesGrid.ItemsSource = null;
        PlayabilityHeadlineText.Text = value;
        PlayabilityDetailText.Text = string.Empty;
        PlayabilityMetricsGrid.ItemsSource = null;
        PlayabilityTickText.Text = string.Empty;
    }

    private void ShowPlayability(ProbeStatistics gameEndpoint, GameProfile game)
    {
        var verdict = PlayabilityAnalyzer.Judge(gameEndpoint, game);
        PlayabilityHeadlineText.Text = $"{verdict.Headline} — decided by {verdict.DecidedBy}";
        PlayabilityDetailText.Text = verdict.Detail;
        PlayabilityMetricsGrid.ItemsSource = verdict.Metrics;
        PlayabilityTickText.Text = $"{game.DisplayName}: {game.TickDisplay} ({game.SourceDisplay}). {game.Evidence} "
            + $"Probes carried {game.PayloadBytes} bytes of payload.";
        WriteLog("playability.judged", $"Game={game.Id}; Grade={verdict.Grade}; DecidedBy={verdict.DecidedBy}.");
    }

    private void WriteLog(string eventName, string message)
    {
        var error = AppLog.Write(eventName, message);
        LogStatusText.Text = error is null ? string.Empty : $"Logging unavailable: {error}";
    }

    private void ApplyDarkTitleBar()
    {
        var enabled = 1;
        var handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);

    /// <summary>
    /// Locates the first degrading segment and describes the shape of the path. Both are diagnosis
    /// over facts already collected plus one traceroute-quality pass; neither changes anything.
    /// </summary>
    private async Task ShowRoutingAnalysisAsync(
        GamingDiagnosticReport report, NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        RoutePathDiagnostic? route = null;
        try
        {
            route = await _routeQuality.RunAsync(
                report.RequestedTarget,
                RouteQualityProbe.DefaultRounds,
                RouteQualityProbe.DefaultMaximumHops,
                TimeSpan.FromSeconds(2),
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            // A blocked traceroute is normal on many paths. The segment walk still works without it.
            WriteLog("route.quality_unavailable", exception.Message);
        }

        var assessment = _bottleneck.Locate(new BottleneckInput(
            BuildLocalLinkEvidence(snapshot),
            report.Gateway,
            report.Reference,
            report.GameTarget,
            route));

        var lines = new List<string> { assessment.Title, $"Segment: {assessment.Segment}. Confidence: {assessment.Confidence}. Owner: {assessment.Owner}." };
        lines.AddRange(assessment.Supporting.Select(item => "• " + item));
        if (assessment.Contradicting.Count > 0)
        {
            lines.Add("Against this reading:");
            lines.AddRange(assessment.Contradicting.Select(item => "• " + item));
        }

        BottleneckResultText.Text = string.Join(Environment.NewLine, lines);

        var topology = TopologyAnalyzer.Analyze(new TopologyInput(
            route,
            route?.Hops.FirstOrDefault(hop => hop.AddressKind == HopAddressKind.Private)?.Address,
            report.FirstPublicBoundary,
            report.PathMtu,
            snapshot.Adapters.FirstOrDefault(adapter => adapter.Ipv4Mtu > 0)?.Ipv4Mtu));

        var topologyLines = new List<string> { $"NAT topology: {topology.Topology}." };
        topologyLines.AddRange(topology.Findings.Select(finding => $"• {finding.Title} — {finding.Evidence} {finding.Action}".TrimEnd()));
        if (topology.Findings.Count == 0) topologyLines.Add("No NAT or path-MTU problem stood out in this run.");
        TopologyResultText.Text = string.Join(Environment.NewLine, topologyLines);
    }

    private static LocalLinkEvidence BuildLocalLinkEvidence(NetworkSnapshot snapshot)
    {
        var adapter = snapshot.Adapters
            .Where(item => item.Kind == AdapterKind.Physical && item.Counters is not null)
            .OrderByDescending(item => item.Status == System.Net.NetworkInformation.OperationalStatus.Up)
            .ThenByDescending(item => item.Counters!.BytesReceived)
            .FirstOrDefault();
        if (adapter?.Counters is not { } counters)
        {
            return LocalLinkEvidence.Healthy;
        }

        return new LocalLinkEvidence(
            adapter.Status == System.Net.NetworkInformation.OperationalStatus.Up,
            adapter.SpeedBitsPerSecond,
            counters.IncomingPacketsWithErrors,
            counters.IncomingPacketsDiscarded,
            counters.OutgoingPacketsWithErrors,
            counters.OutgoingPacketsDiscarded,
            adapter.InterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211);
    }

    private void CancelThroughput_Click(object sender, RoutedEventArgs e)
    {
        _throughputCancellation?.Cancel();
        StatusText.Text = "Stopping the transfer…";
    }

    private async void RunThroughput_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = ThroughputEndpointText.Text.Trim();
        var latencyTarget = LoadedLatencyTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(latencyTarget))
        {
            StatusText.Text = "Enter both a throughput endpoint and a latency target.";
            return;
        }

        if (!int.TryParse(ThroughputStreamsText.Text.Trim(), out var streams)
            || streams < 1 || streams > ThroughputProbe.MaximumStreams)
        {
            StatusText.Text = $"Streams must be between 1 and {ThroughputProbe.MaximumStreams}.";
            return;
        }

        if (!int.TryParse(ThroughputSecondsText.Text.Trim(), out var seconds)
            || seconds < 1 || seconds > ThroughputProbe.MaximumDuration.TotalSeconds)
        {
            StatusText.Text = $"Seconds per phase must be between 1 and {ThroughputProbe.MaximumDuration.TotalSeconds:0}.";
            return;
        }

        if (ThroughputDirectionComboBox.SelectedItem is not TransferDirection direction)
        {
            StatusText.Text = "Select a transfer direction.";
            return;
        }

        _throughputCancellation?.Dispose();
        _throughputCancellation = new CancellationTokenSource();
        SetThroughputBusy(true);
        BufferbloatGradeText.Text = "Measuring…";
        ThroughputResultText.Text = "Running…";
        LoadedLatencyResultText.Text = "Running…";
        BufferbloatAssessmentText.Text = "Running…";
        StatusText.Text = $"Measuring {direction.ToString().ToLowerInvariant()} throughput and loaded latency…";
        WriteLog("throughput.started", $"Endpoint={endpoint}; Latency={latencyTarget}; Direction={direction}; Streams={streams}; Seconds={seconds}.");

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

            _lastLoadedLatency = result;
            if (result.Direction == TransferDirection.Download) _lastDownload = result;
            else _lastUpload = result;
            ShowLoadedLatency(result, utilization);
            StatusText.Text = "Measurement complete.";
            WriteLog("throughput.completed", result.Summary);
        }
        catch (OperationCanceledException)
        {
            BufferbloatGradeText.Text = "Cancelled";
            BufferbloatAssessmentText.Text = "The run was cancelled before it could be graded.";
            StatusText.Text = "Measurement cancelled.";
            WriteLog("throughput.cancelled", "Cancelled by the operator.");
        }
        catch (Exception exception)
        {
            BufferbloatGradeText.Text = "Failed";
            BufferbloatAssessmentText.Text = exception.Message;
            StatusText.Text = "Measurement failed.";
            WriteLog("throughput.failed", exception.Message);
        }
        finally
        {
            SetThroughputBusy(false);
        }
    }

    private IReadOnlyList<LinkUtilization> BuildUtilization(
        IReadOnlyList<AdapterCounterSample> before, IReadOnlyList<AdapterCounterSample> after, TimeSpan elapsed)
    {
        var speeds = (_snapshot?.Adapters ?? [])
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
            : "Not gradable";

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
        if (DiagnosticGameComboBox.SelectedItem is GameProfile game)
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
        SetBusy(isBusy);
        RunThroughputButton.IsEnabled = !isBusy;
        CancelThroughputButton.IsEnabled = isBusy;
    }

    private void RemediationGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SendToPlanButton.IsEnabled = RemediationGrid.SelectedItem is RemediationAction { AppliesLocally: true };

    /// <summary>
    /// Turns what the last runs established into actions. Everything here is derived from facts
    /// already collected — nothing is measured, and nothing is written.
    /// </summary>
    private void BuildRecommendations_Click(object sender, RoutedEventArgs e)
    {
        var report = _lastReport;
        var measured = _lastDownload is not null || _lastUpload is not null;
        if (report is null && !measured)
        {
            RecommendationSummaryText.Text =
                "Nothing to derive from yet. Run a diagnosis, measure bufferbloat, or import an online "
                + "bufferbloat result — recommendations come from a measurement, never from a preset alone.";
            return;
        }

        try
        {
            var adapter = _snapshot?.Adapters.FirstOrDefault(item =>
                item.Kind == AdapterKind.Physical
                && item.Status == System.Net.NetworkInformation.OperationalStatus.Up
                && Guid.TryParse(item.Id, out _));
            var adapterId = adapter is not null && Guid.TryParse(adapter.Id, out var parsed) ? parsed : (Guid?)null;

            var capabilities = WindowsAdapterCapabilityInventory.Read().Capabilities;
            var globals = WindowsGlobalSettingInventory.Read().Capabilities;

            // The receive-window advice is computed from this path, so it is only offered when a
            // throughput run actually measured one.
            TcpPathMeasurement? path = null;
            var loadedForPath = _lastDownload ?? _lastUpload;
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

            var findings = report?.Findings ?? [];
            var actions = RemediationPlanner.Plan(findings, context).ToList();

            // The use-case profile is a deliberate preset rather than a finding, so it is added
            // explicitly and only when there is an adapter whose advertised keywords can carry it.
            if (UseCaseComboBox.SelectedItem is UseCaseProfile profile && adapterId is { } id)
            {
                actions.Insert(0, UseCaseProfiles.PlanFor(profile, id, capabilities));
            }

            RemediationGrid.ItemsSource = actions;
            SendToPlanButton.IsEnabled = false;
            SendAllToPlanButton.IsEnabled = actions.Any(action => action.AppliesLocally);

            var wifi = ShowWifiRadio();
            ShowRouterGuidance(wifi);

            var local = actions.Count(action => action.AppliesLocally);
            var basis = report is null
                ? $"an imported {_importedBufferbloat?.SourceDisplay ?? "bufferbloat"} result"
                : $"{findings.Count} finding(s)";
            RecommendationSummaryText.Text =
                $"{actions.Count} action(s) from {basis}: {local} this machine can make, "
                + $"{actions.Count - local} belonging elsewhere. Adapter: {adapter?.Name ?? "none selected"}.";
            WriteLog("recommendations.built", $"Actions={actions.Count}; Local={local}; Imported={report is null}.");
        }
        catch (Exception exception)
        {
            RecommendationSummaryText.Text = $"Recommendations could not be built: {exception.Message}";
            WriteLog("recommendations.failed", exception.Message);
        }
    }

    private WifiRadioInfo? ShowWifiRadio()
    {
        var inventory = WindowsWifiInventory.Read();
        if (!inventory.Supported || inventory.Radios.Count == 0)
        {
            WifiRadioText.Text = inventory.Error is { } error
                ? $"Wi-Fi radio inventory unavailable: {error}"
                : "No Wi-Fi radio is connected; this connection is wired.";
            return null;
        }

        var radio = inventory.Radios[0];
        var findings = WifiRadioAnalyzer.Analyze(radio);
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
        return radio;
    }

    private void ShowRouterGuidance(WifiRadioInfo? wifi)
    {
        var items = RouterGuidance.For(new RouterGuidanceInput(_lastDownload, _lastUpload, wifi));
        if (items.Count == 0)
        {
            RouterGuidanceText.Text = _lastDownload is null && _lastUpload is null
                ? "Run a bufferbloat measurement first: shaping advice is computed from the rate this connection actually reached, never from the advertised one."
                : "Nothing measured here needs a router change.";
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
            StatusText.Text = "Select an action that this machine can make.";
            return;
        }

        var accepted = TuningPlan.Propose(action.Changes);
        StatusText.Text = accepted == action.Changes.Count
            ? $"Sent {accepted} change(s) to the tuning plan. Nothing is applied until you review and confirm there."
            : $"Sent {accepted} of {action.Changes.Count} change(s); the rest are not advertised for the selected adapter.";
        WriteLog("recommendations.sent_to_plan", $"Action={action.Id}; Accepted={accepted}/{action.Changes.Count}.");
    }

    private void CancelDnsBenchmark_Click(object sender, RoutedEventArgs e)
    {
        _dnsBenchmarkCancellation?.Cancel();
        StatusText.Text = "Stopping the resolver benchmark\u2026";
    }

    private async void RunDnsBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DnsRoundsText.Text.Trim(), out var rounds) || rounds is < 1 or > 10)
        {
            StatusText.Text = "Queries per name must be between 1 and 10.";
            return;
        }

        var candidates = BuildResolverCandidates();
        if (candidates.Count == 0)
        {
            StatusText.Text = "No resolver to measure.";
            return;
        }

        _dnsBenchmarkCancellation?.Dispose();
        _dnsBenchmarkCancellation = new CancellationTokenSource();
        SetBusy(true);
        RunDnsBenchmarkButton.IsEnabled = false;
        CancelDnsBenchmarkButton.IsEnabled = true;
        DnsVerdictText.Text = "Measuring\u2026";
        DnsResolverGrid.ItemsSource = null;
        StatusText.Text = $"Benchmarking {candidates.Count} resolver(s)\u2026";
        WriteLog("dns.benchmark_started", $"Resolvers={candidates.Count}; Rounds={rounds}.");

        try
        {
            var report = await _dnsBenchmark.RunAsync(
                candidates,
                DnsBenchmarkProbe.DefaultHostnames,
                rounds,
                DnsBenchmarkProbe.DefaultTimeout,
                _dnsBenchmarkCancellation.Token);

            DnsResolverGrid.ItemsSource = report.Results
                .OrderBy(result => result.Usable ? 0 : 1)
                .ThenBy(result => result.MedianMs ?? double.MaxValue)
                .ToArray();
            DnsVerdictText.Text = report.Verdict;
            _lastDnsReport = report;
            ApplyBestDnsButton.IsEnabled = WorthApplying(report);
            StatusText.Text = "Resolver benchmark complete.";
            WriteLog("dns.benchmark_completed", report.Verdict);

            if (DnsAutoApplyCheck.IsChecked == true && WorthApplying(report))
            {
                DnsApplyStatusText.Text = "A worthwhile resolver was found; applying automatically\u2026";
                await ApplyDnsAsync(report.Fastest!.Resolver.Address);
            }
        }
        catch (OperationCanceledException)
        {
            DnsVerdictText.Text = "Cancelled before every resolver was measured.";
            StatusText.Text = "Resolver benchmark cancelled.";
            WriteLog("dns.benchmark_cancelled", "Cancelled by the operator.");
        }
        catch (Exception exception)
        {
            DnsVerdictText.Text = $"Benchmark failed: {exception.Message}";
            StatusText.Text = "Resolver benchmark failed.";
            WriteLog("dns.benchmark_failed", exception.Message);
        }
        finally
        {
            SetBusy(false);
            RunDnsBenchmarkButton.IsEnabled = true;
            CancelDnsBenchmarkButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// The well-known list, the resolvers this machine is actually configured to use, and anything
    /// typed in. Without the ones in use there is nothing to compare against, so a "faster" result
    /// would have no meaning.
    /// </summary>
    private IReadOnlyList<DnsResolverCandidate> BuildResolverCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<DnsResolverCandidate>();

        foreach (var address in (_snapshot?.Adapters ?? [])
            .Where(adapter => adapter.Status == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(adapter => adapter.DnsServers))
        {
            if (IPAddress.TryParse(address, out var parsed)
                && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && seen.Add(address))
            {
                candidates.Add(new DnsResolverCandidate("Currently configured", address, InUse: true));
            }
        }

        foreach (var extra in DnsExtraResolversText.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(extra)) candidates.Add(new DnsResolverCandidate("Custom", extra));
        }

        foreach (var known in DnsBenchmarkProbe.WellKnown)
        {
            if (seen.Add(known.Address)) candidates.Add(known);
        }

        return candidates;
    }

    /// <summary>
    /// Whether a measured winner is worth acting on. The same noise floor the verdict uses: a gain
    /// under 5 ms is inside run-to-run variation, and acting on it automatically would be churn.
    /// </summary>
    private static bool WorthApplying(DnsBenchmarkReport report) =>
        report.Fastest is not null
        && !ReferenceEquals(report.Fastest, report.Current)
        && report.ImprovementMs is >= 5;

    private async void ApplyBestDns_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDnsReport?.Fastest is not { } fastest)
        {
            DnsApplyStatusText.Text = "Run a benchmark first.";
            return;
        }

        await ApplyDnsAsync(fastest.Resolver.Address);
    }

    private async void RevertDns_Click(object sender, RoutedEventArgs e) => await ApplyDnsAsync(null);

    /// <summary>
    /// Applies a resolver list, or restores DHCP when <paramref name="primary"/> is null. Goes
    /// through the same elevated worker and the same snapshot, verify and rollback path as every
    /// other change; nothing here writes directly.
    /// </summary>
    private async Task ApplyDnsAsync(string? primary)
    {
        if (DnsAdapterComboBox.SelectedItem is not AdapterInfo adapter || !Guid.TryParse(adapter.Id, out _))
        {
            DnsApplyStatusText.Text = "Select an adapter to change.";
            return;
        }

        var specification = new DnsServerSpecification();
        var store = new DnsServerStore();
        var address = specification.ResolveAddress(adapter.Id);

        try
        {
            var before = await store.ReadAsync(address, CancellationToken.None);
            var after = primary is null
                ? StoredSettingValue.Missing
                : new StoredSettingValue(true, DnsServerSpecification.Canonical([primary]));

            if (before == after)
            {
                DnsApplyStatusText.Text = primary is null
                    ? $"{adapter.Name} already takes its resolvers from DHCP."
                    : $"{adapter.Name} is already using {primary}.";
                return;
            }

            var request = new ElevatedWorkerRequest(
                ElevatedWorker.SchemaVersion,
                Guid.NewGuid(),
                WorkerOperationKind.Apply,
                [new WorkerSettingOperation(
                    address.SettingId,
                    address.TargetId,
                    new WorkerStoredValue(before.Exists, before.Value),
                    new WorkerStoredValue(after.Exists, after.Value),
                    ChangeSource.Manual)]);

            DnsApplyStatusText.Text = "Approve the Windows elevation prompt to continue\u2026";
            var response = await _dnsWorker.ExecuteAsync(request, CancellationToken.None);
            DnsApplyStatusText.Text = response.Status;
            WriteLog("dns.applied", $"Adapter={adapter.Name}; To={after.Value}; Success={response.Success}; {response.Status}");
            if (response.Success) await RefreshInventoryAsync();
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            DnsApplyStatusText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            DnsApplyStatusText.Text = $"Resolver change failed: {exception.Message}";
            WriteLog("dns.apply_failed", exception.Message);
        }
    }

    /// <summary>
    /// The initial pass: reads the inventory just captured and reports what it can already see,
    /// with the tab that acts on each finding. Pure and cheap, so it runs on every refresh without
    /// the user asking and without generating any traffic.
    /// </summary>
    private void ShowHealthCheck(NetworkSnapshot snapshot)
    {
        try
        {
            var findings = NetworkHealthAnalyzer.Analyze(snapshot, DateTimeOffset.Now);
            HealthGrid.ItemsSource = findings;
            OpenHealthSectionButton.IsEnabled = false;
            HealthSummaryText.Text = findings.Count == 0
                ? "Nothing stood out in the current inventory. This looks at configuration only; run a diagnosis to measure the path."
                : $"{findings.Count} finding(s) from the current inventory. "
                    + $"{findings.Count(finding => finding.Severity == ChangeRisk.High)} worth fixing, "
                    + $"{findings.Count(finding => finding.Severity == ChangeRisk.Medium)} worth checking. "
                    + "Select one to open the section that acts on it.";
        }
        catch (Exception exception)
        {
            HealthSummaryText.Text = $"Health check failed: {exception.Message}";
            WriteLog("health.failed", exception.Message);
        }
    }

    private void HealthGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenHealthSectionButton.IsEnabled = HealthGrid.SelectedItem is HealthFinding;


    // ---- Network devices -------------------------------------------------------------------

    /// <summary>
    /// How aggressively to read the advice. This is the one judgement in the analyzer that is a
    /// preference rather than a fact, so it is the one thing the profile controls; every other
    /// rule — and the refusal to touch the carrying adapter — is the same either way.
    /// </summary>
    private sealed record InterfaceProfile(string DisplayName, bool SinglePathPreferred);

    private static readonly IReadOnlyList<InterfaceProfile> InterfaceProfiles =
    [
        new("E-sports: one path only", true),
        new("Balanced: keep spare paths", false)
    ];

    private bool SinglePathPreferred =>
        (InterfaceProfileComboBox.SelectedItem as InterfaceProfile)?.SinglePathPreferred ?? true;

    private void ShowInterfaceAdvice()
    {
        var selectedId = (InterfaceAdviceGrid.SelectedItem as InterfaceAdvice)?.Adapter.Id;
        _interfaceAdvice.Clear();
        foreach (var advice in InterfaceAdvisor.Advise(_adapterRows, SinglePathPreferred))
        {
            _interfaceAdvice.Add(advice);
        }

        InterfaceAdviceGrid.SelectedItem =
            _interfaceAdvice.FirstOrDefault(item => item.Adapter.Id == selectedId);

        var flagged = _interfaceAdvice.Count(item => item.Verdict == InterfaceVerdict.ConsiderDisabling);
        var carrying = _interfaceAdvice.FirstOrDefault(item => item.Role == InterfaceRole.Carrying);
        var hidden = _adapterRows.Count(InterfaceAdvisor.IsOutOfScope);
        InterfaceSummaryAdviceText.Text = _interfaceAdvice.Count == 0
            ? "Refresh the inventory to list network devices."
            : $"{_interfaceAdvice.Count} network device(s); {hidden} filter and loopback pseudo-interface(s) hidden. "
              + $"{flagged} worth considering for disabling. "
              + (carrying is null
                  ? "No interface currently carries a default route, so nothing is protected as the way back in."
                  : $"{carrying.Name} carries the default route and is never offered for disabling.");
        UpdateInterfaceActions();
    }

    private void InterfaceProfile_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowInterfaceAdvice();
    }

    private void InterfaceAdvice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateInterfaceActions();

    /// <summary>
    /// Which actions the selected device allows. Disabling is offered only where the analyzer said
    /// so, which is what keeps the carrying adapter — and anything Windows owns — off the button.
    /// </summary>
    private void UpdateInterfaceActions()
    {
        var advice = InterfaceAdviceGrid.SelectedItem as InterfaceAdvice;
        var isDevice = advice is not null && advice.Role != InterfaceRole.NotApplicable;
        var isRealAdapter = isDevice && Guid.TryParse(advice!.Adapter.Id, out _);

        DisableInterfaceButton.IsEnabled = isRealAdapter && advice!.CanDisable;
        EnableInterfaceButton.IsEnabled = isRealAdapter && !advice!.Adapter.IsUp;
        PowerOffInterfaceButton.IsEnabled = isRealAdapter;
        PowerDefaultInterfaceButton.IsEnabled = isRealAdapter;

        if (advice is null)
        {
            InterfaceDetailHeadingText.Text = "Select a device";
            InterfaceDetailText.Text =
                "The reason behind each verdict, and what disabling the device would cost, are shown here.";
            return;
        }

        InterfaceDetailHeadingText.Text =
            $"{advice.Name}  ·  {advice.KindDisplay}  ·  {advice.RoleDisplay}  ·  {advice.VerdictDisplay}";
        InterfaceDetailText.Text =
            $"{advice.Reason}{Environment.NewLine}{advice.Evidence}"
            + $"{Environment.NewLine}Link {advice.Adapter.SpeedDisplay}, addresses {advice.Adapter.AddressesDisplay}, "
            + $"gateways {advice.Adapter.GatewaysDisplay}.";
    }

    private void QueueDisableInterface_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterStateSpecification.SettingId, AdapterStateSpecification.Disabled,
            "disable", requiresFlagged: true);

    private void QueueEnableInterface_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterStateSpecification.SettingId, AdapterStateSpecification.Enabled,
            "enable", requiresFlagged: false);

    private void QueuePowerOff_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterPowerSavingSpecification.SettingId,
            AdapterPowerSavingSpecification.PowerManagementOff.ToString(),
            "turn power management off on", requiresFlagged: false);

    /// <summary>
    /// Restoring the default removes the value rather than writing a zero, so a null proposal is
    /// what this queues — the same thing the transaction engine treats as absent.
    /// </summary>
    private void QueuePowerDefault_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterPowerSavingSpecification.SettingId, null,
            "restore the power-management default on", requiresFlagged: false);

    private void QueueDeviceChange(string settingId, string? value, string verb, bool requiresFlagged)
    {
        if (InterfaceAdviceGrid.SelectedItem is not InterfaceAdvice advice)
        {
            InterfaceActionStatusText.Text = "Select a device first.";
            return;
        }

        // Re-checked at the moment of the click, not only when the button was drawn: the advice can
        // have been rebuilt by a refresh since.
        if (requiresFlagged && !advice.CanDisable)
        {
            InterfaceActionStatusText.Text =
                $"{advice.Name} is not offered for disabling: {advice.Reason}";
            return;
        }

        if (!Guid.TryParse(advice.Adapter.Id, out var adapterId))
        {
            InterfaceActionStatusText.Text = $"{advice.Name} has no adapter GUID to target.";
            return;
        }

        var accepted = TuningPlan.ProposeDeviceChanges(
            [new ChangeRequest(settingId, adapterId.ToString(), value, ChangeSource.Manual)]);
        InterfaceActionStatusText.Text = accepted == 1
            ? $"Queued: {verb} {advice.Name}. {TuningPlan.PendingDeviceChangeCount} device change(s) waiting. "
              + "Nothing is written until you preview and confirm it in the tuning plan."
            : $"Refused: this machine no longer offers {settingId} on {advice.Name}.";
        WriteLog("interfaces.queued", $"Setting={settingId}; Adapter={adapterId}; Accepted={accepted}.");
    }


    // ---- External bufferbloat results ------------------------------------------------------

    /// <summary>
    /// Reads an online bufferbloat export and restates it as this app's own measurement, so a
    /// result measured from outside the machine drives the same recommendations as a local run.
    /// </summary>
    private void ImportBufferbloatReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import an online bufferbloat result",
            Filter = "Bufferbloat results (*.csv;*.json)|*.csv;*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var report = BufferbloatReportImporter.Load(dialog.FileName);
            _importedBufferbloat = report;
            _lastDownload = report.Download ?? _lastDownload;
            _lastUpload = report.Upload ?? _lastUpload;
            _lastLoadedLatency = report.Download ?? report.Upload ?? _lastLoadedLatency;

            ShowImportedBufferbloat(report);
            ImportedReportRecommendButton.IsEnabled = true;
            StatusText.Text = $"Imported a {report.SourceDisplay} result.";
            WriteLog("bufferbloat.imported",
                $"Source={report.Source}; Test={report.TestId}; Derived={report.DerivedGrade}; Reported={report.ReportedGrade}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                          or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            ImportedBufferbloatText.Text = $"Import refused: {exception.Message}";
            ImportedReportRecommendButton.IsEnabled = false;
            StatusText.Text = "Bufferbloat import failed.";
            WriteLog("bufferbloat.import_failed", exception.Message);
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
            $"Imported from {report.SourceDisplay}. Latency increase under load is graded on the Waveform scale; "
            + "it describes queue growth in front of the slowest link, not link speed.";
    }

    /// <summary>Builds the recommendations from the import and moves to the tab that shows them.</summary>
    private void RecommendFromImport_Click(object sender, RoutedEventArgs e)
    {
        BuildRecommendations_Click(sender, e);
        SelectTab("Recommendations");
    }

    /// <summary>
    /// The automatic path: every action this machine can make, queued in one click. It stops where
    /// the manual path stops — on the tuning plan — because the preview, the read-back check and
    /// the typed confirmation are what make any of this reversible, and no button skips them.
    /// </summary>
    private void SendAllRecommendationsToPlan_Click(object sender, RoutedEventArgs e)
    {
        if (RemediationGrid.ItemsSource is not IEnumerable<RemediationAction> actions)
        {
            StatusText.Text = "Build recommendations first.";
            return;
        }

        var applicable = actions.Where(action => action.AppliesLocally).ToArray();
        if (applicable.Length == 0)
        {
            StatusText.Text = "Nothing here is a change this machine can make.";
            return;
        }

        var requested = 0;
        var accepted = 0;
        foreach (var action in applicable)
        {
            requested += action.Changes.Count;
            accepted += TuningPlan.Propose(action.Changes);
        }

        StatusText.Text = accepted == requested
            ? $"Sent {accepted} change(s) from {applicable.Length} action(s) to the tuning plan. "
              + "Nothing is applied until you preview and confirm it there."
            : $"Sent {accepted} of {requested} change(s); the rest are not advertised by the selected adapter's driver.";
        WriteLog("recommendations.sent_all", $"Actions={applicable.Length}; Accepted={accepted}/{requested}.");
        if (accepted > 0) SelectTab("Tuning plan");
    }

    private void OpenTuningPlan_Click(object sender, RoutedEventArgs e) => SelectTab("Tuning plan");

    /// <summary>
    /// Opens a Windows management console. Both targets are fixed strings in this method — nothing
    /// the user or a report supplies reaches a process start.
    /// </summary>
    private void LaunchWindowsConsole(string target, string description)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            StatusText.Text = $"Opened {description}.";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Could not open {description}: {exception.Message}";
        }
    }

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e) =>
        LaunchWindowsConsole("devmgmt.msc", "Device Manager");

    private void OpenNetworkConnections_Click(object sender, RoutedEventArgs e) =>
        LaunchWindowsConsole("ncpa.cpl", "Network Connections");

    private void SelectTab(string header)
    {
        var tab = InventoryTabs.Items
            .OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));
        if (tab is not null) InventoryTabs.SelectedItem = tab;
    }

    private void OpenHealthSection_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is not HealthFinding finding) return;

        var tab = InventoryTabs.Items
            .OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(item => string.Equals(
                item.Header?.ToString()?.Replace("_", string.Empty),
                finding.Section,
                StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            StatusText.Text = $"No section named {finding.Section}.";
            return;
        }

        InventoryTabs.SelectedItem = tab;
        StatusText.Text = finding.Action;
    }

    /// <summary>
    /// Opens a reference in the default browser. The URL comes from the fixed list rather than from
    /// the control, and only https is launched, so a link can never become a way to start something
    /// local.
    /// </summary>
    private void OpenReference_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string url }) return;
        if (!ReferenceLinks.All.Any(link => string.Equals(link.Url, url, StringComparison.Ordinal)))
        {
            ReferenceStatusText.Text = "That link is not one of the listed references.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            ReferenceStatusText.Text = $"Opened {url} in your browser.";
        }
        catch (Exception exception)
        {
            ReferenceStatusText.Text = $"Could not open the link: {exception.Message}";
        }
    }

    /// <summary>
    /// Loads a capture report produced by an external analyzer. The file is untrusted input: it is
    /// size-limited, parsed defensively, and nothing in it is executed or turned into a path.
    /// </summary>
    private void ImportGameReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a capture report",
            Filter = "Capture report (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > GameReportImporter.MaximumBytes)
            {
                ImportedReportText.Text = $"That file is larger than {GameReportImporter.MaximumBytes / (1024 * 1024)} MB and was not read.";
                return;
            }

            var report = GameReportImporter.Parse(File.ReadAllText(dialog.FileName));
            var findings = GameReportImporter.Analyze(report);

            _importedReport = report;
            ImportedReportText.Text = report.Summary
                + (report.CapturedAt == DateTimeOffset.MinValue ? string.Empty : $"  Captured {report.CapturedAt:yyyy-MM-dd HH:mm}.")
                + (report.Scores.Count == 0
                    ? string.Empty
                    : "  Reported grades: " + string.Join(", ", report.Scores.Select(score => $"{score.Key} {score.Value}")) + ".");
            ImportedFindingsGrid.ItemsSource = findings;
            ImportedFindingsGrid.Visibility = Visibility.Visible;
            UseReportTargetButton.IsEnabled = !string.IsNullOrWhiteSpace(report.DiagnosticTarget);
            WriteLog("report.imported", $"Game={report.Game}; Target={report.DiagnosticTarget}; Findings={findings.Count}.");
        }
        catch (Exception exception)
        {
            _importedReport = null;
            UseReportTargetButton.IsEnabled = false;
            ImportedFindingsGrid.Visibility = Visibility.Collapsed;
            ImportedReportText.Text = $"That file could not be read as a capture report: {exception.Message}";
            WriteLog("report.import_failed", exception.Message);
        }
    }

    /// <summary>
    /// Points the diagnosis at the server the capture saw. This is the whole point of importing:
    /// the report supplies the target, and SockTuner then measures it directly.
    /// </summary>
    private void UseReportTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_importedReport?.DiagnosticTarget is not { } target) return;

        DiagnosticTargetText.Text = target;
        if (!string.IsNullOrWhiteSpace(_importedReport.RemotePort)) DiagnosticPortText.Text = _importedReport.RemotePort;
        LoadedLatencyTargetText.Text = target;
        var tick = SelectImportedTickRate(_importedReport);
        StatusText.Text = $"Diagnosis target set to {target} from the imported report.{tick} Nothing has been measured yet.";
    }

    /// <summary>
    /// Carries the capture's own tick rate over to the live measurement, so the endpoint the report
    /// found is judged against the cadence the report saw rather than against whatever was selected
    /// beforehand. A rate that is not a catalogue title is added as its own entry rather than being
    /// snapped to the nearest one.
    /// </summary>
    private string SelectImportedTickRate(GameFlowReport report)
    {
        if (report.ExpectedTickMs is not { } tickMs || tickMs <= 0)
        {
            return " It carries no tick rate, so the game profile is unchanged.";
        }

        var profile = GameProfile.FromTickIntervalMs(report.Game, tickMs);
        if (!_gameProfiles.Contains(profile))
        {
            // Only ever one imported entry: a second import replaces the first rather than
            // accumulating stale rates the user has to tell apart.
            for (var index = _gameProfiles.Count - 1; index >= 0; index--)
            {
                if (_gameProfiles[index].Id == "imported") _gameProfiles.RemoveAt(index);
            }

            _gameProfiles.Insert(0, profile);
        }

        DiagnosticGameComboBox.SelectedItem = _gameProfiles.First(item => item == profile);
        return $" Judging against {profile.DisplayName} at {profile.TickRateHz:0.#} Hz, from the report.";
    }

    // ---- Interrupt affinity ---------------------------------------------------------------

    private void IrqRescan_Click(object sender, RoutedEventArgs e) => LoadInterruptAffinity();

    private void IrqFilter_Changed(object sender, RoutedEventArgs e) => ShowInterruptDevices();

    private void LoadInterruptAffinity()
    {
        _interrupts = InterruptAffinityInventory.Read();
        if (_interrupts.Error is { } error)
        {
            IrqSummaryText.Text = $"Device scan failed: {error}";
            return;
        }

        _coreChoices.Clear();
        for (var core = 0; core < _interrupts.LogicalProcessors; core++)
        {
            _coreChoices.Add(new CoreChoice(core));
        }

        ShowInterruptDevices();
    }

    private void ShowInterruptDevices()
    {
        if (_interrupts is not { } inventory) return;

        var devices = inventory.Devices.AsEnumerable();
        if (IrqNetworkOnlyCheck.IsChecked == true) devices = devices.Where(device => device.IsNetwork);
        if (IrqOverriddenOnlyCheck.IsChecked == true) devices = devices.Where(device => device.HasOverride);

        var rows = devices.ToArray();
        IrqDeviceGrid.ItemsSource = rows;
        var overridden = inventory.Devices.Count(device => device.HasOverride);
        IrqSummaryText.Text =
            $"{rows.Length} device(s) shown of {inventory.Devices.Count} present. "
            + $"{overridden} currently carry an override; the rest are placed by Windows. "
            + $"This machine reports {inventory.LogicalProcessors} logical processors.";
    }

    private void IrqDeviceGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = IrqDeviceGrid.SelectedItem as InterruptAffinityDevice;
        IrqApplyButton.IsEnabled = selected is not null;
        IrqResetButton.IsEnabled = selected is { HasOverride: true };
        if (selected is null)
        {
            IrqSelectedDeviceText.Text = "Select a device above.";
            return;
        }

        IrqSelectedDeviceText.Text =
            $"{selected.FriendlyName}  —  {selected.StateDisplay}. Instance: {selected.InstanceId}";

        // Start from what the device already has, so applying without touching anything is a no-op
        // rather than a silent change to whatever the controls happened to show.
        var current = selected.Cores.ToHashSet();
        foreach (var choice in _coreChoices) choice.Selected = current.Contains(choice.Core);

        var wanted = selected.Policy == InterruptPolicy.MachineDefault
            ? InterruptPolicy.SpecifiedProcessors
            : selected.Policy;
        IrqPolicyComboBox.SelectedItem = IrqPolicyComboBox.ItemsSource
            .OfType<PolicyChoice>()
            .FirstOrDefault(choice => choice.Policy == wanted);
        IrqPriorityComboBox.SelectedItem = selected.Priority;
        UpdateIrqWarning();
    }

    private void IrqPolicy_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var specified = IrqPolicyComboBox.SelectedItem is PolicyChoice { Policy: InterruptPolicy.SpecifiedProcessors };
        IrqCoreList.IsEnabled = specified;
        UpdateIrqWarning();
    }

    /// <summary>
    /// Says the two things that actually go wrong: pinning to CPU 0, which already carries most of
    /// the system's deferred work, and stacking several devices onto one core.
    /// </summary>
    private void UpdateIrqWarning()
    {
        var warnings = new List<string>();
        var chosen = _coreChoices.Where(choice => choice.Selected).Select(choice => choice.Core).ToArray();

        if (IrqPolicyComboBox.SelectedItem is PolicyChoice { Policy: InterruptPolicy.SpecifiedProcessors })
        {
            if (chosen.Length == 0)
            {
                warnings.Add("Select at least one processor, or the device would have no core to run on.");
            }
            else if (chosen.Contains(0))
            {
                warnings.Add("CPU 0 already services most of the system's deferred work by default; it is rarely the right place to add more.");
            }

            if (chosen.Length == 1 && _interrupts is { Devices.Count: > 0 })
            {
                var sharing = _interrupts.Devices.Count(device =>
                    device.Policy == InterruptPolicy.SpecifiedProcessors && device.Cores.Contains(chosen[0]));
                if (sharing > 0)
                {
                    warnings.Add($"{sharing} other device(s) are already pinned to CPU {chosen[0]}.");
                }
            }
        }

        IrqWarningText.Text = string.Join(Environment.NewLine, warnings);
        IrqWarningText.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void IrqApply_Click(object sender, RoutedEventArgs e)
    {
        if (IrqDeviceGrid.SelectedItem is not InterruptAffinityDevice device) return;
        if (IrqPolicyComboBox.SelectedItem is not PolicyChoice { Policy: var policy }
            || IrqPriorityComboBox.SelectedItem is not InterruptPriority priority)
        {
            IrqStatusText.Text = "Select a policy and priority.";
            return;
        }

        var mask = policy == InterruptPolicy.SpecifiedProcessors
            ? InterruptAffinityDevice.ToMask(_coreChoices.Where(choice => choice.Selected).Select(choice => choice.Core))
            : 0UL;

        await ApplyInterruptAffinityAsync(
            device, new StoredSettingValue(true, InterruptAffinitySpecification.Canonical(policy, priority, mask)));
    }

    private async void IrqReset_Click(object sender, RoutedEventArgs e)
    {
        if (IrqDeviceGrid.SelectedItem is not InterruptAffinityDevice device) return;
        await ApplyInterruptAffinityAsync(device, StoredSettingValue.Missing);
    }

    private async Task ApplyInterruptAffinityAsync(InterruptAffinityDevice device, StoredSettingValue desired)
    {
        if (_interrupts is not { } inventory) return;

        try
        {
            var specification = new InterruptAffinitySpecification(
                Math.Max(inventory.LogicalProcessors, 1),
                inventory.Devices.Select(item => item.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (desired.Exists) specification.Validate(desired.Value);

            var address = specification.ResolveAddress(device.InstanceId);
            var store = new InterruptAffinityStore(specification);
            var before = await store.ReadAsync(address, CancellationToken.None);
            if (before == desired)
            {
                IrqStatusText.Text = $"{device.FriendlyName} already has that placement.";
                return;
            }

            var request = new ElevatedWorkerRequest(
                ElevatedWorker.SchemaVersion,
                Guid.NewGuid(),
                WorkerOperationKind.Apply,
                [new WorkerSettingOperation(
                    address.SettingId,
                    address.TargetId,
                    new WorkerStoredValue(before.Exists, before.Value),
                    new WorkerStoredValue(desired.Exists, desired.Value),
                    ChangeSource.Manual)]);

            IrqStatusText.Text = "Approve the Windows elevation prompt to continue\u2026";
            var response = await _irqWorker.ExecuteAsync(request, CancellationToken.None);
            IrqStatusText.Text = response.Success
                ? $"{response.Status} The new placement takes effect after a restart."
                : response.Status;
            WriteLog("irq.applied",
                $"Device={device.FriendlyName}; To={(desired.Exists ? desired.Value : "default")}; Success={response.Success}");
            if (response.Success) LoadInterruptAffinity();
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            IrqStatusText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            IrqStatusText.Text = $"Interrupt affinity change failed: {exception.Message}";
            WriteLog("irq.apply_failed", exception.Message);
        }
    }

    /// <summary>A policy with the wording shown to the user rather than its enum name.</summary>
    private sealed record PolicyChoice(InterruptPolicy Policy)
    {
        public string Display => Policy switch
        {
            InterruptPolicy.SpecifiedProcessors => "Only the processors I select",
            InterruptPolicy.AllCloseProcessors => "All processors near the device",
            InterruptPolicy.OneCloseProcessor => "One processor near the device",
            InterruptPolicy.AllProcessorsInMachine => "Any processor",
            InterruptPolicy.SpreadMessagesAcrossAllProcessors => "Spread messages across all processors",
            _ => Policy.ToString()
        };
    }

    /// <summary>One selectable processor. Bound to the pill toggles.</summary>
    private sealed class CoreChoice(int core) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _selected;

        public int Core { get; } = core;
        public string Label => $"CPU {Core}";

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record NdisPropertyRow(
        string Adapter,
        string Driver,
        string Property,
        string Keyword,
        string Current,
        string Default,
        string Type,
        string ValidValues);
}
