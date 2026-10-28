using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;
using SockTuner.Views;

namespace SockTuner;

public partial class MainWindow : Window
{
    private const int MonitorMaximumSamples = 1000;

    /// <summary>The catalogue, plus whatever tick rate an imported capture brought with it.</summary>
    private readonly ObservableCollection<GameProfile> _gameProfiles = new(GameProfiles.All);

    private readonly SystemInventoryService _inventory = new();
    private readonly NetworkDiagnosticService _diagnostics = new();
    private readonly NetworkMonitorService _monitor = new();
    private readonly RouteGatewayResolver _routeGatewayResolver = new();
    private NetworkSnapshot? _snapshot;
    private GamingDiagnosticReport? _lastReport;
    private CancellationTokenSource? _diagnosticCancellation;
    private CancellationTokenSource? _monitorCancellation;
    private readonly RouteQualityProbe _routeQuality = new();
    private GameFlowReport? _importedReport;
    private readonly BottleneckLocator _bottleneck = new();

    // The run that was current when a plan was applied. Held so the identical run afterwards has
    // something to be compared against; this is the whole verify-after-apply loop.
    private GamingDiagnosticReport? _baselineBeforeApply;
    private readonly ObservableCollection<MonitorSample> _monitorSamples = [];
    private IReadOnlyList<AdapterInfo> _adapterRows = [];
    private IReadOnlyList<NdisPropertyRow> _ndisRows = [];
    private UserPreferences _preferences = new();
    private int _busyCount;
    private object? _selectedInventoryItem;
    private System.Windows.Controls.DataGrid? _selectedInventoryGrid;

