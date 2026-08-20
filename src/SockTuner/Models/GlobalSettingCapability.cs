using System.Globalization;

namespace SockTuner.Models;

/// <summary>
/// One writable property on one instance of a Windows CIM management class — the TCP template
/// settings and the global offload switches that <c>netsh int tcp set global</c> and
/// <c>Set-NetOffloadGlobalSetting</c> reach.
/// </summary>
/// <remarks>
/// The provider is the allowlist here, exactly as the driver is for NIC keywords: an enumerated
/// property's accepted values come from the class's own <c>ValueMap</c> qualifier, read from the
/// live namespace. A property that advertises neither an enumeration nor a documented range is not
/// exposed at all — there is no free-form tier, because a wrong value in the TCP stack is not
/// something the user can see and undo by looking at it.
/// </remarks>
public sealed record GlobalSettingCapability(
    string ClassName,
    string? InstanceKey,
    string Property,
    string DisplayName,
    string Category,
    string CurrentValue,
    IReadOnlyList<CapabilityChoice> Choices,
    long? Minimum,
    long? Maximum,
    EvidenceLevel Evidence,
    ChangeRisk Risk,
    string RestartRequirement,
    string TradeOff)
{
    public const string Prefix = "cim.";

    public string SettingId => $"{Prefix}{ClassName}.{Property}";

    public bool IsEnumerated => Choices.Count > 0;
    public bool IsNumericRange => !IsEnumerated && Minimum.HasValue && Maximum.HasValue;

    public string InstanceDisplay => InstanceKey ?? "System";
    public string CurrentDisplay => DisplayFor(CurrentValue);

    public string ConstraintDisplay => IsEnumerated
        ? string.Join(", ", Choices.Select(choice => choice.Display))
        : IsNumericRange
            ? $"{Minimum}–{Maximum}"
            : "Not writable: the provider advertises no constraint";

    public string DisplayFor(string value) =>
        Choices.FirstOrDefault(choice => string.Equals(choice.RegistryValue, value, StringComparison.Ordinal))
            ?.DisplayValue
        ?? value;

    /// <summary>
    /// Accepts a value only against the provider's own advertised enumeration or the documented
    /// range for that property. Re-run inside the elevated worker against freshly read metadata, so
    /// a caller's stale view of what is allowed is never trusted.
    /// </summary>
    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (IsEnumerated)
        {
            if (!Choices.Any(choice => string.Equals(choice.RegistryValue, value, StringComparison.Ordinal)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), $"{Property} does not advertise the value {value} on {InstanceDisplay}.");
            }

            return;
        }

        if (!IsNumericRange)
        {
            throw new InvalidOperationException(
                $"{Property} advertises neither an enumeration nor a range, so it is not writable.");
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed.ToString(CultureInfo.InvariantCulture) != value
            || parsed < Minimum || parsed > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"{Property} accepts {ConstraintDisplay} on {InstanceDisplay}; {value} does not fit.");
        }
    }
}

public sealed record GlobalSettingInventoryResult(
    IReadOnlyList<GlobalSettingCapability> Capabilities,
    string? Error);

/// <summary>
/// One entry from <c>MSFT_NetTransportFilter</c>: the mapping that decides which TCP template a
/// connection actually uses. Writing a template no filter points at is a silent no-op — the write
/// succeeds, reads back correctly, and changes nothing.
/// </summary>
public sealed record TcpTransportFilter(
    string SettingName,
    uint Protocol,
    uint LocalPortStart,
    uint LocalPortEnd,
    uint RemotePortStart,
    uint RemotePortEnd,
    string DestinationPrefix)
{
    /// <summary>IANA protocol number for TCP.</summary>
    public const uint Tcp = 6;

    public bool IsTcp => Protocol == Tcp;

    /// <summary>Whether this filter covers a connection to an arbitrary host on an arbitrary port.</summary>
    public bool CoversOrdinaryTraffic => IsTcp
        && DestinationPrefix is "*" or "0.0.0.0/0" or "::/0"
        && RemotePortStart == 0 && RemotePortEnd >= 65535;

    /// <summary>How much of the port space the filter claims, used to pick the widest when several match.</summary>
    public long Coverage => (long)(LocalPortEnd - LocalPortStart) + (RemotePortEnd - RemotePortStart);

    public string Summary => $"{SettingName}: protocol {Protocol}, local {LocalPortStart}-{LocalPortEnd}, "
        + $"remote {RemotePortStart}-{RemotePortEnd}, destination {DestinationPrefix}";
}

/// <summary>Which TCP template ordinary internet traffic is mapped to, and how that was decided.</summary>
public sealed record TcpTemplateResolution(
    string Template,
    bool FromFilter,
    IReadOnlyList<TcpTransportFilter> Filters,
    string Reason,
    string? Error = null);
