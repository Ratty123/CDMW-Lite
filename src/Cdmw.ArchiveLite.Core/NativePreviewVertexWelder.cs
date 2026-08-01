using System.Buffers.Binary;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Rejoins a package's corner-by-corner geometry by matching attribute bits, for packages written
/// before the identity buffer recorded where each corner came from.
/// </summary>
/// <remarks>
/// The package holds what the renderer submits to the GPU: three vertices for every triangle, with
/// nothing shared, because the index buffer is spent when the blob is written. Exported as it
/// stands, that produces a mesh whose every triangle is an island -- it renders correctly, since
/// each corner keeps its own normal, but it has no edge loops, nothing to select as linked, and no
/// clean way to subdivide.
///
/// Matching on position, normal and texture coordinate recovers most of what the blob split, but it
/// cannot recover the source's vertex order, and it merges two source vertices that agree on all
/// three. <see cref="NativePreviewVertexRebuild"/> uses the identity buffer instead wherever the
/// package has one, and only falls back here when it does not.
/// </remarks>
internal sealed class NativePreviewVertexWelder
{
    private readonly Dictionary<WeldKey, int> _assigned;

    public NativePreviewVertexWelder(int expectedCorners) =>
        // Most meshes collapse several-fold; sizing for a third avoids the early growth spurts
        // without reserving for a worst case that does not occur.
        _assigned = new Dictionary<WeldKey, int>(Math.Clamp(expectedCorners / 3, 16, 1 << 20));

    public int UniqueCount => _assigned.Count;

    /// <summary>
    /// Returns the index this corner's vertex occupies, and whether it is newly emitted.
    /// </summary>
    public bool TryAssign(ReadOnlySpan<byte> corner, out int index)
    {
        var key = KeyFor(corner);
        if (_assigned.TryGetValue(key, out index))
        {
            return false;
        }
        index = _assigned.Count;
        _assigned.Add(key, index);
        return true;
    }

    private static WeldKey KeyFor(ReadOnlySpan<byte> corner) => new(
        Pair(corner, 0, 4),
        Pair(corner, 8, 12),
        Pair(corner, 16, 20),
        Pair(corner, 36, 40));

    private static ulong Pair(ReadOnlySpan<byte> corner, int first, int second) =>
        ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(corner[first..]) << 32)
        | BinaryPrimitives.ReadUInt32LittleEndian(corner[second..]);

    // Position, normal and texture coordinate as raw bits: the comparison has to be exact, and
    // float equality would fold negative zero onto zero and blow up on a value it cannot order.
    private readonly record struct WeldKey(ulong Position, ulong PositionNormal, ulong Normal, ulong Texture);
}
