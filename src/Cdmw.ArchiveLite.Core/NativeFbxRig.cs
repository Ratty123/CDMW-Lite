namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// The rig an FBX export carries: the bone payload the native writer builds LimbNodes from, and
/// the per-vertex influences it groups into Skin and Cluster deformers.
/// </summary>
/// <remarks>
/// <para>Every matrix here is in the row-vector convention the <c>.pab</c> stores and FBX uses:
/// row-major, translation in row 3, so the sixteen values map across without transposition.</para>
/// <para>Unlike the glTF path, the whole skeleton travels rather than the bones the mesh uses plus
/// their ancestors. FBX is the format an animation clip is brought back in through, and a clip
/// addresses bones this mesh does not happen to be weighted to; a rig pruned to one garment's
/// bones cannot receive one. It is also what CDMW Full writes, so the two agree.</para>
/// </remarks>
internal static class NativeFbxRig
{
    /// <summary>
    /// The <c>bones</c> array for the export job, or an empty list when there is no rig.
    /// </summary>
    public static List<Dictionary<string, object?>> BonePayloads(NativePreviewSkeleton skeleton)
    {
        var payloads = new List<Dictionary<string, object?>>();
        if (!skeleton.IsRigged)
        {
            return payloads;
        }

        // The global bind poses first, so a bone's local transform can be taken against its
        // parent's.
        var binds = new double[skeleton.Bones.Count][];
        for (var index = 0; index < skeleton.Bones.Count; index++)
        {
            binds[index] = Orthonormal(skeleton.Bones[index].BindMatrix);
        }

        for (var index = 0; index < skeleton.Bones.Count; index++)
        {
            var bone = skeleton.Bones[index];
            var bind = binds[index];
            var local = bone.ParentIndex >= 0
                ? Multiply(bind, InvertRigid(binds[bone.ParentIndex]))
                : bind;
            payloads.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["name"] = bone.Name,
                ["parent_index"] = bone.ParentIndex,
                ["position"] = new[] { local[12], local[13], local[14] },
                ["rotation"] = EulerXyzDegrees(local),
                ["bind_matrix"] = bind,
            });
        }
        return payloads;
    }

    /// <summary>
    /// One submesh's influences in skeleton-bone space, normalized, or null if it cannot bind.
    /// </summary>
    /// <remarks>
    /// A bone named twice in one vertex has its shares summed rather than passed through as two
    /// entries. The source does split a bone's share that way, and left alone it puts the same
    /// vertex into one cluster twice; an importer that assigns rather than accumulates then keeps
    /// only the last and leaves the vertex weighted to less than one.
    /// </remarks>
    public static FbxSkinRows? BuildRows(NativePreviewVertexRebuild rebuilt, NativePreviewSkeleton skeleton)
    {
        var skin = rebuilt.Skin;
        if (!skeleton.IsRigged || skin is null)
        {
            return null;
        }
        var influences = NativePreviewMeshPackage.SkinInfluencesPerVertex;
        var counts = new int[rebuilt.VertexCount];
        var flatBones = new List<int>(rebuilt.VertexCount);
        var flatWeights = new List<double>(rebuilt.VertexCount);
        var merged = new List<(int Bone, int Weight)>(influences);
        for (var vertex = 0; vertex < rebuilt.VertexCount; vertex++)
        {
            merged.Clear();
            var total = 0;
            for (var influence = 0; influence < influences; influence++)
            {
                var slot = (vertex * influences) + influence;
                var bone = skin.Joints[slot];
                var weight = skin.Weights[slot];
                if (bone == NativePreviewMeshPackage.UnusedSkinBone || weight == 0)
                {
                    continue;
                }
                if (bone >= skeleton.Bones.Count)
                {
                    return null;
                }
                total += weight;
                var existing = merged.FindIndex(entry => entry.Bone == bone);
                if (existing >= 0)
                {
                    merged[existing] = (bone, merged[existing].Weight + weight);
                }
                else
                {
                    merged.Add((bone, weight));
                }
            }
            if (total == 0)
            {
                // Every vertex of a bound mesh has weight to give. One that does not cannot be
                // placed, and a cluster on a guessed bone is worse than no cluster at all.
                return null;
            }
            counts[vertex] = merged.Count;
            foreach (var (bone, weight) in merged)
            {
                flatBones.Add(bone);
                flatWeights.Add(weight / (double)total);
            }
        }
        return new FbxSkinRows(counts, [.. flatBones], [.. flatWeights]);
    }

    /// <summary>The same bind pose with its 3x3 reduced to a pure rotation.</summary>
    /// <remarks>
    /// Real rig bones carry scale, and a skeleton bone cannot hold scale in a rest pose, so it is
    /// dropped here rather than left to be dropped inconsistently further down. The mesh still
    /// arrives undeformed because the cluster's inverse bind is taken from this same matrix.
    /// </remarks>
    private static double[] Orthonormal(float[] matrix)
    {
        var axes = new double[3][];
        for (var axis = 0; axis < 3; axis++)
        {
            var vector = new double[3];
            for (var component = 0; component < 3; component++)
            {
                vector[component] = matrix[(axis * 4) + component];
            }
            for (var done = 0; done < axis; done++)
            {
                // Gram-Schmidt against the axes already fixed.
                var projection = 0.0;
                for (var component = 0; component < 3; component++)
                {
                    projection += vector[component] * axes[done][component];
                }
                for (var component = 0; component < 3; component++)
                {
                    vector[component] -= projection * axes[done][component];
                }
            }
            var length = Math.Sqrt((vector[0] * vector[0]) + (vector[1] * vector[1]) + (vector[2] * vector[2]));
            if (length <= 1.0e-9)
            {
                vector = [axis == 0 ? 1.0 : 0.0, axis == 1 ? 1.0 : 0.0, axis == 2 ? 1.0 : 0.0];
                length = 1.0;
            }
            for (var component = 0; component < 3; component++)
            {
                vector[component] /= length;
            }
            axes[axis] = vector;
        }
        var result = new double[16];
        for (var axis = 0; axis < 3; axis++)
        {
            for (var component = 0; component < 3; component++)
            {
                result[(axis * 4) + component] = axes[axis][component];
            }
        }
        result[12] = matrix[12];
        result[13] = matrix[13];
        result[14] = matrix[14];
        result[15] = 1.0;
        return result;
    }

    private static double[] Multiply(double[] left, double[] right)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var sum = 0.0;
                for (var k = 0; k < 4; k++)
                {
                    sum += left[(row * 4) + k] * right[(k * 4) + column];
                }
                result[(row * 4) + column] = sum;
            }
        }
        return result;
    }

    /// <summary>Inverse of a rotation-plus-translation matrix in row-vector form.</summary>
    private static double[] InvertRigid(double[] matrix)
    {
        var result = new double[16];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                result[(row * 4) + column] = matrix[(column * 4) + row];
            }
        }
        for (var column = 0; column < 3; column++)
        {
            var moved = 0.0;
            for (var k = 0; k < 3; k++)
            {
                moved -= matrix[12 + k] * result[(k * 4) + column];
            }
            result[12 + column] = moved;
        }
        result[15] = 1.0;
        return result;
    }

    /// <summary>FBX eEulerXYZ angles, in degrees, for a row-vector rotation matrix.</summary>
    private static double[] EulerXyzDegrees(double[] matrix)
    {
        // Column-vector form, where the standard R = Rz * Ry * Rx extraction applies.
        double Element(int row, int column) => matrix[(column * 4) + row];
        var sy = Math.Clamp(-Element(2, 0), -1.0, 1.0);
        var y = Math.Asin(sy);
        double x;
        double z;
        if (Math.Abs(Element(2, 0)) < 1.0 - 1.0e-9)
        {
            x = Math.Atan2(Element(2, 1), Element(2, 2));
            z = Math.Atan2(Element(1, 0), Element(0, 0));
        }
        else
        {
            // Gimbal lock: fold the free angle into x.
            x = Math.Atan2(-Element(1, 2), Element(1, 1));
            z = 0.0;
        }
        return [x * 180.0 / Math.PI, y * 180.0 / Math.PI, z * 180.0 / Math.PI];
    }
}

/// <summary>
/// A submesh's influences flattened the way the native writer reads them: how many bones drive
/// each vertex, then those bones and their shares run together in vertex order.
/// </summary>
internal sealed record FbxSkinRows(int[] Counts, int[] Bones, double[] Weights);
