using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>Hardware provenance and live driver constraints are both required at the write boundary.</summary>
public static class NicHardwareCompatibility
{
    private sealed record Hardware(string Model, bool IsVirtual, DriverInfo? Driver,
        IReadOnlyList<AdapterSettingCapability> Capabilities);
    private static readonly Lazy<Hardware[]> Archive = new(Load);

    public static string? Restriction(AdapterSettingCapability live, DriverInfo? driver)
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
