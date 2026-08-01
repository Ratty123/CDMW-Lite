using System.Buffers.Binary;
using static Cdmw.ArchiveLite.Core.NativePreviewGeometryIO;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// The armature a GLB export writes, and the per-vertex binding that drives it.
/// </summary>
/// <remarks>
/// <para>Only the bones the mesh actually uses are emitted, plus the ancestors that carry them.
/// A character rig has 448 bones and a body binds to 206 of them; writing the rest hands the
/// user hundreds of joints that nothing moves and nothing can be posed through.</para>
/// <para>A batch is bound only if every one of its vertices has weight to give. glTF states that
/// a skinned vertex's weights sum to one, and a vertex with none would import into Blender
/// belonging to no vertex group at all -- so rather than park those on whichever joint happened
/// to be first, which is how a mesh ends up following the root of the skeleton around, the batch
/// is written unrigged and says so.</para>
/// </remarks>
internal sealed class NativeGlbRig
{
    /// <summary>Six influences per vertex; a glTF attribute set holds four, so it takes two.</summary>
    public const int AttributeSets = 2;

    private const int JointsPerSet = 4;

    private readonly NativePreviewSkeleton skeleton;
    private readonly IReadOnlyList<NativePreviewVertexRebuild> meshes;
    private readonly bool[] bound;
    private readonly int[] jointOfBone;

    private NativeGlbRig(
        NativePreviewSkeleton skeleton,
        IReadOnlyList<NativePreviewVertexRebuild> meshes,
        bool[] bound,
        IReadOnlyList<int> joints,
        int[] jointOfBone)
    {
        this.skeleton = skeleton;
        this.meshes = meshes;
        this.bound = bound;
        this.jointOfBone = jointOfBone;
        Joints = joints;
    }

    /// <summary>Bone indices in the skeleton, one per glTF joint, in joint order.</summary>
    public IReadOnlyList<int> Joints { get; }

    public bool IsEmpty => Joints.Count == 0;

    public static NativeGlbRig Empty(IReadOnlyList<NativePreviewVertexRebuild> meshes) =>
        new(NativePreviewSkeleton.None, meshes, new bool[meshes.Count], [], []);

    public bool Binds(int batchIndex) => bound[batchIndex];

    public NativePreviewBone Bone(int joint) => skeleton.Bones[Joints[joint]];

