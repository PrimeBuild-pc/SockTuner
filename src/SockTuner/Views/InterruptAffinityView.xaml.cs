using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;

namespace SockTuner.Views;

/// <summary>Shows which processors service each device's interrupts, and changes that placement.</summary>
public partial class InterruptAffinityView : UserControl
{
    private readonly ElevatedWorkerClient _irqWorker = new();
    private InterruptAffinityInventoryResult? _interrupts;
    private readonly ObservableCollection<CoreChoice> _coreChoices = [];

    public InterruptAffinityView()
    {
        InitializeComponent();
        IrqPolicyComboBox.ItemsSource = new[]
        {
            InterruptPolicy.SpecifiedProcessors,
            InterruptPolicy.AllCloseProcessors,
            InterruptPolicy.OneCloseProcessor,
            InterruptPolicy.AllProcessorsInMachine,
            InterruptPolicy.SpreadMessagesAcrossAllProcessors
        }.Select(policy => new PolicyChoice(policy)).ToArray();
        IrqPolicyComboBox.SelectedIndex = 0;
        IrqPriorityComboBox.ItemsSource = Enum.GetValues<InterruptPriority>();
        IrqPriorityComboBox.SelectedItem = InterruptPriority.Undefined;
        IrqCoreList.ItemsSource = _coreChoices;
    }

    private void IrqRescan_Click(object sender, RoutedEventArgs e) => Load();

    private void IrqFilter_Changed(object sender, RoutedEventArgs e) => ShowInterruptDevices();

