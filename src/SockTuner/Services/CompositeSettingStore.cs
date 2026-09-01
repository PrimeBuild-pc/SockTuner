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
        CimGlobalSettingStore? globals = null,
        ISettingStore? dns = null,
        ISettingStore? interrupts = null,
        ISettingStore? adapterState = null,
        ISettingStore? adapterPower = null,
        ISettingStore? qos = null)
    {
        _registry = registry;
        Adapters = adapters;
        Globals = globals ?? new CimGlobalSettingStore();
        Dns = dns ?? new DnsServerStore();
        Interrupts = interrupts ?? CreateInterruptStore();
        AdapterState = adapterState ?? new AdapterStateStore();
        AdapterPower = adapterPower
            ?? new AdapterPowerSavingStore(new AdapterPowerSavingSpecification(
                AdapterPowerSavingSpecification.ReadAdapterKeys()));
        Qos = qos ?? new QosPolicyStore(new QosPolicySpecification());
    }

    // Built from the devices actually present, so the specification can refuse an instance ID that
    // is not one of them before it ever becomes a registry path.
    private static ISettingStore CreateInterruptStore()
    {
        var inventory = Collection.InterruptAffinityInventory.Read(onlyInteresting: false);
        return new InterruptAffinityStore(new InterruptAffinitySpecification(
            Math.Max(inventory.LogicalProcessors, 1),
            inventory.Devices.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }

    public CimAdapterSettingStore Adapters { get; }

    public CimGlobalSettingStore Globals { get; }

    public ISettingStore Dns { get; }

    public ISettingStore Interrupts { get; }

    public ISettingStore AdapterState { get; }

    public ISettingStore AdapterPower { get; }

    public ISettingStore Qos { get; }

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken) =>
        For(address).ReadAsync(address, cancellationToken);

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken) =>
        For(address).WriteAsync(address, value, cancellationToken);

    private ISettingStore For(SettingAddress address) => address.SettingId switch
    {
        _ when address.SettingId.StartsWith(SettingSpecifications.NicPrefix, StringComparison.Ordinal) => Adapters,
        _ when address.SettingId.StartsWith(SettingSpecifications.CimPrefix, StringComparison.Ordinal) => Globals,
        DnsServerSpecification.SettingId => Dns,
        QosPolicySpecification.SettingId => Qos,
        AdapterStateSpecification.SettingId => AdapterState,
        AdapterPowerSavingSpecification.SettingId => AdapterPower,
        InterruptAffinitySpecification.SettingId => Interrupts,
        _ => _registry
    };
}
