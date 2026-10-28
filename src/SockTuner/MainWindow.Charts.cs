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

/// <summary>The dashboard latency and Wi-Fi charts.</summary>
public partial class MainWindow
{
    private void ShowDashboardLatency(ProbeStatistics statistics, bool live)
    {
        var values = statistics.Samples.Select(sample => sample.RoundTripTimeMs).ToArray();
        DrawChart(DashboardLatencyCanvas, values, (Brush)FindResource("AccentFillColorDefaultBrush"));
        AddLossMarkers(DashboardLatencyCanvas, values);

        DashboardLatencyLegendText.Text = statistics.Received == 0
            ? live ? Loc.T("Waiting for game-endpoint replies…") : Loc.T("No completed latency measurement")
            : Loc.F($"{statistics.MedianMs:0.0} ms median · {statistics.P95Ms:0.0} ms P95 · {(statistics.JitterMs is { } jitter ? $"{jitter:0.0} ms" : "n/a")} jitter · {statistics.LossPercent:0.#}% loss");
        System.Windows.Automation.AutomationProperties.SetHelpText(
            DashboardLatencyCanvas, DashboardLatencyLegendText.Text);
        if (statistics.Sent > 0)
        {
            DashboardNetworkSummaryText.Text = live
                ? Loc.F($"Live game-endpoint measurement: {statistics.Received}/{statistics.Sent} replies.")
                : Loc.F($"Latest completed gaming diagnostic for {statistics.Target}.");
        }
    }

    private void ShowDashboardWifi(IReadOnlyList<WifiObservationSample> samples)
    {
        var signal = samples.Select(sample => sample.RssiDbm is { } rssi ? (double?)rssi : null).ToArray();
        var rates = samples.Select(sample =>
        {
            var rate = Math.Max(sample.ReceiveRateKbps, sample.TransmitRateKbps);
            return rate > 0 ? (double?)(rate / 1000d) : null;
        }).ToArray();
        DrawChart(DashboardWifiCanvas, signal, (Brush)FindResource("AccentFillColorDefaultBrush"));
        DrawSeries(DashboardWifiCanvas, rates, (Brush)FindResource("SystemFillColorCautionBrush"));

        var validSignal = signal.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var validRates = rates.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        DashboardWifiLegendText.Text = samples.Count == 0
            ? Loc.T("No passive Wi-Fi observation")
            : Loc.F($"{samples.Count} samples · RSSI {Range(validSignal, "dBm")} · rate {Range(validRates, "Mbps")} (independent scales)");
        DashboardNetworkSummaryText.Text = samples.Count == 0
            ? Loc.T("Passive Wi-Fi observation started; no samples collected yet.")
            : Loc.F($"Passive Wi-Fi association observation contains {samples.Count} sample(s).");
        System.Windows.Automation.AutomationProperties.SetHelpText(
            DashboardWifiCanvas, DashboardWifiLegendText.Text);
    }

    private static string Range(double[] values, string unit) => values.Length == 0
        ? $"n/a {unit}"
        : $"{values.Min():0.#}–{values.Max():0.#} {unit}";

    private void DrawChart(System.Windows.Controls.Canvas canvas, IReadOnlyList<double?> values, Brush brush)
    {
        canvas.Children.Clear();
        var gridBrush = (Brush)FindResource("CardStrokeColorDefaultBrush");
        foreach (var y in new[] { 30d, 60d, 90d })
        {
            canvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = 600,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }
        DrawSeries(canvas, values, brush);
    }

    private static void DrawSeries(System.Windows.Controls.Canvas canvas, IReadOnlyList<double?> values, Brush brush)
    {
        foreach (var segment in SparklineGeometry.Build(values))
        {
            if (segment.Count == 1)
            {
                var point = segment[0];
                var marker = new Ellipse { Width = 7, Height = 7, Fill = brush };
                System.Windows.Controls.Canvas.SetLeft(marker, point.X - 3.5);
                System.Windows.Controls.Canvas.SetTop(marker, point.Y - 3.5);
                canvas.Children.Add(marker);
                continue;
            }

            canvas.Children.Add(new Polyline
            {
                Points = new PointCollection(segment),
                Stroke = brush,
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round
            });
        }
    }

    private void AddLossMarkers(System.Windows.Controls.Canvas canvas, IReadOnlyList<double?> values)
    {
        if (values.Count == 0) return;
        var danger = (Brush)FindResource("SystemFillColorCriticalBrush");
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].HasValue) continue;
            var marker = new Rectangle { Width = 4, Height = 12, Fill = danger };
            System.Windows.Controls.Canvas.SetLeft(marker, values.Count == 1 ? 298 : index * 596d / (values.Count - 1));
            System.Windows.Controls.Canvas.SetTop(marker, 104);
            canvas.Children.Add(marker);
        }
    }
}
