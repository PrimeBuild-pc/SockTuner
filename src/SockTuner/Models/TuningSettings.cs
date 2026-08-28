using System.Globalization;
using Microsoft.Win32;

namespace SockTuner.Models;

public enum SettingScope
{
    System,
    AdapterInterface
}

public enum EvidenceLevel
{
    Documented,
    DriverAdvertised,
    Experimental,
    Blocked
}

public enum ChangeRisk
{
    Low,
    Medium,
    High
}

public enum ChangeSource
{
    Unknown,
    Manual,
    Profile,
    Recovery
}

/// <summary>
/// What the transaction engine needs to know about a setting, regardless of whether it comes
/// from the static registry catalog or from a driver's advertised capabilities.
/// </summary>
public interface ISettingSpecification
{
    string Id { get; }
    string Title { get; }
    string Category { get; }
    EvidenceLevel Evidence { get; }
    ChangeRisk Risk { get; }
    string RestartRequirement { get; }
    string TradeOff { get; }

    /// <summary>Whether removing the value entirely is a meaningful, verifiable operation.</summary>
    bool SupportsAbsentValue { get; }

    void Validate(string value);
    SettingAddress ResolveAddress(string? targetId);
}

public sealed record SettingDefinition(
    string Id,
    string Title,
    string Category,
    SettingScope Scope,
    EvidenceLevel Evidence,
    ChangeRisk Risk,
    string RestartRequirement,
    string Description,
    string TradeOff,
    string RegistryPath,
    string ValueName,
    RegistryValueKind ValueKind,
    uint Minimum,
    uint Maximum,
    IReadOnlySet<uint>? AllowedValues = null,
    // Why this entry carries its EvidenceLevel: the documentation, or the Windows component that
    // actually consumes the value. A level on its own is an assertion; this makes it a citation a
    // reviewer can check. NIC settings do not need one — the driver advertises them — so this lives
    // on SettingDefinition rather than on ISettingSpecification. Enforced non-empty by tests.
    string EvidenceNote = "") : ISettingSpecification
{
    // A registry value can legitimately be absent, which is how "Windows default" is expressed.
    public bool SupportsAbsentValue => true;

    public SettingAddress ResolveAddress(string? targetId)
    {
        var path = RegistryPath;
        string? normalizedTarget = null;
        if (Scope == SettingScope.AdapterInterface)
        {
            if (!Guid.TryParse(targetId, out var targetGuid))
            {
                throw new ArgumentException("A valid adapter GUID is required.", nameof(targetId));
            }

            normalizedTarget = targetGuid.ToString("B").ToUpperInvariant();
            path = $"{RegistryPath}\\{normalizedTarget}";
        }
        else if (targetId is not null)
        {
            throw new ArgumentException("A system setting cannot have an adapter target.", nameof(targetId));
        }

        return new SettingAddress(Id, normalizedTarget, path, ValueName, ValueKind);
    }

    public void Validate(string value)
    {
        if (Evidence == EvidenceLevel.Blocked)
        {
            throw new InvalidOperationException($"{Id} is read-only because its evidence level is Blocked.");
        }

        if (!TryParseCanonical(value, out var parsed)
            || parsed < Minimum || parsed > Maximum
            || (AllowedValues is not null && !AllowedValues.Contains(parsed)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} is not valid for {Id}.");
        }
    }

    // Registry-backed catalog values round-trip through text, so only the canonical decimal
    // form is accepted: a leading zero, sign, or space would read back differently and turn
    // an exact read-back check into a confusing verification failure.
    public static bool TryParseCanonical(string? value, out uint parsed)
    {
        parsed = 0;
        return value is not null
            && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
            && parsed.ToString(CultureInfo.InvariantCulture) == value;
    }
}

public sealed record SettingAddress(
    string SettingId,
    string? TargetId,
    string RegistryPath,
    string ValueName,
    RegistryValueKind ValueKind);

public readonly record struct StoredSettingValue(bool Exists, string Value)
{
    // NDIS advanced properties are REG_SZ and MMCSS/TCP entries are DWORD; text is the one
    // representation both stores can convert to exactly, so equality stays an exact match.
    public string Value { get; } = Value ?? string.Empty;

    public static StoredSettingValue Missing => new(false, string.Empty);
}

public sealed record ChangeRequest(
    string SettingId,
    string? TargetId,
    string? ProposedValue,
    ChangeSource Source = ChangeSource.Manual);

public sealed record PlannedChange(
    ISettingSpecification Definition,
    SettingAddress Address,
    StoredSettingValue Before,
    StoredSettingValue After,
    ChangeSource Source = ChangeSource.Manual)
{
    public string BeforeDisplay => Before.Exists ? Before.Value : "Missing";
    public string AfterDisplay => After.Exists ? After.Value : "Remove value";

    /// <summary>
    /// High-risk or experimental changes need a deliberate, typed confirmation rather than a
    /// single click: they can sever connectivity or rest on undocumented behaviour.
    /// </summary>
    public bool RequiresExplicitConfirmation =>
        Definition.Risk == ChangeRisk.High || Definition.Evidence == EvidenceLevel.Experimental;
}

public sealed record ChangePlan(DateTimeOffset CreatedAt, IReadOnlyList<PlannedChange> Changes);

public sealed record SettingSnapshot(
    Guid Id,
    Guid AuthorityId,
    string MachineName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PlannedChange> Changes,
    bool AppliedSuccessfully,
    string Signature);

public sealed record ApplyResult(
    bool Success,
    SettingSnapshot Snapshot,
    string? Error,
    IReadOnlyList<string> RollbackErrors);
