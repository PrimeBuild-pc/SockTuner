using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>Archived hardware constraints, or explicitly experimental templates bounded by the live driver.</summary>
public static class NicHardwareCompatibility
{
    private sealed record Hardware(string Model, bool IsVirtual, DriverInfo? Driver,
        IReadOnlyList<AdapterSettingCapability> Capabilities);
    private static readonly Lazy<Hardware[]> Archive = new(Load);

    public static string? Restriction(AdapterSettingCapability live, DriverInfo? driver)
    {
        var restriction = ArchiveRestriction(live, driver);
        return restriction is not null && AllowsExperimentalTemplate(live, driver) ? null : restriction;
    }

    internal static bool IsUnverifiedTemplate(AdapterSettingCapability live, DriverInfo? driver) =>
        ArchiveRestriction(live, driver) is not null && AllowsExperimentalTemplate(live, driver);

    private static bool AllowsExperimentalTemplate(AdapterSettingCapability live, DriverInfo? driver)
    {
        if (driver is null || string.IsNullOrWhiteSpace(driver.ComponentId) || live.Rejected
            || live.RegistryDataType != AdapterSettingCapability.RegistrySz || !live.IsEnumerated) return false;
        var keyword = live.Keyword.ToLowerInvariant();
        var standard = keyword is "*selectivesuspend" or "*idlerestriction" or "*interruptmoderation"
            or "*lsov2ipv4" or "*lsov2ipv6" or "*rscipv4" or "*rscipv6";
        var vendor = keyword is "autodisablegigabit" or "enablepme" or "ulpmode" or "sipsenabled";
        var family = live.InterfaceDescription.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || live.InterfaceDescription.Contains("Realtek", StringComparison.OrdinalIgnoreCase);
        return (standard || (vendor && family)) && live.Choices.Any(choice => choice.RegistryValue == (keyword == "*idlerestriction" ? "1" : "0"));
    }

    private static string? ArchiveRestriction(AdapterSettingCapability live, DriverInfo? driver)
    {
        if (AdapterVerificationCatalog.For(live.InterfaceDescription) != AdapterVerification.Verified)
            return "Read-only: this adapter has no characterised hardware report. Create a compatibility report in Preferences.";
        if (driver is null || string.IsNullOrWhiteSpace(driver.ComponentId))
            return "Read-only: the installed driver identity is unavailable.";
        var candidates = Archive.Value.Where(item => !item.IsVirtual
            && AdapterVerificationCatalog.Normalize(item.Model) == AdapterVerificationCatalog.Normalize(live.InterfaceDescription)
            && item.Driver is { } known
            && string.Equals(known.Version, driver.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(known.Provider, driver.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(known.ComponentId, driver.ComponentId, StringComparison.OrdinalIgnoreCase));
        if (!candidates.Any()) return "Read-only: this driver version and hardware identity have not been characterised together.";
        return NicKeywordCatalog.IsCharacterised(live.Keyword) && !live.Rejected
            && candidates.SelectMany(item => item.Capabilities).Any(known => SameConstraints(known, live))
            ? null : "Read-only: this property's live driver constraints do not match a characterised hardware report.";
    }

    internal static bool SameConstraints(AdapterSettingCapability known, AdapterSettingCapability live) =>
        string.Equals(known.Keyword, live.Keyword, StringComparison.OrdinalIgnoreCase)
        && known.RegistryDataType == live.RegistryDataType
        && known.Minimum == live.Minimum && known.Maximum == live.Maximum && known.Step == live.Step
        && known.DefaultValue == live.DefaultValue && known.CanRemove == live.CanRemove
        && known.Choices.Count == live.Choices.Count
        && known.Choices.OrderBy(item => item.RegistryValue, StringComparer.Ordinal)
            .SequenceEqual(live.Choices.OrderBy(item => item.RegistryValue, StringComparer.Ordinal))
        && (live.IsEnumerated || live.IsNumericRange);

    private static Hardware[] Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith("Hardware.", StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return JsonSerializer.Deserialize<Hardware>(stream, options)!;
            }).ToArray();
    }
}
