using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SockTuner.Services.Collection;

internal enum UdpQualityMessage : byte { Hello = 1, Ready, Probe, Echo, SummaryRequest, Summary }

internal static class UdpQualityProtocol
{
    internal const int HeaderBytes = 32;
    internal const int TagBytes = 32;
    internal const int MaximumPackets = 1200;

    internal static byte[] ParseKey(string text)
    {
        if (text.Length != 64 || !text.All(Uri.IsHexDigit))
            throw new ArgumentException("The responder key must contain 64 hexadecimal characters.", nameof(text));
        return Convert.FromHexString(text);
    }

    internal static byte[] Create(UdpQualityMessage type, Guid session, ReadOnlySpan<byte> key,
        int sequence = 0, int value = 0, int size = 96, ReadOnlySpan<byte> body = default)
    {
        if (key.Length != 32 || size is < 96 or > 1200 || body.Length > size - HeaderBytes - TagBytes)
            throw new ArgumentOutOfRangeException(nameof(size));
        var data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x31515453); // STQ1
        data[4] = (byte)type;
        session.TryWriteBytes(data.AsSpan(5, 16));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(21), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(25), value);
        body.CopyTo(data.AsSpan(HeaderBytes));
        HMACSHA256.HashData(key, data.AsSpan(0, size - TagBytes), data.AsSpan(size - TagBytes));
        return data;
    }

    internal static bool TryRead(byte[] data, ReadOnlySpan<byte> key,
        out UdpQualityMessage type, out Guid session, out int sequence, out int value)
    {
        type = default; session = default; sequence = value = 0;
        if (key.Length != 32 || data.Length is < 96 or > 1200
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x31515453
            || data[4] is < 1 or > 6) return false;
        Span<byte> expected = stackalloc byte[TagBytes];
        HMACSHA256.HashData(key, data.AsSpan(0, data.Length - TagBytes), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, data.AsSpan(data.Length - TagBytes))) return false;
        type = (UdpQualityMessage)data[4];
        session = new Guid(data.AsSpan(5, 16));
        sequence = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(21));
        value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(25));
        return true;
    }

    internal static byte[] Bitmap(IReadOnlyList<bool> seen)
    {
        var bitmap = new byte[(seen.Count + 7) / 8];
        for (var i = 0; i < seen.Count; i++) if (seen[i]) bitmap[i / 8] |= (byte)(1 << (i % 8));
        return bitmap;
    }

    internal static bool[]? ReadBitmap(byte[] data, int count, int reported)
    {
        if (count is < 1 or > MaximumPackets || data.Length < HeaderBytes + (count + 7) / 8 + TagBytes) return null;
        var seen = Enumerable.Range(0, count).Select(i => (data[HeaderBytes + i / 8] & (1 << (i % 8))) != 0).ToArray();
        return seen.Count(x => x) == reported ? seen : null;
    }
}
