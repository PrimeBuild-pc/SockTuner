using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace SockTuner.Services.Collection;

internal sealed record StunBinding(IPEndPoint Mapped, IPEndPoint? Other);

/// <summary>IPv4 binding subset of RFC 8489 / RFC 5780. Untrusted replies never become verdicts without validation.</summary>
internal static class StunMessage
{
    private const uint Cookie = 0x2112A442;

    internal static byte[] Request(bool changePort)
    {
        var bytes = new byte[changePort ? 28 : 20];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)(bytes.Length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), Cookie);
        RandomNumberGenerator.Fill(bytes.AsSpan(8, 12));
        if (changePort)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 0x0003);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 4);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 2);
        }
        return bytes;
    }

    internal static StunBinding? Parse(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> request)
    {
        if (request.Length < 20 || bytes.Length < 20 || bytes.Length > 4096
            || BinaryPrimitives.ReadUInt16BigEndian(bytes) != 0x0101
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) != Cookie
            || !bytes.Slice(8, 12).SequenceEqual(request.Slice(8, 12))) return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if (length % 4 != 0 || length != bytes.Length - 20) return null;

        IPEndPoint? mapped = null, xorMapped = null, other = null;
        Span<byte> ip = stackalloc byte[4];
        for (var offset = 20; offset < bytes.Length;)
        {
            if (offset + 4 > bytes.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
            var end = offset + 4 + ((size + 3) & ~3);
            if (end > bytes.Length) return null;
            var value = bytes.Slice(offset + 4, size);
            if (type is 0x0001 or 0x0020 or 0x802c)
            {
                if (value.Length != 8 || value[0] != 0 || value[1] != 1) return null;
                var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
                var address = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                if (type == 0x0020) { port ^= (ushort)(Cookie >> 16); address ^= Cookie; }
                if (port == 0) return null;
                BinaryPrimitives.WriteUInt32BigEndian(ip, address);
                var endpoint = new IPEndPoint(new IPAddress(ip), port);
                if (type == 0x0020) xorMapped = endpoint;
                else if (type == 0x0001) mapped = endpoint;
                else other = endpoint;
            }
            offset = end;
        }
        return (xorMapped ?? mapped) is { } result ? new StunBinding(result, other) : null;
    }
}
