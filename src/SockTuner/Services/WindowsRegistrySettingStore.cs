using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class WindowsRegistrySettingStore : ISettingStore
{
    // The catalog is the allowlist. It was a separately maintained list while the first five
    // entries were being unlocked, which meant a new catalog entry could be planned and then
    // refused at write time; deriving it removes that drift entirely. Every entry still carries its
    // own evidence level, risk and restart requirement, and a Blocked one is never writable. NIC
    // and CIM global properties are absent by design: they are gated by what the driver and the
    // provider advertise, not by a static list.
    internal static readonly HashSet<string> WritableSettingIds = new(
        SettingCatalog.All
            .Where(definition => definition.Evidence != EvidenceLevel.Blocked)
            .Select(definition => definition.Id),
        StringComparer.Ordinal);
    private readonly bool _allowWrites;

    private WindowsRegistrySettingStore(bool allowWrites) => _allowWrites = allowWrites;

    public static WindowsRegistrySettingStore CreateReadOnly() => new(false);

    /// <summary>
    /// Creates the writable store. Elevation is the real boundary here; the user-facing alpha
    /// consent is recorded in preferences and checked before the elevated worker is launched.
    /// </summary>
    public static WindowsRegistrySettingStore CreateWritable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows registry settings require Windows.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Administrator rights are required for live registry changes.");
        }

        return new(true);
    }

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(address, null);
        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: false);
        var raw = key?.GetValue(address.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return Task.FromResult(StoredSettingValue.Missing);
        }

        if (key!.GetValueKind(address.ValueName) != address.ValueKind || raw is not int signed)
        {
            throw new InvalidDataException($"Unexpected registry type for {address.SettingId}.");
        }

        return Task.FromResult(new StoredSettingValue(
            true,
            unchecked((uint)signed).ToString(CultureInfo.InvariantCulture)));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(address, value);
        if (!_allowWrites)
        {
            throw new InvalidOperationException("This registry store is read-only.");
        }

        EnsureWritable(address);
        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: true)
            ?? throw new InvalidOperationException($"Registry key does not exist: HKLM\\{address.RegistryPath}");
        if (value.Exists)
        {
            if (!SettingDefinition.TryParseCanonical(value.Value, out var number))
            {
                throw new InvalidDataException($"{address.SettingId} requires a canonical DWORD value.");
            }

            key.SetValue(address.ValueName, unchecked((int)number), address.ValueKind);
        }
        else
        {
            key.DeleteValue(address.ValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    internal static void EnsureWritable(SettingAddress address)
    {
        SettingCatalog.ValidateAddress(address);
        if (!WritableSettingIds.Contains(address.SettingId))
        {
            throw new InvalidOperationException($"{address.SettingId} is not enabled for writing.");
        }
    }

    private static void EnsureAllowed(SettingAddress address, StoredSettingValue? proposedValue)
    {
        if (address.ValueKind != RegistryValueKind.DWord)
        {
            throw new InvalidOperationException("Only allowlisted DWORD settings are supported in this phase.");
        }

        SettingCatalog.ValidateAddress(address);
        var definition = SettingCatalog.Get(address.SettingId);
        if (definition.Evidence == EvidenceLevel.Blocked)
        {
            throw new InvalidOperationException($"{definition.Id} is blocked from writes.");
        }

        if (proposedValue is { Exists: true } value)
        {
            definition.Validate(value.Value);
        }

        if (definition.Scope == SettingScope.AdapterInterface)
        {
            var target = Guid.Parse(address.TargetId!);
            var exists = NetworkInterface.GetAllNetworkInterfaces()
                .Any(adapter => Guid.TryParse(adapter.Id, out var adapterId) && adapterId == target);
            if (!exists)
            {
                throw new InvalidOperationException("The target adapter is no longer present on this machine.");
            }
        }
    }
}
