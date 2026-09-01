using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Whether one network adapter is enabled at all, as a typed setting the transaction engine can
/// snapshot, apply, verify by read-back and roll back.
/// </summary>
/// <remarks>
/// <para>
/// This is the most disruptive thing SockTuner can do: disabling the wrong adapter is how a machine
/// loses its network. Three separate things stand in front of it. The adapter must be one this
/// machine actually has, checked here rather than taken from the plan. The UI never offers the
/// adapter carrying the default route. And the operation is expressed as a value — Enabled or
/// Disabled — so rolling back is the same code path as applying, with the captured value.
/// </para>
/// <para>
/// Absent is not a state an adapter can be in, so it is refused rather than silently treated as
/// "leave it alone".
/// </para>
/// </remarks>
public sealed class AdapterStateSpecification : ISettingSpecification
{
    public const string SettingId = "adapter.state";
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";

    /// <summary>Applying this drops the adapter's link immediately, not at the next reboot.</summary>
    public const string DisableRestart = "Adapter disable";

    private readonly IReadOnlySet<Guid> _presentAdapters;

    public AdapterStateSpecification(IReadOnlySet<Guid> presentAdapters) =>
        _presentAdapters = presentAdapters ?? throw new ArgumentNullException(nameof(presentAdapters));

    public string Id => SettingId;
    public string Title => "Adapter enabled state";
    public string Category => "Network devices";

    // Documented: MSFT_NetAdapter exposes Enable and Disable, and this is the same operation the
    // Network Connections folder performs. What it does is certain; that switching an adapter off
    // helps a given workload is not, which is why the advisor states a cost rather than a gain.
    public EvidenceLevel Evidence => EvidenceLevel.Documented;

    public ChangeRisk Risk => ChangeRisk.High;

    public string RestartRequirement => DisableRestart;

    public string TradeOff =>
        "A disabled adapter carries nothing: no routes, no name resolution, no filter drivers on its stack. "
        + "That is the whole point and the whole risk. Anything that was reaching the network through it stops, "
        + "and on a machine with one usable path that means the machine is offline until it is enabled again.";

