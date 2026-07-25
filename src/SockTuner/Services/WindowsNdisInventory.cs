using System.Globalization;
using System.IO;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

public static class WindowsNdisInventory
{
    private const string AdapterClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static NdisInventoryResult Read(string adapterId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(null, [], false, null);
        }

        try
        {
            using var adapterClass = Registry.LocalMachine.OpenSubKey(AdapterClassPath);
            if (adapterClass is null)
            {
                return new(null, [], false, "The network adapter registry class is unavailable.");
            }

            var candidates = adapterClass.GetSubKeyNames()
                .Where(IsAdapterInstanceKey)
                .Select(keyName =>
                {
                    using var key = adapterClass.OpenSubKey(keyName);
                    return (KeyName: keyName, AdapterId: key?.GetValue("NetCfgInstanceId") as string);
                })
                .ToArray();
            var matchingKeyName = FindMatchingAdapterKey(adapterId, candidates);
            if (matchingKeyName is null)
            {
                return new(null, [], false, null);
            }

            using var adapterKey = adapterClass.OpenSubKey(matchingKeyName);
            if (adapterKey is null)
            {
                return new(null, [], false, "The matching NDIS adapter instance became unavailable.");
            }

            var properties = ReadProperties(adapterKey);
            var driver = ReadDriver(adapterKey);
            return new(driver.Driver, properties ?? [], properties is not null, driver.Error);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new(null, [], false, exception.Message);
        }
    }

    public static string FormatValue(object? value) => value switch
    {
        null => "—",
        byte[] bytes => Convert.ToHexString(bytes),
        string[] strings => string.Join(", ", strings),
        Array values => string.Join(", ", values.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
    };

    private static DriverReadResult ReadDriver(RegistryKey key)
    {
        var rawCharacteristics = key.GetValue("Characteristics");
        var hasCharacteristics = TryReadCharacteristics(rawCharacteristics, out var characteristics);
        return new(
            new DriverInfo(
                FormatValue(key.GetValue("ProviderName")),
                FormatValue(key.GetValue("DriverVersion")),
                FormatValue(key.GetValue("DriverDate")),
                FormatValue(key.GetValue("InfPath")),
                FormatValue(key.GetValue("ComponentId")),
                FormatValue(key.GetValue("NdisVersion")),
                FormatValue(key.GetValue("PnPInstanceID")),
                characteristics),
            hasCharacteristics
                ? null
                : $"Driver characteristics have unsupported type {rawCharacteristics?.GetType().Name ?? "null"}.");
    }

    internal static bool TryReadCharacteristics(object? value, out uint characteristics)
    {
        switch (value)
        {
            case null:
                characteristics = 0;
                return true;
            case byte number:
                characteristics = number;
                return true;
            case ushort number:
                characteristics = number;
                return true;
            case int number when number >= 0:
                characteristics = (uint)number;
                return true;
            case uint number:
                characteristics = number;
                return true;
            case long number when number is >= 0 and <= uint.MaxValue:
                characteristics = (uint)number;
                return true;
            default:
                characteristics = 0;
                return false;
        }
    }

    private static IReadOnlyList<NdisAdvancedProperty>? ReadProperties(RegistryKey adapterKey)
    {
        using var parametersKey = adapterKey.OpenSubKey(@"Ndi\Params");
        if (parametersKey is null)
        {
            return null;
        }

        return parametersKey.GetSubKeyNames()
            .Select(keyword => ReadProperty(adapterKey, parametersKey, keyword))
            .Where(property => property is not null)
            .Cast<NdisAdvancedProperty>()
            .OrderBy(property => property.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static NdisAdvancedProperty? ReadProperty(RegistryKey adapterKey, RegistryKey parametersKey, string keyword)
    {
        using var propertyKey = parametersKey.OpenSubKey(keyword);
        if (propertyKey is null)
        {
            return null;
        }

        var description = propertyKey.GetValue("ParamDesc") as string;
        var displayName = string.IsNullOrWhiteSpace(description) || description.StartsWith('@') ? keyword : description;
        var validValues = ReadValidValues(propertyKey);

        return new(
            keyword,
            displayName,
            FormatValue(adapterKey.GetValue(keyword)),
            FormatValue(propertyKey.GetValue("Default")),
            FormatValue(propertyKey.GetValue("Type")),
            validValues);
    }

    private static string ReadValidValues(RegistryKey propertyKey)
    {
        using var enumKey = propertyKey.OpenSubKey("enum");
        if (enumKey is not null)
        {
            return string.Join(", ", enumKey.GetValueNames()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => $"{name}: {FormatValue(enumKey.GetValue(name))}"));
        }

        var minimum = propertyKey.GetValue("Min");
        var maximum = propertyKey.GetValue("Max");
        var step = propertyKey.GetValue("Step");
        return minimum is null && maximum is null
            ? "—"
            : $"{FormatValue(minimum)}–{FormatValue(maximum)} (step {FormatValue(step)})";
    }

    internal static bool IsAdapterInstanceKey(string keyName) =>
        keyName.Length > 0 && keyName.All(char.IsDigit);

    internal static string? FindMatchingAdapterKey(
        string adapterId,
        IEnumerable<(string KeyName, string? AdapterId)> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (SameAdapter(adapterId, candidate.AdapterId))
            {
                return candidate.KeyName;
            }
        }

        return null;
    }

    private static bool SameAdapter(string expected, string? actual) =>
        actual is not null && string.Equals(
            expected.Trim().Trim('{', '}'),
            actual.Trim().Trim('{', '}'),
            StringComparison.OrdinalIgnoreCase);

    private sealed record DriverReadResult(DriverInfo Driver, string? Error);
}
