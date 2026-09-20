using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

public sealed class ArchiveCharacterPreviewService(
    ArchiveSessionManager sessions, ArchiveCharacterCatalogService catalog,
    NativeModelPreviewService models, ArchiveWorkPriority priority) : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _thumbnailPreparation = new(2, 2);
    private readonly CharacterThumbnailRenderer _thumbnailRenderer = new();

    public async Task<CharacterPreviewResult> BuildAsync(CharacterPreviewRequest request,
        Func<ProgressUpdate, Task>? progress, CancellationToken token)
    {
        var detail = await catalog.DetailAsync(new(request.SessionId, request.Key), progress, token).ConfigureAwait(false);
        var previewModels = PreviewModels(detail);
        if (previewModels.Count == 0 || detail.Row.Resolution == "ambiguous")
            return new(request.SessionId, request.Key, null, ["The model is unresolved or ambiguous. Inspect its files and resolution evidence."]);
        if (previewModels.Count > 32)
            return new(request.SessionId, request.Key, null, ["This appearance exceeds the preview limit of 32 model components."]);
        var session = sessions.GetRequired(request.SessionId);
        var includeTextures = request.IncludeTextures;
        if (request.FirstFrameOnly && includeTextures)
        {
            foreach (var model in previewModels)
                if (!await models.HasReusableTexturedPackageAsync(session, model, token).ConfigureAwait(false))
                { includeTextures = false; break; }
        }
        var parts = new List<CharacterPreviewPart>();
        foreach (var model in previewModels)
        {
            token.ThrowIfCancellationRequested();
            var package = await models.BuildAsync(session, model, includeTextures, progress, token).ConfigureAwait(false);
            var component = detail.Components.FirstOrDefault(component => component.ModelEntryIds.Contains(model.EntryId));
            parts.Add(new(package, component?.Scale ?? 1));
        }
        var warnings = detail.Evidence.Where(static value => value.Contains("Customization", StringComparison.OrdinalIgnoreCase)
            || value.Contains("incomplete", StringComparison.OrdinalIgnoreCase)).ToList();
        if (detail.Row.View == "appearances")
            warnings.Add("Base-model preview: customization, morphs and animation are not applied.");
        if (parts.Count == 1 && parts[0].Scale == 1)
            return new(session.Id, request.Key, parts[0].PackagePath, warnings, includeTextures);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "character-preview-v1:" + session.Fingerprint + ":" + detail.ContextKey + ":" + includeTextures + ":"
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
                    await NativePreviewPackageAdapter.PrepareAsync(destination, detail.Row.Path, token, includeTextures).ConfigureAwait(false);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            }
        }
        finally { gate.Release(); }
        return new(session.Id, request.Key, destination, warnings, includeTextures);
    }

    internal static IReadOnlyList<ArchiveEntryDto> PreviewModels(CharacterCatalogDetailResult detail)
    {
        if (!detail.Row.EmbeddedFace || detail.Row.Role is not ("body" or "whole_character")) return detail.Models;
        var bodyIds = detail.Components.Where(component => component.Role is "body" or "whole_character")
            .SelectMany(component => component.ModelEntryIds).ToHashSet();
        // A body that already includes its face must not also draw the separate head.
        return bodyIds.Count == 0 ? detail.Models : detail.Models.Where(model => bodyIds.Contains(model.EntryId)).ToArray();
    }

    public async Task<CharacterThumbnailResult> ThumbnailsAsync(CharacterThumbnailRequest request, CancellationToken token)
    {
        if (request.Keys.Count > 4) throw new ArgumentException("Character thumbnail batches may contain at most four rows.");
        var results = new List<CharacterThumbnail>();
        foreach (var key in request.Keys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                CharacterPreviewResult preview;
                await _thumbnailPreparation.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await priority.WaitForForegroundAsync(token).ConfigureAwait(false);
                    preview = await BuildAsync(new(request.SessionId, key), null, token).ConfigureAwait(false);
                }
                finally { _thumbnailPreparation.Release(); }
                if (preview.PackagePath is null)
                {
                    results.Add(new(key, null, string.Join(" ", preview.Warnings)));
                    continue;
                }
                var output = Path.Combine(preview.PackagePath, "character-thumbnail-v1.png");
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    await priority.WaitForForegroundAsync(token).ConfigureAwait(false);
                    await _thumbnailRenderer.RenderAsync(ResolveRendererPath(), key, preview.PackagePath, output, token).ConfigureAwait(false);
                }
                results.Add(File.Exists(output) ? new(key, output)
                    : new(key, null, "Thumbnail rendering was unavailable."));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { results.Add(new(key, null, error.Message)); }
        }
        token.ThrowIfCancellationRequested();
        return new(request.SessionId, results);
    }

    public void Dispose() => _thumbnailRenderer.Dispose();

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

}
