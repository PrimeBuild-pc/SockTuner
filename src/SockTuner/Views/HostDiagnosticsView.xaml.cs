using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Remediation;

namespace SockTuner.Views;

/// <summary>
/// Passive display plus the three recommended actions a DPC/ISR capture can offer, each with an
/// Apply this view hands back to whichever delegate <see cref="MainWindow.HostDiagnostics"/> wired
/// up — this view never talks to the elevated worker or the transaction engine itself.
/// </summary>
public partial class HostDiagnosticsView : UserControl
{
    private Func<Task>? _applyAffinity;
    private Action? _modifyAffinity;
    private Func<Task>? _applyUninstall;
    private Func<Task>? _applyDisable;

    public HostDiagnosticsView() => InitializeComponent();

    public void SetProcessConnections(IReadOnlyList<ProcessConnectionSummary> summaries, string? error)
    {
        ProcessConnectionsGrid.ItemsSource = summaries;
        ProcessConnectionsStatusText.Text = error is not null
            ? Loc.F($"Could not read process connections: {error}")
            : summaries.Count == 0
                ? Loc.T("No active TCP connections were found.")
                : Loc.F($"{summaries.Count} process(es) with at least one TCP connection.");
    }

    public void SetDpcIsrResult(
        DpcIsrFinding finding,
        RemediationAction? affinityAction, Func<Task>? applyAffinity, Action? modifyAffinity,
        HostGuidanceItem? uninstallGuidance, Func<Task>? applyUninstall,
        HostGuidanceItem? disableGuidance, Func<Task>? applyDisable)
    {
        DpcIsrRankingGrid.ItemsSource = finding.Ranking;
        DpcIsrFindingText.Text = finding.CulpritDriver is null
            ? Loc.F($"{finding.Reason}")
            : Loc.F($"Busiest driver: {finding.CulpritDriver} ({finding.CulpritSharePercent:0.#}% of the capture). {finding.Reason}");

        _applyAffinity = applyAffinity;
        _modifyAffinity = modifyAffinity;
        _applyUninstall = applyUninstall;
        _applyDisable = applyDisable;

        SetRow(AffinityRow, AffinityTitleText, AffinityDetailText, AffinityStatusText, AffinityModifyButton,
            affinityAction?.Title, affinityAction is null ? null : $"{affinityAction.ExpectedEffect} {affinityAction.TradeOff}",
            showModify: true);
        SetRow(UninstallRow, UninstallTitleText, UninstallDetailText, UninstallStatusText, null,
            uninstallGuidance?.Title, uninstallGuidance?.Instructions.FirstOrDefault()?.Summary, showModify: false);
        SetRow(DisableRow, DisableTitleText, DisableDetailText, DisableStatusText, null,
            disableGuidance?.Title, disableGuidance?.Instructions.FirstOrDefault()?.Summary, showModify: false);

        NoActionsText.Visibility = affinityAction is null && uninstallGuidance is null && disableGuidance is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void SetRow(
        Border row, TextBlock title, TextBlock detail, TextBlock status, Button? modifyButton,
        string? titleText, string? detailText, bool showModify)
    {
        if (titleText is null)
        {
            row.Visibility = Visibility.Collapsed;
            return;
        }

        row.Visibility = Visibility.Visible;
        title.Text = titleText;
        detail.Text = detailText ?? string.Empty;
        status.Text = string.Empty;
        if (modifyButton is not null)
        {
            modifyButton.Visibility = showModify ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void AffinityApply_Click(object sender, RoutedEventArgs e)
    {
        if (_applyAffinity is null) return;
        AffinityApplyButton.IsEnabled = false;
        try
        {
            await _applyAffinity();
        }
        finally
        {
            AffinityApplyButton.IsEnabled = true;
        }
    }

    private void AffinityModify_Click(object sender, RoutedEventArgs e) => _modifyAffinity?.Invoke();

    private void AffinityDismiss_Click(object sender, RoutedEventArgs e) => AffinityRow.Visibility = Visibility.Collapsed;

    private async void UninstallApply_Click(object sender, RoutedEventArgs e)
    {
        if (_applyUninstall is null) return;
        UninstallApplyButton.IsEnabled = false;
        try
        {
            await _applyUninstall();
        }
        finally
        {
            UninstallApplyButton.IsEnabled = true;
        }
    }

    private void UninstallDismiss_Click(object sender, RoutedEventArgs e) => UninstallRow.Visibility = Visibility.Collapsed;

    private async void DisableApply_Click(object sender, RoutedEventArgs e)
    {
        if (_applyDisable is null) return;
        DisableApplyButton.IsEnabled = false;
        try
        {
            await _applyDisable();
        }
        finally
        {
            DisableApplyButton.IsEnabled = true;
        }
    }

    private void DisableDismiss_Click(object sender, RoutedEventArgs e) => DisableRow.Visibility = Visibility.Collapsed;

    /// <summary>So MainWindow can report success/failure after an Apply without the view knowing what the action was.</summary>
    public void SetAffinityStatus(string text) => AffinityStatusText.Text = text;
    public void SetUninstallStatus(string text) => UninstallStatusText.Text = text;
    public void SetDisableStatus(string text) => DisableStatusText.Text = text;
}
