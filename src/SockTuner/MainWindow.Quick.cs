using System.Windows;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner;

/// <summary>The dashboard's four quick actions and their one-line status, apart from the rest of the window.</summary>
public partial class MainWindow
{
    private DateTimeOffset? _lastCheckAt;
    private bool _restartScheduled;

    private void DiagnosisCompatibility_Click(object sender, RoutedEventArgs e) => Preferences.CreateCompatibilityReport();

    /// <summary>The dashboard answers in one line per step; the evidence stays in the sections below it.</summary>
    private void UpdateQuickStatus()
    {
        QuickCheckStatusText.Text = _lastCheckAt is not { } at ? Loc.T("Test your connection.")
            : DiagnosticTargetText.Text.Trim() == "1.1.1.1"
                ? Loc.F($"Checked at {at:HH:mm} against a public server. Enter your game's server in Gaming diagnostics for a precise check.")
                : Loc.F($"Checked at {at:HH:mm}.");
        QuickPlayStatusText.Text = _lastReport is null ? Loc.T("Not checked yet.") : Loc.T("Repeat after a change to check its effect.");
    }

    private async void QuickCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!RunDiagnosticButton.IsEnabled) return;
        // Beginner flow has no dependency on an advanced tab's previous input or profile.
        DiagnosticTargetText.Text = "1.1.1.1";
        DiagnosticProfileComboBox.SelectedItem = DiagnosticProfiles.All[1];
        DiagnosticGameComboBox.SelectedItem = _gameProfiles[0];
        DiagnosticLoadComboBox.SelectedItem = DiagnosticLoadCondition.Unspecified;
        DiagnosticPortText.Text = "443";
        CaptureDpcIsrCheckBox.IsChecked = false;
        await RunDiagnosticAsync();
    }

    /// <summary>
    /// Opens current evidence-based recommendations; choices, preview and apply remain separate.
    /// </summary>
    private async void QuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!RunDiagnosticButton.IsEnabled) return;
        if (_lastReport is null)
        {
            QuickPresetStatusText.Text = Loc.T("Run the check first: the preset is built from it.");
            return;
        }

        await Recommendations.BuildAsync(diagnosisOnly: true);
        SelectTab("Recommendations");
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