    /// <summary>
    /// Reloads and selects one device by its PnP instance ID — how the "Modify…" option on a DPC/ISR
    /// recommendation hands the user to this view's own core picker instead of a second one.
    /// </summary>
    public void SelectDevice(string instanceId)
    {
        Load();
        if (IrqDeviceGrid.ItemsSource is IEnumerable<InterruptAffinityDevice> devices)
        {
            IrqDeviceGrid.SelectedItem = devices.FirstOrDefault(
                device => string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Reads the devices and their current placement. Read-only.</summary>
    public void Load()
    {
        _interrupts = InterruptAffinityInventory.Read();
        if (_interrupts.Error is { } error)
        {
            IrqSummaryText.Text = Loc.F($"Device scan failed: {error}");
            return;
        }

        _coreChoices.Clear();
        for (var core = 0; core < _interrupts.LogicalProcessors; core++)
        {
            _coreChoices.Add(new CoreChoice(core));
        }

        ShowInterruptDevices();
    }

    private void ShowInterruptDevices()
    {
        if (_interrupts is not { } inventory) return;

        var devices = inventory.Devices.AsEnumerable();
        if (IrqNetworkOnlyCheck.IsChecked == true) devices = devices.Where(device => device.IsNetwork);
        if (IrqOverriddenOnlyCheck.IsChecked == true) devices = devices.Where(device => device.HasOverride);

        var rows = devices.ToArray();
        IrqDeviceGrid.ItemsSource = rows;
        var overridden = inventory.Devices.Count(device => device.HasOverride);
        IrqSummaryText.Text =
            Loc.F($"{rows.Length} device(s) shown of {inventory.Devices.Count} present. "
            + $"{overridden} currently carry an override; the rest are placed by Windows. "
            + $"This machine reports {inventory.LogicalProcessors} logical processors.");
    }

    private void IrqDeviceGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = IrqDeviceGrid.SelectedItem as InterruptAffinityDevice;
        IrqApplyButton.IsEnabled = selected is not null;
        IrqResetButton.IsEnabled = selected is { HasOverride: true };
        if (selected is null)
        {
            IrqSelectedDeviceText.Text = Loc.T("Select a device above.");
            return;
        }

        IrqSelectedDeviceText.Text =
            Loc.F($"{selected.FriendlyName}  —  {selected.StateDisplay}. Instance: {selected.InstanceId}");

        // Start from what the device already has, so applying without touching anything is a no-op
        // rather than a silent change to whatever the controls happened to show.
        var current = selected.Cores.ToHashSet();
        foreach (var choice in _coreChoices) choice.Selected = current.Contains(choice.Core);

        var wanted = selected.Policy == InterruptPolicy.MachineDefault
            ? InterruptPolicy.SpecifiedProcessors
            : selected.Policy;
        IrqPolicyComboBox.SelectedItem = IrqPolicyComboBox.ItemsSource
            .OfType<PolicyChoice>()
            .FirstOrDefault(choice => choice.Policy == wanted);
        IrqPriorityComboBox.SelectedItem = selected.Priority;
        UpdateIrqWarning();
    }

    private void IrqPolicy_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var specified = IrqPolicyComboBox.SelectedItem is PolicyChoice { Policy: InterruptPolicy.SpecifiedProcessors };
        IrqCoreList.IsEnabled = specified;
        UpdateIrqWarning();
    }

    /// <summary>
    /// Says the two things that actually go wrong: pinning to CPU 0, which already carries most of
    /// the system's deferred work, and stacking several devices onto one core.
    /// </summary>
    private void UpdateIrqWarning()
    {
        var warnings = new List<string>();
        var chosen = _coreChoices.Where(choice => choice.Selected).Select(choice => choice.Core).ToArray();

        if (IrqPolicyComboBox.SelectedItem is PolicyChoice { Policy: InterruptPolicy.SpecifiedProcessors })
        {
            if (chosen.Length == 0)
            {
                warnings.Add("Select at least one processor, or the device would have no core to run on.");
            }
            else if (chosen.Contains(0))
            {
                warnings.Add("CPU 0 already services most of the system's deferred work by default; it is rarely the right place to add more.");
            }

            if (chosen.Length == 1 && _interrupts is { Devices.Count: > 0 })
            {
                var sharing = _interrupts.Devices.Count(device =>
                    device.Policy == InterruptPolicy.SpecifiedProcessors && device.Cores.Contains(chosen[0]));
                if (sharing > 0)
                {
                    warnings.Add($"{sharing} other device(s) are already pinned to CPU {chosen[0]}.");
                }
            }
        }

        IrqWarningText.Text = string.Join(Environment.NewLine, warnings);
        IrqWarningText.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void IrqApply_Click(object sender, RoutedEventArgs e)
    {
        if (IrqDeviceGrid.SelectedItem is not InterruptAffinityDevice device) return;
        if (IrqPolicyComboBox.SelectedItem is not PolicyChoice { Policy: var policy }
            || IrqPriorityComboBox.SelectedItem is not InterruptPriority priority)
        {
            IrqStatusText.Text = Loc.T("Select a policy and priority.");
            return;
        }

        var mask = policy == InterruptPolicy.SpecifiedProcessors
            ? InterruptAffinityDevice.ToMask(_coreChoices.Where(choice => choice.Selected).Select(choice => choice.Core))
            : 0UL;

        await ApplyInterruptAffinityAsync(
            device, new StoredSettingValue(true, InterruptAffinitySpecification.Canonical(policy, priority, mask)));
    }

    private async void IrqReset_Click(object sender, RoutedEventArgs e)
    {
        if (IrqDeviceGrid.SelectedItem is not InterruptAffinityDevice device) return;
        await ApplyInterruptAffinityAsync(device, StoredSettingValue.Missing);
    }

    private async Task ApplyInterruptAffinityAsync(InterruptAffinityDevice device, StoredSettingValue desired)
    {
        if (_interrupts is not { } inventory) return;
        if (!WriteConsentDialog.Ensure(out var consentProblem))
        {
            IrqStatusText.Text = consentProblem;
            return;
        }

        try
        {
            var specification = new InterruptAffinitySpecification(
                Math.Max(inventory.LogicalProcessors, 1),
                inventory.Devices.Select(item => item.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (desired.Exists) specification.Validate(desired.Value);

            var address = specification.ResolveAddress(device.InstanceId);
            var store = new InterruptAffinityStore(specification);
            var before = await store.ReadAsync(address, CancellationToken.None);
            if (before == desired)
            {
                IrqStatusText.Text = Loc.F($"{device.FriendlyName} already has that placement.");
                return;
            }

            var request = ElevatedChange.Request(WorkerOperationKind.Apply,
                [ElevatedChange.Operation(address.SettingId, address.TargetId, before, desired, ChangeSource.Manual)]);

            IrqStatusText.Text = Loc.T("Approve the Windows elevation prompt to continue\u2026");
            AppLog.Write("irq.apply_requested",
                $"Device={device.FriendlyName}; To={(desired.Exists ? desired.Value : "default")}");
            var response = await ElevatedChange.RunAsync(
                _irqWorker.ExecuteAsync, request, "irq.apply", status => IrqStatusText.Text = status, Failed);
            if (response is null) return;
            IrqStatusText.Text = response.Success
                ? Loc.F($"{response.Status} The new placement takes effect after a restart.")
                : response.Status;
            if (response.Success) Load();
        }
        // Validation and the registry read can fail too; the worker call itself never throws.
        catch (Exception exception)
        {
            IrqStatusText.Text = Failed(exception.Message);
            AppLog.Write("irq.apply_failed", exception.Message);
        }

        static string Failed(string message) => Loc.F($"Interrupt affinity change failed: {message}");
    }

    /// <summary>A policy with the wording shown to the user rather than its enum name.</summary>
    private sealed record PolicyChoice(InterruptPolicy Policy)
    {
        public string Display => Policy switch
        {
            InterruptPolicy.SpecifiedProcessors => "Only the processors I select",
            InterruptPolicy.AllCloseProcessors => "All processors near the device",
            InterruptPolicy.OneCloseProcessor => "One processor near the device",
            InterruptPolicy.AllProcessorsInMachine => "Any processor",
            InterruptPolicy.SpreadMessagesAcrossAllProcessors => "Spread messages across all processors",
            _ => Policy.ToString()
        };
    }

    /// <summary>One selectable processor. Bound to the pill toggles.</summary>
    private sealed class CoreChoice(int core) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _selected;

        public int Core { get; } = core;
        public string Label => $"CPU {Core}";

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
