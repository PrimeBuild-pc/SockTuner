using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class RouteGatewayResolver
{
    public async Task<string?> ResolveAsync(string target, NetworkSnapshot snapshot, CancellationToken cancellationToken)
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
                    return SelectGateway(snapshot, local.Address);
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            // The diagnostic run will report DNS/connectivity errors; gateway fallback remains useful.
        }

        return SelectOnlyActiveGateway(snapshot);
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
