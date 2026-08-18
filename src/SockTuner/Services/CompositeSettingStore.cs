using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Routes each change to the backend that owns it: driver-advertised NIC properties and global
/// TCP/offload properties go through their CIM providers, registry-backed catalog settings through
/// the registry store. One plan can therefore span all three without any backend learning about
/// the others.
/// </summary>
public sealed class CompositeSettingStore : ISettingStore
{
    private readonly ISettingStore _registry;

    public CompositeSettingStore(
        ISettingStore registry,
        CimAdapterSettingStore adapters,
        CimGlobalSettingStore? globals = null)
    {
        _registry = registry;
        Adapters = adapters;
        Globals = globals ?? new CimGlobalSettingStore();
    }

    public CimAdapterSettingStore Adapters { get; }

    public CimGlobalSettingStore Globals { get; }

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken) =>
        For(address).ReadAsync(address, cancellationToken);

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken) =>
        For(address).WriteAsync(address, value, cancellationToken);

    private ISettingStore For(SettingAddress address) => address.SettingId switch
    {
        _ when address.SettingId.StartsWith(SettingSpecifications.NicPrefix, StringComparison.Ordinal) => Adapters,
        _ when address.SettingId.StartsWith(SettingSpecifications.CimPrefix, StringComparison.Ordinal) => Globals,
        _ => _registry
    };
}
