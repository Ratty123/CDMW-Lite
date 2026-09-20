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
    internal static byte[] LocalizationPayload(byte[] data)
    {
        if (!data.AsSpan().StartsWith("paloc"u8)) return data;
        const int headerSize = 512;
        if (data.Length < headerSize) throw new InvalidDataException("PALOC container header is truncated.");
        if (ReadUInt32(data, 5) != 0) throw new InvalidDataException("Unsupported PALOC container version.");
        if (ReadUInt32(data, 9) != data.Length - headerSize)
            throw new InvalidDataException("PALOC compressed size does not match its bytes.");
        var size = ReadUInt32(data, 13);
        if (size is < 4 or > 256 * 1024 * 1024)
            throw new InvalidDataException("PALOC uncompressed size is out of bounds.");
        var output = new byte[(int)size];
        var inputAt = headerSize;
        var outputAt = 0;
        int Length(int length)
        {
            if (length != 15) return length;
            byte extra;
            do
            {
                if (inputAt >= data.Length) throw new InvalidDataException("PALOC LZ4 length is truncated.");
                extra = data[inputAt++];
                if (length > output.Length - extra) throw new InvalidDataException("PALOC LZ4 run exceeds its output size.");
                length += extra;
            } while (extra == 255);
            return length;
        }
        while (inputAt < data.Length)
        {
            var control = data[inputAt++];
            var literals = Length(control >> 4);
            if (literals > data.Length - inputAt || literals > output.Length - outputAt)
                throw new InvalidDataException("PALOC LZ4 literal run is out of bounds.");
            data.AsSpan(inputAt, literals).CopyTo(output.AsSpan(outputAt));
            inputAt += literals; outputAt += literals;
            if (inputAt == data.Length) break;
            if (data.Length - inputAt < 2) throw new InvalidDataException("PALOC LZ4 match is truncated.");
            var offset = data[inputAt] | data[inputAt + 1] << 8;
            inputAt += 2;
            if (offset == 0 || offset > outputAt) throw new InvalidDataException("PALOC LZ4 match offset is invalid.");
            var match = checked(Length(control & 15) + 4);
            if (match > output.Length - outputAt) throw new InvalidDataException("PALOC LZ4 match run is out of bounds.");
            for (var i = 0; i < match; i++, outputAt++) output[outputAt] = output[outputAt - offset];
        }
        if (outputAt != output.Length) throw new InvalidDataException("PALOC LZ4 decoded size does not match its header.");
        return output;
    }

    internal static Dictionary<string, string> ParseLocalization(
        byte[] data,
        IReadOnlySet<string> wantedIds,
        CancellationToken cancellationToken)
    {
        data = LocalizationPayload(data);
        if (data.Length < 4) throw new InvalidDataException("PALOC record count is missing.");
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var utf8 = new UTF8Encoding(false, true);
        var limit = data.Length - 4;
        var declared = ReadUInt32(data, limit);
        var position = 0;
        var count = 0;
        while (position < limit)
        {
            if ((count & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (limit - position < 12) throw new InvalidDataException("PALOC record header is truncated.");
            var idLength = ReadUInt32(data, position + 8);
            position += 12;
            if (idLength > limit - position || limit - position - idLength < 4)
                throw new InvalidDataException("PALOC key is truncated.");
            var id = utf8.GetString(data, position, (int)idLength);
            position += (int)idLength;
            var textLength = ReadUInt32(data, position);
            position += 4;
            if (textLength > limit - position) throw new InvalidDataException("PALOC text is truncated.");
            var text = utf8.GetString(data, position, (int)textLength);
            if (wantedIds.Contains(id)) rows[id] = text;
            position += (int)textLength;
            count++;
        }
        if (count != declared) throw new InvalidDataException("PALOC record count does not match its footer.");
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
    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));

    internal static uint ModelNameHash(string stem) => HashLittle(Encoding.UTF8.GetBytes(stem), NameHashSeed);
}
