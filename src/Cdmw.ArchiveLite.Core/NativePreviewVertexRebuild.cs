using System.Buffers.Binary;
using static Cdmw.ArchiveLite.Core.NativePreviewGeometryIO;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Rebuilds one batch's indexed vertex array from the preview package, for export.
/// </summary>
/// <remarks>
/// Two things have to be recovered, and the package answers both.
///
/// The order comes from the identity buffer, which records for every triangle corner the source
/// vertex it was copied from. Numbering those in ascending source order reproduces the array the
/// archive holds, because the preview parser itself emits vertices in that order. That matters
/// because morph targets, shape keys and every other per-vertex correspondence are matched by
/// index: a mesh whose vertices are the right points in the wrong order loads, and then deforms
/// into noise.
///
/// The coordinates come from the package's export geometry, which the parser decodes in double
/// straight from the source records and never frames. The render blob beside it holds the same
/// mesh the way the GPU wants it -- one vertex per corner, recentred and rescaled into the
/// preview's display cube, narrowed to floats, normals normalized for shading -- and exporting
/// from that means undoing the framing and living with whatever the floats kept. Both leave marks:
/// coordinates that differ from CDMW Full's in the eighth digit, and normals scaled to unit length
/// where the source record said otherwise.
///
/// A package written before either sidecar existed still exports, from the render blob and by
/// matching attribute bits. Neither substitute can reconstruct what it is standing in for, and
/// they are kept only so a stale cache exports something rather than failing.
/// </remarks>
internal sealed class NativePreviewVertexRebuild
{
    /// <summary>Two 32-bit fields per corner: the source submesh, then the source vertex.</summary>
    private const int IdentityBytesPerCorner = 8;

    private NativePreviewVertexRebuild(
        int[] cornerIndices,
        int vertexCount,
        double[] positions,
        double[] normals,
        double[] textureCoordinates,
        int[]? sourceVertexMap,
        bool isSourceSpace,
        NativePreviewVertexSkin? skin)
    {
        CornerIndices = cornerIndices;
        VertexCount = vertexCount;
        Positions = positions;
        Normals = normals;
        TextureCoordinates = textureCoordinates;
        SourceVertexMap = sourceVertexMap;
        IsSourceSpace = isSourceSpace;
        Skin = skin;
    }

    /// <summary>The exported vertex each triangle corner refers to, in the package's corner order.</summary>
    public int[] CornerIndices { get; }

    public int VertexCount { get; }

    /// <summary>Three components per vertex. See <see cref="IsSourceSpace"/> for which space.</summary>
    public double[] Positions { get; }

    public double[] Normals { get; }

    public double[] TextureCoordinates { get; }

    /// <summary>
    /// The source vertex each exported vertex came from, or null when the package carries no
    /// identity buffer. Ascending by construction, and the identity mapping whenever the source
    /// numbered its vertices from zero without gaps.
    /// </summary>
    public int[]? SourceVertexMap { get; }

    /// <summary>
    /// Whether the positions are already in the archive's own space. When false they carry the
    /// preview's framing transform and the caller has to undo it.
    /// </summary>
    public bool IsSourceSpace { get; }

    /// <summary>
    /// The rig binding of each exported vertex, or null when this batch carries none.
    /// </summary>
    /// <remarks>
    /// The package writes one skin row per vertex the parser decoded, in the parser's order. The
    /// exported array is not that array -- <see cref="SourceVertexMap"/> exists precisely because
    /// rebuilding renumbers it -- so the rows are read through that map rather than straight
    /// across. Bound the other way round, a rig lands on the wrong points: the mesh imports and
    /// weights cleanly, and then folds itself inside out the moment a bone moves.
    /// </remarks>
    public NativePreviewVertexSkin? Skin { get; }