    /// <summary>An adapter is always in one state or the other; there is no absent.</summary>
    public bool SupportsAbsentValue => false;

    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value, Enabled, StringComparison.Ordinal)
            && !string.Equals(value, Disabled, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"An adapter state is '{Enabled}' or '{Disabled}', not '{value}'.");
        }
    }

    public SettingAddress ResolveAddress(string? targetId)
    {
        if (!Guid.TryParse(targetId, out var adapterId))
        {
            throw new ArgumentException("An adapter state requires a valid adapter GUID.", nameof(targetId));
        }

        // Checked against the adapters actually present, in the process doing the write, so a plan
        // cannot name a device this machine does not have.
        if (!_presentAdapters.Contains(adapterId))
        {
            throw new KeyNotFoundException($"No present network adapter with ID {adapterId}.");
        }

        return new SettingAddress(
            SettingId,
            adapterId.ToString("B").ToUpperInvariant(),
            "MSFT_NetAdapter",
            "State",
            RegistryValueKind.String);
    }

    /// <summary>The adapters this machine currently has, by interface GUID.</summary>
    public static IReadOnlySet<Guid> PresentAdapters() => NetworkInterface
        .GetAllNetworkInterfaces()
        .Select(item => Guid.TryParse(item.Id, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToHashSet();
}

/// <summary>
/// Enables and disables adapters through the Windows CIM provider, which owns the device identity —
/// nothing here composes a registry path or shells out.
/// </summary>
public sealed class AdapterStateStore : ISettingStore
{
    private readonly ManagementScope _scope;

    public AdapterStateStore()
        : this(new ManagementScope(WindowsAdapterCapabilityInventory.NamespacePath)) { }

    internal AdapterStateStore(ManagementScope scope) => _scope = scope;

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);
        using var adapter = FindAdapter(address);

        // InterfaceAdminStatus is the administrative state — what the user set — rather than the
        // link state, which also goes down when a cable is unplugged. Rolling back must restore
        // what was administered, not what happened to be plugged in.
        var administrativelyUp = Convert.ToInt32(adapter["InterfaceAdminStatus"], CultureInfo.InvariantCulture) == 1;
        return Task.FromResult(new StoredSettingValue(
            true,
            administrativelyUp ? AdapterStateSpecification.Enabled : AdapterStateSpecification.Disabled));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);
        if (!value.Exists)
        {
            throw new InvalidOperationException(
                "An adapter is always enabled or disabled; propose one of those instead of removing the value.");
        }

        var enable = string.Equals(value.Value, AdapterStateSpecification.Enabled, StringComparison.Ordinal);
        if (!enable && !string.Equals(value.Value, AdapterStateSpecification.Disabled, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Unknown adapter state '{value.Value}'.");
        }

        using var adapter = FindAdapter(address);
        adapter.InvokeMethod(enable ? "Enable" : "Disable", null);
        return Task.CompletedTask;
    }

    private ManagementObject FindAdapter(SettingAddress address)
    {
        var expected = address.TargetId
            ?? throw new InvalidOperationException($"{address.SettingId} has no adapter target.");

        using var searcher = new ManagementObjectSearcher(
            _scope, new ObjectQuery("SELECT InterfaceGuid, InterfaceAdminStatus FROM MSFT_NetAdapter"));
        using var results = searcher.Get();
        ManagementObject? match = null;
        foreach (ManagementObject item in results)
        {
            if (match is null
                && string.Equals(
                    Convert.ToString(item["InterfaceGuid"], CultureInfo.InvariantCulture),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                match = item;
                continue;
            }

            item.Dispose();
        }

        return match ?? throw new InvalidOperationException($"Adapter {expected} is no longer present.");
    }

    private static void EnsureOwned(SettingAddress address)
    {
        if (!string.Equals(address.SettingId, AdapterStateSpecification.SettingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{address.SettingId} is not an adapter state setting.");
        }
    }
}

/// <summary>
/// The two power-management checkboxes on a network adapter's device properties, as one typed
/// setting.
/// </summary>
/// <remarks>
/// <para>
/// Windows stores both checkboxes in a single <c>PnPCapabilities</c> DWORD on the adapter's own key
/// under the network class, so they are one setting here as well: writing half of it is not a state
/// the Device Manager UI can produce. Bit 0x08 removes "allow the computer to turn off this device
/// to save power"; bit 0x10 removes "allow this device to wake the computer". 24 is both.
/// </para>
/// <para>
/// Absent is the real Windows default — no value at all, meaning the driver's INF decides — so
/// rolling back to a machine that never had the value removes it rather than writing a zero that
/// looks like a deliberate choice to anything reading the key later.
/// </para>
/// </remarks>
public sealed class AdapterPowerSavingSpecification : ISettingSpecification
{
    public const string SettingId = "adapter.power-saving";

    /// <summary>The network adapter device class. Every NIC's settings key lives under it.</summary>
    public const string NetClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    /// <summary>Power management allowed: the value Windows writes when both boxes are ticked.</summary>
    public const uint PowerManagementAllowed = 0;

    /// <summary>Both boxes cleared: no selective suspend, no wake.</summary>
    public const uint PowerManagementOff = 24;

    /// <summary>
    /// The documented bits. Anything outside this mask is refused: an arbitrary DWORD here is a
    /// driver-defined capability flag, not a checkbox.
    /// </summary>
    private const uint KnownBits = 0x18 | 0x100;

    private readonly IReadOnlyDictionary<Guid, string> _adapterKeys;

    public AdapterPowerSavingSpecification(IReadOnlyDictionary<Guid, string> adapterKeys) =>
        _adapterKeys = adapterKeys ?? throw new ArgumentNullException(nameof(adapterKeys));

    public string Id => SettingId;
    public string Title => "Adapter power management";
    public string Category => "Network devices";

    // Documented by Microsoft as an INF-set NDIS power-management capability, and it is the value
    // the Device Manager power-management tab reads and writes.
    public EvidenceLevel Evidence => EvidenceLevel.Documented;

    // Reversible and non-destructive, but it only takes effect when the adapter restarts, and the
    // restart itself drops the link.
    public ChangeRisk Risk => ChangeRisk.Medium;

    public string RestartRequirement => Diagnosis.RemoteSessionGuard.AdapterRestart;

    public string TradeOff =>
        "Stopping Windows from powering the adapter down removes the wake-up delay on the first packet after an idle "
        + "period, and stops a suspend landing in the middle of a session. It costs power on a laptop, and it disables "
        + "wake-on-LAN for this adapter, so a machine you wake remotely should keep it.";

    /// <summary>No value is the Windows default, and rolling back to it has to remove the value.</summary>
    public bool SupportsAbsentValue => true;

    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"'{value}' is not an unsigned decimal DWORD.");
        }

        if ((bits & ~KnownBits) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"PnPCapabilities {bits} sets bits outside the documented power-management mask {KnownBits}.");
        }
    }

    public SettingAddress ResolveAddress(string? targetId)
    {
        if (!Guid.TryParse(targetId, out var adapterId))
        {
            throw new ArgumentException("Adapter power management requires a valid adapter GUID.", nameof(targetId));
        }

        // The class subkey is discovered from the adapter, never taken from the plan: the plan
        // chooses which present adapter, and this chooses which key that is.
        if (!_adapterKeys.TryGetValue(adapterId, out var subkey))
        {
            throw new KeyNotFoundException($"No network class key for adapter {adapterId}.");
        }

        return new SettingAddress(
            SettingId,
            adapterId.ToString("B").ToUpperInvariant(),
            $@"{NetClassKey}\{subkey}",
            "PnPCapabilities",
            RegistryValueKind.DWord);
    }

    /// <summary>
    /// Maps each adapter's interface GUID to its four-digit subkey under the network class, by
    /// reading the <c>NetCfgInstanceId</c> each subkey publishes.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> ReadAdapterKeys()
    {
        var map = new Dictionary<Guid, string>();
        using var root = Registry.LocalMachine.OpenSubKey(NetClassKey, writable: false);
        if (root is null) return map;

        foreach (var name in root.GetSubKeyNames())
        {
            // Only the numbered instance keys; Properties and friends are not adapters.
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;

            using var instance = root.OpenSubKey(name, writable: false);
            if (instance?.GetValue("NetCfgInstanceId") is string raw && Guid.TryParse(raw, out var id))
            {
                map[id] = name;
            }
        }

        return map;
    }
}

