using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services.Remediation;

/// <summary>A descriptive checklist step; any optional Apply is separately consented and executed by the UI.</summary>
public sealed record HostInstruction(string Action, string Reason)
{
    public string Summary => $"{Action} — {Reason}";
}

public sealed record HostGuidanceItem(string Title, IReadOnlyList<HostInstruction> Instructions, string Verification)
{
    /// <summary>Fixed by construction: every item this type produces is a manual action, never a local write.</summary>
    public RemediationOwner Owner => RemediationOwner.ManualOnly;
}

/// <summary>
/// Remediation layer: turns a DPC/ISR finding into the shapes the host-diagnostics view can offer an
/// Apply button for — a real, reversible <see cref="RemediationAction"/> for interrupt affinity
/// (reusing the existing write path, topology-aware core choice), and guidance for the driver
/// removal and non-NIC device disable, whose actual Apply goes through <c>HostActionClient</c> in
/// the view rather than this purely descriptive layer.
/// </summary>
public static class DpcIsrGuidance
{
    /// <summary>
    /// Describes optional cleanup of an unused older version, independent of the DPC/ISR ranking.
    /// </summary>
    public static HostGuidanceItem? UninstallGuidance(DriverDuplicate? duplicate)
    {
        if (duplicate is null)
        {
            return null;
        }

        return new HostGuidanceItem(
            $"Remove unused older driver package {duplicate.Outdated.PublishedName}",
            [
                new HostInstruction(
                    $"Delete driver package {duplicate.Outdated.PublishedName} ({duplicate.Outdated.DriverVersionRaw}) "
                        + $"from the driver store, keeping the newest version {duplicate.Retained.PublishedName} ({duplicate.Retained.DriverVersionRaw}) "
                        + "and all versions used by any device. You can use Apply or Driver Store Explorer (RAPR) manually.",
                    "Optional disk cleanup, not a diagnosis or a latency fix. Removal is not automatically reversible; keep the vendor installer if you need an older version for recovery.")
            ],
            "Re-run \"pnputil /enum-drivers\" (or this check). The outdated package should no longer be listed, and the "
                + "device keeps working on the version that was already active.");
    }

    // Only ancillary classes: never storage, system/chipset, display, NICs, or input devices.
    public static bool CanDisableDevice(InterruptAffinityDevice? device) => device is { IsNetwork: false }
        && new[] { "MEDIA", "Bluetooth", "Camera", "Image", "Printer" }
            .Contains(device.DeviceClass, StringComparer.OrdinalIgnoreCase);

    public static HostGuidanceItem? DisableNonNicGuidance(DpcIsrFinding finding, DriverImageDevice? device)
    {
        if (!finding.HasRepeatedEvidence || finding.CulpritDriver is null || device is null)
        {
            return null;
        }

        return new HostGuidanceItem(
            $"Disable {device.DeviceName}",
            [
                new HostInstruction(
                    $"Disable {device.DeviceName} if {finding.CulpritDriver} is not needed.",
                    "Disabling the device stops its driver from running at all, which is more drastic than moving its "
                        + "interrupts to a different core but also fully reversible from the same place.")
            ],
            "Re-run this check with the device disabled. If the jitter in the same window improves, the device was "
                + "contending for the core the network stack needed; re-enable it if nothing changed.");
    }

    /// <summary>
    /// Pin the observed device's interrupts to a core chosen
    /// from this machine's own CPU topology (hybrid P-core/E-core, multi-CCD), through the
    /// interrupt-affinity setting that already exists.
    /// </summary>
    public static RemediationAction? AffinityAction(
        DpcIsrFinding finding,
        DriverImageDevice? device,
        IReadOnlyList<CpuCoreUsage> cpuUsage,
        CpuTopologyResult topology,
        int logicalProcessors,
        IReadOnlySet<string> presentDevices)
    {
        if (!finding.HasRepeatedEvidence || finding.CulpritDriver is null || device is null || !presentDevices.Contains(device.DeviceInstanceId))
        {
            return null;
        }

        if (CpuCorePicker.PickQuietest(cpuUsage, topology) is not { } choice)
        {
            return null;
        }

        var specification = new InterruptAffinitySpecification(logicalProcessors, presentDevices);
        var mask = InterruptAffinityDevice.ToMask([choice.CoreIndex]);
        var value = InterruptAffinitySpecification.Canonical(InterruptPolicy.SpecifiedProcessors, InterruptPriority.Undefined, mask);
        specification.Validate(value);

        return new RemediationAction(
            "dpcisr.affinity",
            $"Pin {device.DeviceName}'s interrupts to CPU {choice.CoreIndex}",
            NetworkSegment.HostDriver,
            ResponsibilityAssigner.Assign(NetworkSegment.HostDriver, LocalControl.RequiresChoice),
            [new ChangeRequest(InterruptAffinitySpecification.SettingId, device.DeviceInstanceId, value, ChangeSource.Profile)],
            $"{finding.Reason} Moving its interrupts to {choice.Reason} (CPU {choice.CoreIndex}, currently at "
                + $"{choice.PercentProcessorTime:0.#}%) stops it from competing for the same core as whatever is latency-sensitive.",
            "Pinning interrupts is not destructive, but a bad choice can move the problem rather than solve it: measure "
                + "before and after, and remove the override from the Interrupt Affinity view if it does not help.",
            "Re-run this same capture. The pinned device's share of the window should fall if the contention was with "
                + "another driver on its old core; if jitter in the same window does not improve, remove the override.");
    }
}
