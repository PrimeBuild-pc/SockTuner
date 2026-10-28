using System.Net;
using System.Net.Sockets;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

public class PeerNatSessionTests
{
    private static readonly PeerNatTiming Fast = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(200));

    // Codes carry whole seconds: round up so the first round is never already over when the sessions start.
    private static DateTimeOffset Soon(int milliseconds) =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMilliseconds(milliseconds + 999).ToUnixTimeSeconds());

    private static UdpClient Loopback() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static IPEndPoint Local(UdpClient udp) => (IPEndPoint)udp.Client.LocalEndPoint!;
    private static PeerNatSession Session(PeerRole role, UdpClient udp, IPEndPoint? advertised = null) =>
        new(role, udp, advertised ?? Local(udp), null, Fast, TimeProvider.System, allowSameAddress: true);

    /// <summary>Stands in for a NAT's inbound filter: forwards only the rounds the scenario lets through.</summary>
    private sealed class Filter : IAsyncDisposable
    {
        private readonly UdpClient _front = Loopback();
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        public IPEndPoint Endpoint => Local(_front);

        public void Start(byte[] secret, IPEndPoint behind, Func<byte, bool> allow) => _loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await _front.ReceiveAsync(_stop.Token);
                    if (PeerPacket.TryParse(packet.Buffer, secret, out var round) && allow(round))
                        await _front.SendAsync(packet.Buffer, behind, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _front.Dispose(); _stop.Dispose();
        }
    }

    private static async Task<(PeerNatResult Host, PeerNatResult Guest)> Pair(Func<byte, bool>? hostInbound)
    {
        using var hostUdp = Loopback();
        using var guestUdp = Loopback();
        await using var filter = new Filter();
        using var host = Session(PeerRole.Host, hostUdp, hostInbound is null ? null : filter.Endpoint);
        using var guest = Session(PeerRole.Guest, guestUdp);
        var hostCode = host.CreateHostCode(Soon(300));
        if (hostInbound is not null) filter.Start(host.Secret, Local(hostUdp), hostInbound);
        var reply = guest.AcceptHostCode(hostCode);
        var results = await Task.WhenAll(host.RunAsync(reply, CancellationToken.None), guest.RunAsync(hostCode, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(10));
        return (results[0], results[1]);
    }

    [Fact]
    public async Task OpenNatsSeeColdAndPunchTraffic()
    {
        var (host, guest) = await Pair(null);
        Assert.All(new[] { host, guest }, r =>
        {
            Assert.Equal(PeerNatFailure.None, r.Failure);
            Assert.True(r.ColdInbound);
            Assert.True(r.PunchInbound);
            Assert.True(r.PunchSourceMatchesCode);
        });
    }

    [Fact]
    public async Task FilterThatDropsColdRoundsStillAllowsPunching()
    {
        var (host, guest) = await Pair(round => round == 3);
        Assert.False(host.ColdInbound);
        Assert.True(host.PunchInbound);
        Assert.True(guest.ColdInbound);
    }

    [Fact]
    public async Task SilentInboundYieldsNothingReceived()
    {
        var (host, _) = await Pair(_ => false);
        Assert.Equal(PeerNatFailure.None, host.Failure);
        Assert.False(host.ColdInbound);
        Assert.False(host.PunchInbound);
    }

    [Fact]
    public async Task ColdRoundsNeverSendFromTheListeningSocket()
    {
        using var hostUdp = Loopback();
        using var observer = Loopback();
        using var host = Session(PeerRole.Host, hostUdp);
        var hostCode = host.CreateHostCode(Soon(200));
        var reply = new PairingCode(Local(observer), hostCode.Secret, hostCode.Start);
        var seen = new List<(byte Round, int Port)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var listener = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await observer.ReceiveAsync(stop.Token);
                    if (PeerPacket.TryParse(packet.Buffer, hostCode.Secret, out var round)) seen.Add((round, packet.RemoteEndPoint.Port));
                }
            }
            catch (OperationCanceledException) { }
        });
        var result = await host.RunAsync(reply, CancellationToken.None);
        await stop.CancelAsync();
        await listener;
        var mainPort = Local(hostUdp).Port;
        Assert.False(result.ColdInbound);
        Assert.DoesNotContain(seen, s => s.Round == 1);
        Assert.Contains(seen, s => s.Round == 2 && s.Port != mainPort);
        Assert.Contains(seen, s => s.Round == 3 && s.Port == mainPort);
        Assert.DoesNotContain(seen, s => s.Round == 2 && s.Port == mainPort);
    }

    [Fact]
    public async Task ClosedPeerPortDoesNotAbortTheListener()
    {
        using var hostUdp = Loopback();
        int closedPort;
        using (var closed = Loopback()) closedPort = Local(closed).Port;
        using var host = Session(PeerRole.Host, hostUdp);
        var code = host.CreateHostCode(Soon(200));
        var result = await host.RunAsync(code with { Endpoint = new IPEndPoint(IPAddress.Loopback, closedPort) }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PeerNatFailure.None, result.Failure);
        Assert.False(result.ColdInbound);
    }

    [Fact]
    public async Task WrongSecretIsAPairingMismatchWithoutTraffic()
    {
        using var hostUdp = Loopback();
        using var host = Session(PeerRole.Host, hostUdp);
        var code = host.CreateHostCode(DateTimeOffset.UtcNow.AddSeconds(5));
        var result = await host.RunAsync(code with { Secret = new byte[8] }, CancellationToken.None);
        Assert.Equal(PeerNatFailure.PairingMismatch, result.Failure);
    }

    [Fact]
    public async Task SamePublicAddressGivesNoVerdict()
    {
        using var hostUdp = Loopback();
        using var host = new PeerNatSession(PeerRole.Host, hostUdp, Local(hostUdp), null, Fast, TimeProvider.System, allowSameAddress: false);
        var code = host.CreateHostCode(DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(PeerNatFailure.SameNetwork, (await host.RunAsync(code, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var hostUdp = Loopback();
        using var host = Session(PeerRole.Host, hostUdp);
        var code = host.CreateHostCode(DateTimeOffset.UtcNow.AddSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(code, cancel.Token));
    }
}
