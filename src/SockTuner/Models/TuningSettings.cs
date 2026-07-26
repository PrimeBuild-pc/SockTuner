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
    IReadOnlySet<uint>? AllowedValues = null)
{
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

    public void Validate(uint value)
    {
        if (Evidence == EvidenceLevel.Blocked)
        {
            throw new InvalidOperationException($"{Id} is read-only because its evidence level is Blocked.");
        }

        if (value < Minimum || value > Maximum || (AllowedValues is not null && !AllowedValues.Contains(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} is not valid for {Id}.");
        }
    }
}

public sealed record SettingAddress(
    string SettingId,
    string? TargetId,
    string RegistryPath,
    string ValueName,
    RegistryValueKind ValueKind);

public readonly record struct StoredSettingValue(bool Exists, uint Value)
{
    public static StoredSettingValue Missing => new(false, 0);
}

public sealed record ChangeRequest(
    string SettingId,
    string? TargetId,
    uint? ProposedValue,
    ChangeSource Source = ChangeSource.Manual);

public sealed record PlannedChange(
    SettingDefinition Definition,
    SettingAddress Address,
    StoredSettingValue Before,
    StoredSettingValue After,
    ChangeSource Source = ChangeSource.Manual)
{
    public string BeforeDisplay => Before.Exists ? Before.Value.ToString() : "Missing";
    public string AfterDisplay => After.Exists ? After.Value.ToString() : "Remove value";
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
