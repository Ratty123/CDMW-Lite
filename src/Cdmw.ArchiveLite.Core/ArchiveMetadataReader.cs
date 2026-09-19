using System.Buffers.Binary;
using System.Text;
namespace Cdmw.ArchiveLite.Core;

internal static class ArchiveMetadataReader
{
    internal static uint HashLittle(ReadOnlySpan<byte> data, uint initialValue)
    {
        unchecked
        {
            var a = 0xDEADBEEFu + (uint)data.Length + initialValue;
            var b = a;
            var c = a;
            var offset = 0;
            var remaining = data.Length;
            while (remaining > 12)
            {
                a += ReadPartialUInt32(data, offset);
                b += ReadPartialUInt32(data, offset + 4);
                c += ReadPartialUInt32(data, offset + 8);
                Mix(ref a, ref b, ref c);
                offset += 12;
                remaining -= 12;
            }
            if (remaining >= 9) c += ReadPartialUInt32(data, offset + 8);
            if (remaining >= 5) b += ReadPartialUInt32(data, offset + 4);
            if (remaining >= 1) a += ReadPartialUInt32(data, offset);
            if (remaining == 0) return c;
            Final(ref a, ref b, ref c);
            return c;
        }
    }

    private static uint ReadPartialUInt32(ReadOnlySpan<byte> data, int offset)
    {
        uint value = 0;
        for (var index = 0; index < 4 && offset + index < data.Length; index++)
        {
            value |= (uint)data[offset + index] << (8 * index);
        }
        return value;
    }

    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        unchecked
        {
            a -= c; a ^= RotateLeft(c, 4); c += b;
            b -= a; b ^= RotateLeft(a, 6); a += c;
            c -= b; c ^= RotateLeft(b, 8); b += a;
            a -= c; a ^= RotateLeft(c, 16); c += b;
            b -= a; b ^= RotateLeft(a, 19); a += c;
            c -= b; c ^= RotateLeft(b, 4); b += a;
        }
    }

    private static void Final(ref uint a, ref uint b, ref uint c)
    {
        unchecked
        {
            c = (c ^ b) - RotateLeft(b, 14);
            a = (a ^ c) - RotateLeft(c, 11);
            b = (b ^ a) - RotateLeft(a, 25);
            c = (c ^ b) - RotateLeft(b, 16);
            a = (a ^ c) - RotateLeft(c, 4);
            b = (b ^ a) - RotateLeft(a, 14);
            c = (c ^ b) - RotateLeft(b, 24);
        }
    }

    private static uint RotateLeft(uint value, int shift) => (value << shift) | (value >> (32 - shift));

    private const uint NameHashSeed = 0xC5EDE;
    private static readonly int[] PabghCountWidths = [1, 2, 4];
    private static readonly int[] PabghKeyWidths = [1, 2, 4, 8, 12];
    internal sealed record PabghDirectoryRow(byte[] Key, uint Offset);
    internal static Dictionary<string, string> ParseLocalization(
        byte[] data,
        IReadOnlySet<string> wantedIds,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        while (position + 8 < data.Length)
        {
            if ((position & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var idLength = ReadUInt32(data, position);
            if (idLength is >= 6 and <= 20 && idLength <= int.MaxValue &&
                position + 4L + idLength <= data.Length)
            {
                var idBytes = data.AsSpan(position + 4, (int)idLength);
                if (IsAsciiDigits(idBytes))
                {
                    var id = Encoding.ASCII.GetString(idBytes);
                    var textPosition = checked(position + 4 + (int)idLength);
                    if (textPosition + 4 < data.Length)
                    {
                        var textLength = ReadUInt32(data, textPosition);
                        if (textLength is > 0 and < 50_000 && textLength <= int.MaxValue &&
                            textPosition + 4L + textLength <= data.Length)
                        {
                            if (wantedIds.Contains(id))
                            {
                                rows[id] = Encoding.UTF8.GetString(
                                    data,
                                    textPosition + 4,
                                    (int)textLength);
                            }
                            position = checked(textPosition + 4 + (int)textLength);
                            continue;
                        }
                    }
                }
            }
            position++;
        }
        return rows;
    }
    internal static List<PabghDirectoryRow>? ResolvePabghDirectory(byte[] header, byte[] payload)
    {
        foreach (var countWidth in PabghCountWidths)
        {
            if (header.Length < countWidth)
            {
                continue;
            }
            long count = 0;
            for (var i = 0; i < countWidth; i++)
            {
                count |= (long)header[i] << (8 * i);
            }
            if (count <= 0)
            {
                continue;
            }
            var remainder = header.Length - countWidth;
            if (remainder % count != 0)
            {
                continue;
            }
            var rowSize = remainder / count;
            if (rowSize < 5)
            {
                continue;
            }
            var keyWidth = (int)(rowSize - 4);
            if (Array.IndexOf(PabghKeyWidths, keyWidth) < 0)
            {
                continue;
            }
            var rows = new List<PabghDirectoryRow>((int)count);
            var cursor = (long)countWidth;
            long previous = -1;
            var usable = true;
            for (long index = 0; index < count; index++)
            {
                var key = new byte[keyWidth];
                Array.Copy(header, checked((int)cursor), key, 0, keyWidth);
                var offset = ReadUInt32(header, checked((int)(cursor + keyWidth)));
                if (offset <= previous)
                {
                    usable = false;
                    break;
                }
                previous = offset;
                rows.Add(new PabghDirectoryRow(key, offset));
                cursor += rowSize;
            }
            if (!usable || rows.Count == 0 || rows[0].Offset != 0 || rows[^1].Offset >= payload.Length)
            {
                continue;
            }
            var inlineKeysMatch = true;
            foreach (var row in rows)
            {
                if (row.Offset + (long)keyWidth > payload.Length || !payload.AsSpan(checked((int)row.Offset), keyWidth).SequenceEqual(row.Key))
                {
                    inlineKeysMatch = false;
                    break;
                }
            }
            if (inlineKeysMatch)
            {
                return rows;
            }
        }
        return null;
    }
    internal static string ReadItemInfoRowInternalName(byte[] data, int rowStart, int rowEnd)
    {
        if (rowStart + 8 > rowEnd)
        {
            return string.Empty;
        }
        var length = ReadUInt32(data, rowStart + 4);
        if (length is 0 or > 256 || rowStart + 8 + length > rowEnd)
        {
            return string.Empty;
        }
        var raw = data.AsSpan(rowStart + 8, checked((int)length));
        var nul = raw.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul < 0 ? raw : raw[..nul]);
    }
    private static bool IsAsciiDigits(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }
        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));

    internal static uint ModelNameHash(string stem) => HashLittle(Encoding.UTF8.GetBytes(stem), NameHashSeed);
}
