using System.Net;
using System.Net.Sockets;

namespace SockTuner.Services.Collection;

/// <summary>A bounded authenticated echo service. Nothing is sent until a key holder contacts it.</summary>
public sealed class UdpQualityResponder : IDisposable
{
    private sealed class Session(IPEndPoint peer, int count)
    {
        internal readonly IPEndPoint Peer = peer;
        internal readonly bool[] Seen = new bool[count];
        internal readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddSeconds(75);
        internal byte[]? Summary;
        internal int RepliesLeft = count * 2 + 10;
    }

    private readonly UdpClient _udp;
    private readonly byte[] _key;
    private readonly Dictionary<Guid, Session> _sessions = [];
    public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public UdpQualityResponder(IPEndPoint endpoint, string key)
    {
        _key = UdpQualityProtocol.ParseKey(key);
        _udp = new UdpClient(endpoint);
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(token); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            if (!UdpQualityProtocol.TryRead(packet.Buffer, _key, out var type, out var id, out var sequence, out var value)) continue;
            foreach (var expired in _sessions.Where(s => s.Value.Expires < DateTimeOffset.UtcNow).Select(s => s.Key).ToArray())
                _sessions.Remove(expired);
            if (!_sessions.TryGetValue(id, out var session))
            {
                if (type != UdpQualityMessage.Hello || sequence is < 1 or > UdpQualityProtocol.MaximumPackets
                    || value is < 1 or > 50 || sequence > value * 60 || _sessions.Count >= 8) continue;
                _sessions[id] = session = new(packet.RemoteEndPoint, sequence);
            }
            if (!session.Peer.Equals(packet.RemoteEndPoint) || session.RepliesLeft <= 0) continue;
            byte[]? response = null;
            switch (type)
            {
                case UdpQualityMessage.Hello when sequence == session.Seen.Length:
                    response = UdpQualityProtocol.Create(UdpQualityMessage.Ready, id, _key, sequence);
                    break;
                case UdpQualityMessage.Probe when sequence >= 0 && sequence < session.Seen.Length && session.Summary is null:
                    session.Seen[sequence] = true;
                    response = UdpQualityProtocol.Create(UdpQualityMessage.Echo, id, _key, sequence, size: packet.Buffer.Length);
                    break;
                case UdpQualityMessage.SummaryRequest when packet.Buffer.Length == 1024:
                    response = session.Summary ??= UdpQualityProtocol.Create(UdpQualityMessage.Summary, id, _key,
                        session.Seen.Length, session.Seen.Count(x => x), 256, UdpQualityProtocol.Bitmap(session.Seen));
                    break;
            }
            // Every reply fits inside the request. Even an authenticated replay cannot amplify bytes.
            if (response is not null && response.Length <= packet.Buffer.Length)
            {
                session.RepliesLeft--;
                try { await _udp.SendAsync(response, packet.RemoteEndPoint, token); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }
            }
        }
    }

    public void Dispose()
    {
        _udp.Dispose();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_key);
    }
}
