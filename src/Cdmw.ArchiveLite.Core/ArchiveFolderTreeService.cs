using System.Text;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Serves the archive's directory structure one level at a time. The whole structure is derived once
/// per session from a single path-only pass over the index and then kept resident, so expanding a
/// folder is a dictionary lookup rather than another scan of a million-entry archive.
/// </summary>
public sealed class ArchiveFolderTreeService(ArchiveSessionManager sessions)
{
    /// <summary>
    /// How many folders one response may carry. A level is normally far smaller than this; the cap
    /// only exists so a pathological directory cannot exceed the bounded protocol message.
    /// </summary>
    private const int MaximumNodesPerResult = 8192;

    private const int MaximumDepth = 3;

    /// <summary>
    /// How many entries one file listing may carry. Entry records are far larger than folder rows,
    /// so this is well below the folder cap to stay inside the bounded protocol message.
    /// </summary>
    private const int MaximumFilesPerResult = 1024;

    public async Task<ArchiveFolderTreeResult> LoadAsync(
        ArchiveFolderTreeRequest request,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = sessions.GetRequired(request.SessionId);
        var tree = await BuildOrGetAsync(session, publishProgress, cancellationToken).ConfigureAwait(false);
        var folder = tree.Find(request.Path);
        if (folder is null)
        {
            return new ArchiveFolderTreeResult(session.Id, request.Path, 0, 0, []);
        }

        var budget = MaximumNodesPerResult;
        var depth = Math.Clamp(request.Depth, 1, MaximumDepth);
        var nodes = Project(folder, depth, ref budget);
        return new ArchiveFolderTreeResult(
            session.Id,
            request.Path,
            folder.DirectCount,
            folder.TotalCount,
            nodes,
            Truncated: budget <= 0);
    }

