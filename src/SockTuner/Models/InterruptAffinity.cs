using System.Globalization;

namespace SockTuner.Models;

/// <summary>
/// Windows <c>IRQ_DEVICE_POLICY</c>: how the bus driver is allowed to place this device's
/// interrupts across processors.
/// </summary>
public enum InterruptPolicy
{
    /// <summary>Windows decides. Removing the override restores this.</summary>
    MachineDefault = 0,
    AllCloseProcessors = 1,
    OneCloseProcessor = 2,
    AllProcessorsInMachine = 3,

    /// <summary>The only policy that uses the affinity mask.</summary>
    SpecifiedProcessors = 4,
    SpreadMessagesAcrossAllProcessors = 5
}

/// <summary>Windows <c>IRQ_PRIORITY</c>.</summary>
public enum InterruptPriority
{
    Undefined = 0,
    Low = 1,
    Normal = 2,
    High = 3
}

/// <summary>
/// One device's interrupt placement, as Windows currently has it.
/// </summary>
/// <param name="InstanceId">
/// The PnP instance ID. It is also the registry location, which is why the store re-derives the
/// path from it rather than accepting one from the caller.
/// </param>
/// <param name="AffinityMask">
/// Processor mask from <c>AssignmentSetOverride</c>, or null when the device has no override. It
/// only takes effect under <see cref="InterruptPolicy.SpecifiedProcessors"/>.
/// </param>
public sealed record InterruptAffinityDevice(
    string InstanceId,
    string FriendlyName,
    string DeviceClass,
    InterruptPolicy Policy,
    InterruptPriority Priority,
    ulong? AffinityMask,
    bool? MsiSupported,
    bool IsNetwork)
{
    public bool HasOverride => Policy != InterruptPolicy.MachineDefault
        || Priority != InterruptPriority.Undefined
        || AffinityMask is not null;

    public IReadOnlyList<int> Cores => ToCores(AffinityMask);

    public string CoresDisplay => AffinityMask is null
        ? "—"
        : Cores.Count == 0 ? "none (mask is empty)" : string.Join(", ", Cores);

    public string PolicyDisplay => Policy switch
    {
        InterruptPolicy.MachineDefault => "Windows default",
        InterruptPolicy.AllCloseProcessors => "All nearby processors",
        InterruptPolicy.OneCloseProcessor => "One nearby processor",
        InterruptPolicy.AllProcessorsInMachine => "All processors",
        InterruptPolicy.SpecifiedProcessors => "Specified processors",
        InterruptPolicy.SpreadMessagesAcrossAllProcessors => "Spread messages across all",
        _ => Policy.ToString()
    };

    public string MsiDisplay => MsiSupported switch
    {
        true => "Enabled",
        false => "Disabled",
        _ => "Not set"
    };

    /// <summary>
    /// A device can carry a priority override with no placement override, which still counts as a
    /// change from what Windows would do on its own and must not read as "Windows default".
    /// </summary>
    public string StateDisplay => !HasOverride
        ? "Windows default"
        : Policy == InterruptPolicy.MachineDefault
            ? $"Windows placement, priority {Priority}"
            : $"{PolicyDisplay}{(Policy == InterruptPolicy.SpecifiedProcessors ? $" → CPU {CoresDisplay}" : string.Empty)}";

    public static IReadOnlyList<int> ToCores(ulong? mask)
    {
        if (mask is not { } value) return [];
        var cores = new List<int>();
        for (var bit = 0; bit < 64; bit++)
        {
            if ((value & (1UL << bit)) != 0) cores.Add(bit);
        }

        return cores;
    }

    public static ulong ToMask(IEnumerable<int> cores)
    {
        var mask = 0UL;
        foreach (var core in cores)
        {
            if (core is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(cores), $"CPU {core} is out of range.");
            mask |= 1UL << core;
        }

        return mask;
    }

    /// <summary>
    /// The one spelling a mask round-trips through, so a read-back check stays an exact match.
    /// Lower-case hex with no prefix and no leading zeros.
    /// </summary>
    public static string CanonicalMask(ulong mask) => mask.ToString("x", CultureInfo.InvariantCulture);

    public static bool TryParseMask(string? value, out ulong mask)
    {
        mask = 0;
        return value is not null
            && ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
            && CanonicalMask(mask) == value;
    }
}

public sealed record InterruptAffinityInventoryResult(
    IReadOnlyList<InterruptAffinityDevice> Devices,
    int LogicalProcessors,
    string? Error);
