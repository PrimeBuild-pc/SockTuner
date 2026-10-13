using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Views;

/// <summary>
/// Which network devices are worth keeping, and queues enable/disable and power changes for the
/// tuning plan. Nothing here writes: every change waits in the plan for a preview.
/// </summary>
public partial class InterfacesView : UserControl
{
    private readonly ObservableCollection<InterfaceAdvice> _interfaceAdvice = [];
    private IReadOnlyList<AdapterInfo> _adapters = [];

    public InterfacesView()
    {
        InitializeComponent();
        InterfaceAdviceGrid.ItemsSource = _interfaceAdvice;
        InterfaceProfileComboBox.ItemsSource = InterfaceProfiles;
        InterfaceProfileComboBox.SelectedIndex = 0;
    }

    /// <summary>A line for the window's status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>The user asked to see the tuning plan.</summary>
    public event EventHandler? OpenPlanRequested;

    /// <summary>Where queued device changes go.</summary>
    public TuningPlanView? Plan { get; set; }

    public void SetAdapters(IReadOnlyList<AdapterInfo> adapters)
    {
        _adapters = adapters;
        ShowInterfaceAdvice();
    }

    /// <summary>
    /// How aggressively to read the advice. This is the one judgement in the analyzer that is a
    /// preference rather than a fact, so it is the one thing the profile controls; every other
    /// rule — and the refusal to touch the carrying adapter — is the same either way.
    /// </summary>
    private sealed record InterfaceProfile(string DisplayName, bool SinglePathPreferred);

    private static readonly IReadOnlyList<InterfaceProfile> InterfaceProfiles =
    [
        new("E-sports: one path only", true),
        new("Balanced: keep spare paths", false)
    ];

    private bool SinglePathPreferred =>
        (InterfaceProfileComboBox.SelectedItem as InterfaceProfile)?.SinglePathPreferred ?? true;

    private void ShowInterfaceAdvice()
    {
        var selectedId = (InterfaceAdviceGrid.SelectedItem as InterfaceAdvice)?.Adapter.Id;
        _interfaceAdvice.Clear();
        foreach (var advice in InterfaceAdvisor.Advise(_adapters, SinglePathPreferred))
        {
            _interfaceAdvice.Add(advice);
        }

        InterfaceAdviceGrid.SelectedItem =
            _interfaceAdvice.FirstOrDefault(item => item.Adapter.Id == selectedId);

        var flagged = _interfaceAdvice.Count(item => item.Verdict == InterfaceVerdict.ConsiderDisabling);
        var carrying = _interfaceAdvice.FirstOrDefault(item => item.Role == InterfaceRole.Carrying);
        var hidden = _adapters.Count(InterfaceAdvisor.IsOutOfScope);
        InterfaceSummaryAdviceText.Text = _interfaceAdvice.Count == 0
            ? Loc.T("Refresh the inventory to list network devices.")
            : Loc.F($"{_interfaceAdvice.Count} network device(s); {hidden} filter and loopback pseudo-interface(s) hidden. "
              + $"{flagged} worth considering for disabling. ")
              + (carrying is null
                  ? Loc.T("No interface currently carries a default route, so nothing is protected as the way back in.")
                  : Loc.F($"{carrying.Name} carries the default route and is never offered for disabling."));
        UpdateInterfaceActions();
    }

