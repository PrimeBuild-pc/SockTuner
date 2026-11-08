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

public partial class UdpQualityView : UserControl
{
    private readonly UdpQualityProbe _probe = new();
    private CancellationTokenSource? _cancellation;
    private UdpQualityReport? _last;
    private UdpQualityReport? _baseline;
    private ApplyVerification? _comparison;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<bool>? BusyChanged;
    private sealed record ResultRow(string Metric, string Value);

    public UdpQualityView()
    {
        InitializeComponent();
        DurationCombo.ItemsSource = new[] { 10, 30, 60 };
        DurationCombo.SelectedItem = 30;
        SizeCombo.ItemsSource = new[] { 128, 512, 1024 };
        SizeCombo.SelectedItem = 128;
    }

    public void CancelActiveWork() => _cancellation?.Cancel();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelActiveWork();
    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync(compare: false);
    private void Baseline_Click(object sender, RoutedEventArgs e) => CaptureBaselineForApply();

    public void CaptureBaselineForApply()
    {
        if (_last is not { } report || _cancellation is not null) return;
        _baseline = report;
        _comparison = null;
        RepeatButton.IsEnabled = true;
        ComparisonText.Text = Loc.F($"UDP baseline saved: {report.Endpoint} at {report.StartedAt:g}. Repeat with the same load and network after applying a change.");
        MetricsText.Text = string.Empty;
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (_baseline is not { } baseline || _cancellation is not null) return;
        if (MessageBox.Show(Loc.T("Restore the same background traffic, network, Wi-Fi position and access point as the UDP baseline. Have you restored those conditions? This comparison does not certify a preset."),
            Loc.T("Repeat under the same conditions"), MessageBoxButton.YesNo, MessageBoxImage.Information,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var endpoint = System.Net.IPEndPoint.Parse(baseline.Endpoint);
        EndpointText.Text = endpoint.Address.ToString();
        PortText.Text = endpoint.Port.ToString();
        DurationCombo.SelectedItem = baseline.Options.DurationSeconds;
        SizeCombo.SelectedItem = baseline.Options.PacketBytes;
        LateText.Text = baseline.Options.LateThresholdMs.ToString();
        await RunAsync(compare: true);
    }

    private async Task RunAsync(bool compare)
    {
        if (_cancellation is not null) return;
        if (!int.TryParse(PortText.Text, out var port) || !int.TryParse(LateText.Text, out var late)
            || DurationCombo.SelectedItem is not int duration || SizeCombo.SelectedItem is not int size)
        {
            StatusText.Text = Loc.T("Choose a valid port, duration, packet size and late threshold.");
            return;
        }
        _cancellation = new();
        var operation = _cancellation;
        InputsPanel.IsEnabled = RunButton.IsEnabled = BaselineButton.IsEnabled = RepeatButton.IsEnabled = ExportButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BusyChanged?.Invoke(this, true);
        Progress.Value = 0;
        _last = null;
        _comparison = null;
        ResultsGrid.ItemsSource = null;
        MetricsText.Text = string.Empty;
        ResultContextText.Text = Loc.T("Waiting for an authenticated responder; no loss verdict yet.");
        ComparisonText.Text = Loc.T("No completed comparison for this run.");
        StatusText.Text = Loc.T("Connecting to the UDP responder…");
        try
        {
            var progress = new Progress<UdpQualityProgress>(p =>
            {
                if (!ReferenceEquals(_cancellation, operation) || _last is not null) return;
                Progress.Value = 100d * p.Sent / p.Total;
                StatusText.Text = p.Sent == p.Total ? Loc.T("Waiting for late echoes and the receiver summary…")
                    : Loc.F($"Sending UDP packets: {p.Sent}/{p.Total}");
            });
            var report = await _probe.RunAsync(EndpointText.Text.Trim(), port, KeyInput.Password.Trim(),
                new(duration, 20, size, late), progress, _cancellation.Token);
            _last = report;
            ResultContextText.Text = Loc.F($"{report.Endpoint} · {report.StartedAt:g} · {report.Sent} packets · {report.Options.PacketBytes} bytes per datagram");
            static string Ms(double? value) => value is { } ms ? $"{ms:0.00} ms" : Loc.T("Not measured");
            ResultsGrid.ItemsSource = new ResultRow[]
            {
                new(Loc.T("Upload loss"), $"{report.UploadLost}/{report.Sent} ({report.UploadLossPercent:0.00}%)"),
                new(Loc.T("Download loss"), report.DownloadLossPercent is { } loss
                    ? $"{report.DownloadLost}/{report.ServerReceived} ({loss:0.00}%)" : Loc.T("Not measured: the responder received no probes")),
                new(Loc.T("Median / P95 RTT"), $"{Ms(report.MedianMs)} / {Ms(report.P95Ms)}"),
                new(Loc.T("RTT jitter"), Ms(report.JitterMs)),
                new(Loc.T("Late / duplicate / reordered"), $"{report.Late} / {report.Duplicates} / {report.Reordered}"),
                new(Loc.T("Locally delayed sends"), $"{report.SchedulingDelays}/{report.Sent}")
            };
            StatusText.Text = report.UploadLost == 0 && report.DownloadLost == 0
                ? Loc.T("No loss observed in this sample. This does not guarantee a loss-free connection.")
                : Loc.T("Packet loss observed on the selected responder path. Repeat before attributing it to a device or preset.");
            if (compare && _baseline is { } baseline)
            {
                var result = UdpQualityComparison.Compare(baseline, report, sameConditions: true);
                _comparison = result;
                ComparisonText.Text = Loc.T(result.Headline) + " " + Loc.T(result.Detail);
                MetricsText.Text = string.Join(Environment.NewLine, result.Metrics.Select(m => m.Display));
            }
        }
        catch (OperationCanceledException) { StatusText.Text = Loc.T("UDP measurement cancelled. No loss verdict."); }
        catch (Exception error)
        {
            StatusText.Text = error is ArgumentException ? Loc.T(error.Message)
                : Loc.T("UDP measurement unavailable: check responder address, key, port and firewall, then repeat. No loss verdict.");
            AppLog.Write("udp.quality.failed", error.GetType().Name);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            InputsPanel.IsEnabled = RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            BaselineButton.IsEnabled = ExportButton.IsEnabled = _last is not null;
            RepeatButton.IsEnabled = _baseline is not null;
            BusyChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, StatusText.Text);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_last is not { } report) return;
        var dialog = new SaveFileDialog
        {
            Filter = "JSON report (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"SockTuner-UDP-{report.StartedAt:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new
            {
                Report = report,
                Baseline = _baseline,
                Comparison = _comparison,
                SameExternalConditionsConfirmed = _comparison is not null
            },
                new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = Loc.T("UDP report saved. The responder key is excluded.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.T("Could not save the UDP report.");
        }
    }
}
