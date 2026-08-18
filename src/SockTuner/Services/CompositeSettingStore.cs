using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Routes each change to the backend that owns it: driver-advertised NIC properties go through
/// the CIM provider, registry-backed catalog settings through the registry store. One plan can
/// therefore span both without either backend learning about the other.
/// </summary>
public sealed class CompositeSettingStore : ISettingStore
{
    private readonly ISettingStore _registry;

    public CompositeSettingStore(ISettingStore registry, CimAdapterSettingStore adapters)
    {
        _registry = registry;
        Adapters = adapters;
    }

    public CimAdapterSettingStore Adapters { get; }

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken) =>
        For(address).ReadAsync(address, cancellationToken);

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken) =>
        For(address).WriteAsync(address, value, cancellationToken);

    private ISettingStore For(SettingAddress address) =>
        address.SettingId.StartsWith(SettingSpecifications.NicPrefix, StringComparison.Ordinal)
            ? Adapters
            : _registry;
}
