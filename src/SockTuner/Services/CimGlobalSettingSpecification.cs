using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// A writable CIM global property presented to the transaction engine. The provider is the
/// allowlist, exactly as the driver is for NIC keywords: the specification only exists because the
/// live namespace reported this property on this instance, and every value is checked against the
/// constraint that namespace advertised.
/// </summary>
public sealed class CimGlobalSettingSpecification : ISettingSpecification
{
    public CimGlobalSettingSpecification(GlobalSettingCapability capability) => Capability = capability;

    public GlobalSettingCapability Capability { get; }

    public string Id => Capability.SettingId;
    public string Title => Capability.DisplayName;
    public string Category => Capability.Category;
    public EvidenceLevel Evidence => Capability.Evidence;
    public ChangeRisk Risk => Capability.Risk;
    public string RestartRequirement => Capability.RestartRequirement;
    public string TradeOff => Capability.TradeOff;

    // These properties always hold a value; there is no "absent" state to restore to, so returning
    // to a default is expressed as proposing that default explicitly.
    public bool SupportsAbsentValue => false;

    public void Validate(string value) => Capability.Validate(value);

    public SettingAddress ResolveAddress(string? targetId)
    {
        var expected = Capability.InstanceKey;
        if (!string.Equals(NormalizeInstance(targetId), NormalizeInstance(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{Capability.Property} belongs to instance {Capability.InstanceDisplay}.", nameof(targetId));
        }

        return new SettingAddress(Id, expected, Capability.ClassName, Capability.Property, RegistryValueKind.String);
    }

    private static string NormalizeInstance(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    /// <summary>
    /// Finds the capability backing a <c>cim.&lt;class&gt;.&lt;property&gt;</c> setting on one
    /// instance. A property this Windows build no longer exposes resolves to nothing, so a stale
    /// plan cannot be applied.
    /// </summary>
    public static CimGlobalSettingSpecification Resolve(
        string settingId,
        string? targetId,
        IReadOnlyList<GlobalSettingCapability> capabilities)
    {
        var parts = settingId[GlobalSettingCapability.Prefix.Length..].Split('.');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"{settingId} is not a cim.<class>.<property> setting id.", nameof(settingId));
        }

        var capability = capabilities.FirstOrDefault(item =>
            string.Equals(item.ClassName, parts[0], StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Property, parts[1], StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeInstance(item.InstanceKey), NormalizeInstance(targetId), StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"This system does not expose {parts[1]} on {parts[0]} instance {targetId ?? "System"}.");

        return new CimGlobalSettingSpecification(capability);
    }
}
