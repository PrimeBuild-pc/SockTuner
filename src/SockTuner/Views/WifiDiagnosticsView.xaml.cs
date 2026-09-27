using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Views;

public partial class WifiDiagnosticsView : UserControl
{
    private readonly WifiStabilityObserver _observer = new();
    private readonly ObservableCollection<WifiObservationSample> _samples = [];
    private WifiInventoryResult? _inventory;
    private ProbeStatistics? _gateway;
    private AdapterCounterDelta? _counterDelta;
    private string? _gamingInterfaceId;
    private CancellationTokenSource? _observationCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private bool _unloaded;

    public WifiDiagnosticsView()
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        SamplesGrid.ItemsSource = _samples;
        Loaded += (_, _) => _unloaded = false;
        Unloaded += (_, _) =>
        {
            _unloaded = true;
            CancelActiveWork();
        };
    }

    public WifiDiagnosticReport? LatestReport { get; private set; }
    public string? PreferredInterfaceId { get; set; }
    public string? SelectedInterfaceId => (InterfaceComboBox.SelectedItem as WifiRadioInfo)?.InterfaceId;
    public event EventHandler<IReadOnlyList<WifiObservationSample>>? ObservationUpdated;

    public void SetGamingContext(
        ProbeStatistics? gateway, AdapterCounterDelta? counterDelta, string? interfaceId)
    {
        _gateway = gateway;
        _counterDelta = counterDelta;
        _gamingInterfaceId = interfaceId;
    }

    public void SetReport(WifiInventoryResult inventory, WifiDiagnosticReport report)
    {
        _refreshCancellation?.Cancel();
        _observationCancellation?.Cancel();
        _inventory = inventory;
        var selectedId = report.Radio?.InterfaceId ?? PreferredInterfaceId;
        InterfaceComboBox.ItemsSource = inventory.Radios;
        InterfaceComboBox.SelectedItem = inventory.Radios.FirstOrDefault(item => item.InterfaceId == selectedId)
            ?? inventory.Radios.FirstOrDefault(item => item.Connected)
            ?? inventory.Radios.FirstOrDefault();
        ShowReport(report);
        ShowAvailability(inventory);
        if (!_unloaded)
        {
            SetStatus(inventory.Error ?? Loc.T("Wi-Fi evidence updated by Gaming diagnostics."));
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        _refreshCancellation?.Cancel();
        var refreshCancellation = new CancellationTokenSource();
        _refreshCancellation = refreshCancellation;
        var cancellationToken = refreshCancellation.Token;
        SetBusy(true);
        SetStatus(Loc.T("Reading passive Windows WLAN data…"));
        try
        {
            var selectedId = (InterfaceComboBox.SelectedItem as WifiRadioInfo)?.InterfaceId ?? PreferredInterfaceId;
            _inventory = await Task.Run(WindowsWifiInventory.Read, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            InterfaceComboBox.ItemsSource = _inventory.Radios;
            InterfaceComboBox.SelectedItem = _inventory.Radios.FirstOrDefault(item => item.InterfaceId == selectedId)
                ?? _inventory.Radios.FirstOrDefault(item => item.Connected)
                ?? _inventory.Radios.FirstOrDefault();
            _samples.Clear();
            ObservationUpdated?.Invoke(this, []);
            ShowAvailability(_inventory);
            RebuildReport();
            var selected = InterfaceComboBox.SelectedItem as WifiRadioInfo;
            SetStatus(_inventory.Radios.Count == 0
                ? _inventory.Error ?? Loc.T("No Wi-Fi radio was found.")
                : selected?.Error is { Length: > 0 } error
                    ? Loc.F($"Passive Wi-Fi data is partial: {error}")
                    : Loc.F($"Passive Wi-Fi data refreshed at {_inventory.CapturedAt:HH:mm:ss}. Windows may return cached beacons."));
        }
        catch (OperationCanceledException)
        {
            // Closing or unloading the view cancels the refresh; there is no UI left to update.
        }
        catch (Exception exception)
        {
            if (!_unloaded) SetStatus(Loc.F($"Wi-Fi refresh failed: {exception.Message}"));
        }
        finally
        {
            refreshCancellation.Dispose();
            if (ReferenceEquals(_refreshCancellation, refreshCancellation))
            {
                _refreshCancellation = null;
                if (!_unloaded) SetBusy(false);
            }
        }
    }

    private void Interface_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _samples.Clear();
        ObservationUpdated?.Invoke(this, []);
        RebuildReport();
        ObserveButton.IsEnabled = InterfaceComboBox.SelectedItem is WifiRadioInfo { Connected: true }
                                  && _observationCancellation is null;
    }

    private async void Observe_Click(object sender, RoutedEventArgs e)
    {
        if (InterfaceComboBox.SelectedItem is not WifiRadioInfo { Connected: true } radio) return;

        _observationCancellation?.Dispose();
        _observationCancellation = new CancellationTokenSource();
        _samples.Clear();
        ObservationUpdated?.Invoke(this, []);
        SetBusy(true, observing: true);
        SetStatus(Loc.T("Observing the current association for 60 seconds without triggering a scan…"));
        var progress = new Progress<WifiObservationSample>(sample =>
        {
            if (_unloaded) return;
            _samples.Add(sample);
            ObservationUpdated?.Invoke(this, _samples.ToArray());
            RebuildReport();
            SetStatus(Loc.F($"Observed {_samples.Count} passive sample(s)…"));
        });

        try
        {
            await _observer.ObserveAsync(
                radio.InterfaceId,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(2),
                progress,
                _observationCancellation.Token);
            if (!_unloaded)
            {
                RebuildReport();
                SetStatus(Loc.F($"Observation complete: {_samples.Count} passive sample(s)."));
            }
        }
        catch (OperationCanceledException)
        {
            if (!_unloaded)
            {
                RebuildReport();
                SetStatus(Loc.F($"Observation stopped; {_samples.Count} passive sample(s) retained."));
            }
        }
        catch (Exception exception)
        {
            if (!_unloaded) SetStatus(Loc.F($"Wi-Fi observation failed: {exception.Message}"));
        }
        finally
        {
            _observationCancellation.Dispose();
            _observationCancellation = null;
            if (!_unloaded) SetBusy(false);
        }
    }

    private void CancelObserve_Click(object sender, RoutedEventArgs e) => _observationCancellation?.Cancel();

    private void RebuildReport()
    {
        if (InterfaceComboBox.SelectedItem is not WifiRadioInfo radio)
        {
            if (_inventory is not null)
            {
                ShowReport(GamingWifiDiagnosticEngine.Analyze(new(null, CapturedAt: _inventory.CapturedAt)));
            }
            return;
        }

        var hasMatchingGamingContext = string.Equals(
            radio.InterfaceId, _gamingInterfaceId, StringComparison.OrdinalIgnoreCase);
        var report = GamingWifiDiagnosticEngine.Analyze(new(
            radio,
            _samples.ToArray(),
            hasMatchingGamingContext ? _gateway : null,
            hasMatchingGamingContext ? _counterDelta : null,
            _inventory?.CapturedAt));
        ShowReport(report);
    }

    private void ShowReport(WifiDiagnosticReport report)
    {
        LatestReport = report;
        EvidenceGrid.ItemsSource = report.Evidence;
        FindingsGrid.ItemsSource = report.Findings;
        ChannelsGrid.ItemsSource = report.Channels;
        VerdictText.Text = report.VerdictDisplay;
        SummaryText.Text = report.Summary;
        UnknownsText.Text = report.Unknowns.Count == 0
            ? Loc.T("Nothing required for this assessment is unknown.")
            : string.Join(Environment.NewLine, report.Unknowns.Select(item => "• " + item));
        NoFindingsText.Visibility = report.Verdict == WifiDiagnosticVerdict.Healthy
            && report.Findings.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        VerdictText.Foreground = report.Verdict switch
        {
            WifiDiagnosticVerdict.Healthy => (System.Windows.Media.Brush)FindResource("GoodBrush"),
            WifiDiagnosticVerdict.AtRisk => (System.Windows.Media.Brush)FindResource("WarningBrush"),
            WifiDiagnosticVerdict.Degraded => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            _ => (System.Windows.Media.Brush)FindResource("MutedTextBrush")
        };

        if (report.Radio is not { } radio)
        {
            ConnectionText.Text = Loc.T("No active Wi-Fi connection.");
            TechnicalText.Text = report.Summary;
        }
        else
        {
            ConnectionText.Text = radio.ConnectedBss is { } bss
                ? $"{radio.Description}{Environment.NewLine}{bss.SsidDisplay} · {bss.BandDisplay} channel {bss.Channel} · {radio.SignalDisplay}{Environment.NewLine}{radio.RateDisplay} · {radio.PhyDisplay} · {(radio.Security ?? bss.Security)?.Display ?? "Security not reported"}"
                : $"{radio.Description} · {radio.State} · {radio.RadioStateDisplay}";
            TechnicalText.Text =
                $"Interface {radio.InterfaceId}{Environment.NewLine}BSSID {radio.Bssid}{Environment.NewLine}"
                + $"Connection mode {radio.ConnectionMode} · radio {radio.RadioStateDisplay}{Environment.NewLine}"
                + $"Supported PHY: {string.Join(", ", radio.SupportedPhys ?? [])}{Environment.NewLine}"
                + $"Beacon flags: HE {radio.ConnectedBss?.HeAdvertised == true}, EHT {radio.ConnectedBss?.EhtAdvertised == true}, WPS {radio.ConnectedBss?.WpsAdvertised == true}{Environment.NewLine}"
                + "Nearby data source: cached Windows BSS list; age is not reported by the API."
                + (radio.Error is null ? string.Empty : Environment.NewLine + "Partial data: " + radio.Error);
        }
    }

    public void CancelActiveWork()
    {
        _refreshCancellation?.Cancel();
        _observationCancellation?.Cancel();
    }

    private void ShowAvailability(WifiInventoryResult inventory)
    {
        PermissionCard.Visibility = inventory.LocationPermissionRequired ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool busy, bool observing = false)
    {
        RefreshButton.IsEnabled = !busy;
        InterfaceComboBox.IsEnabled = !busy;
        ObserveButton.IsEnabled = !busy && InterfaceComboBox.SelectedItem is WifiRadioInfo { Connected: true };
        CancelObserveButton.IsEnabled = busy && observing;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(StatusText)
            ?? new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(StatusText);
        peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void OpenLocationSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location")
            {
                UseShellExecute = true
            });
            SetStatus(Loc.T("Location settings opened. Return here and refresh after changing access."));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(Loc.F($"Location settings could not be opened: {exception.Message}"));
        }
    }
}
