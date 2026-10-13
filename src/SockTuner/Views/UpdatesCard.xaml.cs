using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>
/// The Dashboard's update card: checks a GitHub Releases channel and hands a verified build to the
/// installer. Owns the channel and last-check preferences.
/// </summary>
public partial class UpdatesCard : UserControl
{
    private readonly AppUpdateService _updates = new();
    private CancellationTokenSource? _updateCancellation;
    private AppRelease? _availableUpdate;
    private readonly bool _initializingUpdateChannel = true;

    public UpdatesCard()
    {
        InitializeComponent();
        var choices = Enum.GetValues<UpdateChannel>()
            .Select(channel => new UpdateChannelChoice(channel, Loc.T(channel.ToString())))
            .ToArray();
        UpdateChannelComboBox.ItemsSource = choices;
        var savedChannel = Enum.TryParse<UpdateChannel>(AppPreferences.Load().UpdateChannel, true, out var channel)
            ? channel
            : UpdateChannel.Stable;
        UpdateChannelComboBox.SelectedItem = choices.First(item => item.Channel == savedChannel);
        _initializingUpdateChannel = false;
        UpdateVersionText.Text = Loc.F($"Installed version: {AppUpdateService.CurrentVersion}");
    }

    /// <summary>The quiet check at startup, at most once a day.</summary>
    public async Task CheckIfDueAsync()
    {
        if (AppPreferences.Load().LastUpdateCheckAt is not { } checkedAt
            || DateTimeOffset.UtcNow - checkedAt >= TimeSpan.FromDays(1))
        {
            await CheckForUpdatesAsync(quiet: true);
        }
    }

    public void CancelActiveWork() => _updateCancellation?.Cancel();

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(quiet: false);

    private void UpdateChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingUpdateChannel || UpdateChannelComboBox.SelectedItem is not UpdateChannelChoice choice) return;
        _availableUpdate = null;
        InstallUpdateButton.IsEnabled = false;
        SavePreferences(preferences => preferences with { UpdateChannel = choice.Channel.ToString(), LastUpdateCheckAt = null });
        SetUpdateStatus(Loc.F($"{choice.DisplayName} channel selected. Check when you are ready."));
    }

    private async Task CheckForUpdatesAsync(bool quiet)
    {
        if (UpdateChannelComboBox.SelectedItem is not UpdateChannelChoice choice) return;
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.ToolTip = null;
        if (!quiet) SetUpdateStatus(Loc.T("Checking GitHub Releases…"));

        try
        {
            _availableUpdate = await _updates.CheckAsync(choice.Channel, cancellation.Token);
            if (_availableUpdate is null)
            {
                SetUpdateStatus(Loc.F($"Version {AppUpdateService.CurrentVersion} is up to date on the {choice.DisplayName} channel."));
            }
            else
            {
                SetUpdateStatus(AppUpdateService.AutomaticInstallationAvailable
                    ? Loc.F($"Version {_availableUpdate.Version} is available: {_availableUpdate.Name}")
                    : Loc.F($"Version {_availableUpdate.Version} is available, but automatic installation requires a signed SockTuner build."));
                UpdateStatusText.ToolTip = _availableUpdate.Notes;
                InstallUpdateButton.IsEnabled = AppUpdateService.AutomaticInstallationAvailable;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!quiet) SetUpdateStatus(Loc.T("Update check canceled."));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                           or InvalidDataException)
        {
            SetUpdateStatus(Loc.F($"Updates could not be checked: {exception.Message}"));
            AppLog.Write("updates.check_failed", exception.Message);
        }
        finally
        {
            SavePreferences(preferences => preferences with { LastUpdateCheckAt = DateTimeOffset.UtcNow });
            CheckUpdatesButton.IsEnabled = true;
            if (ReferenceEquals(_updateCancellation, cancellation)) _updateCancellation = null;
            cancellation.Dispose();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } release || Environment.ProcessPath is not { } currentExecutable) return;
        var notes = string.IsNullOrWhiteSpace(release.Notes)
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + release.Notes[..Math.Min(release.Notes.Length, 1200)];
        if (MessageBox.Show(
                Loc.F($"Download and install SockTuner {release.Version}? The app will restart after verifying the archive checksum and Authenticode signature.{notes}"),
                Loc.T("Install SockTuner update"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        SetUpdateStatus(Loc.F($"Downloading and verifying SockTuner {release.Version}…"));
        try
        {
            var prepared = await _updates.PrepareAsync(release, currentExecutable, cancellation.Token);
            var start = new ProcessStartInfo(prepared.ExecutablePath) { UseShellExecute = true };
            if (AppUpdateService.InstallationNeedsElevation(currentExecutable)) start.Verb = "runas";
            start.ArgumentList.Add(UpdateInstaller.Argument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(currentExecutable);
            Process.Start(start);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetUpdateStatus(Loc.T("Update installation canceled."));
            CheckUpdatesButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetUpdateStatus(Loc.F($"The update could not be installed: {exception.Message}"));
            AppLog.Write("updates.install_failed", exception.Message);
            CheckUpdatesButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
        finally
        {
            if (ReferenceEquals(_updateCancellation, cancellation)) _updateCancellation = null;
            cancellation.Dispose();
        }
    }

    private static void SavePreferences(Func<UserPreferences, UserPreferences> change)
    {
        try
        {
            AppPreferences.Update(change);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("updates.preference_failed", exception.Message);
        }
    }

    private void SetUpdateStatus(string value)
    {
        UpdateStatusText.Text = value;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(UpdateStatusText)
            ?? new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(UpdateStatusText);
        peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private sealed record UpdateChannelChoice(UpdateChannel Channel, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
