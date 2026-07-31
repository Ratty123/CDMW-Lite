using System.Text;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Writes the material library an exported OBJ names.
/// </summary>
/// <remarks>
/// The OBJ writer emits a <c>usemtl</c> for every submesh whether or not anything defines those
/// materials. Without a library beside it the names resolve to nothing and the model arrives
/// uniformly grey, having lost even the distinction between its parts. The definitions here are
/// deliberately plain -- a Wavefront material cannot express the packed roughness and metal maps
/// the source actually uses -- but they name each part and bind a texture when one was exported
/// alongside.
/// </remarks>
internal static class NativeObjMaterialWriter
{
    public static string DestinationFor(string objDestination) =>
        Path.ChangeExtension(objDestination, ".mtl");

    public static async Task WriteAsync(
        NativePreviewMeshPackage package,
        string objDestination,
        IReadOnlyList<string> companionTextures,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = DestinationFor(objDestination);
        var builder = new StringBuilder();
        builder.Append("# Crimson Desert Materials\n\n");
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in package.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NativeModelExportService.CleanName(batch.MaterialName, $"part_{batch.Index:000}");
            if (!written.Add(name))
            {
                // Several submeshes can share one material, and a repeated newmtl is an error in
                // some importers and silently last-wins in others.
                continue;
            }
            builder.Append("newmtl ").Append(name).Append('\n');
            builder.Append("Ka 1.000 1.000 1.000\n");
            // Neutral rather than the batch's base colour. A mesh-only package resolves no
            // textures, so that colour is the palette hue preview-core assigns each submesh purely
            // to tell them apart on screen -- publishing it here would hand over a model tinted
            // peach and mauve as though the source said so.
            builder.Append("Kd 0.800 0.800 0.800\n");
            builder.Append("Ks 0.100 0.100 0.100\n");
            builder.Append("Ns 50.000\n");
            builder.Append("d 1.000\n");
            builder.Append("illum 2\n");
            var texture = MatchTexture(name, batch.MaterialName, companionTextures);
            if (texture is not null)
            {
                builder.Append("map_Kd ").Append(texture).Append('\n');
            }
            builder.Append('\n');
        }

        await AtomicFile.WriteAsync(
            destination,
            async (stream, token) =>
            {
                var bytes = Encoding.UTF8.GetBytes(builder.ToString());
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            },
            cancellationToken,
            overwrite).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the exported texture that belongs to a material.
    /// </summary>
    /// <remarks>
    /// A material and the texture authored for it are named the same thing in different dress --
    /// <c>CD_PHW_00_Nude_00_0001</c> against <c>cd_phw_00_nude_00_0001.dds</c> -- so the comparison
    /// drops case and every separator rather than trying to match them literally. A texture that
    /// belongs to no material is simply not bound; it still ships beside the mesh.
    /// </remarks>
    private static string? MatchTexture(
        string materialName,
        string rawMaterialName,
        IReadOnlyList<string> companionTextures)
    {
        if (companionTextures.Count == 0)
        {
            return null;
        }
        var candidates = new[] { Normalize(materialName), Normalize(rawMaterialName) }
            .Where(static alias => alias.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        string? best = null;
        foreach (var texture in companionTextures)
        {
            var stem = Normalize(Path.GetFileNameWithoutExtension(texture));
            if (stem.Length == 0)
            {
                continue;
            }
            foreach (var alias in candidates)
            {
                if (stem.Equals(alias, StringComparison.Ordinal))
                {
                    return texture.Replace('\\', '/');
                }
                // A base map often carries a suffix the material name does not, such as a
                // per-part `_01`. A prefix match is the weaker evidence, so it only stands in
                // when nothing matches outright.
                if (best is null && stem.StartsWith(alias, StringComparison.Ordinal))
                {
                    best = texture.Replace('\\', '/');
                }
            }
        }
        return best;
    }

    private static string Normalize(string? value) => value is null
        ? string.Empty
        : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
