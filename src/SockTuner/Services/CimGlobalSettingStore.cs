using System.Globalization;
using System.Management;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Reads and writes the global TCP and offload properties through the Windows CIM provider — the
/// surface behind <c>netsh int tcp set global</c> and <c>Set-NetOffloadGlobalSetting</c>. No
/// registry path is composed from plan data, and the provider enforces its own constraints
/// underneath the checks SockTuner already made.
/// </summary>
public sealed class CimGlobalSettingStore : ISettingStore
{
    private readonly ManagementScope _scope;
    private readonly List<string> _ineffectiveWrites = [];

    public CimGlobalSettingStore() : this(new ManagementScope(WindowsGlobalSettingInventory.NamespacePath)) { }

    internal CimGlobalSettingStore(ManagementScope scope) => _scope = scope;

    /// <summary>
    /// Writes that the provider accepted but that did not change what the stack is actually using.
    /// A TCP template is not necessarily the template a connection is mapped to, and group policy
    /// outranks a local value, so "the write succeeded" and "the setting applies" are two different
    /// questions. Reading back the address the write touched only answers the first.
    /// </summary>
    public IReadOnlyList<string> IneffectiveWrites => _ineffectiveWrites;

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var instance = Find(address);
        return Task.FromResult(WindowsGlobalSettingInventory.TryRead(instance, address.ValueName, out var value)
            ? new StoredSettingValue(true, value)
            : StoredSettingValue.Missing);
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!value.Exists)
        {
            throw new InvalidOperationException(
                $"{address.ValueName} always holds a value; propose its default instead of removing it.");
        }

        using var instance = Find(address);
        var property = instance.Properties[address.ValueName];
        instance[address.ValueName] = Convert(property.Type, property.Name, value.Value);
        instance.Put();
        RecordIfIneffective(address, value.Value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Two ways a write can succeed and still change nothing, checked after every write because
    /// reading back the address only proves the provider stored the value.
    /// </summary>
    private void RecordIfIneffective(SettingAddress address, string written)
    {
        RecordIfPolicyOverrides(address, written);
        RecordIfTemplateCarriesNoTraffic(address);
    }

    // The "effective" property names the winning source, not the winning value: its ValueMap is
    // {Local, GroupPolicy}. Comparing it against the level that was written would flag every write
    // on a machine with no policy at all, because the selector reads Local — which is success.
    private void RecordIfPolicyOverrides(SettingAddress address, string written)
    {
        if (!CimGlobalPropertyCatalog.PolicySources.TryGetValue(address.ValueName, out var source))
        {
            return;
        }

        using var reread = Find(address);
        if (!WindowsGlobalSettingInventory.TryRead(reread, source.SelectorProperty, out var selector)
            || selector != PolicySource.GroupPolicyWins)
        {
            return;
        }

        var policyValue = WindowsGlobalSettingInventory.TryRead(reread, source.PolicyValueProperty, out var value)
            ? value
            : "unreadable";
        _ineffectiveWrites.Add(
            $"{address.ValueName} was written as {written} on {address.TargetId ?? "System"}, but "
            + $"{source.SelectorProperty} reports group policy as the winning source and "
            + $"{source.PolicyValueProperty} holds {policyValue}. The local value is stored and ignored.");
    }

    // Writing a template no transport filter points at is the quietest failure available here.
    private void RecordIfTemplateCarriesNoTraffic(SettingAddress address)
    {
        if (address.TargetId is not { Length: > 0 } template
            || !string.Equals(address.RegistryPath, CimGlobalPropertyCatalog.TcpSettingClass, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var resolution = WindowsTcpTemplateResolver.Read(_scope);
        if (resolution.FromFilter && !string.Equals(resolution.Template, template, StringComparison.OrdinalIgnoreCase))
        {
            _ineffectiveWrites.Add(
                $"{address.ValueName} was written to the {template} template, but ordinary TCP traffic is mapped to "
                + $"{resolution.Template}. {resolution.Reason}");
        }
    }

    /// <summary>Matches the CLR type the provider declared, so a uint8 property is never handed a string.</summary>
    internal static object Convert(CimType type, string name, string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"{name} expects a whole number; {value} is not one.", nameof(value));
        }

        return type switch
        {
            CimType.UInt8 => (byte)parsed,
            CimType.UInt16 => (ushort)parsed,
            CimType.UInt32 => (uint)parsed,
            CimType.UInt64 => parsed,
            CimType.SInt32 => (int)parsed,
            _ => throw new InvalidOperationException(
                $"{name} is a {type}, which SockTuner does not write.")
        };
    }

    // Matched in code rather than in a WQL WHERE clause: the instance key is provider-supplied text
    // and building a query string from it would create a filter-injection surface for no gain.
    // ponytail: enumerates the class per lookup; add an instance cache if plan sizes grow.
    private ManagementObject Find(SettingAddress address)
    {
        var keyProperty = CimGlobalPropertyCatalog.InstanceKeyProperty.GetValueOrDefault(address.RegistryPath);
        using var searcher = new ManagementObjectSearcher(
            _scope, new ObjectQuery($"SELECT * FROM {address.RegistryPath}"));
        using var results = searcher.Get();
        ManagementObject? match = null;
        foreach (ManagementObject item in results)
        {
            var key = keyProperty is null ? null : WindowsGlobalSettingInventory.Text(item[keyProperty]);
            if (match is null && string.Equals(key ?? string.Empty, address.TargetId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                match = item;
                continue;
            }

            item.Dispose();
        }

        return match ?? throw new InvalidOperationException(
            $"{address.RegistryPath} instance {address.TargetId ?? "System"} is no longer present.");
    }
}
