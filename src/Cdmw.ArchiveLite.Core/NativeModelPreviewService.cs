using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

public sealed class NativeModelPreviewService
{
    private const string PackageVersion = "archive_lite_native_model_v3_pat";
    private const string TexturedPackageVersion = "archive_lite_native_model_v4_textured";
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ColdBuildCoalesceDelay = TimeSpan.FromMilliseconds(35);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildGates = new(StringComparer.Ordinal);

    public static bool Supports(string extension) => extension.ToLowerInvariant() is ".pac" or ".pam" or ".pamlod" or ".pat";

    public async Task<string> BuildAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken) =>
        await BuildAsync(
            session,
            entry,
            includeTextures: false,
            publishProgress,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> BuildAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        bool includeTextures,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        if (!Supports(entry.Extension))
        {
            throw new NotSupportedException($"Native model preview does not support {entry.Extension}.");
        }

        var companion = FindCompanion(session.Index, entry);
        ArchiveLiteDataPaths.EnsureCreated();
        var packageVersion = includeTextures ? TexturedPackageVersion : PackageVersion;
        var key = NativeModelPreviewCache.ComputeKey(packageVersion, session, entry, companion);
        var modelRoot = Path.Combine(ArchiveLiteDataPaths.PreviewCache, "models");
        var nativeCacheRoot = Path.Combine(ArchiveLiteDataPaths.PreviewCache, "native");
        Directory.CreateDirectory(modelRoot);
        Directory.CreateDirectory(nativeCacheRoot);
        var destination = Path.Combine(modelRoot, key);
        var sourceIdentity = $"{packageVersion}:{key}:{entry.Path}";
        if (await NativeModelPreviewCache.IsReusableAsync(
                destination,
                packageVersion,
                key,
                session,
                entry,
                cancellationToken).ConfigureAwait(false)
            && NativePreviewPackageAdapter.HasCurrentAdapterMetadata(destination))
        {
            return destination;
        }
        await Task.Delay(ColdBuildCoalesceDelay, cancellationToken).ConfigureAwait(false);
        if (await NativeModelPreviewCache.IsReusableAsync(
                destination,
                packageVersion,
                key,
                session,
                entry,
                cancellationToken).ConfigureAwait(false)
            && NativePreviewPackageAdapter.HasCurrentAdapterMetadata(destination))
        {
            return destination;
        }

        var gate = _buildGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await NativeModelPreviewCache.IsReusableAsync(
                    destination,
                    packageVersion,
                    key,
                    session,
                    entry,
                    cancellationToken).ConfigureAwait(false))
            {
                if (!NativePreviewPackageAdapter.HasCurrentAdapterMetadata(destination))
                {
                    await PublishAsync(publishProgress, "model_preview_adapt", cancellationToken).ConfigureAwait(false);
                    await NativePreviewPackageAdapter.PrepareAsync(
                        destination,
                        sourceIdentity,
                        cancellationToken,
                        includeTextures).ConfigureAwait(false);
                }
                return destination;
            }
            if (Directory.Exists(destination))
            {
                DeleteOwnedDirectory(modelRoot, destination);
            }

            var staging = Path.Combine(modelRoot, $"_staging_{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                var packageRoot = Path.Combine(staging, "package");
                var protocolRoot = Path.Combine(staging, "protocol");
                Directory.CreateDirectory(protocolRoot);
                var jobPath = Path.Combine(protocolRoot, "job.json");
                var reportPath = Path.Combine(protocolRoot, "report.json");
                await PublishAsync(publishProgress, "model_preview_native", cancellationToken).ConfigureAwait(false);
                await WriteJobAsync(
                    jobPath,
                    packageRoot,
                    nativeCacheRoot,
                    session,
                    entry,
                    companion,
                    includeTextures,
                    cancellationToken).ConfigureAwait(false);
                await RunPreviewCoreAsync(jobPath, reportPath, cancellationToken).ConfigureAwait(false);
                var dependencyTrace = ValidateReport(reportPath, staging, packageRoot);

                await PublishAsync(publishProgress, "model_preview_adapt", cancellationToken).ConfigureAwait(false);
                var cacheManifest = await NativeModelPreviewCache.CaptureAsync(
                    packageVersion,
                    key,
                    session,
                    entry,
                    dependencyTrace,
                    cancellationToken).ConfigureAwait(false);
                await NativePreviewPackageAdapter.PrepareAsync(
                    packageRoot,
                    cacheManifest.SourceIdentity,
                    cancellationToken,
                    includeTextures).ConfigureAwait(false);
                await AtomicFile.WriteAsync(
                    Path.Combine(packageRoot, "archive_lite_preview.json"),
                    async (stream, token) => await JsonSerializer.SerializeAsync(
                        stream,
                        cacheManifest,
                        NativePreviewPackageAdapter.JsonOptions,
                        token).ConfigureAwait(false),
                    cancellationToken,
                    flushToDisk: false).ConfigureAwait(false);

                try
                {
                    Directory.Move(packageRoot, destination);
                }
                catch (IOException)
                {
                    if (!await NativeModelPreviewCache.IsReusableAsync(
                            destination,
                            packageVersion,
                            key,
                            session,
                            entry,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }
                    // Another worker published the same reusable package first.
                }
                return destination;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    DeleteOwnedDirectory(modelRoot, staging);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task PublishAsync(
        Func<ProgressUpdate, Task>? publishProgress,
        string phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publishProgress is not null)
        {
            await publishProgress(new ProgressUpdate(0, 0, phase)).ConfigureAwait(false);
        }
    }

    private static async Task WriteJobAsync(
        string path,
        string outputRoot,
        string cacheRoot,
        ArchiveSession session,
        ArchiveEntryDto entry,
        ArchiveEntryDto? companion,
        bool includeTextures,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["backend"] = "cdmw_preview_core_0.1",
            ["renderer_backend"] = "d3d11",
            ["schema_version"] = 8,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["package_root"] = session.PackageRoot,
            ["archive_index_path"] = session.Index.Path,
            ["archive_basename_index_path"] = session.BasenameIndex.Path,
            ["cache_root"] = cacheRoot,
            ["output_root"] = outputRoot,
            ["entry"] = EntryPayload(entry),
            ["companion_entry"] = companion is null ? new Dictionary<string, object?>() : EntryPayload(companion),
            ["render_settings"] = new Dictionary<string, object?>
            {
                ["visible_texture_mode"] = "mesh_base_first",
                ["d3d11_view_mode"] = "lit",
                ["use_textures_by_default"] = includeTextures,
                ["high_quality_by_default"] = true,
            },
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["direct_dds"] = true,
                ["d3d11_package"] = true,
                ["material_index"] = false,
                ["material_graph"] = false,
                ["material_graph_version"] = 3,
                ["python_fallback_allowed"] = false,
                ["native_material_runtime"] = false,
            },
        };
        await AtomicFile.WriteAsync(
            path,
            async (stream, token) => await JsonSerializer.SerializeAsync(
                stream,
                payload,
                NativePreviewPackageAdapter.JsonOptions,
                token).ConfigureAwait(false),
            cancellationToken,
            flushToDisk: false).ConfigureAwait(false);
    }

    private static ArchiveEntryDto? FindCompanion(ArchiveIndex index, ArchiveEntryDto entry)
    {
        var normalizedPath = entry.Path.Replace('\\', '/').Trim('/');
        var candidates = new List<string>(2);
        if (entry.Extension.Equals(".pam", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".pam", StringComparison.OrdinalIgnoreCase))
        {
            var stem = normalizedPath[..^4];
            candidates.Add(stem + ".pamlod");
            if (stem.EndsWith("_breakable", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(stem[..^10] + ".pamlod");
            }
        }
        else if (entry.Extension.Equals(".pamlod", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".pamlod", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(normalizedPath[..^7] + ".pam");
        }

        foreach (var path in candidates)
        {
            var matches = index.FindEntriesByPath(path);
            if (matches.Count == 0)
            {
                continue;
            }
            return matches.FirstOrDefault(candidate => candidate.SourcePamt.Equals(entry.SourcePamt, StringComparison.OrdinalIgnoreCase))
                ?? matches[0];
        }
        return null;
    }

    private static Dictionary<string, object?> EntryPayload(ArchiveEntryDto entry) => new()
    {
        ["path"] = entry.Path,
        ["basename"] = entry.Name,
        ["extension"] = entry.Extension,
        ["pamt_path"] = entry.SourcePamt,
        ["paz_file"] = entry.PazFile,
        ["offset"] = entry.Offset,
        ["comp_size"] = entry.StoredSize,
        ["orig_size"] = entry.OriginalSize,
        ["flags"] = entry.Flags,
        ["paz_index"] = entry.PazIndex,
        ["compression_type"] = entry.CompressionType,
    };

    private static async Task RunPreviewCoreAsync(
        string jobPath,
        string reportPath,
        CancellationToken cancellationToken)
    {
        var executable = ResolvePreviewCorePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("preview-job");
        startInfo.ArgumentList.Add(jobPath);
        startInfo.ArgumentList.Add(reportPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cdmw-preview-core could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PreviewTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            await ObserveCaptureAsync(stdout, stderr).ConfigureAwait(false);
            throw new TimeoutException($"cdmw-preview-core did not finish within {PreviewTimeout.TotalSeconds:N0} seconds.");
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            await ObserveCaptureAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var stdoutText = await stdout.ConfigureAwait(false);
        var stderrText = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderrText) ? stdoutText : stderrText;
            throw new InvalidDataException($"cdmw-preview-core exited with code {process.ExitCode}: {detail.Trim()}");
        }
    }

    private static NativePreviewDependencyTrace ValidateReport(
        string reportPath,
        string stagingRoot,
        string expectedPackageRoot)
    {
        if (!File.Exists(reportPath))
        {
            throw new InvalidDataException("cdmw-preview-core did not produce a report.");
        }
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = report.RootElement;
        var status = ReadString(root, "status").Trim().ToLowerInvariant();
        var message = ReadString(root, "fallback_reason", ReadString(root, "message"));
        if (status != "ok")
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message)
                ? $"cdmw-preview-core reported '{status}'."
                : message);
        }
        var packagePath = Path.GetFullPath(ReadString(root, "package_path"));
        RequireContained(stagingRoot, packagePath);
        if (!packagePath.Equals(Path.GetFullPath(expectedPackageRoot), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(packagePath, "manifest.json")))
        {
            throw new InvalidDataException("cdmw-preview-core reported an unexpected or incomplete package path.");
        }
        return NativeModelPreviewCache.ReadTrace(root);
    }

    private static string ResolvePreviewCorePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_PREVIEW_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }
        var packaged = Path.Combine(AppContext.BaseDirectory, "preview", "cdmw-preview-core.exe");
        if (File.Exists(packaged))
        {
            return packaged;
        }
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "native",
                    "cdmw_preview_core",
                    "build",
                    configuration,
                    "cdmw-preview-core.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        throw new FileNotFoundException(
            "cdmw-preview-core.exe was not found. Rebuild the Archive Lite portable package or set CDMW_ARCHIVE_LITE_PREVIEW_CORE_PATH.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 64 * 1024;
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            if (output.Length < maximumCharacters)
            {
                output.Append(buffer, 0, Math.Min(count, maximumCharacters - output.Length));
            }
        }
        return output.ToString();
    }

    private static async Task ObserveCaptureAsync(params Task<string>[] captures)
    {
        foreach (var capture in captures)
        {
            try
            {
                _ = await capture.ConfigureAwait(false);
            }
            catch
            {
                // The process is already being torn down; only observe the reader task.
            }
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Preserve the cancellation or timeout that owns this teardown.
        }
    }

    private static void DeleteOwnedDirectory(string root, string path)
    {
        RequireContained(root, path);
        Directory.Delete(path, recursive: true);
    }

    private static void RequireContained(string root, string path)
    {
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(path);
        if (!resolvedPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Preview cache path escapes its owned root: {resolvedPath}");
        }
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }
}