    private void InterfaceProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowInterfaceAdvice();
    }

    private void InterfaceAdvice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateInterfaceActions();

    /// <summary>
    /// Which actions the selected device allows. Disabling is offered only where the analyzer said
    /// so, which is what keeps the carrying adapter — and anything Windows owns — off the button.
    /// </summary>
    private void UpdateInterfaceActions()
    {
        var advice = InterfaceAdviceGrid.SelectedItem as InterfaceAdvice;
        var isDevice = advice is not null && advice.Role != InterfaceRole.NotApplicable;
        var isRealAdapter = isDevice && Guid.TryParse(advice!.Adapter.Id, out _);

        DisableInterfaceButton.IsEnabled = isRealAdapter && advice!.CanDisable;
        EnableInterfaceButton.IsEnabled = isRealAdapter && !advice!.Adapter.IsUp;
        PowerOffInterfaceButton.IsEnabled = isRealAdapter;
        PowerDefaultInterfaceButton.IsEnabled = isRealAdapter;

        if (advice is null)
        {
            InterfaceDetailHeadingText.Text = Loc.T("Select a device");
            InterfaceDetailText.Text =
                Loc.T("The reason behind each verdict, and what disabling the device would cost, are shown here.");
            return;
        }

        InterfaceDetailHeadingText.Text =
            $"{advice.Name}  ·  {advice.KindDisplay}  ·  {advice.RoleDisplay}  ·  {advice.VerdictDisplay}";
        InterfaceDetailText.Text =
            Loc.F($"{advice.Reason}{Environment.NewLine}{advice.Evidence}"
            + $"{Environment.NewLine}Link {advice.Adapter.SpeedDisplay}, addresses {advice.Adapter.AddressesDisplay}, "
            + $"gateways {advice.Adapter.GatewaysDisplay}.");
    }

    private void QueueDisableInterface_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterStateSpecification.SettingId, AdapterStateSpecification.Disabled,
            "disable", requiresFlagged: true);

    private void QueueEnableInterface_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterStateSpecification.SettingId, AdapterStateSpecification.Enabled,
            "enable", requiresFlagged: false);

    private void QueuePowerOff_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterPowerSavingSpecification.SettingId,
            AdapterPowerSavingSpecification.PowerManagementOff.ToString(),
            "turn power management off on", requiresFlagged: false);

    /// <summary>
    /// Restoring the default removes the value rather than writing a zero, so a null proposal is
    /// what this queues — the same thing the transaction engine treats as absent.
    /// </summary>
    private void QueuePowerDefault_Click(object sender, RoutedEventArgs e) =>
        QueueDeviceChange(AdapterPowerSavingSpecification.SettingId, null,
            "restore the power-management default on", requiresFlagged: false);

    private void QueueDeviceChange(string settingId, string? value, string verb, bool requiresFlagged)
    {
        if (InterfaceAdviceGrid.SelectedItem is not InterfaceAdvice advice)
        {
            InterfaceActionStatusText.Text = Loc.T("Select a device first.");
            return;
        }

        // Re-checked at the moment of the click, not only when the button was drawn: the advice can
        // have been rebuilt by a refresh since.
        if (requiresFlagged && !advice.CanDisable)
        {
            InterfaceActionStatusText.Text =
                Loc.F($"{advice.Name} is not offered for disabling: {advice.Reason}");
            return;
        }

        if (!Guid.TryParse(advice.Adapter.Id, out var adapterId))
        {
            InterfaceActionStatusText.Text = Loc.F($"{advice.Name} has no adapter GUID to target.");
            return;
        }

        if (Plan is not { } plan) return;
        var accepted = plan.ProposeDeviceChanges(
            [new ChangeRequest(settingId, adapterId.ToString(), value, ChangeSource.Manual)]);
        InterfaceActionStatusText.Text = accepted == 1
            ? Loc.F($"Queued: {verb} {advice.Name}. {plan.PendingDeviceChangeCount} device change(s) waiting. "
              + $"Nothing is written until you preview and confirm it in the tuning plan.")
            : Loc.F($"Refused: this machine no longer offers {settingId} on {advice.Name}.");
        AppLog.Write("interfaces.queued", $"Setting={settingId}; Adapter={adapterId}; Accepted={accepted}.");
    }

    private void OpenTuningPlan_Click(object sender, RoutedEventArgs e) => OpenPlanRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens a Windows management console. Both targets are fixed strings in this method — nothing
    /// the user or a report supplies reaches a process start.
    /// </summary>
    private void LaunchWindowsConsole(string target, string description)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            StatusChanged?.Invoke(this, Loc.F($"Opened {description}."));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusChanged?.Invoke(this, Loc.F($"Could not open {description}: {exception.Message}"));
        }
    }

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e) =>
        LaunchWindowsConsole("devmgmt.msc", "Device Manager");

    private void OpenNetworkConnections_Click(object sender, RoutedEventArgs e) =>
        LaunchWindowsConsole("ncpa.cpl", "Network Connections");
}
