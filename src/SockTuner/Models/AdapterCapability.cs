using System.Globalization;

namespace SockTuner.Models;

// Intent areas used to filter the tuning surface. A capability can belong to more than one:
// *EEE for example affects both latency and power.
[Flags]
public enum TuningArea
{
    None = 0,
    Latency = 1,
    Throughput = 2,
    Power = 4,
    Wake = 8,
    Vlan = 16,
    WiFiRadio = 32,
    Identity = 64,
    Other = 128
}

public sealed record CapabilityChoice(string RegistryValue, string DisplayValue)
{
    public string Display => string.IsNullOrWhiteSpace(DisplayValue) || DisplayValue == RegistryValue
        ? RegistryValue
        : $"{DisplayValue} ({RegistryValue})";
}

/// <summary>
/// One driver-advertised advanced property on one adapter. Every constraint here comes from
/// the installed driver through the CIM provider; SockTuner never invents a range or a value.
/// </summary>
public sealed record AdapterSettingCapability(
    Guid AdapterId,
    string AdapterName,
    string InterfaceDescription,
    string Keyword,
    string DisplayName,
    string CurrentValue,
    string? DefaultValue,
    IReadOnlyList<CapabilityChoice> Choices,
    long? Minimum,
    long? Maximum,
    long? Step,
    uint RegistryDataType,
    bool CanRemove,
    TuningArea Areas,
    ChangeRisk Risk,
    string TradeOff,
    // Set from NicKeywordProfile.Rejected: the keyword is unsafe to write at any value. It stays
    // visible here so the inventory can explain it, but it is never writable.
    bool Rejected = false)
{
    public const uint RegistrySz = 1;
    private const int MaximumFreeFormLength = 255;

    public string SettingId => $"nic.{Keyword}";

    // A leading '*' marks a Microsoft-standardised NDIS keyword with documented semantics;
    // anything else is vendor-defined and only as trustworthy as the driver's own metadata.
    public bool IsStandardKeyword => Keyword.StartsWith('*');

    public EvidenceLevel Evidence => Rejected
        ? EvidenceLevel.Blocked
        : IsStandardKeyword
            ? EvidenceLevel.DriverAdvertised
            : EvidenceLevel.Experimental;

    public bool IsEnumerated => Choices.Count > 0;
    public bool IsNumericRange => !IsEnumerated && Minimum.HasValue && Maximum.HasValue;

    public string CurrentDisplay => DisplayFor(CurrentValue);
    public string DefaultDisplay => DefaultValue is null ? "Unavailable" : DisplayFor(DefaultValue);
    public string AreasDisplay => Areas == TuningArea.None ? "Other" : Areas.ToString();
    public string ConstraintDisplay => IsEnumerated
        ? string.Join(", ", Choices.Select(choice => choice.Display))
        : IsNumericRange
            ? $"{Minimum}–{Maximum}{(Step is > 1 ? $" (step {Step})" : string.Empty)}"
            : "Free text (driver advertises no constraint)";
    public bool IsModifiedFromDefault =>
        DefaultValue is not null && !string.Equals(CurrentValue, DefaultValue, StringComparison.Ordinal);

    public string DisplayFor(string registryValue) =>
        Choices.FirstOrDefault(choice => string.Equals(choice.RegistryValue, registryValue, StringComparison.Ordinal))
            ?.DisplayValue
        ?? registryValue;

    /// <summary>
    /// Accepts a proposed value only if the driver's own advertised metadata permits it.
    /// This is what replaces the static allowlist for NIC settings, so it is re-run inside the
    /// elevated worker against freshly read capabilities rather than trusted from the caller.
    /// </summary>
    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Checked before the driver's own constraints: a rejected keyword has no acceptable value,
        // so a plan naming one is refused even if the driver would happily take it. This runs inside
        // the elevated worker too, so the refusal cannot be bypassed by a hand-built plan.
        if (Rejected)
        {
            throw new InvalidOperationException(
                $"{Keyword} is blocked: {TradeOff}");
        }

        if (IsEnumerated)
        {
            if (!Choices.Any(choice => string.Equals(choice.RegistryValue, value, StringComparison.Ordinal)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), $"{Keyword} does not advertise the value {value} on {AdapterName}.");
            }

            return;
        }

        if (IsNumericRange)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed.ToString(CultureInfo.InvariantCulture) != value
                || parsed < Minimum || parsed > Maximum
                || (Step is > 1 && (parsed - Minimum!.Value) % Step.Value != 0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), $"{Keyword} accepts {ConstraintDisplay} on {AdapterName}; {value} does not fit.");
            }

            return;
        }

        // The driver advertises no constraint (typically an "edit" keyword such as
        // NetworkAddress). We refuse to invent one, but the payload is still bounded so a
        // control character or an unbounded blob cannot reach the registry.
        if (value.Length is 0 or > MaximumFreeFormLength || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"{Keyword} requires 1–{MaximumFreeFormLength} printable characters.");
        }
    }
}

public sealed record AdapterCapabilityInventoryResult(
    IReadOnlyList<AdapterSettingCapability> Capabilities,
    string? Error);
