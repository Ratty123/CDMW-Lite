using System.Collections.Concurrent;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

public sealed class ArchiveItemIconService(
    ArchiveSessionManager sessions,
    ArchiveItemNameIndexService catalogueBuilder,
    NativeArchiveCore native,
    NativeTexturePreviewService textures,
    ArchiveWorkPriority? workPriority = null)
{
    private const int MaximumVisibleBatch = 64;
    private const int MaximumWarmBatch = 16;
    private const int ProgressPublishInterval = 10;
    private const long MaximumIconBytes = 256L * 1024L * 1024L;
    private const int MaximumIconCandidates = 8;
    private const int MaximumArchiveDecodeConcurrency = 3;
    private const string NoArchiveDdsWarning = "No archive DDS could be resolved for the recovered inventory icon paths.";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NegativeIconCacheEntry> _negativeCache = new(StringComparer.Ordinal);
    private int _visibleRequestCount;

    public async Task<ItemIconBatchResult> LoadAsync(
        ItemIconBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateThumbnailSize(request.ThumbnailSize);
        var itemIds = request.ItemIds
            .Distinct()
            .Take(MaximumVisibleBatch + 1)
            .ToArray();
        if (itemIds.Length > MaximumVisibleBatch)
        {
            throw new InvalidDataException($"An Item Finder icon request may contain at most {MaximumVisibleBatch} items.");
        }

        var (session, catalog) = await GetCatalogAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _visibleRequestCount);
        try
        {
            var results = await LoadBatchAsync(session, catalog, itemIds, request.ThumbnailSize, cancellationToken).ConfigureAwait(false);
            return new ItemIconBatchResult(request.SessionId, results);
        }
        finally
        {
            Interlocked.Decrement(ref _visibleRequestCount);
        }
    }

    public async Task<WarmItemIconsResult> WarmAsync(
        WarmItemIconsRequest request,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateThumbnailSize(request.ThumbnailSize);
        if (request.MaximumIcons < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumIcons));
        }
        var (session, catalog) = await GetCatalogAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        var priority = (request.PrioritizedItemIds ?? [])
            .Distinct()
            .ToArray();
        var prioritySet = priority.ToHashSet();
        var candidates = priority
            .Select(itemId => catalog.TryGet(itemId, out var item) ? item : null)
            .Concat(catalog.Items.Where(item => !prioritySet.Contains(item.ItemId)))
            .Where(static item => item is { IconPaths.Count: > 0 })
            .Cast<ArchiveItemCatalogRecord>();
        if (request.MaximumIcons > 0)
        {
            candidates = candidates.Take(request.MaximumIcons);
        }
        var items = candidates.ToArray();
        long ready = 0;
        long missing = 0;
        long failed = 0;
        var completed = 0;
        var lastPublished = 0;
        // Warm-up runs in chunks rather than one icon at a time so the texture helper starts once
        // per chunk. The chunk stays well under a visible page because a visible request that
        // arrives mid-chunk waits on the item gates this chunk holds.
        for (var offset = 0; offset < items.Length; offset += MaximumWarmBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForVisibleRequestsAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(MaximumWarmBatch, items.Length - offset);
            var chunk = new int[count];
            for (var index = 0; index < count; index++)
            {
                chunk[index] = items[offset + index].ItemId;
            }

            var results = await LoadBatchAsync(session, catalog, chunk, request.ThumbnailSize, cancellationToken)
                .ConfigureAwait(false);
            string? lastSource = null;
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.PngPath)) ready++;
                else if (result.Warning?.StartsWith("No archive DDS", StringComparison.OrdinalIgnoreCase) == true
                    || result.Warning?.StartsWith("No inventory icon", StringComparison.OrdinalIgnoreCase) == true) missing++;
                else failed++;
                if (result.SourcePath is not null)
                {
                    lastSource = result.SourcePath;
                }
            }
            completed += results.Length;

            if (publishProgress is not null
                && (completed - lastPublished >= ProgressPublishInterval || completed == items.Length))
            {
                lastPublished = completed;
                await publishProgress(new ProgressUpdate(completed, items.Length, "item_icon_warmup", lastSource))
                    .ConfigureAwait(false);
            }
        }
        return new WarmItemIconsResult(request.SessionId, items.LongLength, ready, missing, failed);
    }

    private async Task<(ArchiveSession Session, ArchiveItemCatalog Catalog)> GetCatalogAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = sessions.GetRequired(sessionId);
        if (!session.TryGetItemCatalog(out var catalog) || catalog is null)
        {
            var built = await catalogueBuilder.BuildAsync(
                new BuildNameIndexRequest(sessionId),
                publishProgress: null,
                cancellationToken).ConfigureAwait(false);
            if (!built.Available || !session.TryGetItemCatalog(out catalog) || catalog is null)
            {
                throw new InvalidOperationException(built.Warning ?? "The Item Finder catalog is unavailable for this archive.");
            }
        }
        return (session, catalog);
    }

    /// <summary>
    /// Resolves a whole visible page of icons together so the shared texture helper is started once
    /// per decode round instead of once per icon.
    /// </summary>
    private async Task<ItemIconResult[]> LoadBatchAsync(
        ArchiveSession session,
        ArchiveItemCatalog catalog,
        IReadOnlyList<int> itemIds,
        int thumbnailSize,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, ItemIconResult>();
        var plans = new List<IconPlan>(itemIds.Count);
        var planned = new HashSet<int>();
        foreach (var itemId in itemIds)
        {
            // A repeated id resolves to the same result, and planning it twice would take the same
            // item gate twice and deadlock.
            if (!planned.Add(itemId))
            {
                continue;
            }
            if (!catalog.TryGet(itemId, out var item) || item is null)
            {
                results[itemId] = new ItemIconResult(itemId, null, null, "The item is not present in the active catalog.");
                continue;
            }
            if (item.IconPaths.Count == 0)
            {
                results[itemId] = new ItemIconResult(itemId, null, null, "No inventory icon was recovered for this item.");
                continue;
            }
            var negativeKey = NegativeKey(session, itemId, thumbnailSize);
            if (_negativeCache.TryGetValue(negativeKey, out var negative))
            {
                if (negative.ExpiresUtc > DateTimeOffset.UtcNow)
                {
                    results[itemId] = new ItemIconResult(itemId, null, null, negative.Warning);
                    continue;
                }
                _negativeCache.TryRemove(negativeKey, out _);
            }
            plans.Add(new IconPlan(itemId, negativeKey, ResolveIconEntry(session, item)));
        }

        if (plans.Count > 0)
        {
            // Gates are taken in one global order so overlapping icon batches cannot deadlock.
            plans.Sort(static (left, right) => string.CompareOrdinal(left.GateKey, right.GateKey));
            var gates = new List<SemaphoreSlim>(plans.Count);
            try
            {
                foreach (var plan in plans)
                {
                    var gate = _itemGates.GetOrAdd(plan.GateKey, static _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    gates.Add(gate);
                }
                await ResolvePlansAsync(session, plans, thumbnailSize, results, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                for (var index = gates.Count - 1; index >= 0; index--)
                {
                    gates[index].Release();
                }
            }
        }

        return itemIds
            .Select(itemId => results.TryGetValue(itemId, out var result)
                ? result
                : new ItemIconResult(itemId, null, null, NoArchiveDdsWarning))
            .ToArray();
    }

    private async Task ResolvePlansAsync(
        ArchiveSession session,
        List<IconPlan> plans,
        int thumbnailSize,
        Dictionary<int, ItemIconResult> results,
        CancellationToken cancellationToken)
    {
        var pending = new List<IconPlan>(plans.Count);
        foreach (var plan in plans)
        {
            if (plan.Candidate is null)
            {
                _negativeCache[plan.NegativeKey] = new NegativeIconCacheEntry(DateTimeOffset.UtcNow.AddMinutes(10), NoArchiveDdsWarning);
                results[plan.ItemId] = new ItemIconResult(plan.ItemId, null, null, NoArchiveDdsWarning);
                continue;
            }
            if (textures.TryGetCachedThumbnail(session, plan.Candidate, thumbnailSize) is { } cached)
            {
                _negativeCache.TryRemove(plan.NegativeKey, out _);
                results[plan.ItemId] = new ItemIconResult(plan.ItemId, cached, plan.Candidate.Path);
                continue;
            }
            pending.Add(plan);
        }
        if (pending.Count == 0)
        {
            return;
        }

        var stagingRoot = Path.Combine(
            ArchiveLiteDataPaths.PreviewCache,
            "item-icon-staging",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            await DecodeArchiveSourcesAsync(pending, stagingRoot, cancellationToken).ConfigureAwait(false);
            var ready = pending.Where(static plan => plan.DdsPath is not null).ToArray();
            if (ready.Length > 0)
            {
                await BuildThumbnailsAsync(session, ready, thumbnailSize, results, cancellationToken).ConfigureAwait(false);
            }
            foreach (var plan in pending)
            {
                if (results.ContainsKey(plan.ItemId))
                {
                    continue;
                }
                var warning = plan.LastError ?? "The inventory icon could not be decoded.";
                _negativeCache[plan.NegativeKey] = new NegativeIconCacheEntry(DateTimeOffset.UtcNow.AddMinutes(2), warning);
                results[plan.ItemId] = new ItemIconResult(plan.ItemId, null, null, warning);
            }
        }
        finally
        {
            DeleteOwnedStaging(stagingRoot);
        }
    }

    private async Task DecodeArchiveSourcesAsync(
        List<IconPlan> targets,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(MaximumArchiveDecodeConcurrency, MaximumArchiveDecodeConcurrency);
        var extractions = targets.Select(async plan =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var decoded = await Task.Run(() => native.Decode(plan.Candidate!), cancellationToken).ConfigureAwait(false);
                var ddsPath = Path.Combine(stagingRoot, $"{plan.ItemId}.dds");
                await File.WriteAllBytesAsync(ddsPath, decoded.Bytes, cancellationToken).ConfigureAwait(false);
                plan.DdsPath = ddsPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                plan.LastError = BoundedMessage(exception.Message);
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(extractions).ConfigureAwait(false);
    }

    private async Task BuildThumbnailsAsync(
        ArchiveSession session,
        IReadOnlyList<IconPlan> targets,
        int thumbnailSize,
        Dictionary<int, ItemIconResult> results,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TexturePreviewResult> decoded;
        try
        {
            decoded = await textures.BuildThumbnailBatchAsync(
                session,
                targets.Select(plan => new TexturePreviewRequest(plan.Candidate!, plan.DdsPath!)).ToArray(),
                thumbnailSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A whole-batch failure (missing helper, timeout) applies to every job it carried.
            var warning = BoundedMessage(exception.Message);
            foreach (var plan in targets)
            {
                plan.LastError = warning;
            }
            return;
        }

        for (var index = 0; index < targets.Count; index++)
        {
            var plan = targets[index];
            var result = decoded[index];
            if (result.PngPath is { } pngPath)
            {
                _negativeCache.TryRemove(plan.NegativeKey, out _);
                results[plan.ItemId] = new ItemIconResult(plan.ItemId, pngPath, plan.Candidate!.Path);
            }
            else
            {
                plan.LastError = BoundedMessage(result.Error ?? "The inventory icon could not be decoded.");
            }
        }
    }

    /// <summary>Picks the first icon path that resolves to an archive DDS inside the size bound.</summary>
    private static ArchiveEntryDto? ResolveIconEntry(ArchiveSession session, ArchiveItemCatalogRecord item)
    {
        foreach (var iconPath in item.IconPaths.Take(MaximumIconCandidates))
        {
            var entry = ResolveIconEntry(session, iconPath);
            if (entry is not null && entry.OriginalSize <= MaximumIconBytes)
            {
                return entry;
            }
        }
        return null;
    }

    private static ArchiveEntryDto? ResolveIconEntry(ArchiveSession session, string iconPath)
    {
        var exact = session.Index.FindEntriesByPath(iconPath, 8)
            .FirstOrDefault(static entry => entry.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }
        var basename = Path.GetFileName(iconPath.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(basename))
        {
            return null;
        }
        var normalizedSuffix = iconPath.Replace('\\', '/').Trim('/');
        return session.BasenameIndex.FindEntriesByBasename(session.Index, basename, 32)
            .Where(static entry => entry.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Path.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase))
            .ThenBy(static entry => entry.Path.Length)
            .FirstOrDefault();
    }

    private async Task WaitForVisibleRequestsAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _visibleRequestCount) > 0)
        {
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
        if (workPriority is not null)
        {
            await workPriority.WaitForForegroundAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NegativeKey(ArchiveSession session, int itemId, int thumbnailSize) =>
        $"{session.Fingerprint}:{itemId}:{thumbnailSize}";

    private static void ValidateThumbnailSize(int size)
    {
        if (size is < 48 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Item Finder thumbnails must be between 48 and 256 pixels.");
        }
    }

    private static string BoundedMessage(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? "The inventory icon could not be decoded."
            : message.Trim()[..Math.Min(message.Trim().Length, 512)];

    private static void DeleteOwnedStaging(string stagingRoot)
    {
        try
        {
            var ownedRoot = Path.GetFullPath(Path.Combine(ArchiveLiteDataPaths.PreviewCache, "item-icon-staging"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolved = Path.GetFullPath(stagingRoot);
            if (resolved.StartsWith(ownedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preview cache maintenance can remove a staging folder that is briefly locked.
        }
    }

    private sealed class IconPlan(int itemId, string negativeKey, ArchiveEntryDto? candidate)
    {
        public int ItemId { get; } = itemId;

        public string NegativeKey { get; } = negativeKey;

        public string GateKey { get; } = negativeKey;

        public ArchiveEntryDto? Candidate { get; } = candidate;

        public string? DdsPath { get; set; }

        public string? LastError { get; set; }
    }

    private sealed record NegativeIconCacheEntry(DateTimeOffset ExpiresUtc, string Warning);
}
