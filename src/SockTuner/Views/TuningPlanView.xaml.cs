using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Views;

public sealed record TuningPreset(string Name, TuningArea Area, string Description)
{
    // Any overlap, not every flag: a preset spanning two areas ("Power & wake") must show a
    // property that belongs to either of them, not only ones belonging to both.
    public bool Includes(AdapterSettingCapability capability) =>
        Area == TuningArea.None || (capability.Areas & Area) != TuningArea.None;

    public static IReadOnlyList<TuningPreset> All { get; } =
    [
        new("All properties", TuningArea.None, "Everything the selected driver advertises."),
        new("Latency", TuningArea.Latency, "Interrupt moderation, coalescing and the power features that add wake-up delay."),
        new("Bandwidth", TuningArea.Throughput, "Offloads, buffers, frame size and flow control."),
        new("Power & wake", TuningArea.Power | TuningArea.Wake, "Energy saving, sleep offloads and wake-on-LAN."),
        new("Wi-Fi radio", TuningArea.WiFiRadio, "Band, channel width, roaming and transmit power."),
        new("VLAN & identity", TuningArea.Vlan | TuningArea.Identity, "Tagging, VLAN ID, MAC address and MTU.")
    ];
}

/// <summary>One editable row: a driver-advertised property plus the value the user proposes.</summary>
public sealed class CapabilityRow : INotifyPropertyChanged
{
    private string _proposedValue;

    public CapabilityRow(AdapterSettingCapability capability)
    {
        Capability = capability;
        _proposedValue = capability.CurrentValue;
        Options = capability.Choices.Select(choice => choice.RegistryValue).ToArray();
    }

    public AdapterSettingCapability Capability { get; }
    public IReadOnlyList<string> Options { get; }

    public string ProposedValue
    {
        get => _proposedValue;
        set
        {
            if (string.Equals(_proposedValue, value, StringComparison.Ordinal)) return;
            _proposedValue = value ?? string.Empty;
            Notify();
            Notify(nameof(HasChange));
        }
    }

    public bool HasChange => !string.Equals(ProposedValue, Capability.CurrentValue, StringComparison.Ordinal);

