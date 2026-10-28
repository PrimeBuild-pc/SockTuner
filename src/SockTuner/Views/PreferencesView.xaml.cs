using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>
/// Appearance, language, log retention, the compatibility report and updates. Each setting is
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
        ThemeComboBox.ItemsSource = Themes;
        ThemeComboBox.SelectedItem = Themes.Contains(preferences.Theme) ? preferences.Theme : "System";
        RememberSectionSwitch.IsChecked = preferences.RememberSection;
    }

    /// <summary>A line for the window's status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>The inventory a compatibility report is built from; null until the first refresh.</summary>
    public Func<NetworkSnapshot?> Snapshot { get; set; } = () => null;

    public static readonly string[] Themes = ["System", "Light", "Dark"];

    /// <summary>Applies a saved theme name; an unknown one falls back to following Windows.</summary>
    // ThemeMode is the supported Fluent switch in this WPF; App.xaml already depends on it.
#pragma warning disable WPF0001
    public static void ApplyTheme(string? name) => Application.Current.ThemeMode = name switch
    {
        "Light" => ThemeMode.Light,
        "Dark" => ThemeMode.Dark,
        _ => ThemeMode.System
    };
#pragma warning restore WPF0001

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ThemeComboBox.SelectedItem is not string theme) return;
        Save(preferences => preferences with { Theme = theme }, () => { ApplyTheme(theme); return Loc.T("Saved."); });
    }

    private void RememberSection_Click(object sender, RoutedEventArgs e) =>
        Save(preferences => preferences with { RememberSection = RememberSectionSwitch.IsChecked == true });

    /// <summary>The one save path: read-modify-write, then say what happened. <paramref name="message"/> may also apply the change.</summary>
    private void Save(Func<UserPreferences, UserPreferences> change, Func<string>? message = null)
    {
        try
        {
            AppPreferences.Update(change);
            PreferenceStatusText.Text = message?.Invoke() ?? Loc.T("Saved.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PreferenceStatusText.Text = Loc.F($"Preference save failed: {exception.Message}");
        }
    }

    private void LogRetention_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The constructor's own selection is not a choice; saving it would pin today's values.
        if (!IsLoaded || LogRetentionComboBox.SelectedItem is not int megabytes) return;
        Save(preferences => preferences with { LogFileMegabytes = megabytes }, () =>
            AppLog.ConfigureRetention(megabytes) is { } error
                ? Loc.F($"Retention saved, but existing logs could not be trimmed: {error}")
                : Loc.F($"Retention saved: two files of up to {megabytes} MB each."));
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
        Save(preferences => preferences with { Language = language.Tag }, () => language.Tag == Loc.CurrentTag
            ? Loc.F($"Language saved: {language.NativeName}.")
            : Loc.F($"Language saved: {language.NativeName}. Restart SockTuner to apply it."));
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

        var saved = new ReportSavedWindow { Owner = owner, GitHubUrl = CompatibilityIssueUrl };
        if (saved.ShowDialog() != true || saved.ChosenUrl is not { } url) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            ContributionStatusText.Text = Loc.T("Page opened. Attach the saved JSON file before sending.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ContributionStatusText.Text = Loc.F($"Report saved, but the page could not be opened: {exception.Message}");
        }
    }
}
