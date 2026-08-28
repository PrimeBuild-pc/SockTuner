using System.Globalization;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Services;

/// <summary>
/// One device's interrupt affinity override, as a typed setting the transaction engine can
/// snapshot, apply, verify and roll back.
/// </summary>
/// <remarks>
/// <para>
/// The three values live together and only make sense together: a processor mask is ignored unless
/// the policy is <see cref="InterruptPolicy.SpecifiedProcessors"/>, and a policy of
/// <see cref="InterruptPolicy.MachineDefault"/> means "no override at all". They are therefore one
/// setting with a composite canonical value — <c>policy:priority:mask</c> — rather than three that
/// could be applied half-way and leave the device in a state Windows never produces.
/// </para>
/// <para>
/// Absent is the real Windows default: the whole Affinity Policy key is removed, which is exactly
/// what rolling back has to restore.
/// </para>
/// </remarks>
public sealed class InterruptAffinitySpecification : ISettingSpecification
{
    public const string SettingId = "irq.affinity";

    private readonly int _logicalProcessors;
    private readonly IReadOnlySet<string> _presentDevices;

    public InterruptAffinitySpecification(int logicalProcessors, IReadOnlySet<string> presentDevices)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessors, 1);
        _logicalProcessors = logicalProcessors;
        _presentDevices = presentDevices;
    }

    public string Id => SettingId;
    public string Title => "Interrupt affinity";
    public string Category => "Interrupt placement";

    // Documented by Microsoft as the interrupt affinity policy, and pci.sys — the bus driver that
    // applies it — contains all of DevicePolicy, DevicePriority, AssignmentSetOverride and the key
    // name itself. What it does is documented; that a given placement helps is not, so any change
    // has to be measured rather than assumed.
    public EvidenceLevel Evidence => EvidenceLevel.Documented;

    // Moving interrupts is not destructive, but a bad mask can leave a device serviced by a core
    // that is already saturated, which shows up as stutter rather than as an error.
    public ChangeRisk Risk => ChangeRisk.High;

    public string RestartRequirement => "System reboot";

    public string TradeOff =>
        "Pinning a device's interrupts concentrates its work on the cores you name and takes it off the others. "
        + "That can help when a busy device and a latency-sensitive one are fighting over the same core, and it hurts "
        + "when the chosen core is already the busiest. There is no generally correct placement: measure before and after.";

    /// <summary>Absent removes the override entirely, which is how Windows expresses "no policy".</summary>
    public bool SupportsAbsentValue => true;

    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (policy, priority, mask) = Parse(value);

        if (policy == InterruptPolicy.MachineDefault)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "Windows default is expressed by removing the override, not by writing policy 0.");
        }

        if (policy == InterruptPolicy.SpecifiedProcessors)
        {
            if (mask == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "Specified processors needs at least one CPU selected.");
            }

            // A mask naming a processor this machine does not have leaves the device with no
            // eligible core, which is far worse than a poor choice of core.
            var highest = InterruptAffinityDevice.ToCores(mask).Max();
            if (highest >= _logicalProcessors)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"CPU {highest} does not exist on this machine ({_logicalProcessors} logical processors).");
            }
        }
        else if (mask != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"A processor mask only applies to {nameof(InterruptPolicy.SpecifiedProcessors)}.");
        }

        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Unknown interrupt priority.");
        }

        if (!string.Equals(Canonical(policy, priority, mask), value, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is not in canonical form.");
        }
    }

    public SettingAddress ResolveAddress(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("A device instance ID is required.", nameof(targetId));
        }

        // The instance ID becomes part of a registry path, so it is checked against the devices
        // actually present rather than trusted. That is what stops a crafted plan from pointing
        // this setting at an arbitrary key under Enum.
        if (!_presentDevices.Contains(targetId))
        {
            throw new KeyNotFoundException($"No present device with instance ID {targetId}.");
        }

        if (targetId.Contains("..", StringComparison.Ordinal) || targetId.StartsWith('\\'))
        {
            throw new ArgumentException("Malformed device instance ID.", nameof(targetId));
        }

        return new SettingAddress(
            SettingId,
            targetId,
            $@"{InterruptAffinityInventory.EnumRoot}\{targetId}\{InterruptAffinityInventory.AffinitySubkey}",
            "DevicePolicy",
            RegistryValueKind.DWord);
    }

    public static string Canonical(InterruptPolicy policy, InterruptPriority priority, ulong mask) =>
        $"{(int)policy}:{(int)priority}:{InterruptAffinityDevice.CanonicalMask(mask)}";

    public static (InterruptPolicy Policy, InterruptPriority Priority, ulong Mask) Parse(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var policy)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var priority)
            || !InterruptAffinityDevice.TryParseMask(parts[2], out var mask)
            || !Enum.IsDefined((InterruptPolicy)policy))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"'{value}' is not a policy:priority:mask triple.");
        }

        return ((InterruptPolicy)policy, (InterruptPriority)priority, mask);
    }
}