public static class NativePreviewPackageAdapter
{
    private const string AdapterMetadataName = "archive_lite_adapter_v3.json";
    private const int AdapterSchemaVersion = 3;
    private const int SupportedSchemaVersion = 8;
    private const int BytesPerVertex = 23 * sizeof(float);
    private const int MaximumVertices = 8_000_000;
    private static readonly HashSet<string> LayerOnlyRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "damage",
        "decal",
        "detail",
        "dye",
        "grime",
        "layer",
        "overlay",
    };
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    public static bool HasCurrentAdapterMetadata(string packageRoot) =>
        File.Exists(Path.Combine(Path.GetFullPath(packageRoot), AdapterMetadataName));

    public static async Task<NativePreviewPackageInfo> PrepareAsync(
        string packageRoot,
        string sourceIdentity,
        CancellationToken cancellationToken,
        bool includeTextures = false)
    {
        var root = Path.GetFullPath(packageRoot);
        var manifestPath = Path.Combine(root, "manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        if (manifest.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Native preview manifest root must be an object.");
        }
        var schemaVersion = ReadInt(manifest.RootElement, "schema_version", -1);
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Native preview manifest schema {schemaVersion} is unsupported; expected {SupportedSchemaVersion}.");
        }
        if (!manifest.RootElement.TryGetProperty("batches", out var batches) || batches.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Native preview manifest has no batches array.");
        }
        var sourcePath = ReadString(manifest.RootElement, "source_path");
        var initialView = ArchiveModelPreviewPolicy.InitialView(sourcePath);

        var slots = new List<Dictionary<string, object?>>();
        var submeshes = new List<Dictionary<string, object?>>();
        var resources = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var resourceFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalVertices = 0;
        foreach (var batch in batches.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var index = ReadInt(batch, "index", submeshes.Count);
            var vertexCount = ReadInt(batch, "vertex_count", 0);
            if (vertexCount <= 0 || vertexCount % 3 != 0)
            {
                throw new InvalidDataException($"Native preview batch {index} has an invalid vertex count.");
            }
            totalVertices = checked(totalVertices + vertexCount);
            if (totalVertices > MaximumVertices)
            {
                throw new InvalidDataException($"Native preview package exceeds the {MaximumVertices:N0}-vertex safety limit.");
            }
            var geometry = ResolveContainedFile(root, ReadString(batch, "vertex_file"));
            if (new FileInfo(geometry).Length != checked((long)vertexCount * BytesPerVertex))
            {
                throw new InvalidDataException($"Native preview batch {index} has an invalid geometry length.");
            }

            var material = ReadString(batch, "material_name", $"material_{index:000}");
            var channelResolution = includeTextures
                ? ResolveChannels(root, batch)
                : new MaterialChannelResolution();
            var channels = channelResolution.Channels;
            var resourceChannels = includeTextures
                ? await BuildResourceBindingsAsync(
                    index,
                    channelResolution,
                    resources,
                    resourceFingerprints,
                    cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, string>();
            slots.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["name"] = material,
                ["texture"] = channels.GetValueOrDefault("base", ""),
                ["channels"] = channels,
            });

            var color = ReadFloatArray(batch, "base_color", [0.65f, 0.65f, 0.65f]);
            var category = ReadString(batch, "material_category", "unknown");
            var rawShaderFamily = ReadString(batch, "shader_family", "generic");
            var shaderFamily = NormalizeShaderFamily(rawShaderFamily);
            var alphaMode = NormalizeAlphaMode(ReadString(batch, "alpha_mode", "opaque"));
            var layerBindings = ReadMaterialInputs(batch);
            var unsupportedFeatures = UnsupportedFeatures(
                shaderFamily,
                alphaMode,
                layerBindings.Length > 0 && !ReadBool(batch, "material_combiner_active"));
            submeshes.Add(new Dictionary<string, object?>
            {
                ["submesh_index"] = index,
                ["material_slot_index"] = index,
                ["material"] = material,
                ["texture"] = channels.GetValueOrDefault("base", ""),
                ["resolved_channels"] = channels,
                ["packaged_channels"] = new Dictionary<string, string>(),
                ["resource_channels"] = resourceChannels,
                ["channel_components"] = channelResolution.Components,
                ["channel_color_spaces"] = channelResolution.ColorSpaces,
                ["channel_authorities"] = channelResolution.Authorities,
                ["normal_y_policy"] = ReadString(batch, "normal_y_policy", "shader_invert_legacy_compat"),
                ["texture_flip_vertical"] = ReadBool(batch, "texture_flip_vertical"),
                ["shader_family"] = shaderFamily,
                ["shader_technique"] = rawShaderFamily,
                ["shader_authority"] = string.Equals(shaderFamily, "generic", StringComparison.OrdinalIgnoreCase)
                    ? "guess"
                    : "sidecar",
                ["shader_family_source"] = "declared_shader_family",
                ["shader_family_reason"] = string.Equals(shaderFamily, "generic", StringComparison.OrdinalIgnoreCase)
                    ? "No supported source shader family was declared."
                    : $"Native material graph declared shader family {rawShaderFamily}.",
                ["material_category"] = category,
                ["material_category_confidence"] = ReadFloat(batch, "material_category_confidence", 0.35f),
                ["material_category_reason"] = ReadString(batch, "material_category_reason"),
                ["material_response_promoted"] = ReadBool(batch, "material_response_promoted"),
                ["alpha_mode"] = alphaMode,
                ["alpha_cutoff"] = ReadFloat(batch, "alpha_threshold", 0.5f),
                ["opacity_factor"] = 1.0f,
                ["alpha_authority"] = "native_preview_core",
                ["alpha_reason"] = "Native material graph alpha contract.",
                ["double_sided"] = ReadBool(batch, "two_sided"),
                ["double_sided_authority"] = "native_preview_core",
                ["double_sided_reason"] = "Native material graph sidedness contract.",
                ["unsupported_features"] = unsupportedFeatures,
                ["layer_bindings"] = layerBindings,
                ["parameters"] = BuildInitialParameters(batch, channels, color, category),
            });
        }
        if (submeshes.Count == 0)
        {
            throw new InvalidDataException("Native preview package did not contain renderable batches.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestSignature = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{manifestSignature}:archive_lite_adapter:{AdapterSchemaVersion}"))).ToLowerInvariant();
        await WriteJsonAsync(
            Path.Combine(root, "net_materials.json"),
            new Dictionary<string, object?>
            {
                ["format"] = "cdmw_mesh_dotnet_materials_v1",
                ["renderer_authority"] = "dotnet_mesh_editor",
                ["source"] = "manifest.json",
                ["adapter"] = "archive_lite_native_material_bridge_v3",
                ["texture_channels"] = new[]
                {
                    "base",
                    "normal",
                    "specular",
                    "roughness",
                    "metallic",
                    "emissive",
                    "height",
                    "material",
                    "occlusion",
                    "opacity",
                },
                ["material_signature"] = signature,
                ["material_slots"] = slots,
                ["resources"] = resources
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => pair.Value)
                    .ToArray(),
                ["submeshes"] = submeshes,
                ["fallbacks"] = new Dictionary<string, string>
                {
                    ["base"] = "neutral_checker",
                    ["normal"] = "flat_normal",
                    ["emissive"] = "black",
                },
                ["source_mesh"] = sourcePath,
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(root, "dotnet_scene.json"),
            new Dictionary<string, object?>
            {
                ["session_id"] = "archive_lite",
                ["source_identity"] = sourceIdentity,
                ["scene_generation"] = 1,
                ["editable_submesh_count"] = submeshes.Count,
                ["reference_submesh_count"] = 0,
                ["interaction_mode"] = "placement",
                ["comparison_mode"] = "replacement_only",
                ["grid"] = new Dictionary<string, object?> { ["visible"] = false, ["origin"] = new[] { 0.0f, -1.0f, 0.0f }, ["spacing"] = 0.25f },
                ["gizmo"] = new Dictionary<string, object?> { ["visible"] = false, ["tool"] = "move" },
                ["archive_preview"] = new Dictionary<string, object?>
                {
                    ["source_path"] = sourcePath,
                    ["textures_enabled"] = includeTextures,
                    ["camera"] = new Dictionary<string, object?>
                    {
                        ["yaw_degrees"] = initialView.YawDegrees,
                        ["pitch_degrees"] = initialView.PitchDegrees,
                        ["fit_to_view"] = initialView.FitToView,
                        ["reason"] = initialView.Reason,
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(root, "mesh.cdmeta.json"),
            new Dictionary<string, object?>
            {
                ["schema"] = "cdmw_archive_lite_preview_metadata_v1",
                ["source_identity"] = sourceIdentity,
                ["native_manifest"] = "manifest.json",
                ["read_only"] = true,
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(root, AdapterMetadataName),
            new Dictionary<string, object?>
            {
                ["schema"] = AdapterSchemaVersion,
                ["source_path"] = sourcePath,
                ["textures_enabled"] = includeTextures,
            },
            cancellationToken).ConfigureAwait(false);
        return new NativePreviewPackageInfo(submeshes.Count, totalVertices, manifestPath);
    }

    private static async Task<Dictionary<string, string>> BuildResourceBindingsAsync(
        int submeshIndex,
        MaterialChannelResolution resolution,
        IDictionary<string, Dictionary<string, object?>> resources,
        IDictionary<string, string> fingerprints,
        CancellationToken cancellationToken)
    {
        var resourceChannels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (semantic, path) in resolution.Channels.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fingerprints.TryGetValue(path, out var fingerprint))
            {
                fingerprint = await FileSha256Async(path, cancellationToken).ConfigureAwait(false);
                fingerprints[path] = fingerprint;
            }
            var resourceId = $"texture:{fingerprint}";
            resourceChannels[semantic] = resourceId;
            if (!resources.TryGetValue(resourceId, out var resource))
            {
                resource = BuildResourcePayload(
                    resourceId,
                    path,
                    fingerprint,
                    submeshIndex,
                    semantic,
                    resolution);
                resources[resourceId] = resource;
            }
            else if (ResourceChannelRank(semantic) < ResourceChannelRank(resource.GetValueOrDefault("material_channel") as string ?? ""))
            {
                resource["material_channel"] = semantic;
                resource["semantic"] = semantic;
                resource["color_space"] = resolution.ColorSpaces.GetValueOrDefault(semantic, "linear");
                resource["semantic_authority"] = resolution.Authorities.GetValueOrDefault(semantic, "native_preview_core");
                resource["fallback_policy"] = ResourceFallbackPolicy(semantic);
            }
        }
        return resourceChannels;
    }

    private static Dictionary<string, object?> BuildResourcePayload(
        string resourceId,
        string path,
        string fingerprint,
        int submeshIndex,
        string semantic,
        MaterialChannelResolution resolution) => new()
    {
        ["resource_id"] = resourceId,
        ["path"] = path,
        ["source_reference"] = path,
        ["fingerprint"] = fingerprint,
        ["role"] = "replacement",
        ["submesh_index"] = submeshIndex,
        ["material_channel"] = semantic,
        ["semantic"] = semantic,
        ["color_space"] = resolution.ColorSpaces.GetValueOrDefault(semantic, "linear"),
        ["semantic_authority"] = resolution.Authorities.GetValueOrDefault(semantic, "native_preview_core"),
        ["profile"] = "legacy_unknown",
        ["required"] = false,
        ["criticality"] = "optional",
        ["fallback_policy"] = ResourceFallbackPolicy(semantic),
    };

    private static int ResourceChannelRank(string channel) => channel switch
    {
        "base" => 0,
        "normal" => 1,
        "material" => 2,
        "roughness" => 3,
        "metallic" => 4,
        "specular" => 5,
        "emissive" => 6,
        "height" => 7,
        _ => 99,
    };

    private static string ResourceFallbackPolicy(string channel) => channel switch
    {
        "base" => "neutral_checker",
        "normal" => "flat_normal",
        "roughness" => "neutral_roughness",
        "metallic" => "nonmetal",
        "material" => "neutral_material",
        "specular" => "neutral_specular",
        "emissive" => "black",
        "height" => "neutral_height",
        _ => "diagnostic_only",
    };

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static MaterialChannelResolution ResolveChannels(string packageRoot, JsonElement batch)
    {
        var result = new MaterialChannelResolution();
        if (!batch.TryGetProperty("dds_textures", out var textures) || textures.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (var texture in textures.EnumerateObject())
        {
            if (texture.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            AddTextureDescriptor(result, packageRoot, texture.Name, texture.Value, overwrite: true);
        }
        if (textures.TryGetProperty("material_inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputs.EnumerateArray())
            {
                if (input.ValueKind != JsonValueKind.Object
                    || LayerOnlyRoles.Contains(ReadString(input, "layer_role")))
                {
                    continue;
                }
                var slot = ReadString(input, "slot", ReadString(input, "semantic_type"));
                AddTextureDescriptor(result, packageRoot, slot, input, overwrite: false);
            }
        }
        return result;
    }

    private static void AddTextureDescriptor(
        MaterialChannelResolution result,
        string packageRoot,
        string rawSlot,
        JsonElement descriptor,
        bool overwrite)
    {
        if (!ReadBool(descriptor, "available", fallback: true)
            || !ReadBool(descriptor, "direct_upload_candidate", fallback: true))
        {
            return;
        }
        var slot = CanonicalChannel(rawSlot);
        var rawPath = ReadString(descriptor, "source_path");
        if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }
        var path = Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : ResolveContainedFile(packageRoot, rawPath);
        if (!File.Exists(path))
        {
            return;
        }
        var colorSpace = NormalizeColorSpace(ReadString(descriptor, "srgb_mode"), slot);
        var authority = ReadString(
            descriptor,
            "source_authority",
            ReadString(descriptor, "binding_authority", "native_preview_core"));
        SetChannel(result, slot, path, colorSpace, authority, overwrite);

        var packedComponents = ParsePackedComponents(ReadString(descriptor, "packed_channels"));
        foreach (var (semantic, component) in packedComponents)
        {
            result.Components[semantic] = component;
        }
        if (slot == "material")
        {
            foreach (var semantic in new[] { "specular", "roughness", "metallic", "occlusion" })
            {
                if (packedComponents.ContainsKey(semantic))
                {
                    SetChannel(result, semantic, path, "linear", authority, overwrite);
                }
            }
        }
    }

    private static void SetChannel(
        MaterialChannelResolution result,
        string channel,
        string path,
        string colorSpace,
        string authority,
        bool overwrite)
    {
        if (!overwrite && result.Channels.ContainsKey(channel))
        {
            return;
        }
        result.Channels[channel] = path;
        result.ColorSpaces[channel] = colorSpace;
        result.Authorities[channel] = authority;
    }

    private static Dictionary<string, string> ParsePackedComponents(string packed)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in packed.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOfAny(['=', ':']);
            if (separator <= 0 || separator >= token.Length - 1)
            {
                continue;
            }
            var left = token[..separator];
            var right = token[(separator + 1)..];
            var leftComponent = CanonicalComponent(left);
            var rightComponent = CanonicalComponent(right);
            var semantic = leftComponent.Length > 0 ? CanonicalChannel(right) : CanonicalChannel(left);
            var component = leftComponent.Length > 0 ? leftComponent : rightComponent;
            if (semantic.Length > 0 && component.Length > 0)
            {
                result[semantic] = component;
            }
        }
        return result;
    }

    private static Dictionary<string, object?> BuildInitialParameters(
        JsonElement batch,
        IReadOnlyDictionary<string, string> channels,
        float[] color,
        string category)
    {
        var result = new Dictionary<string, object?>
        {
            ["base_tint_color"] = color,
            ["base_tint_strength"] = Math.Clamp(ReadFloat(batch, "base_tint_strength", 0.0f), 0.0f, 1.0f),
            ["base_tint_metallic"] = string.Equals(category, "metal", StringComparison.OrdinalIgnoreCase),
            ["material_role"] = category,
        };
        if (batch.TryGetProperty("native_material_hints", out var hints) && hints.ValueKind == JsonValueKind.Object)
        {
            CopyHint(hints, "roughness", result, "roughness_hint");
            CopyHint(hints, "metalness", result, "metalness_hint");
            CopyHint(hints, "specular", result, "specular_hint");
        }
        else
        {
            CopyScalar(batch, "roughness", result, channels.ContainsKey("roughness") ? "roughness_scale" : "roughness");
            CopyScalar(batch, "metalness", result, channels.ContainsKey("metallic") ? "metalness_scale" : "metalness");
            var specular = ReadOptionalFloat(batch, "specular", 0.0f, 1.0f);
            if (specular is not null && Math.Abs(specular.Value - 1.0f) > 0.000001f)
            {
                result["specular"] = specular.Value;
            }
        }
        var emissiveIntensity = ReadOptionalFloat(batch, "emissive_intensity", 0.0f, 32.0f);
        if (emissiveIntensity is > 0.0f || channels.ContainsKey("emissive"))
        {
            result["emissive_intensity"] = emissiveIntensity ?? 1.0f;
            result["emissive_color"] = ReadFloatArray(batch, "emissive_color", [1.0f, 1.0f, 1.0f]);
            result["emissive_color_authoritative"] = true;
        }
        return result;
    }

    private static void CopyHint(
        JsonElement source,
        string sourceName,
        IDictionary<string, object?> destination,
        string destinationName)
    {
        var value = ReadOptionalFloat(source, sourceName, 0.0f, 1.0f);
        if (value is not null)
        {
            destination[destinationName] = value.Value;
        }
    }

    private static void CopyScalar(
        JsonElement source,
        string sourceName,
        IDictionary<string, object?> destination,
        string destinationName)
    {
        var value = ReadOptionalFloat(source, sourceName, 0.0f, 1.0f);
        if (value is not null)
        {
            destination[destinationName] = value.Value;
        }
    }

    private static JsonElement[] ReadMaterialInputs(JsonElement batch)
    {
        if (!batch.TryGetProperty("dds_textures", out var textures)
            || textures.ValueKind != JsonValueKind.Object
            || !textures.TryGetProperty("material_inputs", out var inputs)
            || inputs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }
        return inputs.EnumerateArray()
            .Where(static input => input.ValueKind == JsonValueKind.Object)
            .Select(static input => input.Clone())
            .ToArray();
    }

    private static string[] UnsupportedFeatures(string shaderFamily, string alphaMode, bool hasLayerGraph)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (alphaMode == "blend") result.Add("per_triangle_alpha_blend_sorting");
        if (hasLayerGraph) result.Add("shader_family_layer_graph");
        if (shaderFamily is "hair" or "fur") result.Add("hair_fur_anisotropy_and_flow");
        if (shaderFamily is "skin" or "skin_wrinkle") result.Add("skin_subsurface_and_wrinkle_response");
        return result.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeShaderFamily(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        var compact = CompactIdentifier(text);
        if (compact.Length == 0) return "generic";
        if ((compact.Contains("skin", StringComparison.Ordinal) && !compact.Contains("skinnedmesh", StringComparison.Ordinal))
            || compact.Contains("skinnedmeshskin", StringComparison.Ordinal)) return "skin";
        if (compact.Contains("skinnedmeshanimalhair", StringComparison.Ordinal)
            || compact.Contains("skinnedmeshhairstandard", StringComparison.Ordinal)
            || compact.Contains("skinnedmeshhair", StringComparison.Ordinal)
            || compact.Contains("skinnedmeshfur", StringComparison.Ordinal)
            || compact.Contains("hair", StringComparison.Ordinal)
            || compact.Contains("fur", StringComparison.Ordinal)) return "hair";
        if (compact.Contains("cloth", StringComparison.Ordinal))
            return compact.Contains("v2", StringComparison.Ordinal) || compact.Contains("ver2", StringComparison.Ordinal)
                ? "cloth_v2"
                : "cloth";
        if (compact.Contains("emissive", StringComparison.Ordinal))
            return compact.Contains("v2", StringComparison.Ordinal) || compact.Contains("ver2", StringComparison.Ordinal)
                ? "emissive_v2"
                : "emissive";
        if (compact.Contains("water", StringComparison.Ordinal)
            || compact.Contains("shallowwater", StringComparison.Ordinal)
            || compact.Contains("sea", StringComparison.Ordinal)) return "environment_water";
        if ((compact.Contains("static", StringComparison.Ordinal) && compact.Contains("multi", StringComparison.Ordinal))
            || compact.Contains("rgbtexture", StringComparison.Ordinal)
            || compact.Contains("multitextured", StringComparison.Ordinal)) return "static_multitextured";
        if (compact.Contains("static", StringComparison.Ordinal)) return "static_standard";
        if (compact.Contains("standard", StringComparison.Ordinal))
            return compact.Contains("v2", StringComparison.Ordinal) || compact.Contains("ver2", StringComparison.Ordinal)
                ? "standard_v2"
                : "standard";
        return text.Replace(' ', '_');
    }

    private static string NormalizeAlphaMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "mask" or "alpha_cutout" or "coverage" or "cutout" => "cutout",
            "transparent" or "alpha" or "blend" => "blend",
            _ => "opaque",
        };
    }

    private static string NormalizeColorSpace(string value, string channel)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("srgb", StringComparison.Ordinal)) return "srgb";
        if (normalized.Contains("linear", StringComparison.Ordinal)) return "linear";
        return channel is "base" or "emissive" ? "srgb" : "linear";
    }

    private static string CanonicalComponent(string value)
    {
        return CompactIdentifier(value) switch
        {
            "r" or "red" => "r",
            "g" or "green" => "g",
            "b" or "blue" => "b",
            "a" or "alpha" => "a",
            _ => string.Empty,
        };
    }

    private static string CanonicalChannel(string value)
    {
        return CompactIdentifier(value) switch
        {
            "base" or "basecolor" or "albedo" or "color" or "diffuse" => "base",
            "normal" or "normalmap" => "normal",
            "material" or "materialmask" or "packedmaterial" or "packedmaterialmask" => "material",
            "specular" or "specularresponse" => "specular",
            "roughness" or "glossiness" => "roughness",
            "metallic" or "metalness" => "metallic",
            "occlusion" or "ambientocclusion" or "ao" => "occlusion",
            "height" or "displacement" => "height",
            "emissive" or "emission" => "emissive",
            "opacity" or "alpha" => "opacity",
            "layermask" or "mask" => "layer_mask",
            _ => string.Empty,
        };
    }

    private static string CompactIdentifier(string value) => new(
        value.Where(char.IsLetterOrDigit).ToArray());

    private sealed class MaterialChannelResolution
    {
        public Dictionary<string, string> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Components { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ColorSpaces { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Authorities { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task WriteJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        await AtomicFile.WriteAsync(
            path,
            async (stream, token) => await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, token).ConfigureAwait(false),
            cancellationToken,
            flushToDisk: false).ConfigureAwait(false);
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
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    }

    private static float ReadFloat(JsonElement element, string name, float fallback)
    {
        return element.TryGetProperty(name, out var value)
            && value.TryGetSingle(out var result)
            && float.IsFinite(result)
                ? result
                : fallback;
    }

    private static float? ReadOptionalFloat(JsonElement element, string name, float minimum, float maximum)
    {
        return element.TryGetProperty(name, out var value)
            && value.TryGetSingle(out var result)
            && float.IsFinite(result)
                ? Math.Clamp(result, minimum, maximum)
                : null;
    }

    private static bool ReadBool(JsonElement element, string name, bool fallback = false)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static float[] ReadFloatArray(JsonElement element, string name, float[] fallback)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }
        var result = value.EnumerateArray()
            .Take(3)
            .Select(item => item.TryGetSingle(out var number) && float.IsFinite(number) ? number : 0.0f)
            .ToArray();
        return result.Length == 3 ? result : fallback;
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }
}

public sealed record NativePreviewPackageInfo(int BatchCount, long VertexCount, string ManifestPath);
