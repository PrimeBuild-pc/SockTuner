using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Reads and writes NIC advanced properties through the Windows CIM provider rather than the
/// raw registry. The provider owns the instance identity, so no registry path is ever composed
/// from plan data and there is nothing to forge; it also enforces the driver's own constraints
/// underneath the checks SockTuner already made.
/// </summary>
public sealed class CimAdapterSettingStore : ISettingStore
{
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ManagementScope _scope;
    private readonly Dictionary<Guid, OperationalStatus> _touchedAdapters = [];

    public CimAdapterSettingStore()
        : this(new ManagementScope(WindowsAdapterCapabilityInventory.NamespacePath)) { }

    internal CimAdapterSettingStore(ManagementScope scope) => _scope = scope;

    /// <summary>Adapters whose properties were written, with the link state seen beforehand.</summary>
    public IReadOnlyDictionary<Guid, OperationalStatus> TouchedAdapters => _touchedAdapters;

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var instance = FindProperty(address);
        var value = instance["RegistryValue"] is string[] { Length: > 0 } values
            ? values[0] ?? string.Empty
            : string.Empty;
        return Task.FromResult(new StoredSettingValue(true, value));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!value.Exists)
        {
            throw new InvalidOperationException(
                $"{address.ValueName} always holds a value; propose the driver default instead of removing it.");
        }

        var adapterId = ParseAdapterId(address);
        using var instance = FindProperty(address);
        instance["RegistryValue"] = new[] { value.Value };
        instance.Put();

        // Remember the pre-change link state so the restart can be checked against what the
        // adapter actually was, not against an assumption that every adapter starts connected.
        if (!_touchedAdapters.ContainsKey(adapterId))
        {
            _touchedAdapters[adapterId] = CurrentStatus(adapterId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Restarts every adapter written to, so the driver picks the new values up, then waits for
    /// each one to return to the link state it had before. Returns one message per adapter that
    /// did not come back; an empty result means every adapter recovered.
    /// </summary>
    public async Task<IReadOnlyList<string>> RestartTouchedAdaptersAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var (adapterId, previousStatus) in _touchedAdapters)
        {
            try
            {
                using (var adapter = FindAdapter(adapterId))
                {
                    // Restart is declared as Restart(Instance CmdletOutput), so it needs a built
                    // parameters object; passing null throws before WMI is ever reached.
                    AdapterStateStore.InvokeAdapterMethod(adapter, "Restart");
                }

                if (!await WaitForStatusAsync(adapterId, previousStatus, cancellationToken))
                {
                    problems.Add(
                        $"Adapter {adapterId} did not return to {previousStatus} within "
                        + $"{ReconnectTimeout.TotalSeconds:0} s; it is currently {CurrentStatus(adapterId)}.");
                }
            }
            catch (Exception exception) when (exception is ManagementException
                or UnauthorizedAccessException or COMException or InvalidOperationException)
            {
                problems.Add($"Adapter {adapterId} restart failed: {exception.Message}");
            }
        }

        _touchedAdapters.Clear();
        return problems;
    }

    private async Task<bool> WaitForStatusAsync(
        Guid adapterId,
        OperationalStatus expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReconnectTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (CurrentStatus(adapterId) == expected)
            {
                return true;
            }

            await Task.Delay(ReconnectPollInterval, cancellationToken);
        }

        return CurrentStatus(adapterId) == expected;
    }

    private static OperationalStatus CurrentStatus(Guid adapterId) => NetworkInterface
        .GetAllNetworkInterfaces()
        .FirstOrDefault(item => Guid.TryParse(item.Id, out var id) && id == adapterId)
        ?.OperationalStatus
        ?? OperationalStatus.Unknown;

    private static Guid ParseAdapterId(SettingAddress address) =>
        Guid.TryParse(address.TargetId, out var adapterId)
            ? adapterId
            : throw new InvalidOperationException($"{address.SettingId} has no valid adapter target.");

    private ManagementObject FindProperty(SettingAddress address)
    {
        var adapterId = ParseAdapterId(address);
        var instanceId = $"{adapterId.ToString("B").ToUpperInvariant()}::{address.ValueName}";
        return Find(
            $"SELECT InstanceID, RegistryValue FROM {WindowsAdapterCapabilityInventory.ClassName}",
            "InstanceID",
            instanceId)
            ?? throw new InvalidOperationException(
                $"The driver no longer advertises {address.ValueName} on adapter {adapterId}.");
    }

    /// <summary>
    /// Deliberately <c>SELECT *</c> and not a projection.
    /// </summary>
    /// <remarks>
    /// A WQL projection that omits the key properties returns objects whose <c>__PATH</c> is empty,
    /// and <see cref="System.Management.ManagementObject.InvokeMethod(string, object[])"/> on a
    /// pathless object throws "Operation is not valid due to the current state of the object". The
    /// keys here are CreationClassName, DeviceID, SystemCreationClassName and SystemName, so any
    /// narrower select silently breaks every method call on the result. Verified on Windows 11
    /// 26200: the projection yields <c>__PATH = ""</c> and the full row yields the real path.
    /// </remarks>
    internal const string AdapterQuery = "SELECT * FROM MSFT_NetAdapter";

    private ManagementObject FindAdapter(Guid adapterId) => Find(
        AdapterQuery,
        "InterfaceGuid",
        adapterId.ToString("B").ToUpperInvariant())
        ?? throw new InvalidOperationException($"Adapter {adapterId} is no longer present.");

    // Matched in code rather than in a WQL WHERE clause: keywords are driver-supplied text, and
    // building a query string from them would create a filter-injection surface for no gain.
    // ponytail: enumerates the class per lookup; add an instance cache if plan sizes grow.
    private ManagementObject? Find(string query, string property, string expected)
    {
        using var searcher = new ManagementObjectSearcher(_scope, new ObjectQuery(query));
        using var results = searcher.Get();
        ManagementObject? match = null;
        foreach (ManagementObject item in results)
        {
            if (match is null
                && string.Equals(
                    Convert.ToString(item[property], CultureInfo.InvariantCulture),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                match = item;
                continue;
            }

            item.Dispose();
        }

        return match;
    }
}