    public void Reset() => ProposedValue = Capability.CurrentValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class TuningPlanView : UserControl
{
    private const string ConfirmationWord = "APPLY";

    private readonly TransactionAuditStore _auditStore = new();
    private readonly ElevatedWorkerClient _worker = new();
    private readonly ObservableCollection<CapabilityRow> _rows = [];

    // Device-level changes — enabling an adapter, its power management — sent here from the
    // Interfaces tab. They are not NDIS keywords on one adapter, so they cannot live in _rows,
    // but they go through exactly the same preview, confirmation, audit and rollback path. There
    // is one write path in this app and this is it.
    private readonly List<ChangeRequest> _deviceChanges = [];
    private IReadOnlyList<AdapterSettingCapability> _capabilities = [];
    private ChangePlan? _preparedPlan;
    private string? _capabilityError;

    public TuningPlanView()
    {
        InitializeComponent();
        PresetComboBox.ItemsSource = TuningPreset.All;
        PresetComboBox.SelectedIndex = 0;
        CapabilityGrid.ItemsSource = _rows;
        RefreshAuditHistory();
    }

    /// <summary>Raised when a change was applied or rolled back, so the shell can re-inventory.</summary>
    public event EventHandler? Applied;

    public void SetAdapters(IReadOnlyList<AdapterInfo> adapters)
    {
        var selectedId = (AdapterComboBox.SelectedItem as AdapterInfo)?.Id;
        var candidates = adapters.Where(adapter => Guid.TryParse(adapter.Id, out _)).ToArray();
        AdapterComboBox.ItemsSource = candidates;
        AdapterComboBox.SelectedItem = candidates.FirstOrDefault(adapter => adapter.Id == selectedId)
            ?? candidates.FirstOrDefault(adapter => adapter.NdisSupported)
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Queues device-level changes for the next preview. Each one is resolved and validated here,
    /// against the adapters this machine actually has, so a request naming a device that is not
    /// present is dropped rather than carried into a plan. Returns how many were accepted.
    /// </summary>
    public int ProposeDeviceChanges(IReadOnlyList<ChangeRequest> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var resolve = DeviceResolver();
        var accepted = 0;
        foreach (var change in changes)
        {
            if (change.ProposedValue is not { } value) continue;
            try
            {
                var specification = resolve(change.SettingId, change.TargetId);
                specification.ResolveAddress(change.TargetId);
                specification.Validate(value);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or KeyNotFoundException)
            {
                continue;
            }

            // One pending change per setting per device: proposing twice replaces, never stacks.
            _deviceChanges.RemoveAll(item =>
                string.Equals(item.SettingId, change.SettingId, StringComparison.Ordinal)
                && string.Equals(item.TargetId, change.TargetId, StringComparison.OrdinalIgnoreCase));
            _deviceChanges.Add(change);
            accepted++;
        }

        if (accepted > 0)
        {
            InvalidatePlan($"{accepted} device change(s) queued. Preview the plan before applying.");
        }

        return accepted;
    }

    /// <summary>Pending device changes, so another surface can show what is waiting.</summary>
    public int PendingDeviceChangeCount => _deviceChanges.Count;

    private SettingSpecificationResolver DeviceResolver() => SettingSpecifications.From(
        _capabilities,
        globals: null,
        presentAdapters: AdapterStateSpecification.PresentAdapters(),
        adapterKeys: AdapterPowerSavingSpecification.ReadAdapterKeys());

    /// <summary>
    /// Fills in proposed values for changes suggested elsewhere in the app. It only sets values on
    /// rows the driver already advertises for the selected adapter and that pass their own
    /// validation, and returns how many were accepted — a caller cannot smuggle in a setting that
    /// is not on this adapter, and nothing is applied: the plan still has to be previewed and
    /// confirmed by hand.
    /// </summary>
    public int Propose(IReadOnlyList<ChangeRequest> changes)
    {
        var accepted = 0;
        foreach (var change in changes)
        {
            if (change.ProposedValue is not { } value) continue;
            var row = _rows.FirstOrDefault(item =>
                string.Equals(item.Capability.SettingId, change.SettingId, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;

            var text = value.ToString();
            try
            {
                row.Capability.Validate(text);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidOperationException)
            {
                continue;
            }

            row.ProposedValue = text;
            accepted++;
        }

        if (accepted > 0)
        {
            InvalidatePlan($"{accepted} proposed value(s) filled in. Preview the plan before applying.");
        }

        return accepted;
    }

    /// <summary>
    /// Reads back every setting this app has written and reports the ones something else has since
    /// changed. Read-only: it opens the same stores the preview uses, in their read-only form.
    /// </summary>
    private void CheckDrift_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var store = new CompositeSettingStore(
                WindowsRegistrySettingStore.CreateReadOnly(), new CimAdapterSettingStore());
            var resolve = DeviceResolver();

            var drift = SettingDriftAnalyzer.Compare(
                _auditStore.Load(),
                (settingId, targetId) =>
                {
                    var address = resolve(settingId, targetId).ResolveAddress(targetId);
                    return store.ReadAsync(address, CancellationToken.None).GetAwaiter().GetResult();
                });

            DriftGrid.ItemsSource = drift;
            DriftSummaryText.Text = SettingDriftAnalyzer.Summarise(drift);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                          or KeyNotFoundException or System.IO.IOException)
        {
            DriftGrid.ItemsSource = null;
            DriftSummaryText.Text = $"Drift check failed: {exception.Message}";
        }
    }

    private void ReloadCapabilities_Click(object sender, RoutedEventArgs e) => LoadCapabilities();

    private void Adapter_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadCapabilities();

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PresetDescriptionText.Text = (PresetComboBox.SelectedItem as TuningPreset)?.Description ?? string.Empty;
        ApplyPresetFilter();
    }

    /// <summary>
    /// The keyword, the trade-off and the full accepted range for the selected property. They used
    /// to be grid columns, where nine columns in a thousand pixels left them two characters wide.
    /// </summary>
    private void Capability_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CapabilityGrid.SelectedItem is not CapabilityRow row)
        {
            CapabilityDetailHeadingText.Text = "Select a property";
            CapabilityDetailText.Text =
                "Its keyword, what it trades against, and everything the driver will accept are shown here.";
            return;
        }

