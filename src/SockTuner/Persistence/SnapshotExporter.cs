using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Persistence;

public static class SnapshotExporter
{
    private const string Redacted = "[redacted]";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(NetworkSnapshot snapshot, bool redact = false, bool probe = false) => JsonSerializer.Serialize(new
    {
        schemaVersion = 12,
        toolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        exportedAt = DateTimeOffset.Now,
        redacted = redact || probe,
        probe,
        snapshot = redact || probe ? Redact(snapshot, probe) : snapshot
    }, Options);

    /// <summary>
    /// The write-verification report. Nothing here identifies the machine — it is template names,
    /// port ranges and accept/refuse outcomes — so there is nothing to redact.
    /// </summary>
    public static string SerializeTcpWriteVerification(TcpWriteVerificationReport report) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        toolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        exportedAt = DateTimeOffset.Now,
        windowsBuild = Environment.OSVersion.Version.ToString(),
        report
    }, Options);

    internal static NetworkSnapshot Redact(NetworkSnapshot snapshot, bool probe = false)
    {
        var adapterNames = snapshot.Adapters
            .Select((adapter, index) => (adapter.Name, Replacement: $"Adapter {index + 1}"))
            .ToDictionary(item => item.Name, item => item.Replacement, StringComparer.OrdinalIgnoreCase);
        string AdapterName(string name) => adapterNames.TryGetValue(name, out var replacement) ? replacement : Redacted;
        string? Error(string? error) => error is null ? null : "Inventory error details redacted.";

        return snapshot with
        {
            System = snapshot.System with { MachineName = Redacted },
            Adapters = snapshot.Adapters.Select((adapter, index) => adapter with
            {
                Id = probe ? adapter.Id : Redacted,
                Name = $"Adapter {index + 1}",
                MacAddress = probe ? MaskMac(adapter.MacAddress) : Redacted,
                Addresses = adapter.Addresses.Select(Address).ToArray(),
                Gateways = adapter.Gateways.Select(Address).ToArray(),
                DnsServers = adapter.DnsServers.Select(Address).ToArray(),
                InventoryError = Error(adapter.InventoryError),
                Driver = adapter.Driver is null ? null : adapter.Driver with
                {
                    InfPath = probe ? adapter.Driver.InfPath : Redacted,
                    PnpInstanceId = probe ? adapter.Driver.PnpInstanceId : Redacted
                },
                NdisProperties = adapter.NdisProperties.Select(property =>
                    probe && !IsUserAssignedValue(property.Keyword)
                        ? property
                        : property with { CurrentValue = Redacted }).ToArray(),
                NdisInventoryError = Error(adapter.NdisInventoryError)
            }).ToArray(),
            Routes = snapshot.Routes.Select(route => route with
            {
                Destination = $"[{route.AddressFamily} destination redacted]",
                NextHop = $"[{route.AddressFamily} next hop redacted]",
                InterfaceName = AdapterName(route.InterfaceName)
            }).ToArray(),
            RouteInventoryError = Error(snapshot.RouteInventoryError),
            IpInterfaceInventoryError = Error(snapshot.IpInterfaceInventoryError),
            NetworkProfiles = snapshot.NetworkProfiles?.Select(profile => profile with
            {
                NetworkId = Guid.Empty,
                Name = Redacted,
                AdapterId = Guid.Empty,
                AdapterName = AdapterName(profile.AdapterName)
            }).ToArray(),
            NetworkProfileInventoryError = Error(snapshot.NetworkProfileInventoryError),
            WinsockProviders = snapshot.WinsockProviders?.Select(provider => provider with { ProviderId = Guid.Empty }).ToArray(),
            WinsockInventoryError = Error(snapshot.WinsockInventoryError),
            NetworkBindings = snapshot.NetworkBindings?.Select(binding => binding with
            {
                AdapterId = Guid.Empty,
                AdapterName = AdapterName(binding.AdapterName),
                InterfaceDescription = Redacted
            }).ToArray(),
            NetworkBindingInventoryError = Error(snapshot.NetworkBindingInventoryError),
            AdapterOffloads = snapshot.AdapterOffloads?.Select(offload => offload with
            {
                AdapterId = Guid.Empty,
                AdapterName = AdapterName(offload.AdapterName),
                InterfaceDescription = Redacted
            }).ToArray(),
            OffloadInventoryError = Error(snapshot.OffloadInventoryError),
            TcpSettingInventoryError = Error(snapshot.TcpSettingInventoryError),
            QosPolicies = snapshot.QosPolicies?.Select(policy => policy with
            {
                Name = Redacted,
                Owner = Redacted,
                AppPath = RedactedValue(policy.AppPath),
                User = RedactedValue(policy.User),
                SourcePrefix = RedactedValue(policy.SourcePrefix),
                DestinationPrefix = RedactedValue(policy.DestinationPrefix),
                Uri = RedactedValue(policy.Uri),
                JobObject = RedactedValue(policy.JobObject)
            }).ToArray(),
            QosPolicyInventoryError = Error(snapshot.QosPolicyInventoryError),
            // Driver-advertised constraints are the point of a probe report, so they survive
            // intact. Only the current value of a user-assigned keyword is masked, exactly as the
            // NDIS property list above is.
            AdapterCapabilities = snapshot.AdapterCapabilities?.Select(capability => capability with
            {
                AdapterId = probe ? capability.AdapterId : Guid.Empty,
                AdapterName = AdapterName(capability.AdapterName),
                InterfaceDescription = probe ? capability.InterfaceDescription : Redacted,
                CurrentValue = probe && !IsUserAssignedValue(capability.Keyword)
                    ? capability.CurrentValue
                    : Redacted
            }).ToArray(),
            AdapterCapabilityInventoryError = Error(snapshot.AdapterCapabilityInventoryError)
        };
    }

    private static string Address(string address) => address.Contains(':') ? "[IPv6 address redacted]" : "[IPv4 address redacted]";

    // Keeps the vendor OUI, masks the device-specific octets.
    private static string MaskMac(string macAddress)
    {
        var octets = macAddress.Split('-');
        return octets.Length == 6 ? $"{octets[0]}-{octets[1]}-{octets[2]}-00-00-00" : Redacted;
    }

    // Keywords whose value is assigned by the user (for example a locally administered MAC).
    private static bool IsUserAssignedValue(string keyword) =>
        string.Equals(keyword, "NetworkAddress", StringComparison.OrdinalIgnoreCase);
    private static string RedactedValue(string value) => string.IsNullOrEmpty(value) ? value : Redacted;
}
