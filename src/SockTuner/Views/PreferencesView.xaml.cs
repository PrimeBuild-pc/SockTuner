using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>
/// Live-changes consent, language, log retention and the compatibility report. Each setting is
/// saved on its own, read-modify-write, so nothing here overwrites what another surface saved.
/// </summary>
public partial class PreferencesView : UserControl
{
    private const string CompatibilityIssueUrl =
        "https://github.com/PrimeBuild-pc/SockTuner/issues/new?template=compatibility-report.yml";

    public PreferencesView()
    {
        InitializeComponent();
        var preferences = AppPreferences.Load();
        LogRetentionComboBox.ItemsSource = Enumerable.Range(1, 64);
        LogRetentionComboBox.SelectedItem = preferences.LogFileMegabytes;
        LanguageComboBox.ItemsSource = Loc.Languages;
        LanguageComboBox.SelectedItem = Loc.Languages.First(language => language.Tag == Loc.CurrentTag);
    }

    /// <summary>A line for the window's status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>The inventory a compatibility report is built from; null until the first refresh.</summary>
    public Func<NetworkSnapshot?> Snapshot { get; set; } = () => null;

    /// <summary>Keeps the switch in step with consent granted or withdrawn anywhere else.</summary>
    public void ShowLiveChanges(bool accepted) => LiveChangesSwitch.IsChecked = accepted;

    /// <summary>
    /// The Preferences switch. On asks the same alpha risk notice as the first apply; off withdraws
    /// consent, after which every write path — plan, resolvers, interrupts — asks again.
    /// </summary>
    private void LiveChangesSwitch_Click(object sender, RoutedEventArgs e)
    {
        var problem = default(string);
        var saved = LiveChangesSwitch.IsChecked == true
            ? WriteConsentDialog.Ensure(out problem)
            : WriteConsentDialog.Revoke(out problem);
        var accepted = WriteConsent.IsAccepted(AppPreferences.Load());
        if (!saved) ShowLiveChanges(accepted);
        PreferenceStatusText.Text = problem ?? (accepted
            ? Loc.T("Live network changes are enabled.")
            : Loc.T("Live network changes are disabled. SockTuner only reads."));
    }

    private void LogRetention_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The constructor's own selection is not a choice; saving it would pin today's values.
        if (!IsLoaded || LogRetentionComboBox.SelectedItem is not int megabytes)
        {
            return;
        }

        try
        {
            AppPreferences.Update(preferences => preferences with { LogFileMegabytes = megabytes });
            var error = AppLog.ConfigureRetention(megabytes);
            PreferenceStatusText.Text = error is null
                ? Loc.F($"Retention saved: two files of up to {megabytes} MB each.")
                : Loc.F($"Retention saved, but existing logs could not be trimmed: {error}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PreferenceStatusText.Text = Loc.F($"Preference save failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Saved immediately, applied on the next start. Retranslating a live window would mean holding
    /// the English original of every string that has since been replaced, which is a cache to keep
    /// correct in exchange for saving one restart.
    /// </summary>
    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Without the IsLoaded guard the first start saved the Windows language as a choice, and
        // the app stopped following Windows from then on.
        if (!IsLoaded || LanguageComboBox.SelectedItem is not Loc.Language language) return;
        if (string.Equals(AppPreferences.Load().Language, language.Tag, StringComparison.Ordinal)) return;

        try
        {
            AppPreferences.Update(preferences => preferences with { Language = language.Tag });
            PreferenceStatusText.Text = language.Tag == Loc.CurrentTag
                ? Loc.F($"Language saved: {language.NativeName}.")
                : Loc.F($"Language saved: {language.NativeName}. Restart SockTuner to apply it.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PreferenceStatusText.Text = Loc.F($"Preference save failed: {exception.Message}");
        }
    }

    private void CreateCompatibilityReport_Click(object sender, RoutedEventArgs e) => CreateCompatibilityReport();

    /// <summary>Also reached from the window's Export menu.</summary>
    public void CreateCompatibilityReport()
    {
        if (Snapshot() is not { } snapshot)
        {
            StatusChanged?.Invoke(this, Loc.T("Refresh inventory before creating a compatibility report."));
            return;
        }

        if (MessageBox.Show(
                Loc.T("The report includes adapter model, driver version and supported setting constraints. It excludes all current values, persistent device IDs, addresses, paths, machine name and exact timestamps. Save it for review?"),
                Loc.T("Create compatibility report"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var report = CompatibilityReportExporter.Serialize(snapshot);
        var preview = new CompatibilityReportPreviewWindow(report) { Owner = owner };
        if (preview.ShowDialog() != true) return;

        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("SockTuner compatibility report (*.json)|*.json"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"SockTuner-compatibility-{Guid.NewGuid():N}.json"
        };
        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, report);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ContributionStatusText.Text = Loc.F($"Compatibility report failed: {exception.Message}");
            AppLog.Write("compatibility_report.failed", exception.Message);
            return;
        }

        ContributionStatusText.Text = Loc.F($"Compatibility report saved to {dialog.FileName}.");
        StatusChanged?.Invoke(this, Loc.T("Compatibility report saved."));
        AppLog.Write("compatibility_report.saved", "A privacy-safe compatibility report was saved.");

        if (MessageBox.Show(
                Loc.T("The JSON file is plain text. Inspect it if you wish, then attach it to the GitHub issue. Open the issue form now?"),
                Loc.T("Compatibility report saved"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CompatibilityIssueUrl)
            {
                UseShellExecute = true
            });
            ContributionStatusText.Text = Loc.T("GitHub issue form opened. Attach the saved JSON file before submitting.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ContributionStatusText.Text = Loc.F($"Report saved, but the GitHub issue form could not be opened: {exception.Message}");
        }
    }
}
