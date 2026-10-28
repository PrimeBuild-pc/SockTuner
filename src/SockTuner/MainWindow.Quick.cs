using System.Windows;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner;

/// <summary>The dashboard's four quick actions and their one-line status, apart from the rest of the window.</summary>
public partial class MainWindow
{
    private PlayabilityGrade? _lastGrade;
    private DateTimeOffset? _lastCheckAt;
    private bool _restartScheduled;

    /// <summary>The dashboard answers in one line per step; the evidence stays in the sections below it.</summary>
    private void UpdateQuickStatus()
    {
        QuickCheckStatusText.Text = _lastCheckAt is not { } at ? Loc.T("Test your connection.")
            : DiagnosticTargetText.Text.Trim() == "1.1.1.1"
                ? Loc.F($"Checked at {at:HH:mm} against a public server. Enter your game's server in Gaming diagnostics for a precise check.")
                : Loc.F($"Checked at {at:HH:mm}.");
        QuickPlayStatusText.Text = Loc.T(QuickStart.Verdict(_lastGrade));
    }

    private async void QuickCheck_Click(object sender, RoutedEventArgs e)
    {
        // A first-time user has no game server to type; a public resolver still shows the path to the internet.
        if (string.IsNullOrWhiteSpace(DiagnosticTargetText.Text)) DiagnosticTargetText.Text = "1.1.1.1";
        await RunDiagnosticAsync();
    }

    /// <summary>
    /// Builds the gaming profile from the last measurement and opens the dry run. It stops there: applying
    /// is still the tuning plan's own confirmed step, so this button cannot change a setting.
    /// </summary>
    private async void QuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            QuickPresetStatusText.Text = Loc.T("Run the check first: the preset is built from it.");
            return;
        }

        Recommendations.SelectProfile("competitive-gaming");
        await Recommendations.BuildAsync();
        var accepted = Recommendations.QueueAll(action => action.Id == "profile.competitive-gaming");
        if (accepted == 0)
        {
            QuickPresetStatusText.Text = Loc.T("Nothing to change: this PC already matches the preset.");
            return;
        }

        var unverified = _snapshot?.Adapters.FirstOrDefault(adapter =>
            adapter.Kind == AdapterKind.Physical && adapter.Status == System.Net.NetworkInformation.OperationalStatus.Up) is { } active
            && AdapterVerificationCatalog.For(active) == AdapterVerification.Unverified;
        QuickPresetStatusText.Text = Loc.T(unverified ? "Generic preset: your adapter is not verified yet. Review the plan." : "Review the plan, then press Apply.");
        WriteLog("quick.preset_queued", $"Accepted={accepted}.");
        await TuningPlan.PreviewAsync();
    }

    private void QuickRestart_Click(object sender, RoutedEventArgs e)
    {
        var arguments = _restartScheduled ? "/a" : "/r /t 30 /c \"SockTuner: restarting to finish applying network changes\"";
        if (!_restartScheduled && MessageBox.Show(
                Loc.T("Windows will restart in 30 seconds. Save your work first. Restart now?"),
                Loc.T("Restart Windows"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", arguments)
            { UseShellExecute = false, CreateNoWindow = true });
            if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
            {
                QuickRestartStatusText.Text = Loc.T("Windows refused the restart request.");
                return;
            }

            _restartScheduled = !_restartScheduled;
            QuickRestartButton.Content = Loc.T(_restartScheduled ? "Cancel restart" : "Restart Windows…");
            QuickRestartStatusText.Text = Loc.T(_restartScheduled ? "Restarting in 30 seconds." : "Restart cancelled.");
            WriteLog("quick.restart", $"Scheduled={_restartScheduled}.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            QuickRestartStatusText.Text = Loc.F($"Could not run the restart: {exception.Message}");
        }
    }
}
