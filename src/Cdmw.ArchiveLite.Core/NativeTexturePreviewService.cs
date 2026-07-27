using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

/// <summary>One DDS source paired with the archive row that owns it.</summary>
public sealed record TexturePreviewRequest(ArchiveEntryDto Entry, string DdsPath);

/// <summary>The published preview for one request, or the reason the decode failed.</summary>
public sealed record TexturePreviewResult(ArchiveEntryDto Entry, string? PngPath, string? Error);

public sealed class NativeTexturePreviewService
{
    public const string DecodePhase = "texture_preview_decode";

    private const string ArtifactVersion = "directxtex_preview_v3";
    private const string BackendId = "directxtex_native_0.2";
    private const string SidecarSuffix = ".cdmw_texture.json";

    // cd-texture-dx returns 2 when at least one job failed but the report is still complete
    // and authoritative. Only codes outside {0, 2} mean the report cannot be trusted.
    private const int PartialFailureExitCode = 2;

    private static readonly TimeSpan MinimumDecodeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MaximumDecodeTimeout = TimeSpan.FromMinutes(60);
    /// <summary>Test seam; a scenario cannot afford to wait out the production interval.</summary>
    internal static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaximumMemoizedValidations = 8192;
    private static readonly ConcurrentDictionary<PngIdentity, bool> PngValidations = new();
    private static readonly uint[] Crc32Table = CreateCrc32Table();
    private static string? _cachedDecoderPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _decodeGates = new(StringComparer.Ordinal);

