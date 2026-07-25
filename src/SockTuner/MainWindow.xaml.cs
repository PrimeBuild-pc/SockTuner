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

namespace SockTuner;

public partial class MainWindow : Window
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int MonitorMaximumSamples = 1000;

    private readonly SystemInventoryService _inventory = new();
    private readonly NetworkDiagnosticService _diagnostics = new();
    private readonly NetworkMonitorService _monitor = new();
    private readonly RouteGatewayResolver _routeGatewayResolver = new();
    private NetworkSnapshot? _snapshot;
    private CancellationTokenSource? _diagnosticCancellation;
    private CancellationTokenSource? _monitorCancellation;
    private readonly ObservableCollection<MonitorSample> _monitorSamples = [];
    private IReadOnlyList<AdapterInfo> _adapterRows = [];
    private IReadOnlyList<NdisPropertyRow> _ndisRows = [];
    private UserPreferences _preferences = new();
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
        MonitorSamplesGrid.ItemsSource = _monitorSamples;
        TuningCatalogGrid.ItemsSource = SettingCatalog.All;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += async (_, _) => await RefreshInventoryAsync();
        WriteLog("app.started", "SockTuner UI started.");
    }

    private async void RefreshInventory_Click(object sender, RoutedEventArgs e) => await RefreshInventoryAsync();

    private async Task RefreshInventoryAsync()
    {
        StatusText.Text = "Reading Windows network inventory…";

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
        _adapterRows = snapshot.Adapters;
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

        if (DiagnosticProfileComboBox.SelectedItem is not DiagnosticProfile profile)
        {
            StatusText.Text = "Select a diagnostic profile.";
            return;
        }

        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        SetDiagnosticBusy(true);
        ClearDiagnosticResults("Running…");
        DiagnosticRunSummaryText.Text = $"{profile.DisplayName}: resolving {target}, then collecting {profile.SampleCount} concurrent samples per endpoint…";
        StatusText.Text = $"Diagnosing {target}…";
        WriteLog("diagnostic.started", $"Target={target}; Port={port?.ToString() ?? "none"}; Profile={profile.Id}.");

        try
        {
            _snapshot ??= await Task.Run(_inventory.Capture, _diagnosticCancellation.Token);
            var gateway = await _routeGatewayResolver.ResolveAsync(target, _snapshot, _diagnosticCancellation.Token);
            var report = await _diagnostics.RunAsync(target, gateway, port, profile, _diagnosticCancellation.Token);
            GatewayResultText.Text = report.Gateway.Summary;
            ReferenceResultText.Text = report.Reference.Summary;
            GameResultText.Text = report.GameTarget.Summary;
            DnsResultText.Text = report.Connection is null
                ? report.Dns.Summary
                : $"{report.Dns.Summary}\n{report.Connection.Summary}";
            FindingsGrid.ItemsSource = report.Findings;
            DiagnosticRunSummaryText.Text = $"{report.Findings.Count} finding(s) for {report.RequestedTarget} from the {profile.DisplayName} profile ({report.Duration.TotalSeconds:0.0}s).";
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

    private void ApplyAdapterFilter()
    {
        var search = AdapterFilterText.Text.Trim();
        var rows = string.IsNullOrEmpty(search)
            ? _adapterRows
            : _adapterRows.Where(adapter => Contains(adapter.Name, search)
                || Contains(adapter.Description, search)
                || Contains(adapter.Id, search)
                || Contains(adapter.Status.ToString(), search)
                || Contains(adapter.AdapterKindDisplay, search)
                || Contains(adapter.InterfaceType.ToString(), search)
                || Contains(adapter.SpeedDisplay, search)
                || Contains(adapter.MtuDisplay, search)
                || Contains(adapter.MetricDisplay, search)
                || Contains(adapter.DefaultRoutePolicyDisplay, search)
                || Contains(adapter.ReceivedDisplay, search)
                || Contains(adapter.SentDisplay, search)
                || Contains(adapter.ReceiveIssuesDisplay, search)
                || Contains(adapter.SendIssuesDisplay, search)
                || Contains(adapter.DriverDisplay, search)
                || Contains(adapter.NdisPropertyCountDisplay, search)
                || Contains(adapter.ProtocolsDisplay, search)
                || Contains(adapter.AddressesDisplay, search)
                || Contains(adapter.GatewaysDisplay, search)
                || Contains(adapter.DnsDisplay, search)
                || Contains(adapter.InventoryStatus, search));
        AdaptersGrid.ItemsSource = Filter(rows, InventoryFilterText.Text);
    }

    private void ApplyNdisFilter()
    {
        var search = NdisFilterText.Text.Trim();
        var rows = string.IsNullOrEmpty(search)
            ? _ndisRows
            : _ndisRows.Where(row => Contains(row.Adapter, search)
                || Contains(row.Driver, search)
                || Contains(row.Property, search)
                || Contains(row.Keyword, search)
                || Contains(row.Current, search)
                || Contains(row.Default, search)
                || Contains(row.Type, search)
                || Contains(row.ValidValues, search));
        NdisPropertiesGrid.ItemsSource = Filter(rows, InventoryFilterText.Text);
    }

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

    private static bool Contains(string value, string search) =>
        value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

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
            var progress = new Progress<MonitorSample>(sample =>
            {
                if (_monitorSamples.Count == MonitorMaximumSamples) _monitorSamples.RemoveAt(0);
                _monitorSamples.Add(sample);
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
            SetMonitorStatus(window + string.Join(" · ", report.Summaries.Select(summary => $"{summary.Label}: {summary.Summary}")));
            WriteLog("monitor.completed", $"Target={target}; Duration={report.Duration.TotalSeconds:0.0}s; Samples={report.Samples.Count}.");
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
        RunDiagnosticButton.IsEnabled = !isBusy;
        CancelDiagnosticButton.IsEnabled = isBusy;
        DiagnosticTargetText.IsEnabled = !isBusy;
        DiagnosticPortText.IsEnabled = !isBusy;
        DiagnosticProfileComboBox.IsEnabled = !isBusy;
    }

    private void ClearDiagnosticResults(string value)
    {
        GatewayResultText.Text = value;
        ReferenceResultText.Text = value;
        GameResultText.Text = value;
        DnsResultText.Text = value;
        FindingsGrid.ItemsSource = null;
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
