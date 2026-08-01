using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cdmw.ArchiveLite.Contracts;
using static Cdmw.ArchiveLite.Core.NativePreviewGeometryIO;

namespace Cdmw.ArchiveLite.Core;

public sealed class NativeModelExportService(NativeModelPreviewService previews)
{
    private static readonly TimeSpan NativeExportTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static bool SupportsFormat(ExportKind kind) => kind is ExportKind.Obj or ExportKind.Fbx or ExportKind.Glb;

    public static string FileExtension(ExportKind kind) => kind switch
    {
        ExportKind.Obj => ".obj",
        ExportKind.Fbx => ".fbx",
        ExportKind.Glb => ".glb",
        _ => throw new NotSupportedException($"{kind} is not a mesh interchange format."),
    };

    public Task ExportAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        ExportKind kind,
        string destination,
        bool overwrite,
        Func<ProgressUpdate, Task>? progress,
        CancellationToken cancellationToken) =>
        ExportAsync(session, entry, kind, destination, overwrite, progress, [], cancellationToken);

    public async Task ExportAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        ExportKind kind,
        string destination,
        bool overwrite,
        Func<ProgressUpdate, Task>? progress,
        IReadOnlyList<string> companionTextures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        if (!NativeModelPreviewService.Supports(entry.Extension))
        {
            throw new NotSupportedException($"Mesh interchange export does not support {entry.Extension} files.");
        }

        var packageRoot = await previews.BuildAsync(session, entry, progress, cancellationToken).ConfigureAwait(false);
        await ExportPackageAsync(
            packageRoot,
            entry.Path,
            kind,
            destination,
            overwrite,
            progress,
            entry.Path,
            companionTextures,
            cancellationToken).ConfigureAwait(false);
    }

    public Task ExportPackageAsync(
        string packageRoot,
        string sourcePath,
        ExportKind kind,
        string destination,
        bool overwrite,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        CancellationToken cancellationToken) =>
        ExportPackageAsync(
            packageRoot,
            sourcePath,
            kind,
            destination,
            overwrite,
            progress,
            currentItem,
            [],
            cancellationToken);

    /// <summary>
    /// Writes a mesh interchange file, binding any textures exported alongside it.
    /// </summary>
    /// <param name="companionTextures">
    /// Paths, relative to the exported mesh, of textures already written beside it. Only OBJ can
    /// name them, through its material library; the other formats carry no such reference.
    /// </param>
    public async Task ExportPackageAsync(
        string packageRoot,
        string sourcePath,
        ExportKind kind,
        string destination,
        bool overwrite,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        IReadOnlyList<string> companionTextures,
        CancellationToken cancellationToken)
    {
        if (!SupportsFormat(kind))
        {
            throw new NotSupportedException($"Archive Lite does not support {kind} as a model interchange format.");
        }

        var fullDestination = Path.GetFullPath(destination);
        var expectedExtension = FileExtension(kind);
        if (!Path.GetExtension(fullDestination).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {kind} export destination must use the {expectedExtension} extension.");
        }

        var package = await NativePreviewMeshPackage.ReadAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidDataException("Mesh export destination has no parent directory.");
        Directory.CreateDirectory(parent);

        if (kind == ExportKind.Glb)
        {
            await NativeGlbExportWriter.WriteAsync(
                package,
                sourcePath,
                fullDestination,
                overwrite,
                progress,
                currentItem,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteNativeInterchangeAsync(
            package,
            sourcePath,
            kind,
            fullDestination,
            overwrite,
            progress,
            currentItem,
            companionTextures,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNativeInterchangeAsync(
        NativePreviewMeshPackage package,
        string sourcePath,
        ExportKind kind,
        string destination,
        bool overwrite,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        IReadOnlyList<string> companionTextures,
        CancellationToken cancellationToken)
    {
        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "cdmw-archive-lite-mesh-export",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workRoot);
        var stagedOutput = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp{FileExtension(kind)}");
        var stagedManifest = Path.Combine(workRoot, "roundtrip.meta.json");
        try
        {
            var totalWork = checked(package.TotalVertices * 2L);
            await ReportAsync(progress, 0, totalWork, "mesh_export_prepare", currentItem).ConfigureAwait(false);
            var prepared = await PrepareNativeSidecarsAsync(
                package,
                package.Normalization,
                workRoot,
                totalWork,
                progress,
                currentItem,
                cancellationToken).ConfigureAwait(false);

            var jobPath = Path.Combine(workRoot, "job.json");
            var reportPath = Path.Combine(workRoot, "report.json");
            var baseName = CleanName(Path.GetFileNameWithoutExtension(sourcePath), "mesh");
            var operation = kind == ExportKind.Obj ? "obj_export" : "fbx_export";
            var job = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["backend"] = "cdmw_mesh_core_0.1",
                ["operation"] = operation,
                ["output_path"] = stagedOutput,
                ["base_name"] = baseName,
                ["scale"] = 1.0,
                ["submeshes"] = prepared.Select(static batch => batch.Payload).ToArray(),
            };
            if (kind == ExportKind.Obj)
            {
                job["export_path"] = destination;
                job["source_path"] = sourcePath;
                job["source_format"] = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                // Naming the library makes the writer emit the mtllib line that binds the
                // usemtl names it was already writing to definitions that exist.
                job["mtl_filename"] = Path.GetFileName(NativeObjMaterialWriter.DestinationFor(destination));
                // The header comment states what the file holds, which is the indexed vertex count,
                // not the corner count the package stored.
                job["total_vertices"] = prepared.Sum(static batch => batch.UniqueVertexCount);
                job["total_faces"] = package.TotalVertices / 3;
                // The round-trip sidecar comes from the same writer CDMW Full uses, so an OBJ
                // exported here carries the manifest that identifies where it came from. It is
                // staged like the mesh and only moved into place once the export has succeeded.
                job["manifest_output_path"] = stagedManifest;
                job["extra_payload"] = new Dictionary<string, object?>
                {
                    ["source_archive_path"] = sourcePath,
                    ["source_archive_format"] = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant(),
                    ["export_format"] = "obj",
                    ["exported_by"] = "cdmw_archive_lite",
                };
            }
            else
            {
                job["bones"] = Array.Empty<object>();
            }

            await WriteJsonAsync(jobPath, job, cancellationToken).ConfigureAwait(false);
            await ReportAsync(
                progress,
                package.TotalVertices,
                totalWork,
                "mesh_export_write",
                currentItem).ConfigureAwait(false);
            await RunNativeExportAsync(
                kind == ExportKind.Obj ? "obj-export-json" : "fbx-export-json",
                operation,
                jobPath,
                reportPath,
                stagedOutput,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedOutput, destination, overwrite);
            if (kind == ExportKind.Obj)
            {
                await NativeObjMaterialWriter.WriteAsync(
                    package,
                    destination,
                    companionTextures,
                    overwrite,
                    cancellationToken).ConfigureAwait(false);
                if (File.Exists(stagedManifest))
                {
                    File.Move(stagedManifest, RoundtripManifestPath(destination), overwrite);
                }
            }
            await ReportAsync(progress, totalWork, totalWork, "mesh_export_write", currentItem).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(stagedOutput);
            TryDeleteOwnedDirectory(workRoot);
        }
    }

    private static async Task<IReadOnlyList<PreparedNativeBatch>> PrepareNativeSidecarsAsync(
        NativePreviewMeshPackage package,
        NativePreviewNormalization normalization,
        string workRoot,
        long totalWork,
        Func<ProgressUpdate, Task>? progress,
        string? currentItem,
        CancellationToken cancellationToken)
    {
        var result = new List<PreparedNativeBatch>(package.Batches.Count);
        long completed = 0;
        for (var batchOrdinal = 0; batchOrdinal < package.Batches.Count; batchOrdinal++)
        {
            var batch = package.Batches[batchOrdinal];
            var prefix = Path.Combine(workRoot, $"batch_{batchOrdinal:000}");
            var verticesPath = prefix + "_vertices.bin";
            var normalsPath = prefix + "_normals.bin";
            var uvsPath = prefix + "_uvs.bin";
            var facesPath = prefix + "_faces.bin";

            // The package stores one vertex per triangle corner, so the array the source held has
            // to be rebuilt before anything is written, in the source's own order.
            var rebuilt = await NativePreviewVertexRebuild.BuildAsync(
                batch,
                async corners =>
                {
                    completed += corners;
                    await ReportAsync(progress, completed, totalWork, "mesh_export_prepare", currentItem)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            await WriteVerticesAsync(
                verticesPath,
                rebuilt.Positions,
                3,
                normalization,
                cancellationToken).ConfigureAwait(false);
            // Normals survive the preview transform: it only recentres and scales uniformly, so
            // every direction it produced still points the way the source authored it.
            await WriteVerticesAsync(normalsPath, rebuilt.Normals, 3, null, cancellationToken).ConfigureAwait(false);
            await WriteVerticesAsync(uvsPath, rebuilt.TextureCoordinates, 2, null, cancellationToken).ConfigureAwait(false);
            await WriteFacesAsync(facesPath, rebuilt.CornerIndices, cancellationToken).ConfigureAwait(false);

            result.Add(new PreparedNativeBatch(new Dictionary<string, object?>
            {
                ["index"] = batch.Index,
                // Full names the object after the submesh and the material after the material, so
                // parts sharing one material stay distinguishable. Its own fallback chain, and
                // ours, is submesh name then material name then the ordinal.
                ["name"] = CleanName(batch.SubmeshName, CleanName(batch.MaterialName, $"part_{batch.Index:000}")),
                ["material"] = CleanName(batch.MaterialName, CleanName(batch.SubmeshName, $"part_{batch.Index:000}")),
                ["vertices_binary"] = BinaryDescriptor(verticesPath, rebuilt.VertexCount, 3, "f64"),
                ["faces_binary"] = BinaryDescriptor(facesPath, batch.VertexCount / 3, 3, "i32"),
                ["normals_binary"] = BinaryDescriptor(normalsPath, rebuilt.VertexCount, 3, "f64"),
                ["uvs_binary"] = BinaryDescriptor(uvsPath, rebuilt.VertexCount, 2, "f64"),
                ["source_vertex_map"] = rebuilt.SourceVertexMap,
            },
            rebuilt.VertexCount));
        }
        return result;
    }

    /// <param name="restore">
    /// The framing transform to undo, for positions; null for an attribute the preview left alone.
    /// </param>
    private static async Task WriteVerticesAsync(
        string path,
        float[] source,
        int components,
        NativePreviewNormalization? restore,
        CancellationToken cancellationToken)
    {
        await using var output = OpenNew(path);
        var buffer = new byte[RecordsPerChunk * components * sizeof(double)];
        var written = 0;
        while (written < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length / sizeof(double), source.Length - written);
            for (var index = 0; index < count; index++)
            {
                var value = (double)source[written + index];
                BinaryPrimitives.WriteDoubleLittleEndian(
                    buffer.AsSpan(index * sizeof(double), sizeof(double)),
                    restore is null ? value : restore.Restore(value, (written + index) % components));
            }
            await output.WriteAsync(buffer.AsMemory(0, count * sizeof(double)), cancellationToken).ConfigureAwait(false);
            written += count;
        }
    }

    private static async Task WriteFacesAsync(
        string path,
        int[] cornerIndices,
        CancellationToken cancellationToken)
    {
        await using var output = OpenNew(path);
        var buffer = new byte[RecordsPerChunk * sizeof(int)];
        var written = 0;
        while (written < cornerIndices.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length / sizeof(int), cornerIndices.Length - written);
            for (var index = 0; index < count; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    buffer.AsSpan(index * sizeof(int), sizeof(int)),
                    cornerIndices[written + index]);
            }
            await output.WriteAsync(buffer.AsMemory(0, count * sizeof(int)), cancellationToken).ConfigureAwait(false);
            written += count;
        }
    }

    private static Dictionary<string, object?> BinaryDescriptor(
        string path,
        int count,
        int components,
        string type) => new()
    {
        ["path"] = path,
        ["count"] = count,
        ["components"] = components,
        ["type"] = type,
    };

    private static async Task RunNativeExportAsync(
        string command,
        string expectedOperation,
        string jobPath,
        string reportPath,
        string expectedOutput,
        CancellationToken cancellationToken)
    {
        var executable = ResolveMeshCorePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(jobPath);
        startInfo.ArgumentList.Add(reportPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cdmw-mesh-core could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NativeExportTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            await ObserveCaptureAsync(stdout, stderr).ConfigureAwait(false);
            throw new TimeoutException($"cdmw-mesh-core did not finish within {NativeExportTimeout.TotalMinutes:N0} minutes.");
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
            throw new InvalidDataException($"cdmw-mesh-core exited with code {process.ExitCode}: {detail.Trim()}");
        }
        await ValidateNativeReportAsync(reportPath, expectedOperation, expectedOutput, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateNativeReportAsync(
        string reportPath,
        string expectedOperation,
        string expectedOutput,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
        {
            throw new InvalidDataException("cdmw-mesh-core did not produce an export report.");
        }
        using var report = JsonDocument.Parse(
            await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false));
        var root = report.RootElement;
        if (ReadString(root, "status") != "ok"
            || ReadString(root, "backend") != "cdmw_mesh_core_0.1"
            || ReadString(root, "operation") != expectedOperation)
        {
            throw new InvalidDataException($"cdmw-mesh-core returned an invalid {expectedOperation} report.");
        }
        var reportedOutput = Path.GetFullPath(ReadString(root, "output_path"));
        if (!reportedOutput.Equals(Path.GetFullPath(expectedOutput), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(reportedOutput)
            || new FileInfo(reportedOutput).Length == 0)
        {
            throw new InvalidDataException("cdmw-mesh-core reported an unexpected or empty output file.");
        }
    }

    private static string ResolveMeshCorePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_MESH_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }
        var packaged = Path.Combine(AppContext.BaseDirectory, "mesh", "cdmw-mesh-core.exe");
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
                    "cdmw_mesh_core",
                    "build",
                    configuration,
                    "cdmw-mesh-core.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        throw new FileNotFoundException(
            "cdmw-mesh-core.exe was not found. Rebuild the Archive Lite portable package or set CDMW_ARCHIVE_LITE_MESH_CORE_PATH.");
    }

    private static async Task WriteJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, payload, CompactJson, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReportAsync(
        Func<ProgressUpdate, Task>? progress,
        long completed,
        long total,
        string phase,
        string? currentItem)
    {
        if (progress is not null)
        {
            await progress(new ProgressUpdate(completed, total, phase, currentItem)).ConfigureAwait(false);
        }
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
                // The helper is already being torn down; only observe the capture task.
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
            // Preserve the cancellation or timeout that owns teardown.
        }
    }

    /// <summary>
    /// The round-trip sidecar's path: the exported file's own name with the suffix appended, which
    /// is the name CDMW Full looks for when it reads an OBJ back in.
    /// </summary>
    internal static string RoundtripManifestPath(string objDestination) => objDestination + ".meta.json";

    internal static string CleanName(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var builder = new StringBuilder(Math.Min(source.Length, 96));
        foreach (var character in source)
        {
            if (builder.Length >= 96)
            {
                break;
            }
            builder.Append(char.IsControl(character) || character is '/' or '\\' ? '_' : character);
        }
        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A later temp cleanup can remove a locked staging file.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary conversion result.
        }
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A later temp cleanup can remove a locked work directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary conversion result.
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed record PreparedNativeBatch(Dictionary<string, object?> Payload, int UniqueVertexCount);
}
