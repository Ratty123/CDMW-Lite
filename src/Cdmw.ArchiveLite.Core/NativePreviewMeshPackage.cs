using System.Text.Json;

namespace Cdmw.ArchiveLite.Core;

internal sealed record NativePreviewMeshPackage(
    IReadOnlyList<NativePreviewMeshBatch> Batches,
    int TotalVertices,
    NativePreviewNormalization Normalization)
{
    private const int MaximumVertices = 8_000_000;

    /// <summary>Position, normal and texture coordinate, as doubles.</summary>
    internal const int ExportBytesPerVertex = 8 * sizeof(double);

    public static async Task<NativePreviewMeshPackage> ReadAsync(
        string packageRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(packageRoot);
        var manifestPath = Path.Combine(root, "manifest.json");
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        if (!manifest.RootElement.TryGetProperty("schema_version", out var schema)
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 8)
        {
            throw new InvalidDataException("Mesh export requires native preview manifest schema 8.");
        }
        if (!manifest.RootElement.TryGetProperty("batches", out var batchesElement)
            || batchesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Native preview manifest has no batches array.");
        }

        var submeshNames = ReadSubmeshNames(manifest.RootElement);
        var batches = new List<NativePreviewMeshBatch>();
        var totalVertices = 0;
        foreach (var element in batchesElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var index = ReadInt(element, "index", batches.Count);
            var vertexCount = ReadInt(element, "vertex_count", 0);
            if (vertexCount <= 0 || vertexCount % 3 != 0)
            {
                throw new InvalidDataException($"Native preview batch {index} has an invalid vertex count.");
            }
            totalVertices = checked(totalVertices + vertexCount);
            if (totalVertices > MaximumVertices)
            {
                throw new InvalidDataException($"Mesh export exceeds the {MaximumVertices:N0}-vertex safety limit.");
            }
            var geometry = ResolveContainedFile(root, ReadString(element, "vertex_file"));
            if (new FileInfo(geometry).Length
                != checked((long)vertexCount * NativePreviewGeometryIO.BytesPerPreviewVertex))
            {
                throw new InvalidDataException($"Native preview batch {index} has an invalid geometry length.");
            }
            var exportVertexCount = ReadInt(element, "export_vertex_count", 0);
            batches.Add(new NativePreviewMeshBatch(
                index,
                vertexCount,
                geometry,
                ResolveIdentityFile(root, element, vertexCount),
                ResolveExportGeometryFile(root, element, exportVertexCount),
                exportVertexCount,
                ReadString(element, "material_name"),
                FirstNonEmpty(ReadString(element, "submesh_name"), submeshNames.GetValueOrDefault(index, string.Empty)),
                ReadString(element, "submesh_texture"),
                ReadColor(element),
                ReadUnitFloat(element, "metalness", 0.0f),
                ReadUnitFloat(element, "roughness", 0.62f)));
        }
        if (batches.Count == 0)
        {
            throw new InvalidDataException("Native preview package did not contain renderable batches.");
        }
        return new NativePreviewMeshPackage(
            batches,
            totalVertices,
            NativePreviewNormalization.Read(manifest.RootElement));
    }

    private static string FirstNonEmpty(string first, string second) =>
        first.Length > 0 ? first : second;

    /// <summary>
    /// The vertices an interchange file carries: position, normal and texture coordinate as eight
    /// doubles each, in the source's own vertex order and its own space.
    /// </summary>
    /// <remarks>
    /// The render blob beside it holds the same mesh as the GPU wants it -- per corner, framed into
    /// the preview's display cube, narrowed to floats -- and exporting from it means undoing the
    /// framing and living with what the floats kept. CDMW Full decodes the source records in
    /// double and never frames them, so anything exported from the render blob differs from Full's
    /// in the eighth digit of every coordinate. This is the same decode, at the same width.
    /// </remarks>
    private static string? ResolveExportGeometryFile(string packageRoot, JsonElement element, int vertexCount)
    {
        var relative = ReadString(element, "export_vertex_file");
        if (string.IsNullOrWhiteSpace(relative) || vertexCount <= 0)
        {
            return null;
        }
        try
        {
            var path = ResolveContainedFile(packageRoot, relative);
            return new FileInfo(path).Length == checked((long)vertexCount * ExportBytesPerVertex)
                ? path
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name the source gave each submesh, keyed by batch index.
    /// </summary>
    /// <remarks>
    /// A batch publishes its material but not the submesh that carries it, so parts sharing one
    /// material would export under one name and stop being tellable apart -- a left and a right eye
    /// both arriving as the eye material. The name is in the manifest's material slots, which pair
    /// it with the batch index it belongs to. A package that predates the slots simply exports
    /// under the material name, as before.
    /// </remarks>
    private static Dictionary<int, string> ReadSubmeshNames(JsonElement manifest)
    {
        var names = new Dictionary<int, string>();
        if (!manifest.TryGetProperty("material_slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
        {
            return names;
        }
        foreach (var slot in slots.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object
                || !slot.TryGetProperty("batch_index", out var batchIndex)
                || !batchIndex.TryGetInt32(out var index))
            {
                continue;
            }
            var name = ReadString(slot, "submesh_name");
            if (name.Length > 0)
            {
                names[index] = name;
            }
        }
        return names;
    }

    /// <summary>
    /// The per-corner record of which source vertex each corner came from, when the package has one.
    /// </summary>
    /// <remarks>
    /// Rebuilding the index buffer numbers vertices in order of first appearance, which is not the
    /// order the source held them in. This is what lets the round-trip sidecar say truthfully which
    /// source vertex each exported one is, rather than claiming an identity mapping that welding
    /// has already broken. A package without it simply exports no mapping.
    /// </remarks>
    private static string? ResolveIdentityFile(string packageRoot, JsonElement element, int vertexCount)
    {
        if (!element.TryGetProperty("editor_identity", out var identity)
            || identity.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var relative = ReadString(identity, "identity_file");
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }
        try
        {
            var path = ResolveContainedFile(packageRoot, relative);
            // Two 32-bit fields per corner: the source submesh and the source vertex.
            return new FileInfo(path).Length == checked((long)vertexCount * 8) ? path : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string ResolveContainedFile(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Native preview package path must be relative.");
        }
        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate))
        {
            throw new InvalidDataException($"Native preview package path is missing or escapes its root: {relativePath}");
        }
        return candidate;
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static float[] ReadColor(JsonElement element)
    {
        if (!element.TryGetProperty("base_color", out var color) || color.ValueKind != JsonValueKind.Array)
        {
            return [0.65f, 0.65f, 0.65f];
        }
        var values = color.EnumerateArray().Take(3).Select(static item =>
            item.TryGetSingle(out var value) && float.IsFinite(value)
                ? Math.Clamp(value, 0.0f, 1.0f)
                : 0.65f).ToArray();
        return values.Length == 3 ? values : [0.65f, 0.65f, 0.65f];
    }

    private static float ReadUnitFloat(JsonElement element, string name, float fallback)
    {
        return element.TryGetProperty(name, out var value)
            && value.TryGetSingle(out var result)
            && float.IsFinite(result)
                ? Math.Clamp(result, 0.0f, 1.0f)
                : fallback;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}

// The preview package stores geometry the renderer's way: every position is
// recentred on the model's bounding box and rescaled into a two-unit cube so the
// camera can frame any asset the same. That normalization is display state, not
// the asset -- an export that copies those positions straight out hands the user
// a mesh with the source's own placement and size erased. The manifest publishes
// the centre and scale that were applied, so the export path can undo them and
// write the coordinates the archive actually holds.
internal sealed record NativePreviewNormalization(double CenterX, double CenterY, double CenterZ, double Scale)
{
    public static NativePreviewNormalization Identity { get; } = new(0.0, 0.0, 0.0, 1.0);

    public bool IsIdentity => Scale == 1.0 && CenterX == 0.0 && CenterY == 0.0 && CenterZ == 0.0;

    public double RestoreX(double value) => (value / Scale) + CenterX;

    public double RestoreY(double value) => (value / Scale) + CenterY;

    public double RestoreZ(double value) => (value / Scale) + CenterZ;

    public double Restore(double value, int axis) => axis switch
    {
        0 => RestoreX(value),
        1 => RestoreY(value),
        _ => RestoreZ(value),
    };

    public static NativePreviewNormalization Read(JsonElement manifest)
    {
        var hasCenter = manifest.TryGetProperty("normalization_center", out var center);
        var hasScale = manifest.TryGetProperty("normalization_scale", out var scale);
        if (!hasCenter && !hasScale)
        {
            // Packages built before the manifest carried the framing transform
            // were written unnormalized, so their geometry is already source space.
            return Identity;
        }
        if (!hasCenter
            || center.ValueKind != JsonValueKind.Array
            || center.GetArrayLength() != 3
            || !hasScale
            || !scale.TryGetDouble(out var scaleValue)
            || !double.IsFinite(scaleValue)
            || scaleValue <= 0.0)
        {
            throw new InvalidDataException("Native preview manifest has an unusable geometry normalization.");
        }
        var components = new double[3];
        var axis = 0;
        foreach (var component in center.EnumerateArray())
        {
            if (!component.TryGetDouble(out var value) || !double.IsFinite(value))
            {
                throw new InvalidDataException("Native preview manifest has an unusable geometry normalization.");
            }
            components[axis++] = value;
        }
        return new NativePreviewNormalization(components[0], components[1], components[2], scaleValue);
    }
}

internal sealed record NativePreviewMeshBatch(
    int Index,
    int VertexCount,
    string GeometryPath,
    string? IdentityPath,
    string? ExportGeometryPath,
    int ExportVertexCount,
    string MaterialName,
    string SubmeshName,
    string TextureName,
    float[] BaseColor,
    float Metalness,
    float Roughness);
