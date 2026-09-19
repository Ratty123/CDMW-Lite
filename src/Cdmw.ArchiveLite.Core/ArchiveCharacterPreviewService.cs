using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

public sealed class ArchiveCharacterPreviewService(
    ArchiveSessionManager sessions, ArchiveCharacterCatalogService catalog,
    NativeModelPreviewService models, ArchiveWorkPriority priority)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);

    public async Task<CharacterPreviewResult> BuildAsync(CharacterPreviewRequest request,
        Func<ProgressUpdate, Task>? progress, CancellationToken token)
    {
        var detail = await catalog.DetailAsync(new(request.SessionId, request.Key), progress, token).ConfigureAwait(false);
        if (detail.Models.Count == 0 || detail.Row.Resolution == "ambiguous")
            return new(request.SessionId, request.Key, null, ["The model is unresolved or ambiguous. Inspect its files and resolution evidence."]);
        if (detail.Models.Count > 32)
            return new(request.SessionId, request.Key, null, ["This appearance exceeds the preview limit of 32 model components."]);
        var session = sessions.GetRequired(request.SessionId);
        var parts = new List<CharacterPreviewPart>();
        foreach (var model in detail.Models)
        {
            token.ThrowIfCancellationRequested();
            var package = await models.BuildAsync(session, model, request.IncludeTextures, progress, token).ConfigureAwait(false);
            var component = detail.Components.FirstOrDefault(component => component.ModelEntryIds.Contains(model.EntryId));
            parts.Add(new(package, component?.Scale ?? 1));
        }
        var warnings = detail.Evidence.Where(static value => value.Contains("Customization", StringComparison.OrdinalIgnoreCase)
            || value.Contains("incomplete", StringComparison.OrdinalIgnoreCase)).ToList();
        if (detail.Row.View == "appearances")
            warnings.Add("Base-model preview: customization, morphs and animation are not applied.");
        if (parts.Count == 1 && parts[0].Scale == 1)
            return new(session.Id, request.Key, parts[0].PackagePath, warnings);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "character-preview-v1:" + session.Fingerprint + ":" + detail.ContextKey + ":" + request.IncludeTextures + ":"
            + string.Join('|', parts.Select(part => part.PackagePath + ":" + part.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)))))).ToLowerInvariant();
        var root = Path.Combine(ArchiveLiteDataPaths.PreviewCache, "characters");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, key);
        var gate = _buildGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!CharacterPreviewPackage.IsReusable(destination))
            {
                var staging = Path.Combine(root, ".work-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await CharacterPreviewPackage.ComposeAsync(staging, detail.Row.Path, parts, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    sessions.GetRequired(session.Id);
                    if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                    Directory.Move(staging, destination);
                    await NativePreviewPackageAdapter.PrepareAsync(destination, detail.Row.Path, token, request.IncludeTextures).ConfigureAwait(false);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            }
        }
        finally { gate.Release(); }
        return new(session.Id, request.Key, destination, warnings);
    }

    public async Task<CharacterThumbnailResult> ThumbnailsAsync(CharacterThumbnailRequest request, CancellationToken token)
    {
        if (request.Keys.Count > 4) throw new ArgumentException("Character thumbnail batches may contain at most four rows.");
        await _thumbnailGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var results = new List<CharacterThumbnail>();
            var jobs = new List<ThumbnailJob>();
            foreach (var key in request.Keys.Distinct(StringComparer.Ordinal))
            {
                await priority.WaitForForegroundAsync(token).ConfigureAwait(false);
                try
                {
                    var preview = await BuildAsync(new(request.SessionId, key), null, token).ConfigureAwait(false);
                    if (preview.PackagePath is null)
                    {
                        results.Add(new(key, null, string.Join(" ", preview.Warnings)));
                        continue;
                    }
                    var output = Path.Combine(preview.PackagePath, "character-thumbnail-v1.png");
                    if (File.Exists(output) && new FileInfo(output).Length > 0) results.Add(new(key, output));
                    else jobs.Add(new(key, preview.PackagePath, output));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { results.Add(new(key, null, error.Message)); }
            }
            if (jobs.Count > 0)
            {
                await priority.WaitForForegroundAsync(token).ConfigureAwait(false);
                try
                {
                    await RenderThumbnailsAsync(jobs, token).ConfigureAwait(false);
                    results.AddRange(jobs.Select(job => File.Exists(job.Output)
                        ? new CharacterThumbnail(job.Key, job.Output)
                        : new CharacterThumbnail(job.Key, null, "Thumbnail rendering was unavailable.")));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { results.AddRange(jobs.Select(job => new CharacterThumbnail(job.Key, null, error.Message))); }
            }
            token.ThrowIfCancellationRequested();
            return new(request.SessionId, results);
        }
        finally { _thumbnailGate.Release(); }
    }

    private static async Task RenderThumbnailsAsync(IReadOnlyList<ThumbnailJob> jobs, CancellationToken token)
    {
        var root = Path.Combine(ArchiveLiteDataPaths.PreviewCache, "character-thumbnail-jobs");
        Directory.CreateDirectory(root);
        var jobPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(jobs), token).ConfigureAwait(false);
            var start = new ProcessStartInfo(ResolveRendererPath())
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--character-thumbnails");
            start.ArgumentList.Add(jobPath);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the character thumbnail renderer.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("Character thumbnail rendering timed out.");
            }
            await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Character thumbnail renderer failed: " + error[..Math.Min(error.Length, 1024)]);
        }
        finally { if (File.Exists(jobPath)) File.Delete(jobPath); }
    }

    private static string ResolveRendererPath()
    {
        var configured = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DOTNET_PREVIEW_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var packaged = Path.Combine(AppContext.BaseDirectory, "renderer", "cdmw-mesh-dotnet-editor.exe");
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(directory.FullName, "tools", "dotnet_mesh_editor_experiment", "bin",
                    configuration, "net8.0-windows", "cdmw-mesh-dotnet-editor.exe");
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("The Archive Lite 3D renderer could not be located.");
    }

    private sealed record ThumbnailJob(string Key, string Package, string Output);
}
