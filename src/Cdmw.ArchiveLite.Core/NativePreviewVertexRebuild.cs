using System.Buffers.Binary;
using static Cdmw.ArchiveLite.Core.NativePreviewGeometryIO;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Rebuilds one batch's indexed vertex array from the preview package's corner-by-corner geometry.
/// </summary>
/// <remarks>
/// The package holds what the renderer submits to the GPU: three vertices for every triangle, with
/// nothing shared, because the index buffer is spent when the blob is written. An interchange file
/// has to carry the array the source held, and it has to carry it in the source's own order:
/// morph targets, shape keys and any other per-vertex correspondence are matched by index, so a
/// mesh whose vertices are the right points in the wrong order is worse than useless -- it loads,
/// and then deforms into noise.
///
/// The order comes from the package's identity buffer, which records for every corner the source
/// vertex it was copied from. Numbering those in ascending source order reproduces the array the
/// archive holds, because the preview parser itself emits vertices in that order. Rejoining by
/// matching attribute bits instead would recover the same points, but in order of first appearance
/// in the triangle stream, and would silently merge two source vertices that happen to agree on
/// position, normal and texture coordinate. Both are the difference between a mesh that morphs and
/// one that explodes.
///
/// A package written before the identity buffer existed has no such record, and falls back to
/// matching attribute bits. That path cannot reconstruct the source order, and is kept only so
/// that a stale cache still exports something rather than failing.
/// </remarks>
internal sealed class NativePreviewVertexRebuild
{
    /// <summary>Two 32-bit fields per corner: the source submesh, then the source vertex.</summary>
    private const int IdentityBytesPerCorner = 8;

    private NativePreviewVertexRebuild(
        int[] cornerIndices,
        int vertexCount,
        float[] positions,
        float[] normals,
        float[] textureCoordinates,
        int[]? sourceVertexMap)
    {
        CornerIndices = cornerIndices;
        VertexCount = vertexCount;
        Positions = positions;
        Normals = normals;
        TextureCoordinates = textureCoordinates;
        SourceVertexMap = sourceVertexMap;
    }

    /// <summary>The exported vertex each triangle corner refers to, in the package's corner order.</summary>
    public int[] CornerIndices { get; }

    public int VertexCount { get; }

    /// <summary>
    /// Three components per vertex, still in the preview's normalized frame. Callers undo that
    /// framing themselves, because the interchange formats disagree on the precision to undo it in.
    /// </summary>
    public float[] Positions { get; }

    public float[] Normals { get; }

    public float[] TextureCoordinates { get; }

    /// <summary>
    /// The source vertex each exported vertex came from, or null when the package carries no
    /// identity buffer. Ascending by construction, and the identity mapping whenever the source
    /// numbered its vertices from zero without gaps.
    /// </summary>
    public int[]? SourceVertexMap { get; }

    /// <param name="cornersRead">
    /// Reports corners consumed by the attribute pass, which visits every corner exactly once.
    /// </param>
    public static async Task<NativePreviewVertexRebuild> BuildAsync(
        NativePreviewMeshBatch batch,
        Func<int, Task>? cornersRead,
        CancellationToken cancellationToken)
    {
        var plan = batch.IdentityPath is null
            ? await PlanByAttributesAsync(batch, cancellationToken).ConfigureAwait(false)
            : await PlanBySourceIdentityAsync(batch, cancellationToken).ConfigureAwait(false);

        var positions = new float[checked(plan.VertexCount * 3)];
        var normals = new float[checked(plan.VertexCount * 3)];
        var textureCoordinates = new float[checked(plan.VertexCount * 2)];
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
            plan.SourceVertexMap);
    }

    private static void ReadComponents(
        byte[] input,
        int sourceOffset,
        float[] output,
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
