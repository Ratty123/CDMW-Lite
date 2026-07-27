using System.Diagnostics;

namespace Cdmw.ArchiveLite.Core;

public static class ArchiveLiteCacheMaintenance
{
    public const long DefaultCacheMaximumBytes = 5L * 1024L * 1024L * 1024L;
    public static readonly TimeSpan DefaultPruneInterval = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim PruneGate = new(1, 1);
    private static long _lastPruneTimestamp = long.MinValue;

    public static CachePruneResult Prune(string cacheRoot, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (!Directory.Exists(cacheRoot))
        {
            return new CachePruneResult(0, 0, 0);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var files = new List<FileInfo>();
        long totalBytes = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(cacheRoot, "*", options))
            {
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    if (info.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) &&
                        info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    {
                        info.Delete();
                        continue;
                    }
                    totalBytes = totalBytes > long.MaxValue - info.Length ? long.MaxValue : totalBytes + info.Length;
                    files.Add(info);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Cache maintenance is best-effort and never blocks startup.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache may change while it is enumerated; prune the rows found so far.
        }

        var before = totalBytes;
        var removed = 0;
        var targetBytes = maximumBytes - maximumBytes / 10;
        if (totalBytes > maximumBytes)
        {
            foreach (var file in files.OrderBy(static info => info.LastWriteTimeUtc).ThenBy(static info => info.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (totalBytes <= targetBytes)
                {
                    break;
                }
                // A caller may be holding this entry or may have just been handed it.
                if (PreviewCacheLeases.IsProtected(file.FullName))
                {
                    continue;
                }
                try
                {
                    var length = file.Length;
                    file.Delete();
                    totalBytes = Math.Max(0, totalBytes - length);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A live cache entry remains authoritative until a later startup.
                }
            }
        }
        return new CachePruneResult(before, totalBytes, removed);
    }

    /// <summary>
    /// Runs a prune on a background thread at most once per interval. Startup-only pruning lets the
    /// cache grow unbounded through a long browsing session.
    /// </summary>
    public static bool RequestPrune(
        string cacheRoot,
        long maximumBytes,
        TimeSpan? minimumInterval = null)
    {
        var interval = minimumInterval ?? DefaultPruneInterval;
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastPruneTimestamp);
        if (last != long.MinValue && Stopwatch.GetElapsedTime(last, now) < interval)
        {
            return false;
        }
        if (!PruneGate.Wait(0))
        {
            return false;
        }
        Interlocked.Exchange(ref _lastPruneTimestamp, now);
        _ = Task.Run(() =>
        {
            try
            {
                Prune(cacheRoot, maximumBytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Background maintenance never surfaces over the operation that triggered it.
            }
            finally
            {
                PruneGate.Release();
            }
        });
        return true;
    }

    /// <summary>Test seam so a scenario is not throttled by an earlier scenario's prune.</summary>
    public static void ResetPruneThrottle() => _lastPruneTimestamp = long.MinValue;
}

public sealed record CachePruneResult(long BytesBefore, long BytesAfter, int FilesRemoved);
