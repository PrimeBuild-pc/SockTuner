using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Reads the writable global CIM properties and, crucially, the values each one accepts. The
/// accepted values come from the class's own <c>ValueMap</c> qualifier in the live namespace, so
/// the allowlist is whatever this Windows build actually implements rather than a table SockTuner
/// carries and hopes is still true.
/// </summary>
internal static class WindowsGlobalSettingInventory
{
    internal const string NamespacePath = @"\\.\root\StandardCimv2";

    internal static GlobalSettingInventoryResult Read() => Read(new ManagementScope(NamespacePath));

    internal static GlobalSettingInventoryResult Read(ManagementScope scope)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        var capabilities = new List<GlobalSettingCapability>();
        var errors = new List<string>();
        foreach (var className in CimGlobalPropertyCatalog.All.Select(item => item.ClassName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                capabilities.AddRange(ReadClass(scope, className));
            }
            catch (Exception exception) when (exception is ManagementException
                or UnauthorizedAccessException or COMException)
            {
                errors.Add($"{className}: {exception.Message}");
            }
        }

        return new(capabilities, errors.Count == 0 ? null : string.Join(" ", errors));
    }

    private static IEnumerable<GlobalSettingCapability> ReadClass(ManagementScope scope, string className)
    {
        // Amended qualifiers carry the human-readable Values list. ValueMap — the part that decides
        // what may be written — is a normal qualifier and is read either way.
        using var definition = new ManagementClass(
            scope, new ManagementPath(className), new ObjectGetOptions { UseAmendedQualifiers = true });
        definition.Get();

        var constraints = CimGlobalPropertyCatalog.ForClass(className)
            .ToDictionary(item => item.Property, item => (item, Choices: ReadChoices(definition, item.Property)),
                StringComparer.OrdinalIgnoreCase);
        var keyProperty = CimGlobalPropertyCatalog.InstanceKeyProperty.GetValueOrDefault(className);

        var results = new List<GlobalSettingCapability>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className}"));
        using var instances = searcher.Get();
        foreach (ManagementObject instance in instances)
        {
            using (instance)
            {
                var key = keyProperty is null ? null : Text(instance[keyProperty]);
                foreach (var (property, (constraint, choices)) in constraints)
                {
                    // A property the provider does not expose on this build is simply absent, which
                    // is the correct outcome: it cannot be planned and cannot be written.
                    if (!TryRead(instance, property, out var current))
                    {
                        continue;
                    }

                    // No enumeration and no documented range means nothing bounds the value. Rather
                    // than invent a bound, the property is left out of the writable surface.
                    if (choices.Count == 0 && constraint.Minimum is null)
                    {
                        continue;
                    }

                    results.Add(new GlobalSettingCapability(
                        className, key, constraint.Property, constraint.DisplayName, constraint.Category,
                        current, choices, constraint.Minimum, constraint.Maximum,
                        EvidenceLevel.Documented, constraint.Risk, constraint.RestartRequirement, constraint.TradeOff));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Pairs the class's <c>ValueMap</c> entries with the <c>Values</c> labels where amended
    /// qualifiers are available. Without the labels the numbers still stand on their own; without
    /// <c>ValueMap</c> the property is not enumerated and falls back to its documented range.
    /// </summary>
    private static IReadOnlyList<CapabilityChoice> ReadChoices(ManagementClass definition, string property)
    {
        var data = definition.Properties.Cast<PropertyData>()
            .FirstOrDefault(item => string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase));
        if (data is null || Qualifier(data, "ValueMap") is not string[] map)
        {
            return [];
        }

        var labels = Qualifier(data, "Values") as string[];
        return map
            .Select((value, index) => new CapabilityChoice(
                value, labels is not null && index < labels.Length ? labels[index] : value))
            .ToArray();
    }

    private static object? Qualifier(PropertyData data, string name)
    {
        foreach (QualifierData qualifier in data.Qualifiers)
        {
            if (string.Equals(qualifier.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return qualifier.Value;
            }
        }

        return null;
    }

    internal static bool TryRead(ManagementBaseObject instance, string property, out string value)
    {
        value = string.Empty;
        try
        {
            if (instance[property] is not { } raw)
            {
                return false;
            }

            value = Text(raw);
            return value.Length > 0;
        }
        catch (ManagementException)
        {
            return false;
        }
    }

    internal static string Text(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
