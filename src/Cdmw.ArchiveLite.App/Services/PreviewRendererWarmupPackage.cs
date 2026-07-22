using System.Text;
using System.Text.Json;

namespace Cdmw.ArchiveLite.App.Services;

internal static class PreviewRendererWarmupPackage
{
    private const string PackageVersion = "renderer-warmup-v1";
    private const int FloatsPerVertex = 23;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<string> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var previewRoot = Path.Combine(AppDataPaths.Cache, "preview");
        var packageRoot = Path.Combine(previewRoot, PackageVersion);
        if (IsComplete(packageRoot))
        {
            return packageRoot;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsComplete(packageRoot))
            {
                return packageRoot;
            }

            Directory.CreateDirectory(previewRoot);
            var stagingRoot = Path.Combine(previewRoot, $".{PackageVersion}.{Guid.NewGuid():N}.tmp");
            try
            {
                await WritePackageAsync(stagingRoot, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(packageRoot))
                {
                    var staleRoot = Path.Combine(previewRoot, $".{PackageVersion}.{Guid.NewGuid():N}.stale");
                    Directory.Move(packageRoot, staleRoot);
                    TryDeleteDirectory(staleRoot);
                }
                try
                {
                    Directory.Move(stagingRoot, packageRoot);
                }
                catch (IOException) when (IsComplete(packageRoot))
                {
                    // Another application instance published the same immutable warmup package.
                }
                if (!IsComplete(packageRoot))
                {
                    throw new InvalidDataException("The renderer warmup package could not be published completely.");
                }
                return packageRoot;
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task WritePackageAsync(string root, CancellationToken cancellationToken)
    {
        var geometryRoot = Path.Combine(root, "geometry");
        Directory.CreateDirectory(geometryRoot);
        var geometryPath = Path.Combine(geometryRoot, "batch_000.bin");
        await using (var stream = new FileStream(
            geometryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var bytes = BuildGeometry();
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteJsonAsync(
            Path.Combine(root, "manifest.json"),
            new
            {
                schema_version = 8,
                backend = "d3d11",
                batches = new[]
                {
                    new
                    {
                        index = 0,
                        material_name = "renderer_warmup",
                        vertex_file = "geometry/batch_000.bin",
                        vertex_count = 3,
                        base_color = new[] { 0.18f, 0.20f, 0.23f },
                        roughness = 0.75f,
                        metalness = 0.0f,
                        specular = 0.2f,
                        material_category = "warmup",
                        material_category_confidence = 1.0f,
                        material_response_promoted = false,
                        dds_textures = new Dictionary<string, object>(),
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(root, "net_materials.json"),
            new
            {
                schema = "cdmw_archive_lite_native_materials_v1",
                material_signature = PackageVersion,
                material_slots = new[]
                {
                    new
                    {
                        index = 0,
                        name = "renderer_warmup",
                        texture = string.Empty,
                        channels = new Dictionary<string, string>(),
                    },
                },
                submeshes = new[]
                {
                    new
                    {
                        submesh_index = 0,
                        material_slot_index = 0,
                        material = "renderer_warmup",
                        texture = string.Empty,
                        resolved_channels = new Dictionary<string, string>(),
                        packaged_channels = new Dictionary<string, string>(),
                        resource_channels = new Dictionary<string, string>(),
                        channel_components = new Dictionary<string, string>(),
                        channel_color_spaces = new Dictionary<string, string>(),
                        channel_authorities = new Dictionary<string, string>(),
                        normal_y_policy = "shader_invert_legacy_compat",
                        texture_flip_vertical = false,
                        shader_family = "generic",
                        shader_technique = "generic",
                        shader_authority = "archive_lite_warmup",
                        shader_family_source = "archive_lite_warmup",
                        shader_family_reason = "Immutable renderer warmup package.",
                        material_category = "warmup",
                        material_category_confidence = 1.0f,
                        material_category_reason = "Renderer warmup only.",
                        material_response_promoted = false,
                        alpha_mode = "opaque",
                        alpha_cutoff = 0.5f,
                        opacity_factor = 1.0f,
                        double_sided = false,
                        unsupported_features = Array.Empty<string>(),
                        parameters = new
                        {
                            base_color = new[] { 0.18f, 0.20f, 0.23f },
                            base_tint_strength = 1.0f,
                            roughness = 0.75f,
                            metalness = 0.0f,
                            specular = 0.2f,
                            height_scale = 0.0f,
                            material_role = "warmup",
                        },
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(root, "dotnet_scene.json"),
            new
            {
                session_id = "archive_lite_warmup",
                source_identity = PackageVersion,
                scene_generation = 1,
                editable_submesh_count = 1,
                reference_submesh_count = 0,
                interaction_mode = "placement",
                comparison_mode = "replacement_only",
                grid = new { visible = false, origin = new[] { 0.0f, -1.0f, 0.0f }, spacing = 0.25f },
                gizmo = new { visible = false, tool = "move" },
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(root, "mesh.cdmeta.json"),
            new
            {
                schema = "cdmw_archive_lite_preview_metadata_v1",
                source_identity = PackageVersion,
                native_manifest = "manifest.json",
                read_only = true,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] BuildGeometry()
    {
        using var stream = new MemoryStream(3 * FloatsPerVertex * sizeof(float));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var position in new[]
        {
            new[] { -0.5f, 0.0f, 0.0f },
            new[] { 0.5f, 0.0f, 0.0f },
            new[] { 0.0f, 1.0f, 0.0f },
        })
        {
            var vertex = new float[FloatsPerVertex];
            vertex[0] = position[0];
            vertex[1] = position[1];
            vertex[2] = position[2];
            vertex[5] = 1.0f;
            vertex[9] = position[0] + 0.5f;
            vertex[10] = position[1];
            foreach (var value in vertex)
            {
                writer.Write(value);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static async Task WriteJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsComplete(string root) =>
        Directory.Exists(root)
        && File.Exists(Path.Combine(root, "manifest.json"))
        && File.Exists(Path.Combine(root, "mesh.cdmeta.json"))
        && File.Exists(Path.Combine(root, "net_materials.json"))
        && File.Exists(Path.Combine(root, "dotnet_scene.json"))
        && new FileInfo(Path.Combine(root, "geometry", "batch_000.bin")) is { Exists: true, Length: 3 * FloatsPerVertex * sizeof(float) };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Bounded cache maintenance can remove an abandoned generated package later.
        }
    }
}
