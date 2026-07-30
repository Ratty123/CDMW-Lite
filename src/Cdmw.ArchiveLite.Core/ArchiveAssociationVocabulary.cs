using System.Text.RegularExpressions;
using Cdmw.Archive.Content;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// The vocabulary that turns file names into links, derived from the one capability manifest so a
/// format the registry already knows can be followed, found as a companion, and grouped without
/// anyone remembering to copy its extension into a second hand-written list.
/// </summary>
internal static class ArchiveAssociationVocabulary
{
    /// <summary>
    /// Sidecar suffixes the manifest cannot express because they carry two extensions: the dotted
    /// twins of the underscore <c>_xml</c> sidecars, and the socket sidecar that only appears dotted.
    /// A reference to one still parses without them, because the reference pattern treats a dot as a
    /// path separator, but stripping a family stem needs the whole suffix in one piece.
    /// </summary>
    private static readonly string[] CompositeSuffixes =
    [
        ".prefabdata.xml", ".prefab.xml", ".pamlod.xml", ".pac.xml", ".pam.xml", ".app.xml", ".sockets.xml",
    ];

    /// <summary>
    /// Variant suffixes the texture classifier does not read as a usage but that archives still name
    /// surfaces with, kept here so widening this list cannot change how a texture is classified.
    /// </summary>
    private static readonly string[] TextureVariantExtras =
    [
        "_r", "_s", "_a", "_emissive", "_opacity",
    ];

    /// <summary>
    /// The formats that take part in a same-name asset family, longest suffix first so stripping a
    /// stem always removes the most specific suffix a name ends with.
    /// </summary>
    public static IReadOnlyList<string> FamilySuffixes { get; } = BuildFamilySuffixes();

    /// <summary>Texture names a family stem can carry, including the variant-suffixed forms.</summary>
    public static IReadOnlyList<string> TextureFamilySuffixes { get; } = BuildTextureFamilySuffixes();

    /// <summary>The formats worth decoding to look for names of other files inside them.</summary>
    public static IReadOnlySet<string> ReferenceContainerExtensions { get; } = BuildReferenceContainers();

    /// <summary>Matches a file name of a registered format inside decoded text.</summary>
    public static Regex AssetReferencePattern { get; } = BuildReferencePattern();

    /// <summary>Matches the trailing texture-variant run of a file stem.</summary>
    public static Regex TextureVariantSuffix { get; } = BuildTextureVariantPattern();

    private static IReadOnlyList<string> BuildFamilySuffixes() =>
    [
        .. ArchiveContentRegistry.All
            .Where(IsFamilyMember)
            .Select(static capability => capability.Extension)
            .Concat(CompositeSuffixes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static suffix => suffix.Length)
            .ThenBy(static suffix => suffix, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Meshes, their metadata, and the animation and scene data authored beside them share a stem.
    /// Textures are excluded because they carry variant suffixes and are expanded separately, and
    /// media and loose text are excluded because a shared stem there is a coincidence, not a family.
    /// </summary>
    private static bool IsFamilyMember(ArchiveContentCapability capability) =>
        capability.Group is "model_mesh_physics" or "material_metadata" or "animation_scene"
        || capability.Role is "metadata";

    private static IReadOnlyList<string> BuildTextureFamilySuffixes()
    {
        var variants = TextureVariantTokens();
        var suffixes = new List<string>();
        foreach (var capability in ArchiveContentRegistry.All.Where(static item => item.Group == "texture_image"))
        {
            suffixes.Add(capability.Extension);

            // Variant naming is a texture-authoring convention that the archives only apply to their
            // own texture formats, so expanding every interchange image format would multiply the
            // candidate names without ever naming a file that exists.
            if (capability.Extension is ".dds" or ".texture")
            {
                suffixes.AddRange(variants.Select(variant => variant + capability.Extension));
            }
        }
        return [.. suffixes.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Anything the manifest says can name another file, minus the formats whose payload is pixels or
    /// samples: scanning those for text finds noise, never a reference. A Wwise bank is the exception
    /// because its media table names the streamed sounds that play with it.
    /// </summary>
    private static IReadOnlySet<string> BuildReferenceContainers() =>
        ArchiveContentRegistry.All
            .Where(static capability => capability.References
                && capability.Group != "texture_image"
                && (capability.Group != "audio_video" || capability.Extension == ".bnk"))
            .Select(static capability => capability.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Regex BuildReferencePattern()
    {
        // A dot separates path segments here as much as a slash does, so a name never has to be
        // guessed apart from the suffix that follows it, and a two-extension sidecar still matches
        // whole. The trailing guard is what keeps `city.paccd` from being read as `city.pac`.
        var alternation = string.Join(
            '|',
            ArchiveContentRegistry.All
                .Select(static capability => capability.Extension)
                .Where(static extension => extension.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static extension => extension.Length)
                .ThenBy(static extension => extension, StringComparer.Ordinal)
                .Select(static extension => Regex.Escape(extension[1..])));
        return new Regex(
            @"(?<path>(?:[A-Za-z0-9_@%+\-]+[./\\])*[A-Za-z0-9_@%+\-]+\.(?:" + alternation + @"))(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));
    }

    private static Regex BuildTextureVariantPattern()
    {
        var alternation = string.Join(
            '|',
            TextureVariantTokens()
                .Select(static token => Regex.Escape(token[1..]))
                .OrderByDescending(static token => token.Length)
                .ThenBy(static token => token, StringComparer.Ordinal));
        return new Regex(
            $"(?:_(?:{alternation}))+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }

    private static IReadOnlyList<string> TextureVariantTokens() =>
    [
        .. ArchiveContentClassification.TextureVariantSuffixes
            .Concat(TextureVariantExtras)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];
}