    public IEnumerable<int> ChildJoints(int joint)
    {
        var bone = Joints[joint];
        for (var candidate = 0; candidate < Joints.Count; candidate++)
        {
            if (skeleton.Bones[Joints[candidate]].ParentIndex == bone)
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Joints whose parent bone is not itself a joint, so they hang from the scene.</summary>
    public IEnumerable<int> RootJoints()
    {
        for (var joint = 0; joint < Joints.Count; joint++)
        {
            var parent = skeleton.Bones[Joints[joint]].ParentIndex;
            if (parent < 0 || jointOfBone[parent] < 0)
            {
                yield return joint;
            }
        }
    }

    public static NativeGlbRig Build(
        NativePreviewMeshPackage package,
        IReadOnlyList<NativePreviewVertexRebuild> meshes)
    {
        var skeleton = package.Skeleton;
        if (!skeleton.IsRigged)
        {
            return Empty(meshes);
        }

        var bound = new bool[meshes.Count];
        var usesBone = new bool[skeleton.Bones.Count];
        var anyBound = false;
        for (var index = 0; index < meshes.Count; index++)
        {
            var skin = meshes[index].Skin;
            if (skin is null || !TryMarkUsedBones(skin, meshes[index].VertexCount, skeleton.Bones.Count, usesBone))
            {
                continue;
            }
            bound[index] = true;
            anyBound = true;
        }
        if (!anyBound)
        {
            return Empty(meshes);
        }

        // Every ancestor of a used bone has to travel too, or the joints that do the deforming
        // arrive detached from the transforms that place them.
        for (var bone = 0; bone < usesBone.Length; bone++)
        {
            if (!usesBone[bone])
            {
                continue;
            }
            for (var parent = skeleton.Bones[bone].ParentIndex;
                 parent >= 0 && !usesBone[parent];
                 parent = skeleton.Bones[parent].ParentIndex)
            {
                usesBone[parent] = true;
            }
        }

        var joints = new List<int>();
        var jointOfBone = new int[skeleton.Bones.Count];
        Array.Fill(jointOfBone, -1);
        for (var bone = 0; bone < usesBone.Length; bone++)
        {
            if (!usesBone[bone])
            {
                continue;
            }
            jointOfBone[bone] = joints.Count;
            joints.Add(bone);
        }
        return new NativeGlbRig(skeleton, meshes, bound, joints, jointOfBone);
    }

    /// <summary>
    /// Marks the bones this batch drives, or reports that it cannot be bound at all.
    /// </summary>
    private static bool TryMarkUsedBones(NativePreviewVertexSkin skin, int vertexCount, int boneCount, bool[] usesBone)
    {
        var influences = NativePreviewMeshPackage.SkinInfluencesPerVertex;
        var marked = new List<int>(influences);
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var total = 0;
            marked.Clear();
            for (var influence = 0; influence < influences; influence++)
            {
                var slot = (vertex * influences) + influence;
                var weight = skin.Weights[slot];
                var joint = skin.Joints[slot];
                if (weight == 0 || joint == NativePreviewMeshPackage.UnusedSkinBone)
                {
                    continue;
                }
                if (joint >= boneCount)
                {
                    return false;
                }
                total += weight;
                marked.Add(joint);
            }
            if (total == 0)
            {
                return false;
            }
            foreach (var joint in marked)
            {
                usesBone[joint] = true;
            }
        }
        return true;
    }

    /// <summary>
    /// Writes this batch's two joint and weight attribute sets, interleaved set by set.
    /// </summary>
    /// <remarks>
    /// The weights are divided by their own total rather than by 255. The record's six bytes sum
    /// to 255 give or take a unit or two of rounding, and glTF wants a sum of exactly one, so
    /// dividing by the constant would leave a vertex a fraction light or heavy and drift its
    /// deformation away from where the source put it.
    /// </remarks>
    public async Task WriteBindingAsync(int batchIndex, Stream output, CancellationToken cancellationToken)
    {
        var mesh = meshes[batchIndex];
        var skin = mesh.Skin
            ?? throw new InvalidOperationException("A bound batch must carry skin rows.");
        // Each accessor reads a run of its own, so a set's joints are written through before its
        // weights begin rather than chunk by chunk in step with them.
        for (var set = 0; set < AttributeSets; set++)
        {
            await WriteLanesAsync(skin, mesh.VertexCount, set, joints: true, output, cancellationToken)
                .ConfigureAwait(false);
            await WriteLanesAsync(skin, mesh.VertexCount, set, joints: false, output, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteLanesAsync(
        NativePreviewVertexSkin skin,
        int vertexCount,
        int set,
        bool joints,
        Stream output,
        CancellationToken cancellationToken)
    {
        var laneBytes = joints ? sizeof(ushort) : sizeof(float);
        var buffer = new byte[RecordsPerChunk * JointsPerSet * laneBytes];
        var lanes = new Influence[NativePreviewMeshPackage.SkinInfluencesPerVertex];
        var vertex = 0;
        while (vertex < vertexCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RecordsPerChunk, vertexCount - vertex);
            for (var local = 0; local < count; local++)
            {
                var (used, total) = Merge(skin, vertex + local, lanes);
                for (var lane = 0; lane < JointsPerSet; lane++)
                {
                    var index = (set * JointsPerSet) + lane;
                    var offset = ((local * JointsPerSet) + lane) * laneBytes;
                    if (joints)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            buffer.AsSpan(offset, sizeof(ushort)),
                            index < used ? (ushort)lanes[index].Joint : (ushort)0);
                    }
                    else
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(
                            buffer.AsSpan(offset, sizeof(float)),
                            index < used ? lanes[index].Weight / (float)total : 0.0f);
                    }
                }
            }
            await output.WriteAsync(
                buffer.AsMemory(0, count * JointsPerSet * laneBytes),
                cancellationToken).ConfigureAwait(false);
            vertex += count;
        }
    }

    /// <summary>
    /// One vertex's influences with any repeated joint summed into a single entry.
    /// </summary>
    /// <remarks>
    /// A record can name the same bone twice and split its share between the two entries; 17 of one
    /// legwear's 8,379 vertices do. The two are the same influence written twice, and adding them
    /// together says so. Passing them through as they stand gives glTF a vertex whose joint list
    /// repeats, and a consumer that assigns each influence to a vertex group rather than
    /// accumulating into it -- which is what Blender's importer does -- keeps only the last and
    /// drops the rest of that bone's weight, leaving the vertex weighted to less than one.
    /// </remarks>
    private (int Used, int Total) Merge(NativePreviewVertexSkin skin, int vertex, Influence[] lanes)
    {
        var influences = NativePreviewMeshPackage.SkinInfluencesPerVertex;
        var row = vertex * influences;
        var used = 0;
        var total = 0;
        for (var influence = 0; influence < influences; influence++)
        {
            var bone = skin.Joints[row + influence];
            var weight = skin.Weights[row + influence];
            if (bone == NativePreviewMeshPackage.UnusedSkinBone || weight == 0)
            {
                continue;
            }
            var joint = jointOfBone[bone];
            total += weight;
            var existing = Array.FindIndex(lanes, 0, used, lane => lane.Joint == joint);
            if (existing >= 0)
            {
                lanes[existing] = lanes[existing] with { Weight = lanes[existing].Weight + weight };
                continue;
            }
            lanes[used++] = new Influence(joint, weight);
        }
        return (used, total);
    }

    private readonly record struct Influence(int Joint, int Weight);

    public async Task WriteInverseBindMatricesAsync(Stream output, CancellationToken cancellationToken)
    {
        if (IsEmpty)
        {
            return;
        }
        var buffer = new byte[16 * sizeof(float)];
        foreach (var bone in Joints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matrix = skeleton.Bones[bone].InverseBindMatrix;
            for (var element = 0; element < matrix.Length; element++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    buffer.AsSpan(element * sizeof(float), sizeof(float)),
                    matrix[element]);
            }
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