    public MainWindow()
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        _preferences = AppPreferences.Load();
        WifiDiagnostics.PreferredInterfaceId = _preferences.WifiInterfaceId;
        AppLog.ConfigureRetention(_preferences.LogFileMegabytes);
        DiagnosticProfileComboBox.ItemsSource = DiagnosticProfiles.All;
        DiagnosticProfileComboBox.SelectedItem = DiagnosticProfiles.All[0];
        DiagnosticGameComboBox.ItemsSource = _gameProfiles;
        DiagnosticGameComboBox.SelectedItem = _gameProfiles[0];
        DiagnosticLoadComboBox.ItemsSource = Enum.GetValues<DiagnosticLoadCondition>();
        DiagnosticLoadComboBox.SelectedIndex = 0;
        QosProtocolComboBox.ItemsSource = new[] { "UDP", "TCP", "*" };
        QosProtocolComboBox.SelectedIndex = 0;
        ReferenceLinkList.ItemsSource = ReferenceLinks.All;
        MonitorSamplesGrid.ItemsSource = _monitorSamples;
        History.ExportRequested += (_, report) => SaveReport(report, html: true, redact: true);
        if (History.Latest?.Report is { } previousReport)
            ShowDashboardLatency(previousReport.GameTarget, live: false);
        else
            ShowDashboardLatency(ProbeStatistics.Calculate("Game endpoint", string.Empty, []), live: false);
        DrawChart(DashboardWifiCanvas, [], (Brush)FindResource("AccentFillColorDefaultBrush"));
        WifiDiagnostics.ObservationUpdated += (_, samples) => ShowDashboardWifi(samples);
        NatDiagnostics.StatusChanged += (_, text) => StatusText.Text = text;
        NatDiagnostics.BusyChanged += (_, busy) => SetBusy(busy);
        DnsResolvers.StatusChanged += (_, text) => StatusText.Text = text;
        DnsResolvers.BusyChanged += (_, busy) => SetBusy(busy);
        DnsResolvers.Applied += async (_, _) => await RefreshInventoryAsync();
        Interfaces.Plan = TuningPlan;
        Interfaces.StatusChanged += (_, text) => StatusText.Text = text;
        Interfaces.OpenPlanRequested += (_, _) => SelectTab("Tuning plan");
        Preferences.Snapshot = () => _snapshot;
        TuningPlan.ReportAdapterRequested += (_, _) => Preferences.CreateCompatibilityReport();
        Preferences.StatusChanged += (_, text) => StatusText.Text = text;
        Throughput.StatusChanged += (_, text) => StatusText.Text = text;
        Throughput.BusyChanged += (_, busy) => SetBusy(busy);
        Throughput.CurrentGame = () => DiagnosticGameComboBox.SelectedItem as GameProfile;
        Throughput.RecommendationsRequested += async (_, _) =>
        {
            await Recommendations.BuildAsync();
            SelectTab("Recommendations");
        };
        Recommendations.LastDiagnosis = () => _lastReport;
        Recommendations.Throughput = Throughput;
        Recommendations.Wifi = WifiDiagnostics;
        Recommendations.Plan = TuningPlan;
        Recommendations.StatusChanged += (_, text) => StatusText.Text = text;
        Recommendations.OpenPlanRequested += (_, _) => SelectTab("Tuning plan");
        // Consent is granted or withdrawn by whichever surface asked for it; the cached preferences
        // follow, so a later save of window geometry cannot quietly write the old consent back.
        WriteConsentDialog.Changed += (_, _) =>
        {
            _preferences = AppPreferences.Load();
            ShowWriteState();
        };
        TuningPlan.Applied += async (_, _) =>
        {
            CaptureVerificationBaseline();
            QuickRestartStatusText.Text = Loc.T("Restart to finish: some changes apply only after a restart.");
            await RefreshInventoryAsync();
            ShowRecoveryState();
        };
        RestoreWindowGeometry();
        Closing += (_, _) =>
        {
            CancelActiveWork();
            SaveWindowGeometry();
        };
        Loaded += async (_, _) =>
        {
            await RefreshInventoryAsync();
            InterruptAffinity.Load();
            await Preferences.Updates.CheckIfDueAsync();
        };
        PreferencesView.ApplyTheme(_preferences.Theme);
        if (_preferences.RememberSection && _preferences.SelectedSection is { Length: > 0 } section) SelectTab(section);
        AdvancedModeCheck.IsChecked = _preferences.AdvancedMode;
        ApplyMode();
        ShowWriteState();
        ShowRecoveryState();
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
        WriteStateText.Text = accepted ? Loc.T("Changes armed") : Loc.T("Inventory only");
        WriteStateText.ToolTip = accepted
            ? Loc.T("Change consent accepted. Applying still needs elevation, a preview, and a typed confirmation, and every change is snapshotted and reversible.")
            : Loc.T("Nothing can be written until you accept the change consent in the tuning plan. Everything else is read-only.");
        // The dashboard used to state flatly that mutations were disabled. That stopped being true
        // when the transaction path was unlocked, and a stale reassurance is worse than none.
        WorkflowWriteStateText.Text = accepted
            ? Loc.T("Changes are armed. Applying still needs elevation and a typed confirmation in the tuning plan.")
            : Loc.T("Nothing is written until you accept the change consent in the tuning plan.");