    public async Task<string> BuildAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        string ddsPath,
        CancellationToken cancellationToken,
        Func<ProgressUpdate, Task>? publishProgress = null)
        => await BuildOneAsync(session, entry, ddsPath, 4096, "textures", publishProgress, cancellationToken).ConfigureAwait(false);

    public async Task<string> BuildThumbnailAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        string ddsPath,
        int maximumDimension,
        CancellationToken cancellationToken)
        => await BuildOneAsync(session, entry, ddsPath, maximumDimension, "item-icons", null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Decodes many DDS sources through a single cd-texture-dx invocation. Per-request failures are
    /// reported in the result rows; only a failure that invalidates the whole batch throws.
    /// </summary>
    public async Task<IReadOnlyList<TexturePreviewResult>> BuildThumbnailBatchAsync(
        ArchiveSession session,
        IReadOnlyList<TexturePreviewRequest> requests,
        int maximumDimension,
        CancellationToken cancellationToken)
        => await BuildBatchCoreAsync(session, requests, maximumDimension, "item-icons", null, cancellationToken)
            .ConfigureAwait(false);

    public string? TryGetCachedThumbnail(
        ArchiveSession session,
        ArchiveEntryDto entry,
        int maximumDimension)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        ValidateMaximumDimension(maximumDimension);
        var destination = ResolveDestination(session, entry, maximumDimension, "item-icons");
        if (!IsCachedPreviewValid(destination))
        {
            return null;
        }
        PreviewCacheLeases.MarkRecent(destination);
        return destination;
    }

    private async Task<string> BuildOneAsync(
        ArchiveSession session,
        ArchiveEntryDto entry,
        string ddsPath,
        int maximumDimension,
        string cacheNamespace,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(ddsPath);
        var results = await BuildBatchCoreAsync(
            session,
            [new TexturePreviewRequest(entry, ddsPath)],
            maximumDimension,
            cacheNamespace,
            publishProgress,
            cancellationToken).ConfigureAwait(false);
        var result = results[0];
        return result.PngPath
            ?? throw new InvalidDataException(result.Error ?? "DirectXTex could not decode the selected DDS.");
    }

    private async Task<IReadOnlyList<TexturePreviewResult>> BuildBatchCoreAsync(
        ArchiveSession session,
        IReadOnlyList<TexturePreviewRequest> requests,
        int maximumDimension,
        string cacheNamespace,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requests);
        ValidateMaximumDimension(maximumDimension);
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request.Entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DdsPath);
            if (!request.Entry.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"DirectXTex preview does not support {request.Entry.Extension}.");
            }
        }
        if (requests.Count == 0)
        {
            return [];
        }

        ArchiveLiteDataPaths.EnsureCreated();
        var textureRoot = Path.Combine(ArchiveLiteDataPaths.PreviewCache, cacheNamespace);
        Directory.CreateDirectory(textureRoot);

        var plans = new List<PreviewPlan>(requests.Count);
        foreach (var request in requests)
        {
            var destination = ResolveDestination(session, request.Entry, maximumDimension, cacheNamespace);
            plans.Add(new PreviewPlan(request, destination, Path.GetFileNameWithoutExtension(destination)));
        }

        var published = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var leases = new List<PreviewCacheLease>(plans.Count);
        try
        {
            // Pin every destination so a concurrent prune cannot evict what this batch is about to
            // read or publish.
            foreach (var plan in plans)
            {
                leases.Add(PreviewCacheLeases.Acquire(plan.Destination));
            }

            var pending = new List<PreviewPlan>(plans.Count);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plan in plans)
            {
                if (IsCachedPreviewValid(plan.Destination))
                {
                    published[plan.Key] = plan.Destination;
                }
                else if (claimed.Add(plan.Key))
                {
                    // Two rows in one batch can share a cache key; decode it once.
                    pending.Add(plan);
                }
            }

            if (pending.Count > 0)
            {
                await DecodePendingAsync(
                    pending,
                    textureRoot,
                    maximumDimension,
                    published,
                    failed,
                    publishProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        var results = new List<TexturePreviewResult>(plans.Count);
        foreach (var plan in plans)
        {
            if (published.TryGetValue(plan.Key, out var pngPath))
            {
                PreviewCacheLeases.MarkRecent(pngPath);
                results.Add(new TexturePreviewResult(plan.Request.Entry, pngPath, null));
                continue;
            }
            var error = failed.TryGetValue(plan.Key, out var message)
                ? message
                : "DirectXTex could not decode the selected DDS.";
            results.Add(new TexturePreviewResult(plan.Request.Entry, null, error));
        }
        if (published.Count > 0)
        {
            ArchiveLiteCacheMaintenance.RequestPrune(
                ArchiveLiteDataPaths.Cache,
                ArchiveLiteCacheMaintenance.DefaultCacheMaximumBytes);
        }
        return results;
    }

    private async Task DecodePendingAsync(
        List<PreviewPlan> pending,
        string textureRoot,
        int maximumDimension,
        Dictionary<string, string> published,
        Dictionary<string, string> failed,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        // Reject sources the helper cannot survive before paying for a process launch.
        var decodable = new List<PreviewPlan>(pending.Count);
        foreach (var plan in pending)
        {
            var rejection = DdsResourceLimits.DescribeRejection(plan.Request.DdsPath);
            if (rejection is null)
            {
                decodable.Add(plan);
                continue;
            }
            failed[plan.Key] = rejection;
            TexturePreviewDiagnostics.RecordFailure(
                "batch-preview-json",
                "unsafe_dds_input",
                plan.Request.Entry.Path,
                rejection);
        }
        if (decodable.Count == 0)
        {
            return;
        }

        // Gates are taken in a single global order so overlapping batches cannot deadlock.
        decodable.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        var gates = new List<(string Key, SemaphoreSlim Gate)>(decodable.Count);
        try
        {
            foreach (var plan in decodable)
            {
                var gate = _decodeGates.GetOrAdd(plan.Key, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gates.Add((plan.Key, gate));
            }

            var stillPending = new List<PreviewPlan>(decodable.Count);
            foreach (var plan in decodable)
            {
                if (IsCachedPreviewValid(plan.Destination))
                {
                    published[plan.Key] = plan.Destination;
                }
                else
                {
                    stillPending.Add(plan);
                }
            }
            if (stillPending.Count > 0)
            {
                await DecodeBatchAsync(
                    stillPending,
                    textureRoot,
                    maximumDimension,
                    published,
                    failed,
                    publishProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            for (var index = gates.Count - 1; index >= 0; index--)
            {
                var (key, gate) = gates[index];
                gate.Release();
                // Drop the gate once nobody holds or waits on it, so browsing a large archive does
                // not retain one semaphore per cache key for the life of the process. A racing
                // caller may still hold this instance and decode alongside a replacement; that
                // costs duplicated work, and publication already tolerates the race.
                if (gate.CurrentCount == 1)
                {
                    _decodeGates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
                }
            }
        }
    }

    private async Task DecodeBatchAsync(
        List<PreviewPlan> pending,
        string textureRoot,
        int maximumDimension,
        Dictionary<string, string> published,
        Dictionary<string, string> failed,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(textureRoot, $".batch.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(staging);
        try
        {
            var stagedOutputs = new string[pending.Count];
            var jobs = new object[pending.Count];
            for (var index = 0; index < pending.Count; index++)
            {
                var plan = pending[index];
                stagedOutputs[index] = Path.Combine(staging, $"{index:D4}.png");
                jobs[index] = new
                {
                    input = Path.GetFullPath(plan.Request.DdsPath),
                    output = stagedOutputs[index],
                    slot = plan.Request.Entry.Role == ArchiveEntryRole.Normal ? "normal" : "base",
                    normal_space = "auto",
                    max_dimension = maximumDimension,
                    requested_mip = 0,
                    output_pixel_type = "rgba8",
                };
            }

            var jobPath = Path.Combine(staging, "job.json");
            var reportPath = Path.Combine(staging, "report.json");
            await AtomicFile.WriteAsync(
                jobPath,
                async (stream, token) => await JsonSerializer.SerializeAsync(
                    stream,
                    new
                    {
                        version = 2,
                        backend = BackendId,
                        jobs,
                    },
                    WorkerProtocol.JsonOptions,
                    token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            var timeout = ResolveDecodeTimeout(pending, maximumDimension);
            await RunDecoderAsync(jobPath, reportPath, timeout, publishProgress, cancellationToken).ConfigureAwait(false);
            var reported = ReadReport(reportPath);

            for (var index = 0; index < pending.Count; index++)
            {
                var plan = pending[index];
                var stagedOutput = stagedOutputs[index];
                if (!reported.TryGetValue(Path.GetFullPath(stagedOutput), out var item))
                {
                    failed[plan.Key] = "cd-texture-dx omitted this job from its decode report.";
                    TexturePreviewDiagnostics.RecordFailure(
                        "batch-preview-json",
                        "missing_job_result",
                        plan.Request.Entry.Path,
                        $"report carried {reported.Count} of {pending.Count} requested job(s)");
                    continue;
                }
                if (!string.Equals(item.Status, "decoded", StringComparison.OrdinalIgnoreCase))
                {
                    var message = item.Message ?? "DirectXTex could not decode the selected DDS.";
                    failed[plan.Key] = message;
                    TexturePreviewDiagnostics.RecordFailure(
                        "batch-preview-json",
                        "helper_reported_error",
                        plan.Request.Entry.Path,
                        message);
                    continue;
                }
                if (!IsValidPng(stagedOutput))
                {
                    failed[plan.Key] = "DirectXTex did not produce a valid PNG preview.";
                    TexturePreviewDiagnostics.RecordFailure(
                        "batch-preview-json",
                        "invalid_helper_output",
                        plan.Request.Entry.Path,
                        stagedOutput);
                    continue;
                }
                if (TryPublish(plan, stagedOutput, item))
                {
                    published[plan.Key] = plan.Destination;
                }
                else
                {
                    failed[plan.Key] = "DirectXTex did not publish a valid PNG preview.";
                    TexturePreviewDiagnostics.RecordFailure(
                        "batch-preview-json",
                        "publication_failed",
                        plan.Request.Entry.Path,
                        plan.Destination);
                }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A later bounded preview-cache prune can remove locked staging output.
            }
        }
    }

    /// <summary>
    /// Publishes the sidecar first, then the PNG. A cache hit requires both, so an interrupted
    /// publication leaves a miss rather than a preview with no provenance.
    /// </summary>
    private static bool TryPublish(PreviewPlan plan, string stagedOutput, ReportItem item)
    {
        try
        {
            var stagedSidecar = stagedOutput + SidecarSuffix;
            File.WriteAllText(
                stagedSidecar,
                JsonSerializer.Serialize(
                    new TexturePreviewSidecar(
                        item.Status ?? "decoded",
                        BackendId,
                        ArtifactVersion,
                        plan.Request.Entry.Path,
                        item.Format,
                        item.Width,
                        item.Height,
                        DateTimeOffset.UtcNow),
                    WorkerProtocol.JsonOptions),
                new UTF8Encoding(false));
            File.Move(stagedSidecar, SidecarPath(plan.Destination), overwrite: true);
            File.Move(stagedOutput, plan.Destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another immutable decode may have won the publication race.
        }
        return IsCachedPreviewValid(plan.Destination);
    }

    private static string SidecarPath(string pngPath) => pngPath + SidecarSuffix;

    private static string ResolveDestination(
        ArchiveSession session,
        ArchiveEntryDto entry,
        int maximumDimension,
        string cacheNamespace)
    {
        var identity = Encoding.UTF8.GetBytes(string.Join(
            '|',
            ArtifactVersion,
            HelperIdentity(),
            maximumDimension,
            session.Fingerprint,
            entry.EntryId,
            entry.Path,
            entry.Offset,
            entry.StoredSize,
            entry.OriginalSize));
        var key = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        return Path.Combine(ArchiveLiteDataPaths.PreviewCache, cacheNamespace, key + ".png");
    }

    /// <summary>
    /// Folds the helper build into the cache key so rebuilding cd-texture-dx retires previews it
    /// produced, instead of waiting for someone to remember to bump a version constant.
    /// </summary>
    private static string HelperIdentity()
    {
        var path = TryResolveDecoderPath();
        if (path is null)
        {
            return "helper=missing";
        }
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? $"helper={path.ToLowerInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
                : "helper=missing";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "helper=unreadable";
        }
    }

    private static void ValidateMaximumDimension(int maximumDimension)
    {
        if (maximumDimension is < 32 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDimension), "Texture preview size must be between 32 and 4096 pixels.");
        }
    }

    /// <summary>
    /// Scales the timeout with the real cost of the batch. A flat allowance either aborts a large
    /// BC7 decode that was progressing or leaves a wedged helper running far too long.
    /// </summary>
    private static TimeSpan ResolveDecodeTimeout(IReadOnlyList<PreviewPlan> pending, int maximumDimension)
    {
        var baseSeconds = 30.0;
        var variableSeconds = 0.0;
        foreach (var plan in pending)
        {
            if (!DdsTextureHeader.TryRead(plan.Request.DdsPath, out var header))
            {
                continue;
            }
            var (jobBase, perMegapixel) = DecodeCost(header.Family);
            baseSeconds = Math.Max(baseSeconds, jobBase);
            variableSeconds += perMegapixel * OutputMegapixels(header, maximumDimension);
        }
        var total = TimeSpan.FromSeconds(baseSeconds + variableSeconds);
        if (total < MinimumDecodeTimeout) return MinimumDecodeTimeout;
        return total > MaximumDecodeTimeout ? MaximumDecodeTimeout : total;
    }

    private static (double BaseSeconds, double PerMegapixel) DecodeCost(DdsCompressedFamily family) => family switch
    {
        DdsCompressedFamily.Bc6h or DdsCompressedFamily.Bc7 => (60.0, 45.0),
        DdsCompressedFamily.Bc1
            or DdsCompressedFamily.Bc2
            or DdsCompressedFamily.Bc3
            or DdsCompressedFamily.Bc4
            or DdsCompressedFamily.Bc5 => (30.0, 10.0),
        _ => (30.0, 3.0),
    };

    private static double OutputMegapixels(DdsTextureHeader header, int maximumDimension)
    {
        double width = header.Width;
        double height = header.Height;
        var longest = Math.Max(width, height);
        if (maximumDimension > 0 && longest > maximumDimension)
        {
            var scale = maximumDimension / longest;
            width = Math.Max(1, width * scale);
            height = Math.Max(1, height * scale);
        }
        return width * height / 1_000_000.0;
    }

    private static async Task RunDecoderAsync(
        string jobPath,
        string reportPath,
        TimeSpan decodeTimeout,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        var executable = ResolveDecoderPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("batch-preview-json");
        startInfo.ArgumentList.Add(jobPath);
        startInfo.ArgumentList.Add(reportPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cd-texture-dx could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(decodeTimeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await WaitWithHeartbeatAsync(process, decodeTimeout, started, publishProgress, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            TexturePreviewDiagnostics.RecordFailure(
                "batch-preview-json",
                "timeout",
                string.Empty,
                $"no exit within {decodeTimeout.TotalSeconds:N0}s");
            throw new TimeoutException($"cd-texture-dx did not finish within {decodeTimeout.TotalSeconds:N0} seconds.");
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var stdoutText = await stdout.ConfigureAwait(false);
        var stderrText = await stderr.ConfigureAwait(false);
        if (process.ExitCode is not (0 or PartialFailureExitCode))
        {
            var detail = string.IsNullOrWhiteSpace(stderrText) ? stdoutText : stderrText;
            TexturePreviewDiagnostics.RecordFailure(
                "batch-preview-json",
                "nonzero_exit_code",
                string.Empty,
                $"exit {process.ExitCode}: {detail}");
            throw new InvalidDataException($"cd-texture-dx exited with code {process.ExitCode}: {detail.Trim()}");
        }
    }

    /// <summary>
    /// Reports elapsed and allowed seconds while a decode runs, so a slow texture is
    /// distinguishable from a wedged helper instead of showing an unchanging busy state.
    /// </summary>
    private static async Task WaitWithHeartbeatAsync(
        Process process,
        TimeSpan decodeTimeout,
        long started,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken timeoutToken)
    {
        var exit = process.WaitForExitAsync(timeoutToken);
        if (publishProgress is null)
        {
            await exit.ConfigureAwait(false);
            return;
        }
        while (true)
        {
            var heartbeat = Task.Delay(HeartbeatInterval, timeoutToken);
            if (await Task.WhenAny(exit, heartbeat).ConfigureAwait(false) == exit)
            {
                await exit.ConfigureAwait(false);
                return;
            }
            await publishProgress(new ProgressUpdate(
                (long)Stopwatch.GetElapsedTime(started).TotalSeconds,
                (long)decodeTimeout.TotalSeconds,
                DecodePhase)).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, ReportItem> ReadReport(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            TexturePreviewDiagnostics.RecordFailure("batch-preview-json", "missing_report", string.Empty, reportPath);
            throw new InvalidDataException("cd-texture-dx did not produce a decode report.");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(reportPath));
        }
        catch (JsonException exception)
        {
            TexturePreviewDiagnostics.RecordFailure("batch-preview-json", "invalid_report_json", string.Empty, exception.Message);
            throw new InvalidDataException("cd-texture-dx returned an unreadable decode report.");
        }
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status)
                || !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                TexturePreviewDiagnostics.RecordFailure("batch-preview-json", "missing_report_items", string.Empty, reportPath);
                throw new InvalidDataException("cd-texture-dx returned an invalid decode report.");
            }

            var reported = new Dictionary<string, ReportItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var output = item.TryGetProperty("output_path", out var outputValue)
                    ? outputValue.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }
                try
                {
                    reported[Path.GetFullPath(output)] = new ReportItem(
                        item.TryGetProperty("status", out var itemStatus) ? itemStatus.GetString() : null,
                        item.TryGetProperty("message", out var message) ? message.GetString() : null,
                        item.TryGetProperty("format", out var format) ? format.GetString() : null,
                        item.TryGetProperty("prepared_width", out var width) && width.TryGetInt32(out var w) ? w : 0,
                        item.TryGetProperty("prepared_height", out var height) && height.TryGetInt32(out var h) ? h : 0);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A report row with an unusable path cannot match a staged output.
                }
            }
            return reported;
        }
    }

    private static string ResolveDecoderPath() =>
        TryResolveDecoderPath()
        ?? throw new FileNotFoundException(
            "cd-texture-dx.exe was not found. Rebuild Archive Lite or set CDMW_ARCHIVE_LITE_TEXTURE_HELPER_PATH.");

    private static string? TryResolveDecoderPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_TEXTURE_HELPER_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }
        // Probing the tree is far more expensive than the stat that reads the helper identity.
        if (_cachedDecoderPath is { } cached && File.Exists(cached))
        {
            return cached;
        }
        var packaged = Path.Combine(AppContext.BaseDirectory, "texture", "cd-texture-dx.exe");
        if (File.Exists(packaged))
        {
            return _cachedDecoderPath = packaged;
        }
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(current.FullName, "native", "cd_texture_dx", "build", configuration, "cd-texture-dx.exe");
                if (File.Exists(candidate))
                {
                    return _cachedDecoderPath = candidate;
                }
            }
        }
        return null;
    }

    /// <summary>A cache entry counts only when both the PNG and its provenance sidecar are intact.</summary>
    private static bool IsCachedPreviewValid(string pngPath) =>
        IsValidPng(pngPath) && IsValidSidecar(SidecarPath(pngPath));

    private static bool IsValidSidecar(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath))
            {
                return false;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("status", out var status))
            {
                return false;
            }
            var text = status.GetString();
            return !string.IsNullOrWhiteSpace(text)
                && text is not ("error" or "failed" or "cancelled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms a cached preview is a structurally complete PNG. A signature-only check accepts a
    /// truncated or half-published file, which then fails at display time and stays cached.
    /// </summary>
    private static bool IsValidPng(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }
            var identity = new PngIdentity(
                Path.GetFullPath(path).ToLowerInvariant(),
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            if (PngValidations.TryGetValue(identity, out var memoized))
            {
                return memoized;
            }
            var valid = ValidatePngStructure(path, info.Length);
            if (PngValidations.Count >= MaximumMemoizedValidations)
            {
                PngValidations.Clear();
            }
            PngValidations[identity] = valid;
            return valid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ValidatePngStructure(string path, long fileLength)
    {
        const int minimumPngLength = 8 + 25 + 12; // signature, IHDR, IEND
        if (fileLength < minimumPngLength)
        {
            return false;
        }
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            if (stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) != signature.Length
                || !signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            var buffer = new byte[64 * 1024];
            Span<byte> header = stackalloc byte[8];
            Span<byte> checksumBytes = stackalloc byte[4];
            Span<byte> headerData = stackalloc byte[13];
            var sawHeader = false;
            var sawImageData = false;
            while (stream.Position < fileLength)
            {
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
                {
                    return false;
                }
                var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var chunkType = header.Slice(4, 4);
                // The chunk body plus its 4-byte CRC must fit inside the file.
                if (chunkLength > int.MaxValue || chunkLength + 4 > (ulong)(fileLength - stream.Position))
                {
                    return false;
                }

                var checksum = Crc32Update(0xFFFFFFFFu, chunkType);
                var isHeaderChunk = chunkType.SequenceEqual("IHDR"u8);
                var headerRead = 0;
                var remaining = (int)chunkLength;
                while (remaining > 0)
                {
                    var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                    {
                        return false;
                    }
                    var block = buffer.AsSpan(0, read);
                    if (isHeaderChunk && headerRead < headerData.Length)
                    {
                        var copy = Math.Min(headerData.Length - headerRead, read);
                        block[..copy].CopyTo(headerData[headerRead..]);
                        headerRead += copy;
                    }
                    checksum = Crc32Update(checksum, block);
                    remaining -= read;
                }
                checksum ^= 0xFFFFFFFFu;
                if (stream.ReadAtLeast(checksumBytes, checksumBytes.Length, throwOnEndOfStream: false) != checksumBytes.Length
                    || BinaryPrimitives.ReadUInt32BigEndian(checksumBytes) != checksum)
                {
                    return false;
                }

                if (!sawHeader)
                {
                    if (!isHeaderChunk || chunkLength != 13)
                    {
                        return false;
                    }
                    var width = BinaryPrimitives.ReadUInt32BigEndian(headerData[..4]);
                    var height = BinaryPrimitives.ReadUInt32BigEndian(headerData.Slice(4, 4));
                    if (width == 0 || height == 0 || headerData[8] is not (1 or 2 or 4 or 8 or 16))
                    {
                        return false;
                    }
                    sawHeader = true;
                }
                else if (isHeaderChunk)
                {
                    return false;
                }

                if (chunkType.SequenceEqual("IDAT"u8))
                {
                    sawImageData = true;
                }
                if (chunkType.SequenceEqual("IEND"u8))
                {
                    return chunkLength == 0 && sawImageData && stream.Position == fileLength;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }

    private static uint Crc32Update(uint checksum, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            checksum = Crc32Table[(checksum ^ value) & 0xFF] ^ (checksum >> 8);
        }
        return checksum;
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0u; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }
            table[index] = value;
        }
        return table;
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

    private static async Task ObserveAsync(params Task<string>[] captures)
    {
        foreach (var capture in captures)
        {
            try
            {
                _ = await capture.ConfigureAwait(false);
            }
            catch
            {
                // The owned process is already stopping; observe reader completion only.
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

    private sealed record PreviewPlan(TexturePreviewRequest Request, string Destination, string Key);

    private sealed record ReportItem(string? Status, string? Message, string? Format, int Width, int Height);

    private sealed record TexturePreviewSidecar(
        string Status,
        string Backend,
        string ArtifactVersion,
        string SourcePath,
        string? Format,
        int Width,
        int Height,
        DateTimeOffset DecodedUtc);

    private readonly record struct PngIdentity(string Path, long Length, long ModifiedTicks);
}
