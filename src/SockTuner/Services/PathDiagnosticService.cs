using System.Net;
using System.Net.NetworkInformation;
using SockTuner.Models;

namespace SockTuner.Services;

public sealed class PathDiagnosticService
{
    private readonly Func<string, int, bool, int, TimeSpan, CancellationToken, Task<PathPingResult>> _ping;

    public PathDiagnosticService() : this(PingAsync) { }
    internal PathDiagnosticService(Func<string, int, bool, int, TimeSpan, CancellationToken, Task<PathPingResult>> ping) => _ping = ping;

    public async Task<(IReadOnlyList<RouteSample> Routes, string? FirstPublicBoundary, PathMtuResult Mtu)> RunAsync(
        string target,
        CancellationToken cancellationToken)
    {
        var routes = new List<RouteSample>(3);
        for (var sample = 0; sample < 3; sample++)
        {
            routes.Add(await TraceAsync(target, cancellationToken));
            if (sample < 2) await Task.Delay(200, cancellationToken);
        }

        var firstPublic = routes.SelectMany(route => route.Hops)
            .Where(hop => hop.State == IPStatus.TtlExpired.ToString())
            .Select(hop => hop.Address)
            .FirstOrDefault(IsPublicAddress);
        return (routes, firstPublic, await DiscoverMtuAsync(target, cancellationToken));
    }

    internal async Task<RouteSample> TraceAsync(string target, CancellationToken cancellationToken)
    {
        var hops = new List<RouteHop>();
        try
        {
            for (var ttl = 1; ttl <= 8; ttl++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reply = await _ping(target, 32, false, ttl, TimeSpan.FromSeconds(1), cancellationToken);
                if (reply.Address is not null)
                    hops.Add(new(ttl, reply.Address, reply.RoundTripTimeMs, reply.Status.ToString()));
                if (reply.Status == IPStatus.Success) break;
            }
            return new(DateTimeOffset.Now, hops, null);
        }
        catch (PingException exception)
        {
            return new(DateTimeOffset.Now, hops, exception.InnerException?.Message ?? exception.Message);
        }
    }

    internal async Task<PathMtuResult> DiscoverMtuAsync(string target, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return new(PathMtuState.UnsupportedAddressFamily, null, "Path MTU discovery currently requires a resolved IPv4 target.");

        var lower = 548;
        var upper = 1472;
        try
        {
            var baseline = await _ping(target, lower, true, 64, TimeSpan.FromSeconds(1), cancellationToken);
            if (baseline.Status != IPStatus.Success)
                return new(PathMtuState.IcmpBlockedOrInconclusive, null, $"Baseline probe returned {baseline.Status}; no MTU claim made.");
            while (lower < upper)
            {
                var payload = (lower + upper + 1) / 2;
                var reply = await _ping(target, payload, true, 64, TimeSpan.FromSeconds(1), cancellationToken);
                if (reply.Status == IPStatus.Success) lower = payload;
                else if (reply.Status == IPStatus.PacketTooBig) upper = payload - 1;
                else return new(PathMtuState.IcmpBlockedOrInconclusive, null, $"Probe returned {reply.Status}; no MTU claim made.");
            }
            return new(PathMtuState.Discovered, lower + 28, $"Largest successful IPv4 ICMP packet: {lower + 28} bytes.");
        }
        catch (PingException exception)
        {
            return new(PathMtuState.Error, null, exception.InnerException?.Message ?? exception.Message);
        }
    }

    internal static bool IsPublicAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return !(bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && (bytes[0] & 0xFE) != 0xFC;
    }

    private static async Task<PathPingResult> PingAsync(
        string target, int payloadSize, bool dontFragment, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(target, timeout, new byte[payloadSize], new PingOptions(ttl, dontFragment), cancellationToken);
        return new(reply.Status, reply.Address?.ToString(), reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired ? reply.RoundtripTime : null);
    }
}

internal sealed record PathPingResult(IPStatus Status, string? Address, double? RoundTripTimeMs);
