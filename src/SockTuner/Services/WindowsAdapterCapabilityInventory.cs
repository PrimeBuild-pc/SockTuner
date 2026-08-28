using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Reads the advanced properties the installed driver advertises for each adapter, together
/// with the constraints it will accept. This is the only source of writable NIC settings:
/// a property that does not appear here cannot be planned, let alone written.
/// </summary>
internal static class WindowsAdapterCapabilityInventory
{
    internal const string NamespacePath = @"\\.\root\StandardCimv2";
    internal const string ClassName = "MSFT_NetAdapterAdvancedPropertySettingData";

    private const string Query = $"""
        SELECT InstanceID, Name, InterfaceDescription, RegistryKeyword, DisplayName,
               RegistryValue, RegistryDataType, DefaultRegistryValue, Optional,
               ValidRegistryValues, ValidDisplayValues,
               NumericParameterMinValue, NumericParameterMaxValue, NumericParameterStepValue
        FROM {ClassName}
        """;

    internal static AdapterCapabilityInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(NamespacePath),
                new ObjectQuery(Query));
            using var results = searcher.Get();
            var capabilities = new List<AdapterSettingCapability>(results.Count);
            var errors = new List<string>();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    try
                    {
                        var capability = ReadCapability(item);
                        if (capability is null)
                        {
                            errors.Add($"Advanced property '{ReadString(item, "InstanceID")}' has no adapter GUID.");
                            continue;
                        }

                        capabilities.Add(capability);
                    }
                    catch (Exception exception) when (exception is InvalidCastException
                        or FormatException or OverflowException)
                    {
                        errors.Add($"Advanced property row: {exception.Message}");
                    }
                }
            }

            return new(
                capabilities
                    .OrderBy(item => item.AdapterName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                errors.Count == 0 ? null : string.Join(" ", errors));
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException or COMException)
        {
            return new([], exception.Message);
        }
    }

    private static AdapterSettingCapability? ReadCapability(ManagementBaseObject item)
    {
        var instanceId = ReadString(item, "InstanceID");
        if (!WindowsBindingInventory.TryParseAdapterId(instanceId, out var adapterId))
        {
            return null;
        }

        var keyword = ReadString(item, "RegistryKeyword");
        var displayName = ReadString(item, "DisplayName");
        var profile = NicKeywordCatalog.For(keyword);

        return new AdapterSettingCapability(
            adapterId,
            ReadString(item, "Name"),
            ReadString(item, "InterfaceDescription"),
            keyword,
            string.IsNullOrWhiteSpace(displayName) ? keyword : displayName,
            ReadFirst(item, "RegistryValue"),
            ReadNullableString(item, "DefaultRegistryValue"),
            ReadChoices(item),
            ReadNumber(item, "NumericParameterMinValue"),
            ReadNumber(item, "NumericParameterMaxValue"),
            ReadNumber(item, "NumericParameterStepValue"),
            ReadUInt32(item, "RegistryDataType") ?? AdapterSettingCapability.RegistrySz,
            ReadBoolean(item, "Optional"),
            profile.Areas,
            profile.Risk,
            profile.TradeOff,
            profile.Rejected);
    }

    // Valid values arrive as two parallel arrays. A driver may ship the registry values without
    // matching display strings, so the display side is padded rather than assumed to line up.
    internal static IReadOnlyList<CapabilityChoice> ReadChoices(ManagementBaseObject item)
    {
        var values = item["ValidRegistryValues"] as string[] ?? [];
        var displays = item["ValidDisplayValues"] as string[] ?? [];
        return values
            .Select((value, index) => new CapabilityChoice(
                value,
                index < displays.Length ? displays[index] : value))
            .ToArray();
    }

    private static string ReadFirst(ManagementBaseObject item, string name) =>
        item[name] is string[] { Length: > 0 } values ? values[0] ?? string.Empty : string.Empty;

    private static string ReadString(ManagementBaseObject item, string name) =>
        Convert.ToString(item[name], CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? ReadNullableString(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToString(item[name], CultureInfo.InvariantCulture);

    private static bool ReadBoolean(ManagementBaseObject item, string name) =>
        item[name] is not null && Convert.ToBoolean(item[name], CultureInfo.InvariantCulture);

    private static uint? ReadUInt32(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt32(item[name], CultureInfo.InvariantCulture);

    // The numeric bounds are exposed as strings and are absent for enumerated keywords.
    private static long? ReadNumber(ManagementBaseObject item, string name) =>
        long.TryParse(
            Convert.ToString(item[name], CultureInfo.InvariantCulture),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
}
