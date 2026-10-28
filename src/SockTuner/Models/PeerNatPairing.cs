using System.Net;
using System.Security.Cryptography;
using SockTuner.Services.Collection;

namespace SockTuner.Models;

public enum PairingError { None, Malformed, Checksum, NotPublic, Expired, TooFar }

/// <summary>
/// What one side tells the other: its public IPv4 endpoint, a shared secret that only filters stray packets
/// (it is not encryption) and the UTC start of the test. Parsing is the trust boundary: a code can never
/// point SockTuner at a private, loopback or multicast address.
/// </summary>
public sealed record PairingCode(IPEndPoint Endpoint, byte[] Secret, DateTimeOffset Start)
{
    public const int SecretLength = 8;
    private const string Prefix = "STP1-";
    private static readonly TimeSpan MaximumLead = TimeSpan.FromMinutes(15);
    private const int PayloadLength = 1 + 4 + 2 + SecretLength + 8;

    public string Format()
    {
        var bytes = new byte[PayloadLength + 2];
        bytes[0] = 1;
        Endpoint.Address.GetAddressBytes().CopyTo(bytes, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(5), (ushort)Endpoint.Port);
        Secret.CopyTo(bytes, 7);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(7 + SecretLength), Start.ToUnixTimeSeconds());
        SHA256.HashData(bytes.AsSpan(0, PayloadLength))[..2].CopyTo(bytes, PayloadLength);
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static PairingCode? Parse(string? text, DateTimeOffset now, out PairingError error)
    {
        error = PairingError.Malformed;
        text = text?.Trim();
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        byte[] bytes;
        try
        {
            var base64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException) { return null; }
        if (bytes.Length != PayloadLength + 2 || bytes[0] != 1) return null;
        if (!SHA256.HashData(bytes.AsSpan(0, PayloadLength))[..2].AsSpan().SequenceEqual(bytes.AsSpan(PayloadLength)))
        {
            error = PairingError.Checksum;
            return null;
        }
        var port = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5));
        var seconds = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(7 + SecretLength));
        if (port == 0 || seconds is < 0 or > 253402300799) return null;
        var address = new IPAddress(bytes.AsSpan(1, 4));
        if (!IsPublic(address.GetAddressBytes()))
        {
            error = PairingError.NotPublic;
            return null;
        }
        var start = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (start <= now)
        {
            error = PairingError.Expired;
            return null;
        }

        // A host sets the start a few minutes ahead; anything much later is a typo that would leave the test waiting.
        if (start > now + MaximumLead)
        {
            error = PairingError.TooFar;
            return null;
        }
        error = PairingError.None;
        return new PairingCode(new IPEndPoint(address, port), bytes[7..(7 + SecretLength)], start);
    }

    private static bool IsPublic(byte[] a) => !(a[0] is 0 or 10 or 127 or >= 224
        || a[0] == 100 && a[1] is >= 64 and <= 127
        || a[0] == 169 && a[1] == 254
        || a[0] == 172 && a[1] is >= 16 and <= 31
        || a[0] == 192 && (a[1] == 168 || a[1] == 0 && a[2] == 0)
        || a[0] == 198 && a[1] is 18 or 19);
}

public enum PeerRole { Host, Guest }

public enum PeerNatFailure { None, PortUnavailable, NoStunReply, SameNetwork, PairingMismatch, MappingChanged, NetworkError }

/// <param name="ColdInbound">A datagram from the friend's IP and a never-contacted port reached this socket before this side sent anything.</param>
/// <param name="PunchInbound">The friend's datagram arrived while both sides were sending from the shared port.</param>
/// <param name="PunchSourceMatchesCode">The friend's source port matched the port in their code (their mapping is stable).</param>
public sealed record PeerNatResult(PeerRole Role, int LocalPort, PeerNatFailure Failure, bool ColdInbound = false,
    bool PunchInbound = false, bool PunchSourceMatchesCode = false, string? Detail = null);

public static class PeerNatReading
{
    /// <summary>Verdict for the local NAT only. A silent result is never reported as a clean "Strict".</summary>
    public static string Of(PeerNatResult r) => r.Failure switch
    {
        PeerNatFailure.PortUnavailable => "Local port unavailable — no verdict. Close the game or choose another port explicitly.",
        PeerNatFailure.NoStunReply => "No STUN reply — SockTuner could not learn its public endpoint, so no code can be made.",
        PeerNatFailure.SameNetwork => "Both codes show the same public IP (same home network or shared connection) — no verdict.",
        PeerNatFailure.PairingMismatch => "The pairing code does not match this session — start again.",
        PeerNatFailure.MappingChanged => "The public port changed while waiting — start again; the code is no longer valid.",
        PeerNatFailure.NetworkError => "Network test unavailable — no NAT verdict.",
        _ when r.ColdInbound => "Open-like: your friend's packets reached this port from a new address without you contacting them first. A game's own check looks for exactly this.",
        _ when r.PunchInbound => "Moderate-like: packets arrive only after you send first. Peer connections usually work; a game that checks from outside may report Moderate.",
        _ => "Nothing arrived: Strict-like NAT, or inbound UDP blocked by Windows Firewall or security software. Allow SockTuner inbound and retry; this result alone cannot tell the two apart."
    };
}
