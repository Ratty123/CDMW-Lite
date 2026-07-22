using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

internal sealed class ArchiveExtensionIndex : IDisposable
{
    private const int Version = 1;
    private const int HeaderSize = 64;
    private const int RecordSize = 32;
    private const int WriteBufferSize = 1024 * 1024;
    private const long MaximumExtensionRecords = 1_000_000;
    private static readonly byte[] Magic = "CDMWAEX1"u8.ToArray();
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _postingsOffset;
    private readonly long _sourceEntryCount;
    private readonly IReadOnlyDictionary<string, PostingRange> _postings;
    private readonly IReadOnlyList<ArchiveExtensionFacet> _facets;
    private readonly ConcurrentDictionary<string, long[]> _entryIdCache = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private ArchiveExtensionIndex(
        string path,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view,
        long postingsOffset,
        long sourceEntryCount,
        IReadOnlyDictionary<string, PostingRange> postings)
    {
        Path = path;
        _mapping = mapping;
        _view = view;
        _postingsOffset = postingsOffset;
        _sourceEntryCount = sourceEntryCount;
        _postings = postings;
        _facets = postings
            .Select(pair => new ArchiveExtensionFacet(
                pair.Key,
                pair.Value.Count,
                ArchiveEntryClassifier.ClassifyExtensionCategory(pair.Key)))
            .OrderBy(static item => item.Category)
            .ThenByDescending(static item => item.Count)
            .ThenBy(static item => item.Extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Path { get; }

    public static async Task<ArchiveExtensionIndex> OpenOrBuildAsync(
        ArchiveIndex source,
        string path,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Open(path, source);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or IOException)
        {
            // A missing, stale, or damaged derived lookup is always safe to rebuild.
        }

        var records = await Task.Run(
            () => BuildRecords(source, publishProgress, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await AtomicFile.WriteAsync(
            path,
            (stream, token) => WriteAsync(stream, source, records, token),
            cancellationToken).ConfigureAwait(false);
        return Open(path, source);
    }

    public IReadOnlyList<ArchiveExtensionFacet> GetFacets()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _facets;
    }

    public bool TryGetEntryIds(IReadOnlyList<string>? extensions, out IReadOnlyList<long> entryIds)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (extensions is not { Count: > 0 })
        {
            entryIds = [];
            return false;
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in extensions)
        {
            var value = candidate.Trim().ToLowerInvariant();
            if (value is "*" or ".*" or "all")
            {
                entryIds = [];
                return false;
            }
            if (!value.StartsWith('.'))
            {
                value = "." + value;
            }
            normalized.Add(value);
        }

        if (normalized.Count == 1)
        {
            var extension = normalized.Single();
            entryIds = _postings.ContainsKey(extension)
                ? _entryIdCache.GetOrAdd(extension, ReadPosting)
                : [];
            return true;
        }

        var combined = new HashSet<long>();
        foreach (var extension in normalized)
        {
            if (_postings.ContainsKey(extension))
            {
                combined.UnionWith(_entryIdCache.GetOrAdd(extension, ReadPosting));
            }
        }
        var sorted = combined.ToArray();
        Array.Sort(sorted);
        entryIds = sorted;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _view.Dispose();
            _mapping.Dispose();
        }
    }

    private long[] ReadPosting(string extension)
    {
        if (!_postings.TryGetValue(extension, out var range) || range.Count == 0)
        {
            return [];
        }
        if (range.Count > Array.MaxLength)
        {
            throw new InvalidDataException("Archive extension posting list is too large for this process.");
        }

        var result = new long[checked((int)range.Count)];
        long previous = -1;
        for (var index = 0; index < result.Length; index++)
        {
            var entryId = checked((long)_view.ReadUInt64(
                checked(_postingsOffset + (range.Start + index) * sizeof(long))));
            if (entryId < 0 || entryId >= _sourceEntryCount || entryId <= previous)
            {
                throw new InvalidDataException("Archive extension posting list is invalid.");
            }
            result[index] = entryId;
            previous = entryId;
        }
        return result;
    }

