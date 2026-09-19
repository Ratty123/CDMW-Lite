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
            var name = NativeModelExportService.CleanName(batch.MaterialName,
                NativeModelExportService.CleanName(batch.SubmeshName, $"part_{batch.Index:000}"));
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
            // A texture exported alongside is bound by its actual relative path. Otherwise the
            // source's own texture name is stated, which is what CDMW Full writes: the name is
            // what the material is for, and an importer that has the file under that name finds it.
            var texture = ResolveTexture(batch, companionTextures);
            if (texture is not null)
            {
                builder.Append("map_Kd ").Append(texture).Append('\n');
            }
            builder.Append('\n');
        }
        // The blank line between definitions separates them; after the last there is nothing to
        // separate, and CDMW Full's library ends on the final definition rather than a blank.
        while (builder.Length >= 2 && builder[^1] == '\n' && builder[^2] == '\n')
        {
            builder.Length--;
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
    /// The source's texture name as a material-library reference, given the extension the archive
    /// stores these under when the name carries none of its own.
    /// </summary>
    private static string? TextureReference(string? textureName)
    {
        var normalized = textureName?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        if (normalized.Any(char.IsControl)) throw new InvalidDataException("Texture reference contains a control character.");
        return Path.HasExtension(normalized) ? normalized : normalized + ".dds";
    }

    internal static string? ResolveTexture(NativePreviewMeshBatch batch, IReadOnlyList<string> companionTextures)
    {
        var authored = TextureReference(batch.TextureName);
        var textures = companionTextures.Select(static path => path.Replace('\\', '/'))
            .Where(static path => path.IndexOfAny(['\r', '\n', '\0']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (authored is not null)
        {
            var pathMatches = textures.Where(path => path.Equals(authored, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/" + authored.TrimStart('/'), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (pathMatches.Length == 1) return pathMatches[0];
            // A qualified source path must never pick a same-named file from another folder.
            if (authored.Contains('/')) return authored;
            var matches = textures.Where(path => Path.GetFileName(path).Equals(authored, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : authored;
        }
        var material = Path.GetFileName(batch.MaterialName.Trim());
        if (material.Length == 0) return null;
        var aliases = new[] { material, material + "_d", material + "_diffuse", material + "_albedo", material + "_basecolor" };
        var candidates = textures.Where(path => aliases.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