        // Windows 11 status pill: neutral with a lock while read-only, caution with a shield once armed.
        WriteStateIcon.Text = accepted ? "\uEA18" : "\uE72E";
        WriteStateBadge.Background = (Brush)FindResource(accepted ? "SystemFillColorCautionBackgroundBrush" : "SubtleFillColorSecondaryBrush");
        WriteStateBadge.BorderBrush = (Brush)FindResource(accepted ? "SystemFillColorCautionBrush" : "CardStrokeColorDefaultBrush");
        var foreground = (Brush)FindResource(accepted ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        WriteStateIcon.Foreground = foreground;
        WriteStateText.Foreground = foreground;
        TuningPlan.ShowLiveChanges(accepted);
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

            // On "Network details" remember the sub-page, so the next start reopens the same table.
            var current = InventoryTabs.SelectedItem is System.Windows.Controls.TabItem { Content: System.Windows.Controls.TabControl inner }
                ? inner.SelectedItem
                : InventoryTabs.SelectedItem;
            var selectedSection = current is System.Windows.Controls.TabItem tab ? SectionName(tab) : null;
            _preferences = AppPreferences.Update(preferences => preferences with
            {
                Window = new WindowGeometry(
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    WindowState == WindowState.Maximized),
                SelectedSection = selectedSection,
                WifiInterfaceId = WifiDiagnostics.SelectedInterfaceId ?? WifiDiagnostics.PreferredInterfaceId
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is not worth blocking a clean shutdown over.
            WriteLog("window.geometry_save_failed", exception.Message);
        }
    }

    private async void RefreshInventory_Click(object sender, RoutedEventArgs e) => await RefreshInventoryAsync();

    /// <summary>
    /// Shows the busy banner (large spinner plus the current step) while anything long is running. A counter rather than a
    /// flag because a diagnosis and a throughput run are separate surfaces that can overlap, and a
    /// flag would clear the bar the moment the first of them finished. Clamped, so a
    /// <c>finally</c> that unwinds a run which never started cannot drive it negative.
    /// </summary>
    private void SetBusy(bool isBusy)
    {
        _busyCount = Math.Max(0, _busyCount + (isBusy ? 1 : -1));
        BusyBanner.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<System.Windows.Controls.TabItem> SelectableTabs() =>
        [.. InventoryTabs.Items.OfType<System.Windows.Controls.TabItem>().Where(item => item.IsEnabled && item.Visibility == Visibility.Visible)];

    /// <summary>What a first-time user needs: look, improve, undo. Everything else is behind Advanced mode.</summary>
    private static readonly HashSet<string> BasicSections = new(StringComparer.Ordinal)
    {
        "Dashboard", "Gaming diagnostics", "Game ports & NAT", "Wi-Fi diagnostics", "Tuning plan"
    };

    /// <summary>
    /// Every destination in navigation order, the "Network details" sub-pages included right after
    /// their parent. Section names are what health findings and saved preferences route by.
    /// </summary>
    private IEnumerable<System.Windows.Controls.TabItem> AllSections() =>
        InventoryTabs.Items.OfType<System.Windows.Controls.TabItem>().Where(item => item.IsEnabled).SelectMany(item => item.Content is System.Windows.Controls.TabControl inner
            ? [item, .. inner.Items.OfType<System.Windows.Controls.TabItem>()]
            : new[] { item });

    // UiTranslator parks the English header in Tag; the analyzers name sections in English.

    private void HealthGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenHealthSection_Click(sender, e);

    private async Task RefreshInventoryAsync()
    {
        StatusText.Text = Loc.T("Reading Windows network inventory…");
        SetBusy(true);

        try
        {
            _snapshot = await Task.Run(_inventory.Capture);
            ShowSnapshot(_snapshot);
            StatusText.Text = Loc.F($"Inventory refreshed at {_snapshot.System.CapturedAt:HH:mm:ss}");
            WriteLog("inventory.completed", $"Captured {_snapshot.Adapters.Count} interfaces, {_snapshot.Routes.Count} IP routes, {_snapshot.NetworkProfiles?.Count ?? 0} network profiles, {_snapshot.NetworkBindings?.Count ?? 0} bindings, {_snapshot.AdapterOffloads?.Count ?? 0} adapter offload rows, {_snapshot.TcpSettings?.Count ?? 0} TCP templates, {_snapshot.QosPolicies?.Count ?? 0} QoS policies, {_snapshot.WinsockProviders?.Count ?? 0} Winsock providers, and {_snapshot.Adapters.Sum(adapter => adapter.NdisProperties.Count)} NDIS properties.");
        }
        catch (Exception exception)
        {
            StatusText.Text = Loc.F($"Inventory failed: {exception.Message}");
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
        PrivilegeText.Text = snapshot.System.IsAdministrator ? Loc.T("Elevated") : Loc.T("Standard user");
        OsText.Text = snapshot.System.OperatingSystem;
        BuildText.Text = snapshot.System.Version;
        MachineText.Text = snapshot.System.MachineName;
        CapturedText.Text = snapshot.System.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz");
        ShowHealthCheck(snapshot);
        _adapterRows = snapshot.Adapters;
        TuningPlan.SetAdapters(snapshot.Adapters);
        Interfaces.SetAdapters(snapshot.Adapters);
        DnsResolvers.SetAdapters(snapshot.Adapters);
        Throughput.SetAdapters(snapshot.Adapters);
        Recommendations.SetAdapters(snapshot.Adapters);
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
        NdisSummaryText.Text = Loc.F($"{_ndisRows.Count} advanced properties advertised across {snapshot.Adapters.Count(adapter => adapter.NdisSupported)} supported adapter(s). Raw keywords remain visible and no values are changed.");
        RoutesGrid.ItemsSource = snapshot.Routes;
        DnsInterfacesGrid.ItemsSource = snapshot.Adapters.Where(adapter => adapter.Ipv4Index > 0 || adapter.Ipv6Index > 0);
        InterfaceSummaryText.Text = snapshot.IpInterfaceInventoryError is null
            ? Loc.F($"{snapshot.Adapters.Sum(adapter => adapter.IpInterfaces?.Count ?? 0)} native IPv4/IPv6 interface row(s).")
            : Loc.F($"Interface metric inventory partial: {snapshot.IpInterfaceInventoryError}");
        var ipv4RouteCount = snapshot.Routes.Count(route => route.AddressFamily == "IPv4");
        var ipv6RouteCount = snapshot.Routes.Count(route => route.AddressFamily == "IPv6");
        RouteSummaryText.Text = snapshot.RouteInventoryError is null
            ? Loc.F($"{ipv4RouteCount} native IPv4 and {ipv6RouteCount} native IPv6 route(s).")
            : Loc.F($"Route inventory partial: {snapshot.RouteInventoryError}");
        NetworkProfilesGrid.ItemsSource = snapshot.NetworkProfiles ?? [];
        NetworkProfileSummaryText.Text = snapshot.NetworkProfileInventoryError is null
            ? Loc.F($"{snapshot.NetworkProfiles?.Count ?? 0} profile connection(s) from Windows Network List Manager.")
            : Loc.F($"Network profile inventory partial: {snapshot.NetworkProfileInventoryError}");
        BindingsGrid.ItemsSource = snapshot.NetworkBindings ?? [];
        BindingSummaryText.Text = snapshot.NetworkBindingInventoryError is null
            ? Loc.F($"{snapshot.NetworkBindings?.Count ?? 0} binding(s) from root/StandardCimv2. Inspection only; no protocol or filter is changed.")
            : Loc.F($"Network binding inventory partial: {snapshot.NetworkBindingInventoryError}");
        GlobalOffloadsGrid.ItemsSource = snapshot.GlobalOffloads ?? [];
        AdapterOffloadsGrid.ItemsSource = snapshot.AdapterOffloads ?? [];
        OffloadSummaryText.Text = snapshot.OffloadInventoryError is null
            ? Loc.F($"{snapshot.GlobalOffloads?.Count ?? 0} global and {snapshot.AdapterOffloads?.Count ?? 0} adapter setting row(s). Inspection only; no offload is changed.")
            : Loc.F($"Offload inventory partial: {snapshot.OffloadInventoryError}");
        TcpSettingsGrid.ItemsSource = snapshot.TcpSettings ?? [];
        TcpSettingSummaryText.Text = snapshot.TcpSettingInventoryError is null
            ? Loc.F($"{snapshot.TcpSettings?.Count ?? 0} TCP template row(s). Read-only values include local/group-policy sources and raw export fields.")
            : Loc.F($"TCP setting inventory partial: {snapshot.TcpSettingInventoryError}");
        QosPoliciesGrid.ItemsSource = snapshot.QosPolicies ?? [];
        QosPolicySummaryText.Text = snapshot.QosPolicyInventoryError is null
            ? Loc.F($"{snapshot.QosPolicies?.Count ?? 0} QoS policy row(s). Inspection only; no policy is created or changed.")
            : Loc.F($"QoS policy inventory partial: {snapshot.QosPolicyInventoryError}");
        WinsockProvidersGrid.ItemsSource = snapshot.WinsockProviders ?? [];
        WinsockSummaryText.Text = snapshot.WinsockInventoryError is null
            ? Loc.F($"{snapshot.WinsockProviders?.Count ?? 0} native protocol provider(s). Inspection only; repair remains separately gated.")
            : Loc.F($"Winsock inventory partial: {snapshot.WinsockInventoryError}");
        ApplyInventoryFilter();
        ShowRecoveryState();
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e) => await RunDiagnosticAsync();

    /// <summary>
    /// The diagnosis itself, awaitable so the verify-after-apply loop can run it and then read the
    /// result rather than racing the handler that fired it.
    /// </summary>
    private async Task RunDiagnosticAsync()
    {
        var target = DiagnosticTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText.Text = Loc.T("Enter a game server, regional endpoint, or IP address.");
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(DiagnosticPortText.Text))
        {
            if (!int.TryParse(DiagnosticPortText.Text, out var parsedPort) || parsedPort is <= 0 or > 65535)
            {
                StatusText.Text = Loc.T("TCP port must be between 1 and 65535.");
                return;
            }

            port = parsedPort;
        }

        if (DiagnosticProfileComboBox.SelectedItem is not DiagnosticProfile profile
            || DiagnosticLoadComboBox.SelectedItem is not DiagnosticLoadCondition loadCondition)
        {
            StatusText.Text = Loc.T("Select a diagnostic profile.");
            return;
        }

        if (DiagnosticGameComboBox.SelectedItem is not GameProfile game)
        {
            StatusText.Text = Loc.T("Select a game tick rate.");
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
        DiagnosticRunSummaryText.Text = Loc.F($"{profile.DisplayName}: resolving {target}, then collecting {profile.SampleCount} concurrent samples per endpoint…");
        DashboardNetworkSummaryText.Text = Loc.F($"Live measurement of {target}; charts update only while this diagnostic runs.");
        ShowDashboardLatency(ProbeStatistics.Calculate("Game endpoint", target, []), live: true);
        var liveSamples = new List<ProbeSample>();
        var progress = new Progress<DiagnosticTimelineSample>(sample =>
        {
            if (sample.Label != "Game endpoint") return;
            liveSamples.Add(new(sample.Timestamp, sample.RoundTripTimeMs, sample.Detail, sample.FailureKind));
            ShowDashboardLatency(ProbeStatistics.Calculate("Game endpoint", target, liveSamples.ToArray()), live: true);
        });
        StatusText.Text = Loc.F($"Diagnosing {target}…");
        WriteLog("diagnostic.started", $"Target={target}; Port={port?.ToString() ?? "none"}; Profile={profile.Id}; Load={loadCondition}.");

        try
        {
            var beforeSnapshot = await Task.Run(_inventory.Capture, _diagnosticCancellation.Token);
            _diagnosticCancellation.Token.ThrowIfCancellationRequested();
            var gateway = await _routeGatewayResolver.ResolveAsync(target, beforeSnapshot, _diagnosticCancellation.Token);
            var beforeCounters = await Task.Run(_inventory.CaptureCounters, _diagnosticCancellation.Token);
            _diagnosticCancellation.Token.ThrowIfCancellationRequested();
            var report = await _diagnostics.RunAsync(
                target, gateway, port, profile, loadCondition, progress, _diagnosticCancellation.Token);
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
            var wifiInventory = await Task.Run(WindowsWifiInventory.Read, _diagnosticCancellation.Token);
            var wifiRadio = wifiInventory.Radios.FirstOrDefault(item => item.Connected)
                ?? wifiInventory.Radios.FirstOrDefault();
            var wifiDelta = FindWifiCounterDelta(report.CounterDeltas, wifiRadio);
            var wifiReport = GamingWifiDiagnosticEngine.Analyze(new(
                wifiRadio, Gateway: report.Gateway, CounterDelta: wifiDelta, CapturedAt: wifiInventory.CapturedAt));
            report = report with
            {
                Wifi = wifiReport,
                Findings = report.Findings.Concat(wifiReport.Findings)
                    .DistinctBy(finding => (finding.Title, finding.Evidence))
                    .ToArray()
            };
            WifiDiagnostics.SetGamingContext(report.Gateway, wifiDelta, wifiRadio?.InterfaceId);
            WifiDiagnostics.SetReport(wifiInventory, wifiReport);
            _lastReport = report;
            ShowDashboardLatency(report.GameTarget, live: false);
            History.Add(report);
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
            DiagnosticRunSummaryText.Text = Loc.F($"{report.Findings.Count} finding(s) for {report.RequestedTarget} from the {profile.DisplayName} profile ({report.Duration.TotalSeconds:0.0}s).{counterSummary}");
            StatusText.Text = Loc.F($"Diagnosis completed at {DateTimeOffset.Now:HH:mm:ss}");
            WriteLog("diagnostic.completed", $"Target={report.RequestedTarget}; Duration={report.Duration.TotalSeconds:0.0}s; Findings={report.Findings.Count}.");
        }
        catch (OperationCanceledException)
        {
            ClearDiagnosticResults("Canceled");
            DiagnosticRunSummaryText.Text = Loc.F($"Diagnosis for {target} canceled. Partial samples were discarded.");
            DashboardNetworkSummaryText.Text = Loc.T("Diagnosis canceled; partial chart samples were discarded.");
            ShowDashboardLatency(ProbeStatistics.Calculate("Game endpoint", target, []), live: false);
            StatusText.Text = Loc.T("Diagnosis canceled");
            WriteLog("diagnostic.canceled", $"Target={target}.");
        }
        catch (Exception exception)
        {
            ClearDiagnosticResults("Failed");
            DiagnosticRunSummaryText.Text = Loc.F($"Diagnosis for {target} failed: {exception.Message}");
            DashboardNetworkSummaryText.Text = Loc.F($"Network quality measurement failed: {exception.Message}");
            ShowDashboardLatency(ProbeStatistics.Calculate("Game endpoint", target, []), live: false);
            StatusText.Text = Loc.T("Diagnosis failed");
            WriteLog("diagnostic.failed", $"Target={target}; Error={exception.Message}");
        }
        finally
        {
            SetDiagnosticBusy(false);
            UpdateQuickStatus();
        }
    }

    private static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string query) =>
        items.Where(item => item is not null && InventorySearch.Matches(item, query)).ToArray();

    // Both per-grid boxes reuse InventorySearch, which already walks every public property
    // (including the computed *Display ones) and so cannot fall behind when a field is added.

    private void CancelDiagnostic_Click(object sender, RoutedEventArgs e) => _diagnosticCancellation?.Cancel();

    private void SetDiagnosticBusy(bool isBusy)
    {
        SetBusy(isBusy);
        QuickCheckSpinner.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        QuickCheckButton.IsEnabled = !isBusy;
        if (isBusy) QuickCheckStatusText.Text = Loc.T("Checking… about 15 seconds.");
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
        _lastGrade = null;
        _lastCheckAt = null;
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
        _lastGrade = verdict.Grade;
        _lastCheckAt = DateTimeOffset.Now;
        PlayabilityHeadlineText.Text = Loc.F($"{verdict.Headline} — decided by {verdict.DecidedBy}");
        PlayabilityDetailText.Text = verdict.Detail;
        PlayabilityMetricsGrid.ItemsSource = verdict.Metrics;
        PlayabilityTickText.Text = Loc.F($"{game.DisplayName}: {game.TickDisplay} ({game.SourceDisplay}). {game.Evidence} "
            + $"Probes carried {game.PayloadBytes} bytes of payload.");
        WriteLog("playability.judged", $"Game={game.Id}; Grade={verdict.Grade}; DecidedBy={verdict.DecidedBy}.");
    }

    private void WriteLog(string eventName, string message)
    {
        var error = AppLog.Write(eventName, message);
        LogStatusText.Text = error is null ? string.Empty : Loc.F($"Logging unavailable: {error}");
    }

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

    private static AdapterCounterDelta? FindWifiCounterDelta(
        IReadOnlyList<AdapterCounterDelta>? deltas, WifiRadioInfo? radio)
    {
        if (deltas is null || radio is null || !Guid.TryParse(radio.InterfaceId, out var interfaceId)) return null;
        return deltas.FirstOrDefault(delta =>
            Guid.TryParse(delta.AdapterId, out var adapterId) && adapterId == interfaceId);
    }

    private void CancelActiveWork()
    {
        _diagnosticCancellation?.Cancel();
        _monitorCancellation?.Cancel();
        Throughput.CancelActiveWork();
        DnsResolvers.CancelActiveWork();
        NatDiagnostics.CancelActiveWork();
        Preferences.Updates.CancelActiveWork();
        WifiDiagnostics.CancelActiveWork();
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
                ? Loc.T("Nothing stood out in the current inventory. This looks at configuration only; run a diagnosis to measure the path.")
                : Loc.F($"{findings.Count} finding(s) from the current inventory. "
                    + $"{findings.Count(finding => finding.Severity == ChangeRisk.High)} worth fixing, "
                    + $"{findings.Count(finding => finding.Severity == ChangeRisk.Medium)} worth checking. "
                    + $"Select one to open the section that acts on it.");
        }
        catch (Exception exception)
        {
            HealthSummaryText.Text = Loc.F($"Health check failed: {exception.Message}");
            WriteLog("health.failed", exception.Message);
        }
    }

    private void HealthGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenHealthSectionButton.IsEnabled = HealthGrid.SelectedItem is HealthFinding;

    // ---- Network devices -------------------------------------------------------------------

    // ---- Did the change help? ---------------------------------------------------------------

    /// <summary>
    /// Freezes the last diagnosis as the baseline for the next one. Called when a plan is applied,
    /// which is the only moment at which "before" is unambiguous.
    /// </summary>
    private void CaptureVerificationBaseline()
    {
        if (_lastReport is not { } report)
        {
            VerificationHeadlineText.Text = Loc.T("No baseline: nothing was measured before this change");
            VerificationDetailText.Text =
                Loc.T("A change was applied without a diagnosis to compare against, so whether it helped cannot be "
                + "established from here. Run a diagnosis now, apply the next change, and this will have a before.");
            return;
        }

        _baselineBeforeApply = report;
        ReRunDiagnosticButton.IsEnabled = true;
        ClearBaselineButton.IsEnabled = true;
        VerificationGrid.ItemsSource = null;
        VerificationHeadlineText.Text = Loc.T("Baseline captured");
        VerificationDetailText.Text =
            Loc.F($"The run against {report.RequestedTarget} at {report.StartedAt:HH:mm:ss} is the before. "
            + $"Re-run it with the same parameters to find out whether the change moved anything.");
        WriteLog("verify.baseline", $"Target={report.RequestedTarget}; At={report.StartedAt:O}.");
    }

    private void ClearBaseline_Click(object sender, RoutedEventArgs e)
    {
        _baselineBeforeApply = null;
        ReRunDiagnosticButton.IsEnabled = false;
        ClearBaselineButton.IsEnabled = false;
        VerificationGrid.ItemsSource = null;
        VerificationHeadlineText.Text = Loc.T("No baseline yet.");
        VerificationDetailText.Text =
            Loc.T("Run a diagnosis, then apply a plan. The run from before the change becomes the baseline automatically.");
    }

    /// <summary>
    /// Repeats the baseline run with exactly its parameters and reports what moved. The parameters
    /// are restored into the controls rather than passed around, so what runs is visibly the same
    /// diagnosis rather than a hidden one.
    /// </summary>
    private async void ReRunAndCompare_Click(object sender, RoutedEventArgs e)
    {
        if (_baselineBeforeApply is not { } baseline)
        {
            VerificationHeadlineText.Text = Loc.T("No baseline to compare against.");
            return;
        }

        DiagnosticTargetText.Text = baseline.RequestedTarget;
        DiagnosticProfileComboBox.SelectedItem = DiagnosticProfiles.All
            .FirstOrDefault(profile => profile == baseline.Profile) ?? DiagnosticProfileComboBox.SelectedItem;
        DiagnosticLoadComboBox.SelectedItem = baseline.LoadCondition;
        DiagnosticPortText.Text = baseline.Connection?.Port is { } port ? port.ToString() : string.Empty;
        if (baseline.Game is { } game)
        {
            DiagnosticGameComboBox.SelectedItem = _gameProfiles.FirstOrDefault(item => item == game)
                ?? DiagnosticGameComboBox.SelectedItem;
        }

        VerificationHeadlineText.Text = Loc.T("Re-running the baseline diagnosis…");
        VerificationDetailText.Text = Loc.T("Same target, same profile, same load condition, same port.");

        await RunDiagnosticAsync();

        if (_lastReport is not { } after || ReferenceEquals(after, baseline))
        {
            VerificationHeadlineText.Text = Loc.T("The re-run did not complete, so nothing can be concluded.");
            return;
        }

        ShowVerification(ApplyVerificationAnalyzer.Verify(baseline, after));
    }

    private void ShowVerification(ApplyVerification verification)
    {
        VerificationHeadlineText.Text = verification.Headline;
        VerificationDetailText.Text = verification.Detail;
        VerificationGrid.ItemsSource = verification.Metrics;
        VerificationHeadlineText.Foreground = verification.Outcome switch
        {
            ApplyOutcome.Improved => (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush"),
            ApplyOutcome.Worse => (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush"),
            ApplyOutcome.Mixed => (System.Windows.Media.Brush)FindResource("SystemFillColorCautionBrush"),
            _ => (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush")
        };

        if (verification.SuggestsRollback)
        {
            StatusText.Text = Loc.T("The re-run measured a regression. The audit history in the tuning plan can roll it back.");
        }

        WriteLog("verify.completed", $"Outcome={verification.Outcome}.");
    }

    // ---- QoS policies -----------------------------------------------------------------------

    internal static IReadOnlyList<string> NormalizeApplicationNames(IEnumerable<string?> names) => names
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void OpenTuningPlan_Click(object sender, RoutedEventArgs e) => SelectTab("Tuning plan");

    private async void UndoLastChange_Click(object sender, RoutedEventArgs e)
    {
        await TuningPlan.UndoLatestSuccessfulChangeAsync();
        ShowRecoveryState();
    }

    private void DashboardCheckDrift_Click(object sender, RoutedEventArgs e)
    {
        TuningPlan.CheckForDrift();
        SelectTab("Tuning plan");
    }

    private void ShowRecoveryState()
    {
        RecoverySummaryText.Text = TuningPlan.RecoverySummary;
        UndoLastChangeButton.IsEnabled = TuningPlan.CanUndoLastChange;
    }

    /// <summary>
    /// Loads a capture report produced by an external analyzer. The file is untrusted input: it is
    /// size-limited, parsed defensively, and nothing in it is executed or turned into a path.
    /// </summary>
    private void ImportGameReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Import a capture report"),
            Filter = Loc.T("Capture report (*.json)|*.json|All files (*.*)|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > GameReportImporter.MaximumBytes)
            {
                ImportedReportText.Text = Loc.F($"That file is larger than {GameReportImporter.MaximumBytes / (1024 * 1024)} MB and was not read.");
                return;
            }

            var report = GameReportImporter.Parse(File.ReadAllText(dialog.FileName));
            var findings = GameReportImporter.Analyze(report);

            _importedReport = report;
            ImportedReportText.Text = report.Summary
                + (report.CapturedAt == DateTimeOffset.MinValue ? string.Empty : Loc.F($"  Captured {report.CapturedAt:yyyy-MM-dd HH:mm}."))
                + (report.Scores.Count == 0
                    ? string.Empty
                    : Loc.T("  Reported grades: ") + string.Join(", ", report.Scores.Select(score => $"{score.Key} {score.Value}")) + ".");
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
            ImportedReportText.Text = Loc.F($"That file could not be read as a capture report: {exception.Message}");
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
        Throughput.UseLatencyTarget(target);
        var tick = SelectImportedTickRate(_importedReport);
        StatusText.Text = Loc.F($"Diagnosis target set to {target} from the imported report.{tick} Nothing has been measured yet.");
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
