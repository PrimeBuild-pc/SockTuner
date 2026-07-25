using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsBindingInventory
{
    private const string NamespacePath = @"\\.\root\StandardCimv2";
    private const string Query = """
        SELECT InstanceID, InterfaceDescription, Name, Source, BindName, Characteristics,
               ComponentClassGuid, ComponentClassName, ComponentID, DisplayName, Enabled
        FROM MSFT_NetAdapterBindingSettingData
        """;

    internal static BindingInventoryResult Read()
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
            var bindings = new List<NetworkBindingInfo>(results.Count);
            var errors = new List<string>();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var instanceId = ReadString(item, "InstanceID");
                    if (!TryParseAdapterId(instanceId, out var adapterId))
                    {
                        errors.Add($"Binding instance '{instanceId}' has no adapter GUID.");
                        continue;
                    }

                    bindings.Add(new NetworkBindingInfo(
                        adapterId,
                        ReadString(item, "Name"),
                        ReadString(item, "InterfaceDescription"),
                        ReadString(item, "ComponentID"),
                        ReadString(item, "DisplayName"),
                        ReadString(item, "BindName"),
                        ReadBoolean(item, "Enabled"),
                        ReadUInt32(item, "Characteristics"),
                        ReadString(item, "ComponentClassGuid"),
                        ReadString(item, "ComponentClassName"),
                        ReadUInt32(item, "Source")));
                }
            }

            return new(
                bindings
                    .OrderBy(binding => binding.AdapterName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(binding => binding.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                errors.Count == 0 ? null : string.Join(" ", errors));
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            return new([], exception.Message);
        }
    }

    internal static bool TryParseAdapterId(string instanceId, out Guid adapterId)
    {
        var separator = instanceId.IndexOf("::", StringComparison.Ordinal);
        return Guid.TryParse(separator < 0 ? instanceId : instanceId[..separator], out adapterId);
    }

    private static string ReadString(ManagementBaseObject item, string name) =>
        Convert.ToString(item[name], CultureInfo.InvariantCulture) ?? "—";

    private static bool ReadBoolean(ManagementBaseObject item, string name) =>
        Convert.ToBoolean(item[name], CultureInfo.InvariantCulture);

    private static uint ReadUInt32(ManagementBaseObject item, string name) =>
        Convert.ToUInt32(item[name], CultureInfo.InvariantCulture);
}

internal sealed record BindingInventoryResult(IReadOnlyList<NetworkBindingInfo> Bindings, string? Error);
