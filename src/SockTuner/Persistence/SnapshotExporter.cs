using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

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

    public static string Serialize(NetworkSnapshot snapshot, bool redact = false) => JsonSerializer.Serialize(new
    {
        schemaVersion = 11,
        toolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        exportedAt = DateTimeOffset.Now,
        redacted = redact,
        snapshot = redact ? Redact(snapshot) : snapshot
    }, Options);

    internal static NetworkSnapshot Redact(NetworkSnapshot snapshot)
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
                Id = Redacted,
                Name = $"Adapter {index + 1}",
                MacAddress = Redacted,
                Addresses = adapter.Addresses.Select(Address).ToArray(),
                Gateways = adapter.Gateways.Select(Address).ToArray(),
                DnsServers = adapter.DnsServers.Select(Address).ToArray(),
                InventoryError = Error(adapter.InventoryError),
                Driver = adapter.Driver is null ? null : adapter.Driver with
                {
                    InfPath = Redacted,
                    PnpInstanceId = Redacted
                },
                NdisProperties = adapter.NdisProperties.Select(property => property with { CurrentValue = Redacted }).ToArray(),
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
            QosPolicyInventoryError = Error(snapshot.QosPolicyInventoryError)
        };
    }

    private static string Address(string address) => address.Contains(':') ? "[IPv6 address redacted]" : "[IPv4 address redacted]";
    private static string RedactedValue(string value) => string.IsNullOrEmpty(value) ? value : Redacted;
}
