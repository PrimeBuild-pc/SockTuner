using System.Net;
using System.Net.NetworkInformation;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Collection layer: samples every hop on a path repeatedly and reports per-hop quality, the way
/// mtr does. It measures and classifies addresses; it draws no conclusions — that is diagnosis.
/// </summary>
public sealed class RouteQualityProbe
{
    public const int DefaultRounds = 5;
    public const int DefaultMaximumHops = 20;

    private readonly Func<string, int, bool, int, TimeSpan, CancellationToken, Task<PathPingResult>> _ping;

    public RouteQualityProbe() : this(PathDiagnosticService.PingForRouteQualityAsync) { }

    internal RouteQualityProbe(
        Func<string, int, bool, int, TimeSpan, CancellationToken, Task<PathPingResult>> ping) => _ping = ping;

    public async Task<RoutePathDiagnostic> RunAsync(
        string target,
        int rounds,
        int maximumHops,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rounds, 50);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHops, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumHops, 64);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startedAt = DateTimeOffset.Now;
        var accumulators = new Dictionary<int, HopAccumulator>();
        var reachedTarget = false;

        try
        {
            for (var round = 0; round < rounds; round++)
            {
                for (var ttl = 1; ttl <= maximumHops; ttl++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var timestamp = DateTimeOffset.Now;
                    var reply = await _ping(target, 32, false, ttl, timeout, cancellationToken);
                    var accumulator = accumulators.TryGetValue(ttl, out var existing)
                        ? existing
                        : accumulators[ttl] = new HopAccumulator(ttl);
                    accumulator.Add(timestamp, reply);

                    if (reply.Status == IPStatus.Success)
                    {
                        reachedTarget = true;
                        break;
                    }
                }
            }
        }
        catch (PingException exception)
        {
            return new RoutePathDiagnostic(
                target, startedAt, rounds, Build(accumulators), reachedTarget,
                exception.InnerException?.Message ?? exception.Message,
                DiagnosticFailureKind.LocalApiFailure);
        }

        var hops = Build(accumulators);
        return hops.Count == 0 || hops.All(hop => !hop.Responded)
            ? new RoutePathDiagnostic(
                target, startedAt, rounds, hops, reachedTarget,
                "No hop replied; ICMP is likely blocked on this path.",
                DiagnosticFailureKind.RouteFailure)
            : new RoutePathDiagnostic(target, startedAt, rounds, hops, reachedTarget, null);
    }

    private static IReadOnlyList<HopMeasurement> Build(Dictionary<int, HopAccumulator> accumulators) =>
        accumulators.Values.OrderBy(item => item.TimeToLive).Select(item => item.ToMeasurement()).ToArray();

    internal static HopAddressKind ClassifyAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return HopAddressKind.Unknown;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // RFC6598 shared address space: the ISP is carrier-grade NAT-ing this connection.
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            {
                return HopAddressKind.CarrierGrade;
            }

            if (bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168))
            {
                return HopAddressKind.Private;
            }

            return HopAddressKind.Public;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC
            ? HopAddressKind.Private
            : HopAddressKind.Public;
    }

    private sealed class HopAccumulator(int timeToLive)
    {
        private readonly List<ProbeSample> _samples = [];
        private readonly Dictionary<string, int> _addressCounts = new(StringComparer.OrdinalIgnoreCase);

        public int TimeToLive { get; } = timeToLive;
        private int Observed { get; set; }
        private int Responded { get; set; }

        public void Add(DateTimeOffset timestamp, PathPingResult reply)
        {
            Observed++;
            if (reply.Address is { Length: > 0 })
            {
                _addressCounts[reply.Address] = _addressCounts.GetValueOrDefault(reply.Address) + 1;
            }

            if (reply.RoundTripTimeMs is { } rtt && reply.Address is not null)
            {
                Responded++;
                _samples.Add(new ProbeSample(timestamp, rtt));
            }
            else
            {
                _samples.Add(new ProbeSample(
                    timestamp, null, reply.Status.ToString(), NetworkDiagnosticService.ClassifyPingStatus(reply.Status)));
            }
        }

        public HopMeasurement ToMeasurement()
        {
            var ranked = _addressCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToArray();
            var primary = ranked.Length == 0 ? "*" : ranked[0].Key;
            return new HopMeasurement(
                TimeToLive,
                primary,
                ClassifyAddress(primary),
                ProbeStatistics.Calculate($"Hop {TimeToLive}", primary, _samples),
                Observed,
                Responded,
                ranked.Skip(1).Select(pair => pair.Key).ToArray());
        }
    }
}