/// <summary>
/// Reads and writes the adapter's <c>PnPCapabilities</c> DWORD, removing it entirely when the
/// rollback target is the Windows default.
/// </summary>
public sealed class AdapterPowerSavingStore : ISettingStore
{
    private readonly AdapterPowerSavingSpecification _specification;

    public AdapterPowerSavingStore(AdapterPowerSavingSpecification specification) =>
        _specification = specification ?? throw new ArgumentNullException(nameof(specification));

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);
        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: false);
        return Task.FromResult(key?.GetValue(address.ValueName) is int raw
            ? new StoredSettingValue(true, unchecked((uint)raw).ToString(CultureInfo.InvariantCulture))
            : StoredSettingValue.Missing);
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);

        // Re-resolve inside the writing process: the plan picks the adapter, this picks the key.
        if (_specification.ResolveAddress(address.TargetId) != address)
        {
            throw new InvalidOperationException("The power-management address does not match the resolved adapter.");
        }

        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKLM\\{address.RegistryPath}");

        if (!value.Exists)
        {
            key.DeleteValue(address.ValueName, throwOnMissingValue: false);
            return Task.CompletedTask;
        }

        _specification.Validate(value.Value);
        key.SetValue(
            address.ValueName,
            unchecked((int)uint.Parse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture)),
            RegistryValueKind.DWord);
        return Task.CompletedTask;
    }

    private static void EnsureOwned(SettingAddress address)
    {
        if (!string.Equals(address.SettingId, AdapterPowerSavingSpecification.SettingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{address.SettingId} is not an adapter power-management setting.");
        }
    }
}
