using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Views;

/// <summary>
/// The saved diagnostic runs: compare two, trend several, check for drift, export or delete.
/// </summary>
public partial class HistoryView : UserControl
{
    private readonly DiagnosticHistoryStore _historyStore = new();
    private readonly ObservableCollection<DiagnosticHistoryEntry> _history = [];

    public HistoryView()
    {
        InitializeComponent();
        foreach (var entry in _historyStore.Load()) _history.Add(entry);
        HistoryGrid.ItemsSource = _history;
    }

    /// <summary>The user asked to export a run; the window owns report export.</summary>
    public event EventHandler<GamingDiagnosticReport>? ExportRequested;

    public DiagnosticHistoryEntry? Latest => _history.FirstOrDefault();

    /// <summary>Saves a finished run, keeping the newest twenty on screen as the store does on disk.</summary>
    public void Add(GamingDiagnosticReport report)
    {
        try
        {
            _history.Insert(0, _historyStore.Save(report));
            while (_history.Count > 20) _history.RemoveAt(_history.Count - 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("history.save_failed", exception.Message);
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
        AppLog.Write("baseline.checked", report.Verdict);
    }

    private void CompareHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().OrderBy(entry => entry.SavedAt).ToArray();
        if (selected.Length != 2)
        {
            ComparisonText.Text = Loc.T("Select exactly two runs.");
            return;
        }

        var comparison = DiagnosticComparisonService.Compare(selected[0].Report, selected[1].Report);
        ComparisonText.Text = comparison.Comparable
            ? comparison.Reason + "\n" + string.Join("\n", comparison.Metrics.Select(metric => metric.Summary))
            : Loc.F($"Not comparable: {comparison.Reason}");
    }

    private void TrendHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().ToArray();
        var trend = DiagnosticComparisonService.Trend(selected);
        ComparisonText.Text = trend.Comparable
            ? trend.Reason + "\n" + string.Join("\n", trend.Points.Select(point => point.Summary))
            : Loc.F($"Not comparable: {trend.Reason}");
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().ToArray();
        if (selected.Length != 1)
        {
            ComparisonText.Text = Loc.T("Select exactly one run to export.");
            return;
        }
        ExportRequested?.Invoke(this, selected[0].Report);
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<DiagnosticHistoryEntry>().ToArray();
        if (selected.Length == 0)
        {
            ComparisonText.Text = Loc.T("Select one or more runs to delete.");
            return;
        }

        if (MessageBox.Show(
                Loc.F($"Delete {selected.Length} selected diagnostic run(s)? This cannot be undone."),
                Loc.T("Delete diagnostic history"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var entry in selected)
            {
                _historyStore.Delete(entry.Id);
                _history.Remove(entry);
            }
            ComparisonText.Text = Loc.F($"Deleted {selected.Length} run(s).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ComparisonText.Text = Loc.F($"Delete failed: {exception.Message}");
        }
    }
}
