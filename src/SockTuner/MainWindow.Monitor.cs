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

/// <summary>The continuous latency monitor.</summary>
public partial class MainWindow
{
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
            if (_snapshot is null)
            {
                _snapshot = await Task.Run(_inventory.Capture, _monitorCancellation.Token);
                ShowSnapshot(_snapshot);
            }
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
}
