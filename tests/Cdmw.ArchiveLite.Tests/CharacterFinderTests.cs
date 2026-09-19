using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Cdmw.Archive.Content;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.App.ViewModels;
using Cdmw.ArchiveLite.Contracts;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Tests;

internal static partial class ArchiveLiteTestRunner
{
    private static async Task TestCharacterCatalogServiceAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync();
        const string body = "character/model/1_pc/1_phm/nude/hero_body_0001.pac";
        const string appearance = "character/appearance/1_pc/1_phm/hero.app_xml";
        await fixture.AddSingleEntryPackageAsync("0009", body, [1]);
        await fixture.AddSingleEntryPackageAsync("0010", appearance,
            Encoding.UTF8.GetBytes("<Appearance><Nude Name=\"hero_body_0001\"/></Appearance>"));
        var before = await ArchiveFingerprint.ComputeAsync(fixture.Root, CancellationToken.None);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(new(fixture.Root), CancellationToken.None);
        var service = new ArchiveCharacterCatalogService(sessions, native);
        var cache = Path.Combine(ArchiveLiteDataPaths.CharacterCatalogCache,
            $"{opened.Fingerprint}-v{ArchiveCharacterCatalogBuilder.Version}.json");
        using var cancelled = new CancellationTokenSource();
        try
        {
            await service.BuildAsync(new(opened.SessionId), update =>
            {
                if (update.CurrentItem == "Character catalogue ready") cancelled.Cancel();
                return Task.CompletedTask;
            }, cancelled.Token);
            throw new InvalidOperationException("cancelled character catalogue published");
        }
        catch (OperationCanceledException) { }
        Require(!File.Exists(cache) && !Directory.EnumerateFiles(Path.GetDirectoryName(cache)!, Path.GetFileName(cache) + ".*.tmp").Any(),
            "cancelled character catalogue left a persistent cache or staging file");
        var cold = await service.BuildAsync(new(opened.SessionId), null, CancellationToken.None);
        Require(!cold.UsedCache && cold.AssetCount == 1 && cold.AppearanceCount == 1, "body or appearance was dropped");
        var search = await service.SearchAsync(new(opened.SessionId), null, CancellationToken.None);
        var detail = await service.DetailAsync(new(opened.SessionId, search.Rows.Single().Key), null, CancellationToken.None);
        var exact = await service.ScopeAsync(new(opened.SessionId, detail.Row.Key), null, CancellationToken.None);
        var related = await service.ScopeAsync(new(opened.SessionId, detail.Row.Key, true), null, CancellationToken.None);
        Require(detail.Models.Single().Path == body && detail.Related.Single().Path == appearance,
            "detail/ownership did not resolve the archive's actual entries");
        Require(exact.EntryIds.Count == 1 && related.EntryIds.Count > exact.EntryIds.Count
            && exact.Extensions!.Any(facet => facet.Extension == ".pac"), "finder scopes lost model links or extension facets");
        var bounded = await service.ScopeAsync(new(opened.SessionId, detail.Row.Key, true, 1), null, CancellationToken.None);
        Require(bounded.Truncated && bounded.EntryIds.Count == 1, "related file scope ignored its limit");
        try
        {
            await service.BuildAsync(new(opened.SessionId), null, cancelled.Token);
            throw new InvalidOperationException("cached character catalogue ignored cancellation");
        }
        catch (OperationCanceledException) { }
        using var second = new ArchiveSessionManager(native);
        var reopened = await second.OpenAsync(new(fixture.Root), CancellationToken.None);
        var reloaded = await new ArchiveCharacterCatalogService(second, native).BuildAsync(new(reopened.SessionId), null, CancellationToken.None);
        Require(reloaded.UsedCache && reloaded.AssetCount == cold.AssetCount && reloaded.AppearanceCount == cold.AppearanceCount,
            "character catalogue did not survive a new session");
        Require((await ArchiveFingerprint.ComputeAsync(fixture.Root, CancellationToken.None)).Value == before.Value,
            "character catalogue modified its archive");
    }

    private static async Task TestCharacterPreviewAssemblyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdmw-character-preview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parts = new List<CharacterPreviewPart>();
            var expected = new List<Vector3[]>();
            for (var part = 0; part < 2; part++)
            {
                var path = Path.Combine(root, "source-" + part);
                Directory.CreateDirectory(path);
                var center = new Vector3(0, part * 3, 0);
                var positions = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0) };
                var bytes = new byte[3 * 92];
                for (var vertex = 0; vertex < 3; vertex++)
                {
                    var position = positions[vertex];
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(vertex * 92), position.X);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(vertex * 92 + 4), position.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(vertex * 92 + 8), position.Z);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(vertex * 92 + 20), 1);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(vertex * 92 + 36), 0.25f);
                }
                await File.WriteAllBytesAsync(Path.Combine(path, "vertices.bin"), bytes);
                await File.WriteAllBytesAsync(Path.Combine(path, "material.bin"), [(byte)part, 42]);
                await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), JsonSerializer.Serialize(new
                {
                    schema_version = 8, source_path = "character/part.pac",
                    normalization_center = new[] { center.X, center.Y, center.Z }, normalization_scale = 2,
                    batches = new[] { new { index = 0, material_name = "part-" + part, vertex_file = "vertices.bin",
                        vertex_count = 3, base_color = new[] { 0.7, 0.3, 0.2 }, material_category = "skin",
                        resource = "material.bin" } },
                }));
                var scale = part == 0 ? 1f : 1.2f;
                expected.Add(positions.Select(position => (position / 2 + center) * scale).ToArray());
                parts.Add(new(path, scale));
            }
            var destination = Path.Combine(root, "combined");
            await CharacterPreviewPackage.ComposeAsync(destination, "character/appearance/test.app_xml", parts, CancellationToken.None);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "manifest.json")));
            var value = manifest.RootElement;
            var centerValues = value.GetProperty("normalization_center").EnumerateArray().Select(number => number.GetSingle()).ToArray();
            var combinedCenter = new Vector3(centerValues[0], centerValues[1], centerValues[2]);
            var combinedScale = value.GetProperty("normalization_scale").GetSingle();
            var batches = value.GetProperty("batches").EnumerateArray().ToArray();
            Require(batches.Length == 2 && value.GetProperty("vertex_count").GetInt32() == 6, "preview assembly lost a component");
            for (var part = 0; part < 2; part++)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(destination, batches[part].GetProperty("vertex_file").GetString()!));
                var resource = await File.ReadAllBytesAsync(Path.Combine(destination, batches[part].GetProperty("resource").GetString()!));
                Require(resource[0] == part, "same-named component resources overwrote each other");
                for (var vertex = 0; vertex < 3; vertex++)
                {
                    var position = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(vertex * 92)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(vertex * 92 + 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(vertex * 92 + 8)));
                    Require(Vector3.Distance(position / combinedScale + combinedCenter, expected[part][vertex]) < 0.00001,
                        "body/head source alignment or authored scale was lost");
                    Require(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(vertex * 92 + 20)) == 1
                        && BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(vertex * 92 + 36)) == 0.25f,
                        "assembly modified a normal or texture coordinate");
                }
            }
            await NativePreviewPackageAdapter.PrepareAsync(destination, "character/appearance/test.app_xml", CancellationToken.None);
            Require(NativePreviewPackageAdapter.HasCurrentAdapterMetadata(destination), "assembled package cannot feed the existing renderer");

            var renderer = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DOTNET_PREVIEW_PATH");
            Require(renderer is not null && File.Exists(renderer), "build the renderer before the character thumbnail test");
            var thumbnail = Path.Combine(destination, "character-thumbnail-v1.png");
            var job = Path.Combine(root, "job.json");
            await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new[] { new { Key = "fixture", Package = destination, Output = thumbnail } }));
            var start = new ProcessStartInfo(renderer!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--character-thumbnails"); start.ArgumentList.Add(job);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            await output;
            var diagnostics = await error;
            Require(process.ExitCode == 0 && File.Exists(thumbnail), "hidden character thumbnail renderer failed: " + diagnostics);
            var image = await PreviewImageLoader.LoadFrozenAsync(thumbnail, CancellationToken.None);
            Require(image.PixelWidth == 160 && image.PixelHeight == 160, "character thumbnail has the wrong dimensions");
            var pixels = new byte[160 * 160 * 4]; image.CopyPixels(pixels, 160 * 4, 0);
            var colors = pixels.Chunk(4).Select(pixel => BitConverter.ToInt32(pixel, 0)).GroupBy(color => color).ToArray();
            var foregroundPixels = 160 * 160 - colors.Max(color => color.Count());
            Require(foregroundPixels > 100, $"thumbnail contains no rendered geometry ({colors.Length} colors, {foregroundPixels} foreground pixels)");
            Require(CharacterPreviewPackage.IsReusable(destination), "a complete character package could not be reused");
            File.Delete(Path.Combine(destination, batches[0].GetProperty("vertex_file").GetString()!));
            Require(!CharacterPreviewPackage.IsReusable(destination), "an evicted geometry file left a reusable blank preview");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task TestCharacterFinderLifecycleAsync()
    {
        var image = Path.Combine(Path.GetTempPath(), "cdmw-character-finder-test-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        try
        {
            await RunOnWpfDispatcherAsync(async () =>
            {
                var worker = new FakeCharacterFinderWorker(image);
                var session = "session-a";
                var scopes = new List<(string, bool)>();
                var model = new CharacterFinderViewModel(worker, () => session, (key, _, related, _) =>
                { scopes.Add((key, related)); return Task.FromResult(true); });
                await model.ActivateAsync(CancellationToken.None);
                await WaitUntilAsync(() => model.Rows.All(row => row.Thumbnail is not null) && model.PackagePath is not null, TimeSpan.FromSeconds(3));
                Require(worker.Searches.Count == 1 && model.Rows.Count == 5 && worker.ThumbnailBatches.SequenceEqual([4, 1]),
                    "character page or bounded thumbnail batches are wrong");
                model.Query = "h"; model.Query = "he"; model.Query = "hero";
                await WaitUntilAsync(() => worker.Searches.Count == 2 && !model.IsBusy, TimeSpan.FromSeconds(2));
                await Task.Delay(300);
                Require(worker.Searches.Count == 2 && worker.Searches[^1].Query == "hero", "typing did not debounce to the latest query");
                model.Query = "immediate"; model.SearchCommand.Execute(null);
                await WaitUntilAsync(() => worker.Searches.Count == 3 && !model.IsBusy, TimeSpan.FromSeconds(2));
                await Task.Delay(300);
                Require(worker.Searches.Count == 3, "Enter/Search left a second debounced request queued");
                model.Role = "body";
                await WaitUntilAsync(() => worker.Searches.Count == 4 && !model.IsBusy, TimeSpan.FromSeconds(2));
                model.RefreshLocalization();
                await Task.Delay(280);
                Require(worker.Searches.Count == 4 && model.Role == "body", "facet refresh reset a filter or scheduled a search");
                model.NextCommand.Execute(null);
                await WaitUntilAsync(() => worker.Searches.Count == 5 && !model.IsBusy, TimeSpan.FromSeconds(2));
                Require(worker.Searches[^1].PageStart == 72, "next page lost its offset");
                await WaitUntilAsync(() => model.PackagePath is not null && model.ShowExactCommand.CanExecute(null), TimeSpan.FromSeconds(2));
                var closes = 0; model.CloseRequested += (_, _) => closes++;
                model.ShowExactCommand.Execute(null);
                await WaitUntilAsync(() => scopes.Count == 1 && !model.IsBusy, TimeSpan.FromSeconds(2));
                Require(!scopes[0].Item2 && closes == 1, "exact scope failed to activate the selected model");

                worker.IgnoreCancellation = true;
                worker.Delay = 350;
                var previous = model.SelectedRow!;
                model.SelectedRow = model.Rows[1];
                await Task.Delay(130);
                model.SelectedRow = previous;
                await WaitUntilAsync(() => model.PackagePath == previous.Source.Key, TimeSpan.FromSeconds(2));
                await Task.Delay(500);
                Require(model.PackagePath == previous.Source.Key, "a late preview replaced the newer selection");
                model.Query = "late-session";
                await WaitUntilAsync(() => worker.Searches.Count == 6, TimeSpan.FromSeconds(2));
                session = "session-b"; model.NotifyArchiveSessionChanged();
                await Task.Delay(450);
                Require(model.Rows.Count == 0 && model.PackagePath is null, "old session results repopulated the finder");
                var reopen = model.ActivateAsync(CancellationToken.None);
                await Task.Delay(40); model.Deactivate(); await reopen;
                Require(model.Rows.Count == 0 && model.PackagePath is null, "closed finder published a late search");
                var settings = model.CaptureSettings();
                var loaded = JsonSerializer.Deserialize<LiteSettings>(JsonSerializer.Serialize(new LiteSettings(CharacterFinder: settings)));
                Require(loaded?.CharacterFinder == settings && JsonSerializer.Deserialize<LiteSettings>("{}")?.CharacterFinder is null,
                    "finder settings failed to round-trip or broke earlier settings");
                model.RequestShutdown();
            });
        }
        finally { File.Delete(image); }
    }

    private sealed class FakeCharacterFinderWorker(string image) : IWorkerRequestClient
    {
        public List<CharacterCatalogSearchRequest> Searches { get; } = [];
        public List<int> ThumbnailBatches { get; } = [];
        public int Delay { get; set; }
        public bool IgnoreCancellation { get; set; }
        private static CharacterCatalogRow Row(string key) => new(key, "assets", "body", key, key,
            "character/model/" + key + ".pac", "1_pc", "1_phm", "resolved", "ready", 1, 1, "fixture");
        public async Task<TResult> SendAsync<TRequest, TResult>(string kind, long generation, TRequest payload,
            CancellationToken token, IProgress<ProgressUpdate>? progress = null)
        {
            if (payload is CharacterCatalogSearchRequest requested) Searches.Add(requested);
            if (Delay > 0) await Task.Delay(Delay, IgnoreCancellation ? CancellationToken.None : token);
            object result = payload switch
            {
                CharacterCatalogSearchRequest search => new CharacterCatalogSearchResult(search.SessionId, 80, search.PageStart, 72,
                    Enumerable.Range(search.PageStart, 5).Select(index => Row("asset:" + index)).ToArray(),
                    [new("role", "body", 80), new("source_group", "1_pc", 80)], []),
                CharacterCatalogDetailRequest detail => new CharacterCatalogDetailResult(detail.SessionId, Row(detail.Key), [], [], [], [],
                    [Row("appearance:hero")], ["fixture details"], 1, 1, 0, false, "", "fixture"),
                CharacterPreviewRequest preview => new CharacterPreviewResult(preview.SessionId, preview.Key, preview.Key, []),
                CharacterThumbnailRequest thumbnails => ThumbnailResult(thumbnails),
                _ => throw new InvalidOperationException("Unexpected character finder request: " + kind),
            };
            return (TResult)result;
        }
        private CharacterThumbnailResult ThumbnailResult(CharacterThumbnailRequest request)
        {
            ThumbnailBatches.Add(request.Keys.Count);
            return new(request.SessionId, request.Keys.Select(key => new CharacterThumbnail(key, image)).ToArray());
        }
    }
}
