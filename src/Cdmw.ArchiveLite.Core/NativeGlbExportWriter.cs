using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cdmw.ArchiveLite.Contracts;
using static Cdmw.ArchiveLite.Core.NativePreviewGeometryIO;

namespace Cdmw.ArchiveLite.Core;

internal static class NativeGlbExportWriter
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static async Task WriteAsync(
        NativePreviewMeshPackage package,
        string sourcePath,
        string destination,
        bool overwrite,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        CancellationToken cancellationToken)
    {
        var totalWork = checked(package.TotalVertices * 4L);
        long completed = 0;
        await ReportAsync(progress, completed, totalWork, "mesh_export_prepare", currentItem).ConfigureAwait(false);
        // glTF carries an index buffer of its own, so the package's corner-by-corner geometry is
        // rejoined into an indexed mesh here exactly as it is for OBJ and FBX. The pass also
        // measures the bounds, which have to describe the vertices actually written.
        var meshes = new List<WeldedBatch>(package.Batches.Count);
        foreach (var batch in package.Batches)
        {
            var result = await WeldBatchAsync(
                batch,
                package.Normalization,
                completed,
                totalWork,
                progress,
                currentItem,
                cancellationToken).ConfigureAwait(false);
            meshes.Add(result.Welded);
            completed = result.Completed;
        }

        var bufferViews = new List<Dictionary<string, object?>>();
        var accessors = new List<Dictionary<string, object?>>();
        var primitives = new List<Dictionary<string, object?>>();
        var materials = new List<Dictionary<string, object?>>();
        long binaryLength = 0;
        for (var index = 0; index < package.Batches.Count; index++)
        {
            var batch = package.Batches[index];
            var welded = meshes[index];
            var positionAccessor = AddFloatAccessor(
                bufferViews,
                accessors,
                ref binaryLength,
                welded.UniqueCount,
                3,
                welded.Bounds.Minimum,
                welded.Bounds.Maximum);
            var normalAccessor = AddFloatAccessor(bufferViews, accessors, ref binaryLength, welded.UniqueCount, 3, null, null);
            var uvAccessor = AddFloatAccessor(bufferViews, accessors, ref binaryLength, welded.UniqueCount, 2, null, null);
            var indexAccessor = AddIndexAccessor(bufferViews, accessors, ref binaryLength, welded.Indices.Length);
            primitives.Add(new Dictionary<string, object?>
            {
                ["attributes"] = new Dictionary<string, object?>
                {
                    ["POSITION"] = positionAccessor,
                    ["NORMAL"] = normalAccessor,
                    ["TEXCOORD_0"] = uvAccessor,
                },
                ["indices"] = indexAccessor,
                ["material"] = index,
                ["mode"] = 4,
            });
            materials.Add(new Dictionary<string, object?>
            {
                ["name"] = CleanName(batch.MaterialName, $"material_{batch.Index:000}"),
                ["pbrMetallicRoughness"] = new Dictionary<string, object?>
                {
                    ["baseColorFactor"] = new[] { batch.BaseColor[0], batch.BaseColor[1], batch.BaseColor[2], 1.0f },
                    ["metallicFactor"] = batch.Metalness,
                    ["roughnessFactor"] = batch.Roughness,
                },
                ["doubleSided"] = false,
            });
        }

        var baseName = CleanName(Path.GetFileNameWithoutExtension(sourcePath), "mesh");
        var document = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?>
            {
                ["version"] = "2.0",
                ["generator"] = "CDMW Archive Lite",
                ["extras"] = new Dictionary<string, object?>
                {
                    ["source_path"] = sourcePath,
                    ["source_format"] = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant(),
                    ["mesh_only"] = true,
                },
            },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object?> { ["nodes"] = new[] { 0 } } },
            ["nodes"] = new[] { new Dictionary<string, object?> { ["name"] = baseName, ["mesh"] = 0 } },
            ["meshes"] = new[] { new Dictionary<string, object?> { ["name"] = baseName, ["primitives"] = primitives } },
            ["materials"] = materials,
            ["buffers"] = new[] { new Dictionary<string, object?> { ["byteLength"] = binaryLength } },
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors,
        };
        var jsonChunk = Pad4(JsonSerializer.SerializeToUtf8Bytes(document, CompactJson), (byte)' ');
        var binaryPaddedLength = Align4(binaryLength);
        var totalLength = checked(12L + 8L + jsonChunk.LongLength + 8L + binaryPaddedLength);
        if (totalLength > uint.MaxValue)
        {
            throw new InvalidDataException("GLB output exceeds the 4 GiB format limit.");
        }

        await AtomicFile.WriteAsync(
            destination,
            async (output, token) =>
            {
                var header = new byte[20];
                "glTF"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), checked((uint)totalLength));
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), checked((uint)jsonChunk.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 0x4E4F534A);
                await output.WriteAsync(header, token).ConfigureAwait(false);
                await output.WriteAsync(jsonChunk, token).ConfigureAwait(false);
                var binaryHeader = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(binaryHeader.AsSpan(0, 4), checked((uint)binaryPaddedLength));
                BinaryPrimitives.WriteUInt32LittleEndian(binaryHeader.AsSpan(4, 4), 0x004E4942);
                await output.WriteAsync(binaryHeader, token).ConfigureAwait(false);

                for (var index = 0; index < package.Batches.Count; index++)
                {
                    var batch = package.Batches[index];
                    var welded = meshes[index];
                    completed = await CopyFloatAttributeAsync(
                        batch,
                        welded,
                        output,
                        0,
                        3,
                        flipSecondComponent: false,
                        package.Normalization,
                        completed,
                        totalWork,
                        progress,
                        currentItem,
                        token).ConfigureAwait(false);
                    // Only positions carry the preview's framing transform; a
                    // uniform recentre and rescale leaves normals and UVs alone.
                    completed = await CopyFloatAttributeAsync(
                        batch,
                        welded,
                        output,
                        3,
                        3,
                        flipSecondComponent: false,
                        restore: null,
                        completed,
                        totalWork,
                        progress,
                        currentItem,
                        token).ConfigureAwait(false);
                    completed = await CopyFloatAttributeAsync(
                        batch,
                        welded,
                        output,
                        9,
                        2,
                        flipSecondComponent: true,
                        restore: null,
                        completed,
                        totalWork,
                        progress,
                        currentItem,
                        token).ConfigureAwait(false);
                    await WriteIndicesAsync(welded, output, token).ConfigureAwait(false);
                }
                for (long padding = binaryLength; padding < binaryPaddedLength; padding++)
                {
                    output.WriteByte(0);
                }
            },
            cancellationToken,
            overwrite).ConfigureAwait(false);
        await ReportAsync(progress, totalWork, totalWork, "mesh_export_write", currentItem).ConfigureAwait(false);
    }

    private static int AddFloatAccessor(
        List<Dictionary<string, object?>> views,
        List<Dictionary<string, object?>> accessors,
        ref long binaryLength,
        int count,
        int components,
        float[]? minimum,
        float[]? maximum)
    {
        var byteLength = checked((long)count * components * sizeof(float));
        var viewIndex = views.Count;
        views.Add(new Dictionary<string, object?>
        {
            ["buffer"] = 0,
            ["byteOffset"] = binaryLength,
            ["byteLength"] = byteLength,
            ["target"] = 34962,
        });
        binaryLength = checked(binaryLength + byteLength);
        var accessor = new Dictionary<string, object?>
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = 5126,
            ["count"] = count,
            ["type"] = components == 3 ? "VEC3" : "VEC2",
        };
        if (minimum is not null && maximum is not null)
        {
            accessor["min"] = minimum;
            accessor["max"] = maximum;
        }
        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    private static async Task<WeldProgress> WeldBatchAsync(
        NativePreviewMeshBatch batch,
        NativePreviewNormalization normalization,
        long completed,
        long totalWork,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        CancellationToken cancellationToken)
    {
        var minimum = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var maximum = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        var welder = new NativePreviewVertexWelder(batch.VertexCount);
        var indices = new int[batch.VertexCount];
        await using var input = OpenRead(batch.GeometryPath);
        var buffer = new byte[RecordsPerChunk * BytesPerPreviewVertex];
        var batchCompleted = 0;
        while (batchCompleted < batch.VertexCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, batch.VertexCount - batchCompleted);
            var bytes = checked(count * BytesPerPreviewVertex);
            await input.ReadExactlyAsync(buffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                var offset = localIndex * BytesPerPreviewVertex;
                var isNew = welder.TryAssign(
                    buffer.AsSpan(offset, BytesPerPreviewVertex),
                    out var vertexIndex);
                indices[batchCompleted + localIndex] = vertexIndex;
                if (!isNew)
                {
                    continue;
                }
                // Bounds describe the vertices the file will hold, so only a vertex that is
                // actually written contributes -- a repeated corner cannot widen them anyway.
                for (var component = 0; component < 3; component++)
                {
                    var value = RestoredPosition(buffer, offset, component, normalization);
                    minimum[component] = Math.Min(minimum[component], value);
                    maximum[component] = Math.Max(maximum[component], value);
                }
            }
            batchCompleted += count;
            completed += count;
            await ReportAsync(progress, completed, totalWork, "mesh_export_prepare", currentItem).ConfigureAwait(false);
        }
        return new WeldProgress(
            new WeldedBatch(indices, welder.UniqueCount, new MeshBounds(minimum, maximum)),
            completed);
    }

    private static async Task WriteIndicesAsync(WeldedBatch welded, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[RecordsPerChunk * sizeof(uint)];
        var written = 0;
        while (written < welded.Indices.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, welded.Indices.Length - written);
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    buffer.AsSpan(localIndex * sizeof(uint), sizeof(uint)),
                    (uint)welded.Indices[written + localIndex]);
            }
            await output.WriteAsync(buffer.AsMemory(0, count * sizeof(uint)), cancellationToken).ConfigureAwait(false);
            written += count;
        }
    }

    private static async Task<long> CopyFloatAttributeAsync(
        NativePreviewMeshBatch batch,
        WeldedBatch welded,
        Stream output,
        int sourceFloatOffset,
        int components,
        bool flipSecondComponent,
        NativePreviewNormalization? restore,
        long completed,
        long totalWork,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        CancellationToken cancellationToken)
    {
        await ReportAsync(progress, completed, totalWork, "mesh_export_write", currentItem).ConfigureAwait(false);
        await using var input = OpenRead(batch.GeometryPath);
        var inputBuffer = new byte[RecordsPerChunk * BytesPerPreviewVertex];
        var outputBuffer = new byte[RecordsPerChunk * components * sizeof(float)];
        var batchCompleted = 0;
        var emitted = 0;
        while (batchCompleted < batch.VertexCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, batch.VertexCount - batchCompleted);
            var inputBytes = checked(count * BytesPerPreviewVertex);
            await input.ReadExactlyAsync(inputBuffer.AsMemory(0, inputBytes), cancellationToken).ConfigureAwait(false);
            var outputOffset = 0;
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                // Indices were assigned in order of first appearance, so a corner introduces its
                // vertex exactly when its index equals the number already written. That replays
                // the welding decision without keeping a second copy of it.
                if (welded.Indices[batchCompleted + localIndex] != emitted)
                {
                    continue;
                }
                emitted++;
                var sourceOffset = (localIndex * BytesPerPreviewVertex) + (sourceFloatOffset * sizeof(float));
                for (var component = 0; component < components; component++)
                {
                    var value = restore is null
                        ? ReadFiniteSingle(inputBuffer, sourceOffset + (component * sizeof(float)), "mesh attribute")
                        : RestoredPosition(inputBuffer, sourceOffset, component, restore);
                    if (flipSecondComponent && component == 1)
                    {
                        value = 1.0f - value;
                    }
                    BinaryPrimitives.WriteSingleLittleEndian(outputBuffer.AsSpan(outputOffset, 4), value);
                    outputOffset += 4;
                }
            }
            await output.WriteAsync(outputBuffer.AsMemory(0, outputOffset), cancellationToken).ConfigureAwait(false);
            batchCompleted += count;
            completed += count;
            await ReportAsync(progress, completed, totalWork, "mesh_export_write", currentItem).ConfigureAwait(false);
        }
        return completed;
    }

    private static float RestoredPosition(
        byte[] buffer,
        int vertexOffset,
        int component,
        NativePreviewNormalization normalization)
    {
        var value = ReadFiniteSingle(buffer, vertexOffset + (component * sizeof(float)), "position");
        return (float)normalization.Restore(value, component);
    }

    private static async Task ReportAsync(
        Func<ProgressUpdate, Task>? progress,
        long completed,
        long total,
        string phase,
        string? currentItem)
    {
        if (progress is not null)
        {
            await progress(new ProgressUpdate(completed, total, phase, currentItem)).ConfigureAwait(false);
        }
    }

    private static byte[] Pad4(byte[] source, byte padding)
    {
        var length = checked((int)Align4(source.LongLength));
        if (length == source.Length)
        {
            return source;
        }
        var result = new byte[length];
        source.CopyTo(result, 0);
        result.AsSpan(source.Length).Fill(padding);
        return result;
    }

    private static long Align4(long value) => checked((value + 3L) & ~3L);

    private static string CleanName(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var builder = new StringBuilder(Math.Min(source.Length, 96));
        foreach (var character in source)
        {
            if (builder.Length >= 96)
            {
                break;
            }
            builder.Append(char.IsControl(character) || character is '/' or '\\' ? '_' : character);
        }
        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static int AddIndexAccessor(
        List<Dictionary<string, object?>> views,
        List<Dictionary<string, object?>> accessors,
        ref long binaryLength,
        int count)
    {
        var byteLength = checked((long)count * sizeof(uint));
        var viewIndex = views.Count;
        views.Add(new Dictionary<string, object?>
        {
            ["buffer"] = 0,
            ["byteOffset"] = binaryLength,
            ["byteLength"] = byteLength,
            ["target"] = 34963,
        });
        binaryLength = checked(binaryLength + byteLength);
        accessors.Add(new Dictionary<string, object?>
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = 5125,
            ["count"] = count,
            ["type"] = "SCALAR",
        });
        return accessors.Count - 1;
    }

    private sealed record MeshBounds(float[] Minimum, float[] Maximum);
    private sealed record WeldedBatch(int[] Indices, int UniqueCount, MeshBounds Bounds);
    private sealed record WeldProgress(WeldedBatch Welded, long Completed);
}
