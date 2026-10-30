using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;
using SockTuner.Views;

namespace SockTuner;

/// <summary>
/// The two host-side captures that ride "Check my connection" / "Run complete check" rather than
/// running on their own: process connections (always) and DPC/ISR driver activity (opt-in, since it
/// needs elevation). See <see cref="Views.HostDiagnosticsView"/> for how the results and their three
/// possible recommended actions are displayed.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan DpcIsrCaptureDuration = TimeSpan.FromSeconds(5);
    private readonly HostActionClient _hostActionClient = new();
    private readonly ElevatedWorkerClient _hostDiagnosticsWorker = new();

    // Cached so the checklist export (RecommendationsView) can carry all three DPC/ISR
    // recommendations, not just the one already folded into the report model.
    private IReadOnlyList<HostGuidanceItem> _lastDpcIsrHostGuidance = [];
    private RemediationAction? _lastDpcIsrAffinityAction;
    private HostGuidanceItem? _lastDpcIsrDisableGuidance;

    private async Task<IReadOnlyList<ProcessConnectionSummary>> ShowProcessConnectionsAsync(CancellationToken cancellationToken)
    {
        var result = await Task.Run(WindowsProcessConnectionInventory.Read, cancellationToken);
        var summaries = ProcessConnectionSummary.Summarize(result.Connections);
        HostDiagnostics.SetProcessConnections(summaries, result.Error);
        return summaries;
    }

    private async Task<DpcIsrFinding> ShowDpcIsrResultAsync(
        GamingDiagnosticReport report, CancellationToken cancellationToken)
    {
        DpcIsrCaptureResult capture;
        try
        {
            var response = await _hostActionClient.ExecuteAsync(
                new HostActionRequest(Guid.NewGuid(), HostActionKind.CaptureDpcIsr,
                    DurationSeconds: (int)DpcIsrCaptureDuration.TotalSeconds, Target: report.GameTarget.Target),
                cancellationToken);
            capture = response.Success && response.Capture is not null
                ? response.Capture
                : new([], DpcIsrCaptureDuration, response.Error ?? "No concurrent capture was returned.");
        }
        catch (ElevatedWorkerDeclinedException)
        {
            capture = new([], DpcIsrCaptureDuration, "The elevation prompt was dismissed, so nothing was captured.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The pipe's own internal connect/completion timeout fired, not this diagnostic's own
            // cancellation (checked by the guard above) — the capture process took too long to
            // respond rather than the user cancelling the run.
            capture = new([], DpcIsrCaptureDuration, "The elevated capture process took too long to respond.");
        }
        catch (Exception exception) when (exception is TimeoutException or System.IO.InvalidDataException
            or InvalidOperationException)
        {
            capture = new([], DpcIsrCaptureDuration, exception.Message);
        }

        var baseline = capture.Baseline;
        var finding = DpcIsrDiagnosticEngine.Analyze(capture,
            baseline is { Sent: >= 3 } && baseline.Received == baseline.Sent ? baseline.JitterMs : null);

        // Independent inventories run concurrently after the bounded measurement.
        var affinityInventoryTask = Task.Run(() => InterruptAffinityInventory.Read(onlyInteresting: false), cancellationToken);
        var driverImagesTask = Task.Run(WindowsDriverImageInventory.Read, cancellationToken);
        var cpuUsageTask = Task.Run(WindowsPerCoreCpuInventory.Read, cancellationToken);
        var topologyTask = Task.Run(WindowsCpuTopologyInventory.Read, cancellationToken);
        await Task.WhenAll(affinityInventoryTask, driverImagesTask, cpuUsageTask, topologyTask);
        var affinityInventory = affinityInventoryTask.Result;
        var driverImages = driverImagesTask.Result;
        var cpuUsage = cpuUsageTask.Result;
        var topology = topologyTask.Result;

        var matches = driverImages.Devices.Where(item =>
            string.Equals(item.DriverFileName, finding.CulpritDriver, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A shared image is not evidence against one particular device.
        var device = matches.Length == 1 ? matches[0] : null;
        var affinityDevice = device is null
            ? null
            : affinityInventory.Devices.FirstOrDefault(item => string.Equals(item.InstanceId, device.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));

        var presentDevices = affinityInventory.Devices.Select(item => item.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affinityAction = DpcIsrGuidance.AffinityAction(
            finding, device, cpuUsage.Cores, topology, Math.Max(affinityInventory.LogicalProcessors, 1), presentDevices);
        var disableGuidance = DpcIsrGuidance.CanDisableDevice(affinityDevice)
            ? DpcIsrGuidance.DisableNonNicGuidance(finding, device) : null;

        _lastDpcIsrAffinityAction = affinityAction;
        _lastDpcIsrDisableGuidance = disableGuidance;
        if (disableGuidance is not null)
            _lastDpcIsrHostGuidance = _lastDpcIsrHostGuidance.Append(disableGuidance).ToArray();

        HostDiagnostics.SetDpcIsrResult(
            finding,
            affinityAction,
            affinityAction is null ? null : () => ApplyAffinityAsync(affinityAction),
            device is null ? null : () => OpenInterruptAffinityForModify(device.DeviceInstanceId),
            disableGuidance,
            disableGuidance is null ? null : () => ApplyDeviceDisableAsync(affinityDevice!));

        return finding;
    }

    private void OpenInterruptAffinityForModify(string instanceId)
    {
        SelectTab("Interrupt affinity");
        InterruptAffinity.SelectDevice(instanceId);
    }

    private async Task ApplyAffinityAsync(RemediationAction action)
    {
        if (!WriteConsentDialog.Ensure(out var problem))
        {
            HostDiagnostics.SetAffinityStatus(problem!);
            return;
        }

        var change = action.Changes[0];
        try
        {
            var specification = new InterruptAffinitySpecification(
                Environment.ProcessorCount,
                InterruptAffinityInventory.Read(onlyInteresting: false).Devices
                    .Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var address = specification.ResolveAddress(change.TargetId);
            var store = new InterruptAffinityStore(specification);
            var before = await store.ReadAsync(address, CancellationToken.None);
            var desired = new StoredSettingValue(true, change.ProposedValue!);
            if (before == desired)
            {
                HostDiagnostics.SetAffinityStatus(Loc.T("That device already has this placement."));
                return;
            }

            var request = ElevatedChange.Request(WorkerOperationKind.Apply,
                [ElevatedChange.Operation(address.SettingId, address.TargetId, before, desired, ChangeSource.Profile)]);
            HostDiagnostics.SetAffinityStatus(Loc.T("Approve the Windows elevation prompt to continue…"));
            var response = await ElevatedChange.RunAsync(
                _hostDiagnosticsWorker.ExecuteAsync, request, "dpcisr.affinity_apply", HostDiagnostics.SetAffinityStatus,
                message => Loc.F($"Interrupt affinity change failed: {message}"));
            if (response is null) return;
            HostDiagnostics.SetAffinityStatus(response.Success
                ? Loc.F($"{response.Status} Takes effect after a restart.")
                : response.Status);
        }
        catch (Exception exception)
        {
            HostDiagnostics.SetAffinityStatus(Loc.F($"Interrupt affinity change failed: {exception.Message}"));
            AppLog.Write("dpcisr.affinity_apply_failed", exception.Message);
        }
    }

    private async Task<IReadOnlyList<DriverDuplicate>> ShowDriverCleanupAsync(CancellationToken cancellationToken)
    {
        var inventory = await Task.Run(WindowsDriverStoreInventory.Read, cancellationToken);
        var duplicates = inventory.Error is null
            ? DriverStoreDuplicateFinder.FindOutdatedDuplicates(inventory.Packages, inventory.InUsePublishedNames)
            : [];
        HostDiagnostics.SetDriverCleanup(duplicates, inventory.Error, ApplyDriverRemovalAsync);
        _lastDpcIsrHostGuidance = duplicates.Select(item => DpcIsrGuidance.UninstallGuidance(item)!)
            .Concat(_lastDpcIsrDisableGuidance is { } disable ? [disable] : Array.Empty<HostGuidanceItem>()).ToArray();
        return duplicates;
    }

    private async Task ApplyDriverRemovalAsync(DriverDuplicate duplicate)
    {
        if (System.Windows.MessageBox.Show(
                $"Remove {duplicate.Outdated.PublishedName} ({duplicate.Outdated.DriverVersionRaw})?\n\n"
                + $"Keep {duplicate.Retained.PublishedName} and every package in use. "
                + "This is optional cleanup, not a latency fix. Removal cannot be undone automatically; "
                + "keep the vendor installer for recovery.", "SockTuner", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return;

        if (!WriteConsentDialog.Ensure(out var problem))
        {
            HostDiagnostics.SetUninstallStatus(problem!);
            return;
        }

        HostDiagnostics.SetUninstallStatus(Loc.T("Approve the Windows elevation prompt to continue…"));
        try
        {
            var response = await _hostActionClient.ExecuteAsync(
                new HostActionRequest(Guid.NewGuid(), HostActionKind.DeleteDriverPackage, Target: duplicate.Outdated.PublishedName),
                CancellationToken.None);
            HostDiagnostics.SetUninstallStatus(response.Success
                ? Loc.F($"Removed {duplicate.Outdated.PublishedName}.")
                : response.Error ?? Loc.T("The driver package could not be removed."));
            AppLog.Write("driver.cleanup_apply", $"Package={duplicate.Outdated.PublishedName}; Success={response.Success}.");
            if (response.Success)
                await ShowDriverCleanupAsync(CancellationToken.None);
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            HostDiagnostics.SetUninstallStatus(exception.Message);
        }
        catch (Exception exception) when (exception is TimeoutException or System.IO.InvalidDataException or InvalidOperationException)
        {
            HostDiagnostics.SetUninstallStatus(Loc.F($"Driver removal failed: {exception.Message}"));
            AppLog.Write("dpcisr.uninstall_apply_failed", exception.Message);
        }
    }

    private async Task ApplyDeviceDisableAsync(InterruptAffinityDevice device)
    {
        if (System.Windows.MessageBox.Show(
                $"Disable {device.FriendlyName}?\n\nThis can stop audio, Bluetooth, camera or printing. "
                + "Re-enable the device in Device Manager if it does not help. Repeated observations are not proof of causation.",
                "SockTuner", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return;

        if (!WriteConsentDialog.Ensure(out var problem))
        {
            HostDiagnostics.SetDisableStatus(problem!);
            return;
        }

        HostDiagnostics.SetDisableStatus(Loc.T("Approve the Windows elevation prompt to continue…"));
        try
        {
            var response = await _hostActionClient.ExecuteAsync(
                new HostActionRequest(Guid.NewGuid(), HostActionKind.DisableDevice, Target: device.InstanceId),
                CancellationToken.None);
            HostDiagnostics.SetDisableStatus(response.Success
                ? Loc.F($"{device.FriendlyName} disabled. Re-enable it from Device Manager if this does not help.")
                : response.Error ?? Loc.T("Windows refused to disable that device — it may be critical to booting."));
            AppLog.Write("dpcisr.disable_apply", $"Device={device.FriendlyName}; Success={response.Success}.");
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            HostDiagnostics.SetDisableStatus(exception.Message);
        }
        catch (Exception exception) when (exception is TimeoutException or System.IO.InvalidDataException or InvalidOperationException)
        {
            HostDiagnostics.SetDisableStatus(Loc.F($"Disabling the device failed: {exception.Message}"));
            AppLog.Write("dpcisr.disable_apply_failed", exception.Message);
        }
    }
}