    /// <param name="cornersRead">
    /// Reports corners consumed, so a caller can report progress over the package's corner count.
    /// </param>
    public static async Task<NativePreviewVertexRebuild> BuildAsync(
        NativePreviewMeshBatch batch,
        Func<int, Task>? cornersRead,
        CancellationToken cancellationToken)
    {
        var plan = batch.IdentityPath is null
            ? await PlanByAttributesAsync(batch, cancellationToken).ConfigureAwait(false)
            : await PlanBySourceIdentityAsync(batch, cancellationToken).ConfigureAwait(false);
        var skin = await ReadSkinAsync(batch, plan, cancellationToken).ConfigureAwait(false);

        // The export geometry is only usable when the rebuild agrees with it about how many
        // vertices this submesh has, which it does whenever the parser numbered them from zero
        // without gaps. Anything else falls back rather than pairing two different arrays.
        if (batch.ExportGeometryPath is not null && batch.ExportVertexCount == plan.VertexCount)
        {
            var exact = await ReadExportGeometryAsync(batch, plan, skin, cancellationToken).ConfigureAwait(false);
            if (cornersRead is not null)
            {
                await cornersRead(batch.VertexCount).ConfigureAwait(false);
            }
            return exact;
        }

        return await ReadRenderGeometryAsync(batch, plan, skin, cornersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This batch's skin rows, reordered onto the exported vertices.
    /// </summary>
    /// <remarks>
    /// Only a rebuild that knows where each exported vertex came from can place them. A package
    /// with no identity buffer numbers its vertices by first appearance, and that order is not the
    /// parser's, so its skin rows are left unread rather than applied to the wrong points.
    /// A batch whose rows do not cover every exported vertex is skipped for the same reason, and
    /// without failing the export: the rig is a sidecar, and the mesh in front of it still stands.
    /// </remarks>
    private static async Task<NativePreviewVertexSkin?> ReadSkinAsync(
        NativePreviewMeshBatch batch,
        VertexPlan plan,
        CancellationToken cancellationToken)
    {
        if (batch.SkinPath is null || plan.SourceVertexMap is null)
        {
            return null;
        }
        if (plan.SourceVertexMap.Any(source => source < 0 || source >= batch.SkinVertexCount))
        {
            return null;
        }
        var rows = await File.ReadAllBytesAsync(batch.SkinPath, cancellationToken).ConfigureAwait(false);
        var influences = NativePreviewMeshPackage.SkinInfluencesPerVertex;
        var joints = new ushort[checked(plan.VertexCount * influences)];
        var weights = new byte[joints.Length];
        for (var index = 0; index < plan.VertexCount; index++)
        {
            var source = plan.SourceVertexMap[index];
            var offset = source * NativePreviewMeshPackage.SkinBytesPerVertex;
            for (var influence = 0; influence < influences; influence++)
            {
                joints[(index * influences) + influence] = BinaryPrimitives.ReadUInt16LittleEndian(
                    rows.AsSpan(offset + (influence * sizeof(ushort)), sizeof(ushort)));
                weights[(index * influences) + influence] = rows[offset + (influences * sizeof(ushort)) + influence];
            }
        }
        return new NativePreviewVertexSkin(joints, weights);
    }

    private static async Task<NativePreviewVertexRebuild> ReadExportGeometryAsync(
        NativePreviewMeshBatch batch,
        VertexPlan plan,
        NativePreviewVertexSkin? skin,
        CancellationToken cancellationToken)
    {
        var positions = new double[checked(plan.VertexCount * 3)];
        var normals = new double[checked(plan.VertexCount * 3)];
        var textureCoordinates = new double[checked(plan.VertexCount * 2)];
        await using var input = OpenRead(batch.ExportGeometryPath!);
        const int verticesPerChunk = 8192;
        var buffer = new byte[verticesPerChunk * NativePreviewMeshPackage.ExportBytesPerVertex];
        var vertex = 0;
        while (vertex < plan.VertexCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(verticesPerChunk, plan.VertexCount - vertex);
            await input.ReadExactlyAsync(
                buffer.AsMemory(0, count * NativePreviewMeshPackage.ExportBytesPerVertex),
                cancellationToken).ConfigureAwait(false);
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                var offset = localIndex * NativePreviewMeshPackage.ExportBytesPerVertex;
                var index = vertex + localIndex;
                for (var component = 0; component < 3; component++)
                {
                    positions[(index * 3) + component] =
                        ReadFiniteDouble(buffer, offset + (component * sizeof(double)), "position");
                    normals[(index * 3) + component] =
                        ReadFiniteDouble(buffer, offset + ((3 + component) * sizeof(double)), "normal");
                }
                for (var component = 0; component < 2; component++)
                {
                    textureCoordinates[(index * 2) + component] =
                        ReadFiniteDouble(buffer, offset + ((6 + component) * sizeof(double)), "UV");
                }
            }
            vertex += count;
        }
        return new NativePreviewVertexRebuild(
            plan.CornerIndices,
            plan.VertexCount,
            positions,
            normals,
            textureCoordinates,
            plan.SourceVertexMap,
            isSourceSpace: true,
            skin);
    }

    private static async Task<NativePreviewVertexRebuild> ReadRenderGeometryAsync(
        NativePreviewMeshBatch batch,
        VertexPlan plan,
        NativePreviewVertexSkin? skin,
        Func<int, Task>? cornersRead,
        CancellationToken cancellationToken)
    {
        var positions = new double[checked(plan.VertexCount * 3)];
        var normals = new double[checked(plan.VertexCount * 3)];
        var textureCoordinates = new double[checked(plan.VertexCount * 2)];
        var filled = new bool[plan.VertexCount];
        await using var input = OpenRead(batch.GeometryPath);
        var buffer = new byte[RecordsPerChunk * BytesPerPreviewVertex];
        var corner = 0;
        while (corner < batch.VertexCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, batch.VertexCount - corner);
            await input.ReadExactlyAsync(
                buffer.AsMemory(0, checked(count * BytesPerPreviewVertex)),
                cancellationToken).ConfigureAwait(false);
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                var index = plan.CornerIndices[corner + localIndex];
                if (filled[index])
                {
                    // Every corner of one source vertex was copied from a single record, so the
                    // first to arrive carries the same attributes as the rest.
                    continue;
                }
                filled[index] = true;
                var sourceOffset = localIndex * BytesPerPreviewVertex;
                ReadComponents(buffer, sourceOffset, positions, index * 3, 3, "position");
                ReadComponents(buffer, sourceOffset + (3 * sizeof(float)), normals, index * 3, 3, "normal");
                ReadComponents(buffer, sourceOffset + (9 * sizeof(float)), textureCoordinates, index * 2, 2, "UV");
            }
            corner += count;
            if (cornersRead is not null)
            {
                await cornersRead(count).ConfigureAwait(false);
            }
        }
        return new NativePreviewVertexRebuild(
            plan.CornerIndices,
            plan.VertexCount,
            positions,
            normals,
            textureCoordinates,
            plan.SourceVertexMap,
            isSourceSpace: false,
            skin);
    }

    private static double ReadFiniteDouble(byte[] input, int offset, string label)
    {
        var value = BinaryPrimitives.ReadDoubleLittleEndian(input.AsSpan(offset, sizeof(double)));
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"Native preview export geometry contains a non-finite {label} value.");
        }
        return value;
    }

    private static void ReadComponents(
        byte[] input,
        int sourceOffset,
        double[] output,
        int outputOffset,
        int components,
        string label)
    {
        for (var component = 0; component < components; component++)
        {
            output[outputOffset + component] =
                ReadFiniteSingle(input, sourceOffset + (component * sizeof(float)), label);
        }
    }

    /// <summary>
    /// Numbers vertices in ascending source order, which is the order the archive holds them in.
    /// </summary>
    private static async Task<VertexPlan> PlanBySourceIdentityAsync(
        NativePreviewMeshBatch batch,
        CancellationToken cancellationToken)
    {
        var sources = new int[batch.VertexCount];
        await using (var identity = OpenRead(batch.IdentityPath!))
        {
            var buffer = new byte[RecordsPerChunk * IdentityBytesPerCorner];
            var corner = 0;
            while (corner < batch.VertexCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(RecordsPerChunk, batch.VertexCount - corner);
                await identity.ReadExactlyAsync(
                    buffer.AsMemory(0, checked(count * IdentityBytesPerCorner)),
                    cancellationToken).ConfigureAwait(false);
                for (var localIndex = 0; localIndex < count; localIndex++)
                {
                    // The pair's second field is the source vertex this corner came from; the
                    // first names its submesh, which the batch already fixes.
                    var source = BinaryPrimitives.ReadInt32LittleEndian(
                        buffer.AsSpan((localIndex * IdentityBytesPerCorner) + 4, 4));
                    if (source < 0)
                    {
                        throw new InvalidDataException(
                            $"Native preview batch {batch.Index} names a negative source vertex.");
                    }
                    sources[corner + localIndex] = source;
                }
                corner += count;
            }
        }

        var ordered = new HashSet<int>(sources).ToArray();
        Array.Sort(ordered);
        var rank = new Dictionary<int, int>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            rank[ordered[index]] = index;
        }
        var cornerIndices = new int[batch.VertexCount];
        for (var index = 0; index < cornerIndices.Length; index++)
        {
            cornerIndices[index] = rank[sources[index]];
        }
        return new VertexPlan(cornerIndices, ordered.Length, ordered);
    }

    /// <summary>
    /// Numbers vertices in order of first appearance, rejoining corners whose position, normal and
    /// texture coordinate are bit-identical. Only for packages that predate the identity buffer.
    /// </summary>
    private static async Task<VertexPlan> PlanByAttributesAsync(
        NativePreviewMeshBatch batch,
        CancellationToken cancellationToken)
    {
        var welder = new NativePreviewVertexWelder(batch.VertexCount);
        var cornerIndices = new int[batch.VertexCount];
        await using var input = OpenRead(batch.GeometryPath);
        var buffer = new byte[RecordsPerChunk * BytesPerPreviewVertex];
        var corner = 0;
        while (corner < batch.VertexCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, batch.VertexCount - corner);
            await input.ReadExactlyAsync(
                buffer.AsMemory(0, checked(count * BytesPerPreviewVertex)),
                cancellationToken).ConfigureAwait(false);
            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                welder.TryAssign(
                    buffer.AsSpan(localIndex * BytesPerPreviewVertex, BytesPerPreviewVertex),
                    out var vertexIndex);
                cornerIndices[corner + localIndex] = vertexIndex;
            }
            corner += count;
        }
        return new VertexPlan(cornerIndices, welder.UniqueCount, null);
    }

    private sealed record VertexPlan(int[] CornerIndices, int VertexCount, int[]? SourceVertexMap);
}

/// <summary>
/// Six bone indices and six raw <c>u8</c> weights per exported vertex, flattened.
/// </summary>
/// <remarks>
/// The weights are the source's own bytes, which descend and sum to 255 give or take rounding.
/// They are left that way here: an exporter that needs them summing to one can divide by their
/// total, and scaling them earlier would throw away what the record actually stated.
/// <see cref="NativePreviewMeshPackage.UnusedSkinBone"/> marks an influence the record leaves
/// empty, and its weight is zero.
/// </remarks>
internal sealed record NativePreviewVertexSkin(ushort[] Joints, byte[] Weights);