        var capability = row.Capability;
        CapabilityDetailHeadingText.Text =
            $"{capability.DisplayName}  ·  {capability.Keyword}  ·  {capability.Risk} risk  ·  {capability.AreasDisplay}";
        CapabilityDetailText.Text =
            $"Accepted: {capability.ConstraintDisplay}{Environment.NewLine}Trade-off: {capability.TradeOff}";
    }

    private void LoadCapabilities()
    {
        var result = WindowsAdapterCapabilityInventory.Read();
        _capabilities = result.Capabilities;
        _capabilityError = result.Error;
        ApplyPresetFilter();
        InvalidatePlan("Capabilities reloaded; rebuild the preview before applying.");
    }

    private void ApplyPresetFilter()
    {
        _rows.Clear();
        if (AdapterComboBox.SelectedItem is not AdapterInfo adapter
            || !Guid.TryParse(adapter.Id, out var adapterId))
        {
            CapabilitySummaryText.Text = "Select an adapter to list its driver-advertised properties.";
            return;
        }

        var preset = PresetComboBox.SelectedItem as TuningPreset ?? TuningPreset.All[0];
        var forAdapter = _capabilities.Where(capability => capability.AdapterId == adapterId).ToArray();
        foreach (var capability in forAdapter.Where(preset.Includes))
        {
            _rows.Add(new CapabilityRow(capability));
        }

        CapabilitySummaryText.Text = forAdapter.Length == 0
            ? $"{adapter.Name} advertises no writable advanced properties. This is normal for virtual and filter adapters."
            : $"{_rows.Count} of {forAdapter.Length} advertised propert(ies) match “{preset.Name}”. "
              + $"{forAdapter.Count(item => item.IsModifiedFromDefault)} currently differ from the driver default."
              + (_capabilityError is null ? string.Empty : $" Partial inventory: {_capabilityError}");
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Reset();
        var devices = _deviceChanges.Count;
        _deviceChanges.Clear();
        InvalidatePlan(devices == 0
            ? "Proposals discarded."
            : $"Proposals discarded, including {devices} queued device change(s).");
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var requests = _rows.Where(row => row.HasChange)
            .Select(row => new ChangeRequest(row.Capability.SettingId, row.Capability.AdapterId.ToString(), row.ProposedValue))
            .Concat(_deviceChanges)
            .ToArray();
        if (requests.Length == 0)
        {
            InvalidatePlan("Change at least one proposed value first.");
            return;
        }

        try
        {
            // Preview reads through a read-only store: nothing here can write.
            var store = new CompositeSettingStore(
                WindowsRegistrySettingStore.CreateReadOnly(), new CimAdapterSettingStore());
            var transactions = new SettingTransactionService(DeviceResolver());
            _preparedPlan = await transactions.PrepareAsync(requests, store, CancellationToken.None);
            PlanPreviewGrid.ItemsSource = _preparedPlan.Changes;

            // A remote session makes any link-dropping change worth a deliberate act, even one the
            // plan would otherwise apply on a single click: the cost of being wrong is the machine.
            var needsConfirmation = _preparedPlan.Changes.Any(change => change.RequiresExplicitConfirmation)
                || RemoteSessionGuard.WarningFor(_preparedPlan.Changes) is not null;
            ConfirmationText.IsEnabled = needsConfirmation;
            ConfirmationText.Text = string.Empty;
            ApplyButton.IsEnabled = _preparedPlan.Changes.Count > 0;
            var remote = RemoteSessionGuard.WarningFor(_preparedPlan.Changes);
            SetStatus(
                $"Dry run at {_preparedPlan.CreatedAt:T}: {_preparedPlan.Changes.Count} effective change(s). "
                + "Applying restarts each affected adapter, which briefly drops its link."
                + (needsConfirmation ? $" Type {ConfirmationWord} to confirm high-risk or experimental changes." : string.Empty)
                + (remote is null ? string.Empty : Environment.NewLine + remote));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                          or KeyNotFoundException)
        {
            InvalidatePlan($"Preview refused: {exception.Message}");
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_preparedPlan is not { Changes.Count: > 0 } plan)
        {
            InvalidatePlan("Build a preview before applying.");
            return;
        }

        var remoteWarning = RemoteSessionGuard.WarningFor(plan.Changes);
        if ((plan.Changes.Any(change => change.RequiresExplicitConfirmation) || remoteWarning is not null)
            && !string.Equals(ConfirmationText.Text.Trim(), ConfirmationWord, StringComparison.Ordinal))
        {
            SetStatus(remoteWarning is null
                ? $"This plan contains high-risk or experimental changes; type {ConfirmationWord} to confirm."
                : $"{remoteWarning} Type {ConfirmationWord} to confirm anyway.");
            return;
        }

        if (!EnsureWriteConsent()) return;

        var request = new ElevatedWorkerRequest(
            ElevatedWorker.SchemaVersion,
            Guid.NewGuid(),
            WorkerOperationKind.Apply,
            plan.Changes.Select(change => new WorkerSettingOperation(
                change.Address.SettingId,
                change.Address.TargetId,
                new WorkerStoredValue(change.Before.Exists, change.Before.Value),
                new WorkerStoredValue(change.After.Exists, change.After.Value),
                change.Source)).ToArray());

        await RunWorkerAsync(request, "Apply");
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (TransactionAuditGrid.SelectedItem is not TransactionAuditEntry entry)
        {
            SetStatus("Select an audit entry to roll back.");
            return;
        }

        if (entry.Outcome != TransactionAuditOutcome.ApplySucceeded)
        {
            SetStatus("Only a successful apply can be rolled back.");
            return;
        }

        if (!EnsureWriteConsent()) return;

        // Rollback inverts the recorded change: the value it wrote becomes what we expect to
        // find, and the value it captured beforehand becomes what we restore.
        var request = new ElevatedWorkerRequest(
            ElevatedWorker.SchemaVersion,
            Guid.NewGuid(),
            WorkerOperationKind.Rollback,
            entry.Changes.Select(change => new WorkerSettingOperation(
                change.SettingId,
                change.TargetId,
                new WorkerStoredValue(change.After.Exists, change.After.Value),
                new WorkerStoredValue(change.Before.Exists, change.Before.Value),
                ChangeSource.Recovery)).ToArray());

        await RunWorkerAsync(request, "Rollback");
    }

    private async Task RunWorkerAsync(ElevatedWorkerRequest request, string operation)
    {
        SetStatus($"{operation} requested. Approve the Windows elevation prompt to continue…");
        ApplyButton.IsEnabled = false;
        try
        {
            var response = await _worker.ExecuteAsync(request, CancellationToken.None);
            SetStatus($"{operation}: {response.Status}");
            AppLog.Write($"tuning.{operation.ToLowerInvariant()}",
                $"Success={response.Success}; Changes={request.Changes.Count}; Status={response.Status}");
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            SetStatus(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException
                                          or System.IO.IOException or System.Text.Json.JsonException)
        {
            SetStatus($"{operation} failed: {exception.Message}");
            AppLog.Write($"tuning.{operation.ToLowerInvariant()}_failed", exception.Message);
        }
        finally
        {
            _preparedPlan = null;
            PlanPreviewGrid.ItemsSource = null;
            ConfirmationText.Text = string.Empty;
            ConfirmationText.IsEnabled = false;
            RefreshAuditHistory();
            LoadCapabilities();
            Applied?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool EnsureWriteConsent()
    {
        var preferences = AppPreferences.Load();
        if (WriteConsent.IsAccepted(preferences)) return true;

        if (MessageBox.Show(
                WriteConsent.Text,
                "Enable live network changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SetStatus("Live changes stay disabled until the alpha risk notice is accepted.");
            return false;
        }

        try
        {
            AppPreferences.Save(WriteConsent.Accept(preferences));
            return true;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            SetStatus($"Consent could not be saved: {exception.Message}");
            return false;
        }
    }

    private void RefreshAuditHistory() => TransactionAuditGrid.ItemsSource = _auditStore.Load();

    private void InvalidatePlan(string status)
    {
        _preparedPlan = null;
        PlanPreviewGrid.ItemsSource = null;
        ApplyButton.IsEnabled = false;
        ConfirmationText.IsEnabled = false;
        SetStatus(status);
    }

    private void SetStatus(string value)
    {
        PlanStatusText.Text = value;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(PlanStatusText)
            ?? new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(PlanStatusText);
        peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
