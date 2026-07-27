using System.Diagnostics;

namespace Cdmw.ArchiveLite.Core;

/// <summary>A process-local pin that keeps one cache entry out of a prune until it is released.</summary>
public sealed class PreviewCacheLease : IDisposable
{
    private readonly string _key;
    private bool _released;

    internal PreviewCacheLease(string key) => _key = key;

    public void Dispose()
    {
        if (!_released)
        {
            _released = true;
            PreviewCacheLeases.Release(_key);
        }
    }
}

/// <summary>
/// Tracks which cache entries a reader is holding or has just been handed. A prune that only sorts
/// by write time can delete the preview a caller is about to open, so eviction consults this first.
/// </summary>
public static class PreviewCacheLeases
{
    public static readonly TimeSpan DefaultRecentUse = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, int> ActiveLeases = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> RecentUse = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();
    private const int MaximumRecentEntries = 8192;

    public static PreviewCacheLease Acquire(string path)
    {
        var key = NormalizeKey(path);
        lock (Gate)
        {
            ActiveLeases[key] = ActiveLeases.GetValueOrDefault(key) + 1;
        }
        return new PreviewCacheLease(key);
    }

    /// <summary>Gives an entry that was just returned to a caller a short grace period.</summary>
    public static void MarkRecent(string path, TimeSpan? grace = null)
    {
        var key = NormalizeKey(path);
        var deadline = Stopwatch.GetTimestamp() + (long)((grace ?? DefaultRecentUse).TotalSeconds * Stopwatch.Frequency);
        lock (Gate)
        {
            if (RecentUse.Count >= MaximumRecentEntries)
            {
                DropExpiredLocked();
            }
            if (RecentUse.Count >= MaximumRecentEntries)
            {
                RecentUse.Clear();
            }
            RecentUse[key] = deadline;
        }
    }

    public static bool IsProtected(string path)
    {
        var key = NormalizeKey(path);
        lock (Gate)
        {
            if (ActiveLeases.ContainsKey(key))
            {
                return true;
            }
            if (!RecentUse.TryGetValue(key, out var deadline))
            {
                return false;
            }
            if (deadline > Stopwatch.GetTimestamp())
            {
                return true;
            }
            RecentUse.Remove(key);
            return false;
        }
    }

    internal static void Release(string key)
    {
        lock (Gate)
        {
            if (!ActiveLeases.TryGetValue(key, out var count))
            {
                return;
            }
            if (count <= 1)
            {
                ActiveLeases.Remove(key);
            }
            else
            {
                ActiveLeases[key] = count - 1;
            }
        }
    }

    /// <summary>Test seam; leases are process-wide so scenarios must not inherit each other's state.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            ActiveLeases.Clear();
            RecentUse.Clear();
        }
    }

    private static void DropExpiredLocked()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var key in RecentUse.Where(pair => pair.Value <= now).Select(static pair => pair.Key).ToArray())
        {
            RecentUse.Remove(key);
        }
    }

    private static string NormalizeKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToLowerInvariant();
        }
    }
}