    /// <summary>
    /// Lists the files stored directly in one folder, a page at a time. Order follows the archive
    /// index, which is already sorted by path, so a page is stable between calls.
    /// </summary>
    public async Task<ArchiveFolderFilesResult> ListFilesAsync(
        ArchiveFolderFilesRequest request,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = sessions.GetRequired(request.SessionId);
        var tree = await BuildOrGetAsync(session, publishProgress, cancellationToken).ConfigureAwait(false);
        var folder = tree.Find(request.Path);
        if (folder is null)
        {
            return new ArchiveFolderFilesResult(session.Id, request.Path, 0, []);
        }

        var pageStart = Math.Max(0, request.PageStart);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumFilesPerResult);
        var ids = folder.DirectFileIds;
        var files = new List<ArchiveEntryDto>(Math.Min(pageSize, Math.Max(0, ids.Count - pageStart)));
        for (var index = pageStart; index < ids.Count && files.Count < pageSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(session.EnrichEntry(session.Index.ReadEntry(ids[index])));
        }
        return new ArchiveFolderFilesResult(
            session.Id,
            request.Path,
            ids.Count,
            files,
            Truncated: pageStart + files.Count < ids.Count);
    }

    private static async Task<ArchiveFolderTree> BuildOrGetAsync(
        ArchiveSession session,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        if (session.TryGetFolderTree(out var cached) && cached is not null)
        {
            return cached;
        }

        await session.FolderTreeBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.TryGetFolderTree(out var raced) && raced is not null)
            {
                return raced;
            }
            var built = await BuildAsync(session, publishProgress, cancellationToken).ConfigureAwait(false);
            session.SetFolderTree(built);
            return built;
        }
        finally
        {
            session.FolderTreeBuildGate.Release();
        }
    }

    private static async Task<ArchiveFolderTree> BuildAsync(
        ArchiveSession session,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        var total = session.Index.EntryCount;
        var root = new ArchiveFolderTreeNode(string.Empty, string.Empty);
        var buffer = new byte[512];
        for (long entryId = 0; entryId < total; entryId++)
        {
            if ((entryId & 0x1FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (publishProgress is not null)
                {
                    await publishProgress(new ProgressUpdate(entryId, total, "folder_scan")).ConfigureAwait(false);
                }
            }

            var length = session.Index.GetPathByteLength(entryId);
            if (buffer.Length < length)
            {
                buffer = new byte[Math.Max(length, buffer.Length * 2)];
            }
            session.Index.ReadPathBytes(entryId, buffer);
            Add(root, Encoding.UTF8.GetString(buffer, 0, length), entryId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (publishProgress is not null)
        {
            await publishProgress(new ProgressUpdate(total, total, "folder_scan")).ConfigureAwait(false);
        }
        return new ArchiveFolderTree(root);
    }

    /// <summary>
    /// Files the entry under every folder on its path. The trailing segment is the file name, so a
    /// path without a separator belongs directly to the archive root.
    /// </summary>
    private static void Add(ArchiveFolderTreeNode root, string virtualPath, long entryId)
    {
        var path = virtualPath.Replace('\\', '/').Trim('/');
        root.TotalCount++;
        var start = 0;
        var folder = root;
        while (true)
        {
            var separator = path.IndexOf('/', start);
            if (separator < 0)
            {
                // Whatever remains is the file name, so the folder walked to so far stores it.
                folder.AddFile(entryId);
                return;
            }
            var name = path[start..separator];
            if (name.Length > 0)
            {
                folder = folder.GetOrAddChild(name, path[..separator]);
                folder.TotalCount++;
            }
            start = separator + 1;
        }
    }

    private static IReadOnlyList<ArchiveFolderNode> Project(ArchiveFolderTreeNode folder, int depth, ref int budget)
    {
        if (depth <= 0 || folder.Children is not { Count: > 0 } children)
        {
            return [];
        }

        var projected = new List<ArchiveFolderNode>(children.Count);
        foreach (var child in children.Values.OrderBy(static child => child.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (budget <= 0)
            {
                break;
            }
            budget--;
            projected.Add(new ArchiveFolderNode(
                child.Name,
                child.Path,
                child.DirectCount,
                child.TotalCount,
                child.Children is { Count: > 0 },
                Project(child, depth - 1, ref budget)));
        }
        return projected;
    }
}

/// <summary>
/// The resident directory structure of one archive session.
/// </summary>
public sealed class ArchiveFolderTree(ArchiveFolderTreeNode root)
{
    public ArchiveFolderTreeNode Root { get; } = root;

    /// <summary>
    /// Resolves a virtual folder path, where null or empty is the archive root. Returns null when the
    /// path names no folder in this archive.
    /// </summary>
    public ArchiveFolderTreeNode? Find(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return Root;
        }

        var folder = Root;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (folder.Children is null || !folder.Children.TryGetValue(segment, out var child))
            {
                return null;
            }
            folder = child;
        }
        return folder;
    }
}

public sealed class ArchiveFolderTreeNode(string name, string path)
{
    private List<long>? _directFileIds;

    public string Name { get; } = name;
    public string Path { get; } = path;

    /// <summary>Files stored in this folder itself.</summary>
    public long DirectCount => _directFileIds?.Count ?? 0;

    /// <summary>Files stored at or below this folder.</summary>
    public long TotalCount { get; set; }

    /// <summary>
    /// The entries stored directly in this folder, in archive order. The tree view lists them under
    /// their folder, which the recursive folder filter cannot express.
    /// </summary>
    public IReadOnlyList<long> DirectFileIds => _directFileIds ?? (IReadOnlyList<long>)[];

    public Dictionary<string, ArchiveFolderTreeNode>? Children { get; private set; }

    internal void AddFile(long entryId)
    {
        _directFileIds ??= [];
        _directFileIds.Add(entryId);
    }

    internal ArchiveFolderTreeNode GetOrAddChild(string name, string path)
    {
        Children ??= new Dictionary<string, ArchiveFolderTreeNode>(StringComparer.OrdinalIgnoreCase);
        if (!Children.TryGetValue(name, out var child))
        {
            child = new ArchiveFolderTreeNode(name, path);
            Children[name] = child;
        }
        return child;
    }
}
