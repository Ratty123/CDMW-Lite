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
        var totalWork = checked(package.TotalVertices * 2L);
        long completed = 0;
        await ReportAsync(progress, completed, totalWork, "mesh_export_prepare", currentItem).ConfigureAwait(false);
        // glTF carries an index buffer of its own, so the package's corner-by-corner geometry is
        // rebuilt into the source's own indexed array here exactly as it is for OBJ and FBX.
        var meshes = new List<NativePreviewVertexRebuild>(package.Batches.Count);
        foreach (var batch in package.Batches)
        {
            meshes.Add(await NativePreviewVertexRebuild.BuildAsync(
                batch,
                async corners =>
                {
                    completed += corners;
                    await ReportAsync(progress, completed, totalWork, "mesh_export_prepare", currentItem)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false));
        }

        var bufferViews = new List<Dictionary<string, object?>>();
        var accessors = new List<Dictionary<string, object?>>();
        var primitives = new List<Dictionary<string, object?>>();
        var materials = new List<Dictionary<string, object?>>();
        long binaryLength = 0;
        for (var index = 0; index < package.Batches.Count; index++)
        {
            var batch = package.Batches[index];
            var rebuilt = meshes[index];
            // Bounds describe the vertices the file will hold, in the frame it writes them in.
            var bounds = MeasureBounds(rebuilt.Positions, package.Normalization);
            var positionAccessor = AddFloatAccessor(
                bufferViews,
                accessors,
                ref binaryLength,
                rebuilt.VertexCount,
                3,
                bounds.Minimum,
                bounds.Maximum);
            var normalAccessor = AddFloatAccessor(bufferViews, accessors, ref binaryLength, rebuilt.VertexCount, 3, null, null);
            var uvAccessor = AddFloatAccessor(bufferViews, accessors, ref binaryLength, rebuilt.VertexCount, 2, null, null);
            var indexAccessor = AddIndexAccessor(bufferViews, accessors, ref binaryLength, rebuilt.CornerIndices.Length);
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
                ["name"] = CleanName(batch.MaterialName, CleanName(batch.SubmeshName, $"material_{batch.Index:000}")),
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

                foreach (var rebuilt in meshes)
                {
                    await WriteFloatAttributeAsync(
                        rebuilt.Positions,
                        3,
                        flipSecondComponent: false,
                        package.Normalization,
                        output,
                        token).ConfigureAwait(false);
                    // Only positions carry the preview's framing transform; a
                    // uniform recentre and rescale leaves normals and UVs alone.
                    await WriteFloatAttributeAsync(
                        rebuilt.Normals,
                        3,
                        flipSecondComponent: false,
                        restore: null,
                        output,
                        token).ConfigureAwait(false);
                    await WriteFloatAttributeAsync(
                        rebuilt.TextureCoordinates,
                        2,
                        flipSecondComponent: true,
                        restore: null,
                        output,
                        token).ConfigureAwait(false);
                    await WriteIndicesAsync(rebuilt.CornerIndices, output, token).ConfigureAwait(false);
                    completed += rebuilt.CornerIndices.Length;
                    await ReportAsync(progress, completed, totalWork, "mesh_export_write", currentItem)
                        .ConfigureAwait(false);
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

    private static MeshBounds MeasureBounds(float[] positions, NativePreviewNormalization normalization)
    {
        var minimum = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var maximum = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (var index = 0; index < positions.Length; index++)
        {
            var component = index % 3;
            var value = (float)normalization.Restore(positions[index], component);
            minimum[component] = Math.Min(minimum[component], value);
            maximum[component] = Math.Max(maximum[component], value);
        }
        return new MeshBounds(minimum, maximum);
    }

    private static async Task WriteIndicesAsync(int[] cornerIndices, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[RecordsPerChunk * sizeof(uint)];
        var written = 0;
        while (written < cornerIndices.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, cornerIndices.Length - written);
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    buffer.AsSpan(localIndex * sizeof(uint), sizeof(uint)),
                    (uint)cornerIndices[written + localIndex]);
            }
            await output.WriteAsync(buffer.AsMemory(0, count * sizeof(uint)), cancellationToken).ConfigureAwait(false);
            written += count;
        }
    }

    private static async Task WriteFloatAttributeAsync(
        float[] source,
        int components,
        bool flipSecondComponent,
        NativePreviewNormalization? restore,
        Stream output,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[RecordsPerChunk * components * sizeof(float)];
        var written = 0;
        while (written < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length / sizeof(float), source.Length - written);
            for (var index = 0; index < count; index++)
            {
                var component = (written + index) % components;
                var value = source[written + index];
                if (restore is not null)
                {
                    value = (float)restore.Restore(value, component);
                }
                if (flipSecondComponent && component == 1)
                {
                    value = 1.0f - value;
                }
                BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(index * sizeof(float), sizeof(float)), value);
            }
            await output.WriteAsync(buffer.AsMemory(0, count * sizeof(float)), cancellationToken).ConfigureAwait(false);
            written += count;
        }
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
}
