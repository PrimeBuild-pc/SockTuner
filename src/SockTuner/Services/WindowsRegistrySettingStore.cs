using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class WindowsRegistrySettingStore : ISettingStore
{
    private const string IsolatedVmEnvironmentVariable = "SOCKTUNER_ISOLATED_VM_MUTATIONS";
    private const string IsolatedVmConfirmation = "DISPOSABLE-VM-ONLY";
    private readonly bool _allowWrites;

    private WindowsRegistrySettingStore(bool allowWrites) => _allowWrites = allowWrites;

    public static WindowsRegistrySettingStore CreateReadOnly() => new(false);

    public static WindowsRegistrySettingStore CreateForIsolatedVm()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows registry settings require Windows.");
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable(IsolatedVmEnvironmentVariable),
                IsolatedVmConfirmation,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Live writes are locked. Set {IsolatedVmEnvironmentVariable}={IsolatedVmConfirmation} only inside a disposable VM.");
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

        return Task.FromResult(new StoredSettingValue(true, unchecked((uint)signed)));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(address, value);
        if (!_allowWrites)
        {
            throw new InvalidOperationException("This registry store is read-only.");
        }

        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: true)
            ?? throw new InvalidOperationException($"Registry key does not exist: HKLM\\{address.RegistryPath}");
        if (value.Exists)
        {
            key.SetValue(address.ValueName, unchecked((int)value.Value), address.ValueKind);
        }
        else
        {
            key.DeleteValue(address.ValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
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
