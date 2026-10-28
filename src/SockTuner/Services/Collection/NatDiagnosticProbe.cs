using System.Net;
using System.Net.Sockets;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// One exclusive local UDP socket, never a game server scan or a firewall/router write.
/// Observations apply to this process and IPv4 path, not the game's executable or console NAT label.
/// </summary>
public sealed class NatDiagnosticProbe
{
    public const string BehaviorServer = "stun.nextcloud.com";
    public const int BehaviorPort = 443;
    public const string MappingServer = "stun.cloudflare.com";
    public const int MappingPort = 3478;
    public const string ThirdServer = "stun.l.google.com";
    public const int ThirdPort = 19302;
    private readonly TimeSpan _timeout;

    public NatDiagnosticProbe() : this(TimeSpan.FromSeconds(2)) { }
    internal NatDiagnosticProbe(TimeSpan timeout) => _timeout = timeout;

    public async Task<NatDiagnosticResult> RunAsync(int localPort, CancellationToken cancellationToken)
    {
        ValidatePort(localPort);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            var primary = await ResolveAsync(BehaviorServer, BehaviorPort, deadline.Token);
            var secondary = await ResolveAsync(MappingServer, MappingPort, deadline.Token);
            var comparisons = new List<IPEndPoint> { secondary };
            // A third, independent operator; if it is unreachable the two-destination comparison still stands.
            try { comparisons.Add(await ResolveAsync(ThirdServer, ThirdPort, deadline.Token)); }
            catch (SocketException) { }
            return await RunAsync(localPort, primary, comparisons, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(localPort, null, NatMappingObservation.NotMeasured, NatFilteringObservation.NotMeasured,
                NatProbeFailure.NetworkError, "The test deadline expired; no blocked-port conclusion can be drawn.");
        }
        catch (SocketException exception)
        {
            return new(localPort, null, NatMappingObservation.NotMeasured, NatFilteringObservation.NotMeasured,
                NatProbeFailure.NetworkError, exception.Message);
        }
    }

    internal Task<NatDiagnosticResult> RunAsync(int localPort, IPEndPoint primary, IPEndPoint secondary,
        CancellationToken cancellationToken) => RunAsync(localPort, primary, [secondary], cancellationToken);

    internal async Task<NatDiagnosticResult> RunAsync(int localPort, IPEndPoint primary,
        IReadOnlyList<IPEndPoint> comparisons, CancellationToken cancellationToken)
    {
        ValidatePort(localPort);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.ExclusiveAddressUse = true;
        try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort)); }
        catch (SocketException exception)
        {
            // Never fall back to a random port or share a socket owned by the running game.
            return new(localPort, null, NatMappingObservation.NotMeasured, NatFilteringObservation.NotMeasured,
                NatProbeFailure.PortUnavailable, exception.Message);
        }
        var actualPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        try
        {
            var baseline = await ExchangeAsync(udp, primary, primary, false, cancellationToken);
            if (baseline is null)
                return new(actualPort, null, NatMappingObservation.NotMeasured, NatFilteringObservation.NotMeasured,
                    NatProbeFailure.NoBaseline);

            var filtering = NatFilteringObservation.Unsupported;
            if (baseline.Other is { } other && other.Port != primary.Port)
            {
                // CHANGE-REQUEST(change-port) must reply from the same IP, OTHER-ADDRESS's port.
                // Do not contact that port until the unprimed test has finished.
                var alternate = new IPEndPoint(primary.Address, other.Port);
                var unprimed = await ExchangeAsync(udp, primary, alternate, true, cancellationToken);
                if (unprimed is not null && unprimed.Mapped.Equals(baseline.Mapped))
                    filtering = NatFilteringObservation.AlternatePortReceived;
                else
                {
                    var contact = await ExchangeAsync(udp, alternate, alternate, false, cancellationToken);
                    var control = await ExchangeAsync(udp, primary, alternate, true, cancellationToken);
                    filtering = unprimed is null && contact is not null && control is not null
                        && contact.Mapped.Equals(baseline.Mapped) && control.Mapped.Equals(baseline.Mapped)
                        ? NatFilteringObservation.AlternatePortFiltered
                        : NatFilteringObservation.Inconclusive;
                }
            }

            var seen = new List<StunBinding>();
            foreach (var destination in comparisons.Where(d => !d.Equals(primary)))
                if (await ExchangeAsync(udp, destination, destination, false, cancellationToken) is { } reply) seen.Add(reply);
            var recheck = await ExchangeAsync(udp, primary, primary, false, cancellationToken);
            var mapping = seen.Count == 0 || recheck is null || !recheck.Mapped.Equals(baseline.Mapped)
                ? NatMappingObservation.NotMeasured
                : seen.All(reply => reply.Mapped.Equals(baseline.Mapped))
                    ? NatMappingObservation.SameOnTestedDestinations
                    : NatMappingObservation.ChangedOnTestedDestinations;
            if (recheck is null || !recheck.Mapped.Equals(baseline.Mapped))
                filtering = NatFilteringObservation.Inconclusive;
            return new(actualPort, baseline.Mapped, mapping, filtering);
        }
        catch (SocketException exception)
        {
            return new(actualPort, null, NatMappingObservation.NotMeasured, NatFilteringObservation.NotMeasured,
                NatProbeFailure.NetworkError, exception.Message);
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static async Task<IPEndPoint> ResolveAsync(string hostname, int port, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(hostname, AddressFamily.InterNetwork, token);
        return new IPEndPoint(addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound), port);
    }

    internal async Task<StunBinding?> ExchangeAsync(UdpClient udp, IPEndPoint target, IPEndPoint expectedSource,
        bool changePort, CancellationToken token)
    {
        var request = StunMessage.Request(changePort);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_timeout);
            try
            {
                await udp.SendAsync(request, target, deadline.Token);
                while (true)
                {
                    UdpReceiveResult reply;
                    // Windows reports an earlier ICMP "port unreachable" as a receive error; it is not a verdict.
                    try { reply = await udp.ReceiveAsync(deadline.Token); }
                    catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    if (reply.RemoteEndPoint.Equals(expectedSource)
                        && StunMessage.Parse(reply.Buffer, request) is { } binding) return binding;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }
}