    private static ArchiveExtensionIndex Open(string path, ArchiveIndex source)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (stream.Length < HeaderSize)
            {
                throw new InvalidDataException("Archive extension index is smaller than its header.");
            }
            var mapping = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);
            try
            {
                var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                try
                {
                    var magic = new byte[Magic.Length];
                    view.ReadArray(0, magic, 0, magic.Length);
                    var version = view.ReadUInt32(8);
                    var recordSize = view.ReadUInt32(12);
                    var sourceEntryCount = checked((long)view.ReadUInt64(16));
                    var sourceFileSize = checked((long)view.ReadUInt64(24));
                    var recordCount = checked((long)view.ReadUInt64(32));
                    var recordsOffset = checked((long)view.ReadUInt64(40));
                    var stringsOffset = checked((long)view.ReadUInt64(48));
                    var postingsOffset = checked((long)view.ReadUInt64(56));
                    var recordsBytes = checked(recordCount * RecordSize);
                    var stringsSize = postingsOffset - stringsOffset;
                    var postingsBytes = stream.Length - postingsOffset;
                    if (!magic.AsSpan().SequenceEqual(Magic)
                        || version != Version
                        || recordSize != RecordSize
                        || sourceEntryCount != source.EntryCount
                        || sourceFileSize != new FileInfo(source.Path).Length
                        || recordCount < 0
                        || recordCount > MaximumExtensionRecords
                        || recordsOffset < HeaderSize
                        || stringsOffset < recordsOffset
                        || recordsBytes > stringsOffset - recordsOffset
                        || stringsSize < 0
                        || postingsOffset < stringsOffset
                        || postingsOffset > stream.Length
                        || postingsBytes < 0
                        || postingsBytes % sizeof(long) != 0)
                    {
                        throw new InvalidDataException("Archive extension index does not match its source archive index.");
                    }

                    var postingCount = postingsBytes / sizeof(long);
                    var postings = new Dictionary<string, PostingRange>(StringComparer.OrdinalIgnoreCase);
                    for (long recordId = 0; recordId < recordCount; recordId++)
                    {
                        var record = checked(recordsOffset + recordId * RecordSize);
                        var stringOffset = checked((long)view.ReadUInt64(record));
                        var postingStart = checked((long)view.ReadUInt64(record + 8));
                        var count = checked((long)view.ReadUInt64(record + 16));
                        var stringLength = checked((int)view.ReadUInt32(record + 24));
                        if (stringOffset < 0
                            || stringLength <= 0
                            || stringOffset > stringsSize
                            || stringLength > stringsSize - stringOffset
                            || postingStart < 0
                            || count <= 0
                            || postingStart > postingCount
                            || count > postingCount - postingStart)
                        {
                            throw new InvalidDataException("Archive extension index record range is invalid.");
                        }
                        var bytes = new byte[stringLength];
                        view.ReadArray(checked(stringsOffset + stringOffset), bytes, 0, stringLength);
                        var extension = Encoding.UTF8.GetString(bytes);
                        if (string.IsNullOrWhiteSpace(extension)
                            || !extension.StartsWith('.')
                            || !postings.TryAdd(extension, new PostingRange(postingStart, count)))
                        {
                            throw new InvalidDataException("Archive extension index contains an invalid extension record.");
                        }
                    }
                    return new ArchiveExtensionIndex(
                        fullPath,
                        mapping,
                        view,
                        postingsOffset,
                        sourceEntryCount,
                        postings);
                }
                catch
                {
                    view.Dispose();
                    throw;
                }
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static ExtensionBuildRecord[] BuildRecords(
        ArchiveIndex source,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        var pathBuffer = new byte[1024];
        for (long entryId = 0; entryId < source.EntryCount; entryId++)
        {
            if ((entryId & 0x1FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                publishProgress?.Invoke(new ProgressUpdate(
                    entryId,
                    source.EntryCount,
                    "extension_index_build",
                    null)).GetAwaiter().GetResult();
            }
            var length = source.GetPathByteLength(entryId);
            if (length > pathBuffer.Length)
            {
                pathBuffer = new byte[Math.Max(length, checked(pathBuffer.Length * 2))];
            }
            var read = source.ReadPathBytes(entryId, pathBuffer);
            if (read != length)
            {
                throw new InvalidDataException("Archive index returned a truncated virtual path.");
            }
            var extension = System.IO.Path.GetExtension(Encoding.UTF8.GetString(pathBuffer, 0, length)).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }
            if (!groups.TryGetValue(extension, out var entries))
            {
                entries = [];
                groups.Add(extension, entries);
            }
            entries.Add(entryId);
        }
        cancellationToken.ThrowIfCancellationRequested();
        publishProgress?.Invoke(new ProgressUpdate(
            source.EntryCount,
            source.EntryCount,
            "extension_index_build",
            "complete")).GetAwaiter().GetResult();
        return groups
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => new ExtensionBuildRecord(pair.Key, pair.Value))
            .ToArray();
    }

    private static async Task WriteAsync(
        Stream stream,
        ArchiveIndex source,
        IReadOnlyList<ExtensionBuildRecord> records,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedRecord>(records.Count);
        long stringOffset = 0;
        long postingStart = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extensionBytes = Encoding.UTF8.GetBytes(record.Extension);
            prepared.Add(new PreparedRecord(extensionBytes, stringOffset, postingStart, record.EntryIds));
            stringOffset = checked(stringOffset + extensionBytes.Length);
            postingStart = checked(postingStart + record.EntryIds.Count);
        }

        var recordsOffset = HeaderSize;
        var stringsOffset = checked(recordsOffset + prepared.Count * RecordSize);
        var postingsOffset = checked(stringsOffset + stringOffset);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), checked((ulong)source.EntryCount));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), checked((ulong)new FileInfo(source.Path).Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), checked((ulong)prepared.Count));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), checked((ulong)recordsOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), checked((ulong)stringsOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), checked((ulong)postingsOffset));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        var recordBuffer = new byte[RecordSize];
        foreach (var record in prepared)
        {
            recordBuffer.AsSpan().Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(recordBuffer, checked((ulong)record.StringOffset));
            BinaryPrimitives.WriteUInt64LittleEndian(recordBuffer.AsSpan(8), checked((ulong)record.PostingStart));
            BinaryPrimitives.WriteUInt64LittleEndian(recordBuffer.AsSpan(16), checked((ulong)record.EntryIds.Count));
            BinaryPrimitives.WriteUInt32LittleEndian(recordBuffer.AsSpan(24), checked((uint)record.ExtensionBytes.Length));
            await stream.WriteAsync(recordBuffer, cancellationToken).ConfigureAwait(false);
        }
        foreach (var record in prepared)
        {
            await stream.WriteAsync(record.ExtensionBytes, cancellationToken).ConfigureAwait(false);
        }

        var postingBuffer = new byte[WriteBufferSize];
        var offset = 0;
        foreach (var record in prepared)
        {
            foreach (var entryId in record.EntryIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset > postingBuffer.Length - sizeof(long))
                {
                    await stream.WriteAsync(postingBuffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                    offset = 0;
                }
                BinaryPrimitives.WriteUInt64LittleEndian(postingBuffer.AsSpan(offset), checked((ulong)entryId));
                offset += sizeof(long);
            }
        }
        if (offset > 0)
        {
            await stream.WriteAsync(postingBuffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct PostingRange(long Start, long Count);
    private sealed record ExtensionBuildRecord(string Extension, IReadOnlyList<long> EntryIds);
    private sealed record PreparedRecord(
        byte[] ExtensionBytes,
        long StringOffset,
        long PostingStart,
        IReadOnlyList<long> EntryIds);
}