/// <summary>
/// Applies the interrupt affinity triple to one device's own registry key, writing all three values
/// together or removing the whole override.
/// </summary>
public sealed class InterruptAffinityStore : ISettingStore
{
    private readonly InterruptAffinitySpecification _specification;

    public InterruptAffinityStore(InterruptAffinitySpecification specification) => _specification = specification;

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);
        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: false);
        if (key is null) return Task.FromResult(StoredSettingValue.Missing);

        var policy = key.GetValue("DevicePolicy") is int rawPolicy && Enum.IsDefined((InterruptPolicy)rawPolicy)
            ? (InterruptPolicy)rawPolicy
            : InterruptPolicy.MachineDefault;
        var priority = key.GetValue("DevicePriority") is int rawPriority && Enum.IsDefined((InterruptPriority)rawPriority)
            ? (InterruptPriority)rawPriority
            : InterruptPriority.Undefined;
        var mask = key.GetValue("AssignmentSetOverride") is byte[] raw
            ? InterruptAffinityInventory.ToMask(raw)
            : 0UL;

        return Task.FromResult(policy == InterruptPolicy.MachineDefault && priority == InterruptPriority.Undefined && mask == 0
            ? StoredSettingValue.Missing
            : new StoredSettingValue(true, InterruptAffinitySpecification.Canonical(policy, priority, mask)));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);

        // Re-resolve the address from the instance ID inside the writing process: a plan cannot
        // choose which key is written, only which present device is targeted.
        var expected = _specification.ResolveAddress(address.TargetId);
        if (expected != address)
        {
            throw new InvalidOperationException("The interrupt affinity address does not match the resolved device.");
        }

        if (!value.Exists)
        {
            // Removing the values, not zeroing them: a DevicePolicy of 0 left behind still reads as
            // an override to anything inspecting the key later.
            using var parent = Registry.LocalMachine.OpenSubKey(
                $@"{InterruptAffinityInventory.EnumRoot}\{address.TargetId}\Device Parameters\Interrupt Management",
                writable: true);
            parent?.DeleteSubKeyTree("Affinity Policy", throwOnMissingSubKey: false);
            return Task.CompletedTask;
        }

        _specification.Validate(value.Value);
        var (policy, priority, mask) = InterruptAffinitySpecification.Parse(value.Value);

        using var key = Registry.LocalMachine.CreateSubKey(address.RegistryPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKLM\\{address.RegistryPath}");
        key.SetValue("DevicePolicy", (int)policy, RegistryValueKind.DWord);

        if (priority == InterruptPriority.Undefined) key.DeleteValue("DevicePriority", throwOnMissingValue: false);
        else key.SetValue("DevicePriority", (int)priority, RegistryValueKind.DWord);

        if (policy == InterruptPolicy.SpecifiedProcessors)
        {
            key.SetValue("AssignmentSetOverride", InterruptAffinityInventory.ToBytes(mask), RegistryValueKind.Binary);
        }
        else
        {
            key.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    private static void EnsureOwned(SettingAddress address)
    {
        if (!string.Equals(address.SettingId, InterruptAffinitySpecification.SettingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{address.SettingId} is not an interrupt affinity setting.");
        }
    }
}
