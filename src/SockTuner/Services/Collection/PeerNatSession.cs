using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

public sealed record PeerNatTiming(TimeSpan Round, TimeSpan Gap, TimeSpan SendInterval, TimeSpan KeepAlive)
{
    public static PeerNatTiming Default { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(15));
}

public sealed record PeerNatOpen(PeerNatSession? Session, PeerNatFailure Failure, string? Detail = null);

/// <summary>
/// Two SockTuner instances take turns sending to each other so each side learns whether its own NAT accepts
/// unsolicited traffic from a new IP. Round 1: guest to host. Round 2: host to guest. Round 3: both, from the shared port.
/// In rounds 1 and 2 the sender uses a throw-away socket, so the listening side's own socket has never
/// sent to that peer and the arrival is a genuinely cold inbound packet. Fixed 16-byte packets, rate limited,
/// only ever to the endpoint in the validated code.
/// </summary>
public sealed class PeerNatSession : IDisposable
{
    private readonly PeerRole _role;
    private readonly UdpClient _udp;
    private readonly IPEndPoint? _stun;
    private readonly PeerNatTiming _timing;
    private readonly TimeProvider _time;
    private readonly bool _allowSameAddress;
    private readonly NatDiagnosticProbe _probe = new();
    private byte[] _secret = [];
    private DateTimeOffset _start;

    internal PeerNatSession(PeerRole role, UdpClient udp, IPEndPoint publicEndpoint, IPEndPoint? stun, PeerNatTiming timing,
        TimeProvider time, bool allowSameAddress)
    {
        (_role, _udp, Endpoint, _stun, _timing, _time, _allowSameAddress) = (role, udp, publicEndpoint, stun, timing, time, allowSameAddress);
    }

    public IPEndPoint Endpoint { get; }
    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    internal byte[] Secret => _secret;

    /// <summary>Binds the port exclusively and learns its public mapping, keeping the socket for the whole session.</summary>
    public static async Task<PeerNatOpen> OpenAsync(PeerRole role, int localPort, CancellationToken token)
    {
        if (localPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        var udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
        try
        {
            try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort)); }
            catch (SocketException exception) { udp.Dispose(); return new(null, PeerNatFailure.PortUnavailable, exception.Message); }
            var addresses = await Dns.GetHostAddressesAsync(NatDiagnosticProbe.BehaviorServer, AddressFamily.InterNetwork, token);
            if (addresses.Length == 0) { udp.Dispose(); return new(null, PeerNatFailure.NetworkError, "No IPv4 address for the STUN server."); }
            var stun = new IPEndPoint(addresses[0], NatDiagnosticProbe.BehaviorPort);
            var binding = await new NatDiagnosticProbe().ExchangeAsync(udp, stun, stun, false, token);
            if (binding is null) { udp.Dispose(); return new(null, PeerNatFailure.NoStunReply); }
            return new(new PeerNatSession(role, udp, binding.Mapped, stun, PeerNatTiming.Default, TimeProvider.System, false),
                PeerNatFailure.None);
        }
        catch (SocketException exception) { udp.Dispose(); return new(null, PeerNatFailure.NetworkError, exception.Message); }
        catch { udp.Dispose(); throw; }
    }

    public PairingCode CreateHostCode(DateTimeOffset start)
    {
        _secret = RandomNumberGenerator.GetBytes(PairingCode.SecretLength);
        // Codes carry whole seconds; keep the same value here so both sides compare equal.
        _start = DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds());
        return new(Endpoint, _secret, _start);
    }

    /// <summary>Guest adopts the host's secret and start; the returned code is what the host must enter.</summary>
    public PairingCode AcceptHostCode(PairingCode host)
    {
        (_secret, _start) = (host.Secret, host.Start);
        return new(Endpoint, _secret, _start);
    }

    public async Task<PeerNatResult> RunAsync(PairingCode peer, CancellationToken token)
    {
        PeerNatResult Fail(PeerNatFailure failure, string? detail = null) => new(_role, LocalPort, failure, Detail: detail);
        if (_secret.Length == 0 || !peer.Secret.AsSpan().SequenceEqual(_secret) || peer.Start != _start)
            return Fail(PeerNatFailure.PairingMismatch);
        if (!_allowSameAddress && peer.Endpoint.Address.Equals(Endpoint.Address)) return Fail(PeerNatFailure.SameNetwork);

        // Keep the NAT mapping alive until the start, and stop if it moved: the code would then point nowhere.
        for (var remaining = _start - _time.GetUtcNow(); remaining > TimeSpan.Zero; remaining = _start - _time.GetUtcNow())
        {
            await Task.Delay(remaining < _timing.KeepAlive ? remaining : _timing.KeepAlive, _time, token);
            if (_stun is null || _start - _time.GetUtcNow() <= TimeSpan.Zero) continue;
            var binding = await _probe.ExchangeAsync(_udp, _stun, _stun, false, token);
            if (binding is not null && !binding.Mapped.Equals(Endpoint)) return Fail(PeerNatFailure.MappingChanged);
        }

        var step = _timing.Round + _timing.Gap;
        var coldRound = _role == PeerRole.Host ? 1 : 2;
        var sendRound = _role == PeerRole.Host ? 2 : 1;
        using var end = CancellationTokenSource.CreateLinkedTokenSource(token);
        end.CancelAfter(step * 3 + _timing.Gap);
        var seen = new bool[4];
        var punchPortMatches = false;
        var receiver = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult packet;
                    // Windows reports an ICMP "port unreachable" for our own send as a receive error; it is not a verdict.
                    try { packet = await _udp.ReceiveAsync(end.Token); }
                    catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    // Classified by the round inside the packet, so a skewed clock cannot turn a cold packet into a late one.
                    if (!packet.RemoteEndPoint.Address.Equals(peer.Endpoint.Address)
                        || !PeerPacket.TryParse(packet.Buffer, _secret, out var round)) continue;
                    seen[round] = true;
                    if (round == 3 && packet.RemoteEndPoint.Port == peer.Endpoint.Port) punchPortMatches = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { } // the sender path reports a broken network; the receiver just stops
        });
        try
        {
            using var throwaway = new UdpClient(AddressFamily.InterNetwork);
            await SendRoundAsync(sendRound, throwaway, peer.Endpoint, _start + step * (sendRound - 1), end.Token);
            await SendRoundAsync(3, _udp, peer.Endpoint, _start + step * 2, end.Token);
            await Task.Delay(_timing.Gap, _time, end.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (SocketException exception)
        {
            await end.CancelAsync();
            await receiver;
            return Fail(PeerNatFailure.NetworkError, exception.Message);
        }
        await end.CancelAsync();
        await receiver;
        token.ThrowIfCancellationRequested();
        return new(_role, LocalPort, PeerNatFailure.None, seen[coldRound], seen[3], punchPortMatches);
    }

    private async Task SendRoundAsync(int round, UdpClient from, IPEndPoint to, DateTimeOffset begin, CancellationToken token)
    {
        var wait = begin - _time.GetUtcNow();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, token);
        var until = begin + _timing.Round;
        for (ushort counter = 0; _time.GetUtcNow() < until; counter++)
        {
            await from.SendAsync(PeerPacket.Create(_secret, (byte)round, counter), to, token);
            await Task.Delay(_timing.SendInterval, _time, token);
        }
    }

    public void Dispose() => _udp.Dispose();
}
