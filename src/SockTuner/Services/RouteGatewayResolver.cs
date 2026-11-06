using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class RouteGatewayResolver
{
    public async Task<string?> ResolveAsync(string target, NetworkSnapshot snapshot, CancellationToken cancellationToken) =>
        (await SelectAsync(target, snapshot, cancellationToken)).Gateway;

    public async Task<DiagnosticInterface> SelectAsync(string target, NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = IPAddress.TryParse(target, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target, cancellationToken);
            var remote = addresses.FirstOrDefault(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
            if (remote is not null)
            {
                using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(remote, 9));
                if (socket.LocalEndPoint is IPEndPoint local)
                {
                    return SelectInterface(snapshot, local.Address);
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            // The diagnostic run will report DNS/connectivity errors; gateway fallback remains useful.
        }

        return new(null, null, SelectOnlyActiveGateway(snapshot), null,
            "The route-selected adapter could not be established. Local changes are unavailable; fallback gateway only.");
    }

    public static DiagnosticInterface SelectInterface(NetworkSnapshot snapshot, IPAddress localAddress)
    {
        var matches = snapshot.Adapters.Where(adapter => adapter.IsUp
            && adapter.Addresses.Any(value => IPAddress.TryParse(value, out var address) && address.Equals(localAddress))).ToArray();
        if (matches.Length != 1) return new(null, localAddress.ToString(), null, null,
            "The local route address does not identify one adapter. No adapter changes will be suggested.");
        var adapter = matches[0];
        return new(adapter.Id, localAddress.ToString(),
            adapter.Gateways.FirstOrDefault(value => IsUsableGateway(value, localAddress.AddressFamily)),
            adapter.SpeedBitsPerSecond,
            adapter.Kind != AdapterKind.Physical
                ? $"Windows routes this target through {adapter.Name} ({adapter.Kind}); the underlying physical link is not identified."
                : $"Windows selected {adapter.Name} for this target. Probes follow Windows routing; route changes during the run cannot be excluded.");
    }

    public static string? SelectGateway(NetworkSnapshot snapshot, IPAddress localAddress)
    {
        var matchingAdapter = snapshot.Adapters.FirstOrDefault(adapter =>
            adapter.Status == OperationalStatus.Up &&
            adapter.Addresses.Any(value => IPAddress.TryParse(value, out var address) && address.Equals(localAddress)));

        return matchingAdapter?.Gateways.FirstOrDefault(value => IsUsableGateway(value, localAddress.AddressFamily));
    }

    private static string? SelectOnlyActiveGateway(NetworkSnapshot snapshot)
    {
        var gateways = snapshot.Adapters
            .Where(adapter => adapter.Status == OperationalStatus.Up)
            .SelectMany(adapter => adapter.Gateways)
            .Where(value => IsUsableGateway(value, null))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return gateways.Length == 1 ? gateways[0] : null;
    }

    private static bool IsUsableGateway(string value, AddressFamily? family) =>
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily != AddressFamily.Unknown &&
        (family is null || address.AddressFamily == family) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.IPv6Any) &&
        !address.Equals(IPAddress.None) &&
        !address.Equals(IPAddress.IPv6None);
}
