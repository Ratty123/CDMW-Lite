using System.Text.Json;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Removes persistent archive indexes and name caches whose fingerprint no longer belongs to any
/// cached root. Both are keyed by content fingerprint, so a game update publishes a new key and
/// leaves the previous one on disk; every superseded set would otherwise be kept forever.
/// </summary>
/// <remarks>
/// The root manifests are the authority on which fingerprints are live, so a set abandoned by an
/// earlier crash or by a root the user no longer opens is reclaimed on the same pass. Deleting is
/// best-effort: an index a session still has mapped stays until a later pass.
/// </remarks>
public static class ArchiveIndexCacheReclamation
{
    private const int FingerprintLength = 64;
    private static readonly string[] IndexExtensions = [".ali", ".abi", ".aex"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static IndexReclaimResult ReclaimSuperseded(IReadOnlyCollection<string>? fingerprintsInUse = null)
    {
        if (!Directory.Exists(ArchiveLiteDataPaths.IndexCache))
        {
            return new IndexReclaimResult(0, 0);
        }

        // A keep-set that could not be established in full must never authorize a deletion.
        if (ReadLiveFingerprints() is not { } keep)
        {
            return new IndexReclaimResult(0, 0);
        }

        if (fingerprintsInUse is not null)
        {
            foreach (var fingerprint in fingerprintsInUse)
            {
                if (IsFingerprint(fingerprint))
                {
                    keep.Add(fingerprint);
                }
            }
        }

        var removed = 0;
        long bytes = 0;
        Reclaim(ArchiveLiteDataPaths.IndexCache, IndexExtensions, keep, ref removed, ref bytes);
        Reclaim(ArchiveLiteDataPaths.NameIndexCache, [".json"], keep, ref removed, ref bytes);
        return new IndexReclaimResult(removed, bytes);
    }

    private static void Reclaim(
        string directory,
        IReadOnlyList<string> extensions,
        HashSet<string> keep,
        ref int removed,
        ref long bytes)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        IReadOnlyList<string> candidates;
        try
        {
            // Top level only: the root manifests live in a subdirectory and are never candidates.
            candidates = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in candidates)
        {
            var extension = Path.GetExtension(path);
            if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only a bare fingerprint stem is owned by this cache; staging and unknown files are left alone.
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!IsFingerprint(stem) || keep.Contains(stem))
            {
                continue;
            }

            try
            {
                var length = new FileInfo(path).Length;
                File.Delete(path);
                removed++;
                bytes += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A mapped or locked index stays authoritative until a later pass.
            }
        }
    }

    /// <summary>
    /// Every fingerprint a cached root currently points at, or null when the full set could not be
    /// read. Null is not "nothing is live" - it means the caller must not delete anything.
    /// </summary>
    private static HashSet<string>? ReadLiveFingerprints()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ArchiveLiteDataPaths.IndexRootManifests))
        {
            return live;
        }

        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(ArchiveLiteDataPaths.IndexRootManifests, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var manifest in manifests)
        {
            try
            {
                using var stream = new FileStream(
                    manifest,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    16 * 1024,
                    FileOptions.SequentialScan);
                var payload = JsonSerializer.Deserialize<RootFingerprint>(stream, JsonOptions);
                if (payload is not null && IsFingerprint(payload.Fingerprint))
                {
                    live.Add(payload.Fingerprint);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable manifest hides the fingerprint it protects, so the pass is abandoned.
                return null;
            }
        }

        return live;
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: FingerprintLength }
        && value.All(static character =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F');

    private sealed record RootFingerprint(string Fingerprint);
}

public sealed record IndexReclaimResult(int FilesRemoved, long BytesRemoved);
