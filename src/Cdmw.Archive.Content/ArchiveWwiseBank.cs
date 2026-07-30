using System.Buffers.Binary;
using System.Text;

namespace Cdmw.Archive.Content;

/// <summary>One embedded sound in a Wwise sound bank, as listed by the bank's DIDX table.</summary>
/// <param name="Ordinal">The one-based position in DIDX, which is also the decoder's subsong number.</param>
/// <param name="SourceId">The Wwise source id, which is the name a streamed <c>.wem</c> would carry.</param>
public sealed record ArchiveWwiseEmbeddedMedia(int Ordinal, uint SourceId, long Offset, long Size);

internal sealed record ArchiveWwiseChunk(string Id, int Start, int Length);

/// <summary>
/// Reads the media table of a Wwise sound bank. A bank is a flat sequence of RIFF-style chunks;
/// DIDX lists the sounds embedded in the following DATA chunk, and banks that only carry events
/// (HIRC) have no DIDX at all because their audio streams from separate <c>.wem</c> files.
/// </summary>
public static class ArchiveWwiseBank
{
    private const int DirectoryRecordSize = 12;
    private const int MaximumEmbeddedMedia = 8192;

    public static bool IsSoundBank(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..4].SequenceEqual("BKHD"u8);

    /// <summary>
    /// Lists the sounds embedded in <paramref name="data"/> in decoder subsong order, or nothing when
    /// the bank carries no audio of its own.
    /// </summary>
    public static IReadOnlyList<ArchiveWwiseEmbeddedMedia> ReadEmbeddedMedia(ReadOnlySpan<byte> data)
    {
        foreach (var chunk in ReadChunks(data, out _))
        {
            if (!string.Equals(chunk.Id, "DIDX", StringComparison.Ordinal))
            {
                continue;
            }
            var count = Math.Min(chunk.Length / DirectoryRecordSize, MaximumEmbeddedMedia);
            var media = new List<ArchiveWwiseEmbeddedMedia>(count);
            for (var record = 0; record < count; record++)
            {
                var start = chunk.Start + (record * DirectoryRecordSize);
                media.Add(new ArchiveWwiseEmbeddedMedia(
                    record + 1,
                    BinaryPrimitives.ReadUInt32LittleEndian(data[start..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(start + 4)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(start + 8)..])));
            }
            return media;
        }
        return [];
    }

    /// <summary>
    /// Walks the chunk envelope, stopping at the first chunk that is truncated or is not a plain
    /// four-letter identifier so that a damaged bank cannot drive a reader off the payload.
    /// <paramref name="consumed"/> reports how far the walk stayed inside well-formed chunks.
    /// </summary>
    internal static IReadOnlyList<ArchiveWwiseChunk> ReadChunks(ReadOnlySpan<byte> data, out int consumed)
    {
        var chunks = new List<ArchiveWwiseChunk>();
        var offset = 0;
        while (offset <= data.Length - 8 && chunks.Count < 128)
        {
            var id = data.Slice(offset, 4);
            if (!IsChunkIdentifier(id))
            {
                break;
            }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var start = offset + 8;
            if (size > (uint)(data.Length - start))
            {
                break;
            }
            chunks.Add(new ArchiveWwiseChunk(Encoding.ASCII.GetString(id), start, (int)size));
            offset = start + (int)size;
        }
        consumed = offset;
        return chunks;
    }

    private static bool IsChunkIdentifier(ReadOnlySpan<byte> id)
    {
        foreach (var character in id)
        {
            if (character is (< (byte)'A' or > (byte)'Z') and (< (byte)'0' or > (byte)'9'))
            {
                return false;
            }
        }
        return true;
    }
}
