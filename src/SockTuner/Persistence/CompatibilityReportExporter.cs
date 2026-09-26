using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SockTuner.Models;

namespace SockTuner.Persistence;

/// <summary>
/// A deliberately small, public hardware-capability report. Adapter identifiers are synthetic for
/// this file, and network state, addresses, MACs, paths, timestamps and machine names never enter
/// the serialized object.
/// </summary>
public static class CompatibilityReportExporter
{
    public const int SchemaVersion = 1;

    private sealed record CommunityDriver(
        string Provider,
        string Version,
        string Date,
        string ComponentId,
        string NdisVersion,
        uint Characteristics);

    private sealed record CommunityAdapter(
        Guid Id,
        string Name,
        string Description,
        string InterfaceType,
        CommunityDriver Driver,
        IReadOnlyList<NdisAdvancedProperty> NdisProperties,
        IReadOnlyList<AdapterSettingCapability> Capabilities);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var capabilities = snapshot.AdapterCapabilities ?? [];
        var adapters = snapshot.Adapters
            .Where(adapter => adapter.Driver is not null
                && adapter.Kind is AdapterKind.Physical or AdapterKind.DriverBacked)
            .Select((adapter, index) => BuildAdapter(adapter, capabilities, index + 1))
            .Where(adapter => adapter.NdisProperties.Count > 0 || adapter.Capabilities.Count > 0)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            reportType = "socktuner.compatibility",
            toolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            redacted = true,
            privacy = new
            {
                persistentDeviceIdentifiers = false,
                networkAddresses = false,
                exactTimestamps = false,
                userPaths = false,
                currentSettingValues = false
            },
            system = new
            {
                snapshot.System.OperatingSystem,
                snapshot.System.Version
            },
            adapters
        }, Options);
    }

    private static CommunityAdapter BuildAdapter(
        AdapterInfo adapter,
        IReadOnlyList<AdapterSettingCapability> allCapabilities,
        int ordinal)
    {
        var syntheticId = new Guid($"00000000-0000-0000-0000-{ordinal:000000000000}");
        var capabilities = Guid.TryParse(adapter.Id, out var realId)
            ? allCapabilities.Where(capability => capability.AdapterId == realId)
            : allCapabilities.Where(capability => string.Equals(
                capability.InterfaceDescription, adapter.Description, StringComparison.OrdinalIgnoreCase));

        return new CommunityAdapter(
            syntheticId,
            $"Adapter {ordinal}",
            adapter.Description,
            adapter.InterfaceType.ToString(),
            new CommunityDriver(
                adapter.Driver!.Provider,
                adapter.Driver.Version,
                adapter.Driver.Date,
                SanitizeComponentId(adapter.Driver.ComponentId),
                adapter.Driver.NdisVersion,
                adapter.Driver.Characteristics),
            adapter.NdisProperties.Select(property => property with
            {
                CurrentValue = "[redacted]"
            }).ToArray(),
            capabilities.Select(capability => capability with
            {
                AdapterId = syntheticId,
                AdapterName = $"Adapter {ordinal}",
                CurrentValue = "[redacted]"
            }).ToArray());
    }

    internal static string SanitizeComponentId(string componentId)
    {
        var normalized = componentId.Trim().ToUpperInvariant();
        var bus = normalized.StartsWith("PCI\\", StringComparison.Ordinal) ? "PCI"
            : normalized.StartsWith("USB\\", StringComparison.Ordinal) ? "USB"
            : null;
        if (bus is null) return "Unavailable";

        var fields = normalized[(normalized.IndexOf('\\') + 1)..];
        var vendor = Token(fields, bus == "PCI" ? "VEN" : "VID", 4);
        var device = Token(fields, bus == "PCI" ? "DEV" : "PID", 4);
        if (vendor is null || device is null) return "Unavailable";
        var revision = Token(fields, "REV", bus == "PCI" ? 2 : 4);
        return $"{bus}\\{(bus == "PCI" ? "VEN" : "VID")}_{vendor}"
            + $"&{(bus == "PCI" ? "DEV" : "PID")}_{device}"
            + (revision is null ? string.Empty : $"&REV_{revision}");
    }

    private static string? Token(string value, string name, int digits)
    {
        var match = Regex.Match(value, $@"(?:^|&){name}_([0-9A-F]{{{digits}}})(?:&|$)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }
}
