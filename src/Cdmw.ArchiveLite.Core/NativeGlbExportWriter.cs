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

        var rig = NativeGlbRig.Build(package, meshes);
        var bufferViews = new List<Dictionary<string, object?>>();
        var accessors = new List<Dictionary<string, object?>>();
        var skinnedPrimitives = new List<Dictionary<string, object?>>();
        var unskinnedPrimitives = new List<Dictionary<string, object?>>();
        var materials = new List<Dictionary<string, object?>>();
        long binaryLength = 0;
        for (var index = 0; index < package.Batches.Count; index++)
        {
            var batch = package.Batches[index];
            var rebuilt = meshes[index];
            // Bounds describe the vertices the file will hold, in the frame it writes them in.
            var bounds = MeasureBounds(rebuilt.Positions, rebuilt.IsSourceSpace ? null : package.Normalization);
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
            var attributes = new Dictionary<string, object?>
            {
                ["POSITION"] = positionAccessor,
                ["NORMAL"] = normalAccessor,
                ["TEXCOORD_0"] = uvAccessor,
            };
            var skinned = rig.Binds(index);
            if (skinned)
            {
                // One glTF set holds four influences, and the record carries six, so the fifth and
                // sixth travel in a second set. Dropping them instead would quietly unweight the
                // 1,761 vertices of a single body that use all six.
                for (var set = 0; set < NativeGlbRig.AttributeSets; set++)
                {
                    attributes[$"JOINTS_{set}"] =
                        AddJointAccessor(bufferViews, accessors, ref binaryLength, rebuilt.VertexCount);
                    attributes[$"WEIGHTS_{set}"] =
                        AddFloatAccessor(bufferViews, accessors, ref binaryLength, rebuilt.VertexCount, 4, null, null);
                }
            }
            var indexAccessor = AddIndexAccessor(bufferViews, accessors, ref binaryLength, rebuilt.CornerIndices.Length);
            (skinned ? skinnedPrimitives : unskinnedPrimitives).Add(new Dictionary<string, object?>
            {
                ["attributes"] = attributes,
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
        var inverseBindAccessor = rig.IsEmpty
            ? -1
            : AddMatrixAccessor(bufferViews, accessors, ref binaryLength, rig.Joints.Count);

        var baseName = CleanName(Path.GetFileNameWithoutExtension(sourcePath), "mesh");
        var scene = BuildScene(baseName, rig, skinnedPrimitives, unskinnedPrimitives, inverseBindAccessor);
        var extras = new Dictionary<string, object?>
        {
            ["source_path"] = sourcePath,
            ["source_format"] = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant(),
            ["mesh_only"] = true,
        };
        if (!rig.IsEmpty)
        {
            // Named only where there is a rig to name. A mesh with no skeleton, and a rigidly bound
            // one that names no bone, write the file they have always written.
            extras["skeleton_status"] = package.Skeleton.Status;
            extras["skeleton_source_path"] = package.Skeleton.SourcePath;
            extras["skeleton_joint_count"] = rig.Joints.Count;
        }
        var document = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?>
            {
                ["version"] = "2.0",
                ["generator"] = "CDMW Archive Lite",
                ["extras"] = extras,
            },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object?> { ["nodes"] = scene.RootNodes } },
            ["nodes"] = scene.Nodes,
            ["meshes"] = scene.Meshes,
            ["materials"] = materials,
            ["buffers"] = new[] { new Dictionary<string, object?> { ["byteLength"] = binaryLength } },
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors,
        };
        if (scene.Skins.Count > 0)
        {
            document["skins"] = scene.Skins;
        }
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

                for (var index = 0; index < meshes.Count; index++)
                {
                    var rebuilt = meshes[index];
                    await WriteFloatAttributeAsync(
                        rebuilt.Positions,
                        3,
                        flipSecondComponent: false,
                        rebuilt.IsSourceSpace ? null : package.Normalization,
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
                    if (rig.Binds(index))
                    {
                        await rig.WriteBindingAsync(index, output, token).ConfigureAwait(false);
                    }
                    await WriteIndicesAsync(rebuilt.CornerIndices, output, token).ConfigureAwait(false);
                    completed += rebuilt.CornerIndices.Length;
                    await ReportAsync(progress, completed, totalWork, "mesh_export_write", currentItem)
                        .ConfigureAwait(false);
                }
                await rig.WriteInverseBindMatricesAsync(output, token).ConfigureAwait(false);
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
            ["type"] = VectorType(components),
        };
        if (minimum is not null && maximum is not null)
        {
            accessor["min"] = minimum;
            accessor["max"] = maximum;
        }
        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    private static string VectorType(int components) => components switch
    {
        2 => "VEC2",
        3 => "VEC3",
        4 => "VEC4",
        _ => throw new InvalidDataException($"glTF has no vector type of {components} components."),
    };

    /// <summary>Four joint indices per vertex, as the unsigned shorts glTF asks for.</summary>
    private static int AddJointAccessor(
        List<Dictionary<string, object?>> views,
        List<Dictionary<string, object?>> accessors,
        ref long binaryLength,
        int count)
    {
        var byteLength = checked((long)count * 4 * sizeof(ushort));
        views.Add(new Dictionary<string, object?>
        {
            ["buffer"] = 0,
            ["byteOffset"] = binaryLength,
            ["byteLength"] = byteLength,
            ["target"] = 34962,
        });
        binaryLength = checked(binaryLength + byteLength);
        accessors.Add(new Dictionary<string, object?>
        {
            ["bufferView"] = views.Count - 1,
            ["componentType"] = 5123,
            ["count"] = count,
            ["type"] = "VEC4",
        });
        return accessors.Count - 1;
    }

    /// <summary>
    /// The skin's inverse bind matrices. Its view carries no target: glTF reserves those for views
    /// a vertex or index buffer is drawn from, and this one is read by the skin instead.
    /// </summary>
    private static int AddMatrixAccessor(
        List<Dictionary<string, object?>> views,
        List<Dictionary<string, object?>> accessors,
        ref long binaryLength,
        int count)
    {
        var byteLength = checked((long)count * 16 * sizeof(float));
        views.Add(new Dictionary<string, object?>
        {
            ["buffer"] = 0,
            ["byteOffset"] = binaryLength,
            ["byteLength"] = byteLength,
        });
        binaryLength = checked(binaryLength + byteLength);
        accessors.Add(new Dictionary<string, object?>
        {
            ["bufferView"] = views.Count - 1,
            ["componentType"] = 5126,
            ["count"] = count,
            ["type"] = "MAT4",
        });
        return accessors.Count - 1;
    }

    /// <summary>
    /// The node graph: the mesh nodes, the bone nodes, and the skin that joins them.
    /// </summary>
    /// <remarks>
    /// glTF requires every primitive under a skinned node to carry joints, so a package that mixes
    /// bound and unbound batches -- a character with an accessory loaded beside it -- gets two mesh
    /// nodes rather than one. Forcing the unbound batches onto a joint to keep them together would
    /// bind them to whichever bone came first, which is how a mesh ends up following the root
    /// around the map. With nothing to rig, the graph is the single mesh node it has always been.
    /// </remarks>
    private static GlbScene BuildScene(
        string baseName,
        NativeGlbRig rig,
        List<Dictionary<string, object?>> skinnedPrimitives,
        List<Dictionary<string, object?>> unskinnedPrimitives,
        int inverseBindAccessor)
    {
        var nodes = new List<Dictionary<string, object?>>();
        var meshList = new List<Dictionary<string, object?>>();
        var rootNodes = new List<int>();
        var skins = new List<Dictionary<string, object?>>();

        if (skinnedPrimitives.Count > 0)
        {
            meshList.Add(new Dictionary<string, object?> { ["name"] = baseName, ["primitives"] = skinnedPrimitives });
            rootNodes.Add(nodes.Count);
            nodes.Add(new Dictionary<string, object?>
            {
                ["name"] = baseName,
                ["mesh"] = meshList.Count - 1,
                ["skin"] = 0,
            });
        }
        if (unskinnedPrimitives.Count > 0)
        {
            var name = skinnedPrimitives.Count > 0 ? baseName + "_unrigged" : baseName;
            meshList.Add(new Dictionary<string, object?> { ["name"] = name, ["primitives"] = unskinnedPrimitives });
            rootNodes.Add(nodes.Count);
            nodes.Add(new Dictionary<string, object?> { ["name"] = name, ["mesh"] = meshList.Count - 1 });
        }

        if (rig.IsEmpty)
        {
            return new GlbScene(nodes, meshList, rootNodes, skins);
        }

        var jointNodes = new int[rig.Joints.Count];
        for (var joint = 0; joint < rig.Joints.Count; joint++)
        {
            jointNodes[joint] = nodes.Count + joint;
        }
        for (var joint = 0; joint < rig.Joints.Count; joint++)
        {
            var bone = rig.Bone(joint);
            var node = new Dictionary<string, object?>
            {
                ["name"] = CleanName(bone.Name, $"bone_{joint:000}"),
                ["translation"] = bone.Translation,
                ["rotation"] = bone.Rotation,
                ["scale"] = bone.Scale,
            };
            var children = rig.ChildJoints(joint).Select(child => jointNodes[child]).ToArray();
            if (children.Length > 0)
            {
                node["children"] = children;
            }
            nodes.Add(node);
        }
        // A bone whose parent is outside the rig hangs from the scene, not from the mesh: a
        // skinned mesh's own transform does not apply to its joints, so parenting them under it
        // would put the hierarchy somewhere glTF never reads it back from.
        rootNodes.AddRange(rig.RootJoints().Select(joint => jointNodes[joint]));
        skins.Add(new Dictionary<string, object?>
        {
            ["name"] = baseName + "_armature",
            ["joints"] = jointNodes,
            ["inverseBindMatrices"] = inverseBindAccessor,
        });
        return new GlbScene(nodes, meshList, rootNodes, skins);
    }

    private static MeshBounds MeasureBounds(double[] positions, NativePreviewNormalization? normalization)
    {
        var minimum = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var maximum = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (var index = 0; index < positions.Length; index++)
        {
            var component = index % 3;
            var value = (float)(normalization is null ? positions[index] : normalization.Restore(positions[index], component));
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
        double[] source,
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
                var value = (float)source[written + index];
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
            builder.Append(char.IsControl(character) ? '_' : character);
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

    private sealed record GlbScene(
        List<Dictionary<string, object?>> Nodes,
        List<Dictionary<string, object?>> Meshes,
        List<int> RootNodes,
        List<Dictionary<string, object?>> Skins);
}
