using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

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

    private void ReloadCapabilities_Click(object sender, RoutedEventArgs e) => LoadCapabilities();

    private void Adapter_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadCapabilities();

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PresetDescriptionText.Text = (PresetComboBox.SelectedItem as TuningPreset)?.Description ?? string.Empty;
        ApplyPresetFilter();
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
        InvalidatePlan("Proposals discarded.");
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var requests = _rows.Where(row => row.HasChange)
            .Select(row => new ChangeRequest(row.Capability.SettingId, row.Capability.AdapterId.ToString(), row.ProposedValue))
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
            var transactions = new SettingTransactionService(SettingSpecifications.From(_capabilities));
            _preparedPlan = await transactions.PrepareAsync(requests, store, CancellationToken.None);
            PlanPreviewGrid.ItemsSource = _preparedPlan.Changes;

            var needsConfirmation = _preparedPlan.Changes.Any(change => change.RequiresExplicitConfirmation);
            ConfirmationText.IsEnabled = needsConfirmation;
            ConfirmationText.Text = string.Empty;
            ApplyButton.IsEnabled = _preparedPlan.Changes.Count > 0;
            SetStatus(
                $"Dry run at {_preparedPlan.CreatedAt:T}: {_preparedPlan.Changes.Count} effective change(s). "
                + "Applying restarts each affected adapter, which briefly drops its link."
                + (needsConfirmation ? $" Type {ConfirmationWord} to confirm high-risk or experimental changes." : string.Empty));
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

        if (plan.Changes.Any(change => change.RequiresExplicitConfirmation)
            && !string.Equals(ConfirmationText.Text.Trim(), ConfirmationWord, StringComparison.Ordinal))
        {
            SetStatus($"This plan contains high-risk or experimental changes; type {ConfirmationWord} to confirm.");
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
