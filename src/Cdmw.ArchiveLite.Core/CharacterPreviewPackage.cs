using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cdmw.ArchiveLite.Core;

internal sealed record CharacterPreviewPart(string PackagePath, double Scale);

/// <summary>Combines native preview parts in their shared source coordinates, before one camera fit.</summary>
internal static class CharacterPreviewPackage
{
    private const int Stride = 23 * sizeof(float);
    private const int MaximumVertices = 8_000_000;

    internal static bool IsReusable(string root)
    {
        if (!NativePreviewPackageAdapter.HasCurrentAdapterMetadata(root)) return false;
        try
        {
            foreach (var name in new[] { "manifest.json", "net_materials.json", "dotnet_scene.json" })
                if (!File.Exists(Path.Combine(root, name))) return false;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            var batches = manifest.RootElement.GetProperty("batches");
            return batches.GetArrayLength() > 0 && batches.EnumerateArray().All(batch =>
            {
                var geometry = new FileInfo(ContainedFile(root, batch.GetProperty("vertex_file").GetString()!));
                return geometry.Exists && geometry.Length == (long)batch.GetProperty("vertex_count").GetInt32() * Stride;
            });
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    public static async Task ComposeAsync(string destination, string sourcePath,
        IReadOnlyList<CharacterPreviewPart> parts, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        var batches = new JsonArray();
        var geometry = new List<(string Path, byte[] Bytes)>();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        long total = 0;
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            token.ThrowIfCancellationRequested();
            var part = parts[partIndex];
            var root = Path.GetFullPath(part.PackagePath);
            var manifestPath = Path.Combine(root, "manifest.json");
            if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Character preview manifest exceeds 16 MiB.");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false))!.AsObject();
            if (manifest["schema_version"]?.GetValue<int>() != 8)
                throw new InvalidDataException("Unsupported character component preview schema.");
            var center = manifest["normalization_center"]!.AsArray();
            var sourceCenter = new Vector3(center[0]!.GetValue<float>(), center[1]!.GetValue<float>(), center[2]!.GetValue<float>());
            var sourceScale = manifest["normalization_scale"]!.GetValue<float>();
            var scale = (float)part.Scale;
            if (!float.IsFinite(sourceScale) || sourceScale <= 0 || !float.IsFinite(scale) || scale <= 0)
                throw new InvalidDataException("Invalid character component scale.");
            var prefix = $"part-{partIndex:00}";
            foreach (var source in manifest["batches"]!.AsArray())
            {
                token.ThrowIfCancellationRequested();
                var batch = source!.DeepClone().AsObject();
                var count = batch["vertex_count"]!.GetValue<int>();
                if (count <= 0 || count % 3 != 0 || (total += count) > MaximumVertices)
                    throw new InvalidDataException("Character preview exceeds the supported vertex limit.");
                var vertexPath = ContainedFile(root, batch["vertex_file"]!.GetValue<string>());
                if (new FileInfo(vertexPath).Length != checked((long)count * Stride))
                    throw new InvalidDataException("Character component geometry length is invalid.");
                var bytes = await File.ReadAllBytesAsync(vertexPath, token).ConfigureAwait(false);
                for (var offset = 0; offset < bytes.Length; offset += Stride)
                {
                    if ((offset / Stride & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                    var position = (ReadPosition(bytes, offset) / sourceScale + sourceCenter) * scale;
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                        throw new InvalidDataException("Character component contains a non-finite position.");
                    minimum = Vector3.Min(minimum, position);
                    maximum = Vector3.Max(maximum, position);
                    WritePosition(bytes, offset, position);
                }
                await CopyReferencesAsync(batch, root, destination, prefix, token).ConfigureAwait(false);
                var combinedVertexPath = ContainedFile(destination, batch["vertex_file"]!.GetValue<string>());
                geometry.Add((combinedVertexPath, bytes));
                batch["index"] = batches.Count;
                batches.Add(batch);
            }
        }
        if (batches.Count == 0) throw new InvalidDataException("Character appearance has no renderable geometry.");
        var combinedCenter = (minimum + maximum) * 0.5f;
        var extent = maximum - minimum;
        var combinedScale = 2f / Math.Max(0.000001f, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)));
        foreach (var (path, bytes) in geometry)
        {
            for (var offset = 0; offset < bytes.Length; offset += Stride)
            {
                if ((offset / Stride & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                WritePosition(bytes, offset, (ReadPosition(bytes, offset) - combinedCenter) * combinedScale);
            }
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
        }
        var combined = new JsonObject
        {
            ["schema_version"] = 8, ["source_path"] = sourcePath,
            ["normalization_center"] = new JsonArray(combinedCenter.X, combinedCenter.Y, combinedCenter.Z),
            ["normalization_scale"] = combinedScale, ["vertex_count"] = total, ["batches"] = batches,
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), combined.ToJsonString(), token).ConfigureAwait(false);
    }

    private static Vector3 ReadPosition(byte[] data, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 8)));

    private static void WritePosition(byte[] data, int offset, Vector3 position)
    {
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 4), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 8), position.Z);
    }

    private static string ContainedFile(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Character preview path escapes its package.");
        return path;
    }

    private static async Task CopyReferencesAsync(JsonNode node, string sourceRoot, string destination, string prefix, CancellationToken token)
    {
        var children = node is JsonObject map ? map.Select(pair => (pair.Key, pair.Value)).ToArray()
            : node is JsonArray array ? array.Select((value, index) => (index.ToString(), value)).ToArray() : [];
        foreach (var (key, value) in children)
        {
            token.ThrowIfCancellationRequested();
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
            {
                // Archive identifiers are preserved; only actual files inside the component package are copied.
                if (Path.IsPathRooted(text)) continue;
                string source;
                try { source = ContainedFile(sourceRoot, text); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(source)) continue;
                var relative = prefix + "/" + Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
                var target = ContainedFile(destination, relative);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = File.OpenRead(source);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
                if (node is JsonObject obj) obj[key] = relative;
                else node[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = relative;
            }
            else if (value is JsonObject or JsonArray)
                await CopyReferencesAsync(value, sourceRoot, destination, prefix, token).ConfigureAwait(false);
        }
    }
}
