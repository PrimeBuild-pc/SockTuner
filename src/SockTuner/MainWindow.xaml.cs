using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner;

public partial class MainWindow : Window
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    private readonly SystemInventoryService _inventory = new();
    private readonly NetworkDiagnosticService _diagnostics = new();
    private readonly RouteGatewayResolver _routeGatewayResolver = new();
    private NetworkSnapshot? _snapshot;
    private CancellationTokenSource? _diagnosticCancellation;

    public MainWindow()
    {
        InitializeComponent();
        TuningCatalogGrid.ItemsSource = SettingCatalog.All;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += async (_, _) => await RefreshInventoryAsync();
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
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Inventory failed: {exception.Message}";
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
        AdaptersGrid.ItemsSource = snapshot.Adapters;

        var ndisProperties = snapshot.Adapters
            .SelectMany(adapter => adapter.NdisProperties.Select(property => new
            {
                Adapter = adapter.Name,
                Driver = adapter.DriverDisplay,
                Property = property.DisplayName,
                property.Keyword,
                Current = property.CurrentValue,
                Default = property.DefaultValue,
                property.Type,
                property.ValidValues
            }))
            .ToArray();
        NdisPropertiesGrid.ItemsSource = ndisProperties;
        NdisSummaryText.Text = $"{ndisProperties.Length} advanced properties advertised across {snapshot.Adapters.Count(adapter => adapter.NdisSupported)} supported adapter(s). Raw keywords remain visible and no values are changed.";
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

        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        SetDiagnosticBusy(true);
        ClearDiagnosticResults("Running…");
        DiagnosticRunSummaryText.Text = $"Resolving {target}, then collecting concurrent gateway, reference, and endpoint samples…";
        StatusText.Text = $"Diagnosing {target}…";

        try
        {
            _snapshot ??= await Task.Run(_inventory.Capture, _diagnosticCancellation.Token);
            var gateway = await _routeGatewayResolver.ResolveAsync(target, _snapshot, _diagnosticCancellation.Token);
            var report = await _diagnostics.RunAsync(target, gateway, port, 12, _diagnosticCancellation.Token);
            GatewayResultText.Text = report.Gateway.Summary;
            ReferenceResultText.Text = report.Reference.Summary;
            GameResultText.Text = report.GameTarget.Summary;
            DnsResultText.Text = report.Connection is null
                ? report.Dns.Summary
                : $"{report.Dns.Summary}\n{report.Connection.Summary}";
            FindingsGrid.ItemsSource = report.Findings;
            DiagnosticRunSummaryText.Text = $"{report.Findings.Count} finding(s) for {report.RequestedTarget} from a {report.Duration.TotalSeconds:0.0}s short run. Confirm issues with a longer run before changing settings.";
            StatusText.Text = $"Diagnosis completed at {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            ClearDiagnosticResults("Canceled");
            DiagnosticRunSummaryText.Text = $"Diagnosis for {target} canceled. Partial samples were discarded.";
            StatusText.Text = "Diagnosis canceled";
        }
        catch (Exception exception)
        {
            ClearDiagnosticResults("Failed");
            DiagnosticRunSummaryText.Text = $"Diagnosis for {target} failed: {exception.Message}";
            StatusText.Text = "Diagnosis failed";
        }
        finally
        {
            SetDiagnosticBusy(false);
        }
    }

    private void CancelDiagnostic_Click(object sender, RoutedEventArgs e) => _diagnosticCancellation?.Cancel();

    private void SetDiagnosticBusy(bool isBusy)
    {
        RunDiagnosticButton.IsEnabled = !isBusy;
        CancelDiagnosticButton.IsEnabled = isBusy;
        DiagnosticTargetText.IsEnabled = !isBusy;
        DiagnosticPortText.IsEnabled = !isBusy;
    }

    private void ClearDiagnosticResults(string value)
    {
        GatewayResultText.Text = value;
        ReferenceResultText.Text = value;
        GameResultText.Text = value;
        DnsResultText.Text = value;
        FindingsGrid.ItemsSource = null;
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
}
