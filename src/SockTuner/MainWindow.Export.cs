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

/// <summary>Every export the Export menu offers.</summary>
public partial class MainWindow
{
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

    private void ExportSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            StatusText.Text = Loc.T("Refresh inventory before exporting a snapshot.");
            return;
        }

        if (MessageBox.Show(
                Loc.T("The snapshot contains machine, network profile names/IDs, adapter, binding component, address, route, DNS, driver, and Winsock provider identifiers. Export it anyway?"),
                Loc.T("Export diagnostic snapshot"),
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
            StatusText.Text = Loc.T("Refresh inventory before exporting a support snapshot.");
            return;
        }

        SaveSnapshot(redact: true);
    }

    private void SaveSnapshot(bool redact)
    {
        var kind = redact ? "support" : "snapshot";
        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("SockTuner JSON snapshot (*.json)|*.json"),
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
            StatusText.Text = Loc.F($"{(redact ? Loc.T("Redacted support snapshot") : Loc.T("Snapshot"))} exported to {dialog.FileName}.");
            WriteLog(redact ? "support_snapshot.exported" : "snapshot.exported", System.IO.Path.GetFileName(dialog.FileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.F($"Snapshot export failed: {exception.Message}");
            WriteLog("snapshot.export_failed", exception.Message);
        }
    }

    private void CreateCompatibilityReport_Click(object sender, RoutedEventArgs e) => Preferences.CreateCompatibilityReport();

    private void ExportReportJson_Click(object sender, RoutedEventArgs e) => SaveReport(html: false, redact: false);

    private void ExportReportHtml_Click(object sender, RoutedEventArgs e) => SaveReport(html: true, redact: false);

    private void ExportRedactedReportHtml_Click(object sender, RoutedEventArgs e) => SaveReport(html: true, redact: true);

    private void SaveReport(bool html, bool redact)
    {
        if (_lastReport is null)
        {
            StatusText.Text = Loc.T("Run a completed diagnosis before exporting a report.");
            return;
        }

        SaveReport(_lastReport, html, redact);
    }

    private void SaveReport(GamingDiagnosticReport report, bool html, bool redact)
    {
        if (!redact && MessageBox.Show(
                Loc.T("The full report contains diagnostic targets, addresses, routes, adapter identifiers, Wi-Fi network names/BSSIDs, and error details. Export it anyway?"),
                Loc.T("Export full diagnostic report"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var extension = html ? ".html" : ".json";
        var dialog = new SaveFileDialog
        {
            Filter = html ? Loc.T("Self-contained HTML report (*.html)|*.html") : Loc.T("SockTuner JSON report (*.json)|*.json"),
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
            StatusText.Text = Loc.F($"Report exported to {dialog.FileName}.");
            WriteLog("report.exported", $"Format={(html ? "html" : "json")}; Redacted={redact}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.F($"Report export failed: {exception.Message}");
            WriteLog("report.export_failed", exception.Message);
        }
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                Loc.T("Logs can contain diagnostic targets and local file paths. Export them anyway?"),
                Loc.T("Export application logs"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("SockTuner JSON Lines log (*.jsonl)|*.jsonl"),
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
            StatusText.Text = Loc.F($"Log exported to {dialog.FileName}.");
            WriteLog("log.exported", System.IO.Path.GetFileName(dialog.FileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.F($"Log export failed: {exception.Message}");
        }
    }
}
