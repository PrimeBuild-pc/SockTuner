using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

public sealed class DpcIsrGuidanceTests
{
    private static readonly CpuTopologyResult FlatTopology = new([], null);

    private static DpcIsrFinding Finding(string driver = "flaky.sys") => new(
        driver, CulpritSharePercent: 5, [new DpcIsrDriverSample(driver, 50, 50, 10)], null, "Test reason.", HasRepeatedEvidence: true);

    private static DriverImageDevice Device(string driver, string instanceId, string name) => new(driver, instanceId, name, InfName: "oem1.inf");

    [Fact]
    public void UninstallGuidance_IsNullWithoutAGenuineDuplicate()
    {
        Assert.Null(DpcIsrGuidance.UninstallGuidance(duplicate: null));
    }

    [Fact]
    public void UninstallGuidance_NamesTheExactOutdatedPackageNeverTheActiveOne()
    {
        var active = new DriverStorePackage("oem17.inf", "culprit.inf", "Vendor", "Net", "01/01/2026 2.0.0.0");
        var outdated = new DriverStorePackage("oem19.inf", "culprit.inf", "Vendor", "Net", "01/01/2025 1.0.0.0");

        var guidance = DpcIsrGuidance.UninstallGuidance(new DriverDuplicate(active, outdated));

        Assert.NotNull(guidance);
        Assert.Equal(RemediationOwner.ManualOnly, guidance.Owner);
        Assert.Contains("oem19.inf", guidance.Title, StringComparison.Ordinal);
        Assert.Contains("Delete driver package oem19.inf", guidance.Instructions[0].Action, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete driver package oem17.inf", guidance.Instructions[0].Action, StringComparison.Ordinal);
    }

    [Fact]
    public void DisableNonNicGuidance_IsNullWithoutAResolvedDevice()
    {
        Assert.Null(DpcIsrGuidance.DisableNonNicGuidance(Finding(), device: null));
    }

    [Fact]
    public void DisableNonNicGuidance_NamesTheDeviceAndIsAlwaysManualOnly()
    {
        var device = Device("culprit.sys", @"PCI\VEN_1234&DEV_5678\0", "Some Audio Device");

        var guidance = DpcIsrGuidance.DisableNonNicGuidance(Finding("culprit.sys"), device);

        Assert.NotNull(guidance);
        Assert.Equal(RemediationOwner.ManualOnly, guidance.Owner);
        Assert.Contains("Some Audio Device", guidance.Instructions[0].Action, StringComparison.Ordinal);
    }

    [Fact]
    public void AffinityAction_IsNullWhenTheDeviceIsNotActuallyPresent()
    {
        var finding = Finding("culprit.sys");
        var device = Device("culprit.sys", @"PCI\absent", "Absent device");
        var cpu = new[] { new CpuCoreUsage(0, 10), new CpuCoreUsage(1, 90) };

        var action = DpcIsrGuidance.AffinityAction(
            finding, device, cpu, FlatTopology, logicalProcessors: 2, presentDevices: new HashSet<string>());

        Assert.Null(action);
    }

    [Fact]
    public void AffinityAction_PinsToTheQuietestCoreAndReusesTheExistingInterruptAffinitySetting()
    {
        var finding = Finding("culprit.sys");
        var device = Device("culprit.sys", @"PCI\present", "Present device");
        var cpu = new[] { new CpuCoreUsage(0, 80), new CpuCoreUsage(1, 5), new CpuCoreUsage(2, 50) };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { device.DeviceInstanceId };

        var action = DpcIsrGuidance.AffinityAction(finding, device, cpu, FlatTopology, logicalProcessors: 3, presentDevices: present);

        Assert.NotNull(action);
        var change = Assert.Single(action.Changes);
        Assert.Equal("irq.affinity", change.SettingId);
        Assert.Equal(device.DeviceInstanceId, change.TargetId);
        Assert.Equal("4:0:2", change.ProposedValue); // SpecifiedProcessors, Undefined priority, mask bit 1 (core 1)
        Assert.Contains("CPU 1", action.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void AffinityAction_PrefersAPerformanceCoreOnAHybridChipEvenWhenAnEcoreIsQuieter()
    {
        var finding = Finding("culprit.sys");
        var device = Device("culprit.sys", @"PCI\present", "Present device");
        // Core 0 is a P-core (EfficiencyClass 1) but busier; core 1 is an idle E-core (EfficiencyClass 0).
        var cpu = new[] { new CpuCoreUsage(0, 40), new CpuCoreUsage(1, 2) };
        var topology = new CpuTopologyResult([new CpuCoreTopology(0, 1, 0), new CpuCoreTopology(1, 0, 0)], null);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { device.DeviceInstanceId };

        var action = DpcIsrGuidance.AffinityAction(finding, device, cpu, topology, logicalProcessors: 2, presentDevices: present);

        Assert.NotNull(action);
        Assert.Contains("CPU 0", action.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void InconclusiveEvidenceCannotEnableAffinityOrDisable()
    {
        var finding = Finding() with { HasRepeatedEvidence = false };
        var device = Device("flaky.sys", @"PCI\present", "Audio");
        Assert.Null(DpcIsrGuidance.DisableNonNicGuidance(finding, device));
        Assert.Null(DpcIsrGuidance.AffinityAction(finding, device, [new(0, 5)], FlatTopology,
            1, new HashSet<string> { device.DeviceInstanceId }));
    }

    [Theory]
    [InlineData("MEDIA", true)]
    [InlineData("Bluetooth", true)]
    [InlineData("Display", false)]
    [InlineData("SCSIAdapter", false)]
    [InlineData("System", false)]
    [InlineData("HIDClass", false)]
    [InlineData("Net", false)]
    [InlineData("", false)]
    public void DeviceDisableOnlyAllowsKnownAncillaryClasses(string deviceClass, bool expected)
    {
        var device = new InterruptAffinityDevice("id", "Device", deviceClass, InterruptPolicy.MachineDefault,
            InterruptPriority.Undefined, null, null, IsNetwork: false);
        Assert.Equal(expected, DpcIsrGuidance.CanDisableDevice(device));
        Assert.False(DpcIsrGuidance.CanDisableDevice(device with { IsNetwork = true }));
    }

    [Fact]
    public void AffinityAction_IsNullWithoutAnyCpuReading()
    {
        var finding = Finding("culprit.sys");
        var device = Device("culprit.sys", @"PCI\present", "Present device");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { device.DeviceInstanceId };

        var action = DpcIsrGuidance.AffinityAction(finding, device, [], FlatTopology, logicalProcessors: 2, presentDevices: present);

        Assert.Null(action);
    }
}
