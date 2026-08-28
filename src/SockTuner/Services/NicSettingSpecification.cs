using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// A driver-advertised NIC advanced property presented to the transaction engine. The driver
/// is the allowlist: the specification only exists because the CIM provider reported this
/// keyword for this adapter, and every value is checked against the constraints it reported.
/// </summary>
public sealed class NicSettingSpecification : ISettingSpecification
{
    public NicSettingSpecification(AdapterSettingCapability capability) => Capability = capability;

    public AdapterSettingCapability Capability { get; }

    public string Id => Capability.SettingId;
    public string Title => Capability.DisplayName;
    public string Category => $"NIC · {Capability.AreasDisplay}";
    public EvidenceLevel Evidence => Capability.Evidence;
    public ChangeRisk Risk => Capability.Risk;
    public string RestartRequirement => "Adapter restart";
    public string TradeOff => Capability.TradeOff;

    // Removing an NDIS keyword would leave the driver on its default, which reads back as that
    // default rather than as "absent" — the read-back check could never confirm it. Restoring a
    // default is therefore expressed as explicitly proposing the default value.
    public bool SupportsAbsentValue => false;

    public void Validate(string value) => Capability.Validate(value);

    public SettingAddress ResolveAddress(string? targetId)
    {
        if (!Guid.TryParse(targetId, out var target) || target != Capability.AdapterId)
        {
            throw new ArgumentException(
                $"{Capability.Keyword} belongs to adapter {Capability.AdapterId}.", nameof(targetId));
        }

        return new SettingAddress(
            Id,
            target.ToString("B").ToUpperInvariant(),
            WindowsAdapterCapabilityInventory.ClassName,
            Capability.Keyword,
            RegistryValueKind.String);
    }

    /// <summary>
    /// Finds the capability backing a <c>nic.&lt;keyword&gt;</c> setting on one adapter. A keyword
    /// the driver no longer advertises resolves to nothing, so a stale plan cannot be applied.
    /// </summary>
    public static NicSettingSpecification Resolve(
        string settingId,
        string? targetId,
        IReadOnlyList<AdapterSettingCapability> capabilities)
    {
        if (!Guid.TryParse(targetId, out var adapterId))
        {
            throw new ArgumentException("A NIC setting requires a valid adapter GUID.", nameof(targetId));
        }

        var keyword = settingId[SettingSpecifications.NicPrefix.Length..];
        var capability = capabilities.FirstOrDefault(item =>
            item.AdapterId == adapterId
            && string.Equals(item.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Adapter {adapterId} does not advertise the property {keyword}.");

        return new NicSettingSpecification(capability);
    }
}

public delegate ISettingSpecification SettingSpecificationResolver(string settingId, string? targetId);

public static class SettingSpecifications
{
    public const string NicPrefix = "nic.";
    public const string CimPrefix = GlobalSettingCapability.Prefix;

    /// <summary>
    /// Resolver backed by live capabilities. Both the driver's NIC keywords and the CIM provider's
    /// global properties are read on first lookup and cached for the life of this resolver, so each
    /// transaction creates its own and re-reads what the system currently allows instead of trusting
    /// constraints captured earlier by the UI.
    /// </summary>
    public static SettingSpecificationResolver Live()
    {
        IReadOnlyList<AdapterSettingCapability>? adapters = null;
        IReadOnlyList<GlobalSettingCapability>? globals = null;
        Models.InterruptAffinityInventoryResult? interrupts = null;
        return (settingId, targetId) =>
        {
            if (settingId.StartsWith(NicPrefix, StringComparison.Ordinal))
            {
                adapters ??= WindowsAdapterCapabilityInventory.Read().Capabilities;
                return NicSettingSpecification.Resolve(settingId, targetId, adapters);
            }

            if (settingId.StartsWith(CimPrefix, StringComparison.Ordinal))
            {
                globals ??= WindowsGlobalSettingInventory.Read().Capabilities;
                return CimGlobalSettingSpecification.Resolve(settingId, targetId, globals);
            }

            if (string.Equals(settingId, DnsServerSpecification.SettingId, StringComparison.Ordinal))
            {
                return new DnsServerSpecification();
            }

            if (string.Equals(settingId, InterruptAffinitySpecification.SettingId, StringComparison.Ordinal))
            {
                interrupts ??= Collection.InterruptAffinityInventory.Read(onlyInteresting: false);
                return new InterruptAffinitySpecification(
                    Math.Max(interrupts.LogicalProcessors, 1),
                    interrupts.Devices.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            return SettingCatalog.Get(settingId);
        };
    }

    public static SettingSpecificationResolver From(
        IReadOnlyList<AdapterSettingCapability> capabilities,
        IReadOnlyList<GlobalSettingCapability>? globals = null) =>
        (settingId, targetId) => settingId switch
        {
            _ when settingId.StartsWith(NicPrefix, StringComparison.Ordinal) =>
                NicSettingSpecification.Resolve(settingId, targetId, capabilities),
            _ when settingId.StartsWith(CimPrefix, StringComparison.Ordinal) =>
                CimGlobalSettingSpecification.Resolve(settingId, targetId, globals ?? []),
            DnsServerSpecification.SettingId => new DnsServerSpecification(),
            InterruptAffinitySpecification.SettingId => new InterruptAffinitySpecification(
                Environment.ProcessorCount, new HashSet<string>([targetId ?? string.Empty], StringComparer.OrdinalIgnoreCase)),
            _ => SettingCatalog.Get(settingId)
        };
}
