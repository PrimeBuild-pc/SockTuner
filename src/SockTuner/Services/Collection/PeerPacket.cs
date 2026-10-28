using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SockTuner.Services.Collection;

/// <summary>
/// Fixed 16-byte datagram: magic, version, round, counter, then a token derived from the shared secret.
/// Anything that does not carry the token is ignored, so strangers cannot influence a verdict.
/// </summary>
internal static class PeerPacket
{
    internal const int Length = 16;
    private static readonly byte[] Magic = "STPN"u8.ToArray();

    internal static byte[] Create(byte[] secret, byte round, ushort counter)
    {
        var bytes = new byte[Length];
        Magic.CopyTo(bytes, 0);
        bytes[4] = 1;
        bytes[5] = round;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), counter);
        Token(secret, bytes).CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    internal static bool TryParse(ReadOnlySpan<byte> bytes, byte[] secret, out byte round)
    {
        round = 0;
        if (bytes.Length != Length || !bytes[..4].SequenceEqual(Magic) || bytes[4] != 1 || bytes[5] is < 1 or > 3
            || !CryptographicOperations.FixedTimeEquals(bytes[8..], Token(secret, bytes))) return false;
        round = bytes[5];
        return true;
    }

    private static byte[] Token(byte[] secret, ReadOnlySpan<byte> header) => HMACSHA256.HashData(secret, header[..8])[..8];
}
