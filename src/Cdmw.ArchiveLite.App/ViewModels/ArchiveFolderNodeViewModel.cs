using System.Collections.ObjectModel;
using System.Windows;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.App.ViewModels;

/// <summary>
/// One row of an Archive Browser tree: a folder, one of the files inside a folder, or the stand-in
/// shown while a level loads. Children are fetched the first time the user expands a folder, so
/// opening an archive never has to carry its whole structure across the worker protocol or into
/// memory on the interface side.
/// </summary>
public sealed class ArchiveFolderNodeViewModel : ObservableObject
{
    private readonly ArchiveFolderTreeContext? _context;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenRequested;

    public ArchiveFolderNodeViewModel(
        ArchiveFolderTreeContext context,
        string name,
        string path,
        long directCount,
        long totalCount,
        bool hasChildren,
        int depth = 0)
    {
        _context = context;
        Depth = depth;
        Name = name;
        Path = path;
        DirectCount = directCount;
        TotalCount = totalCount;
        // A folder with no subfolders still expands when the tree carries files, because its own
        // files are what it opens onto.
        HasChildren = hasChildren || (context.IncludesFiles && directCount > 0);
        if (HasChildren)
        {
            // WPF only draws an expander when an item already has children, so an unexpanded folder
            // needs one stand-in row to stay expandable before its real level has been fetched. It
            // sits one level down like any other child: the flattened rows tell a folder's own rows
            // from the rest by their depth, and a stand-in left at the root's depth is a row nothing
            // can identify as belonging to the folder that put it there.
            Children.Add(new ArchiveFolderNodeViewModel(LocalizationManager.Get("FolderTreeLoading"), Depth + 1));
        }
    }

    /// <summary>Creates the row for one archive entry inside a folder.</summary>
    private ArchiveFolderNodeViewModel(ArchiveFolderTreeContext context, ArchiveEntryDto entry, int depth)
    {
        _context = context;
        Depth = depth;
        Entry = entry;
        Name = entry.Name;
        Path = entry.Path;
    }

    /// <summary>Creates the non-selectable placeholder row shown while children are loading.</summary>
    private ArchiveFolderNodeViewModel(string label, int depth)
    {
        Depth = depth;
        Name = label;
        Path = string.Empty;
        IsPlaceholder = true;
    }

    public string Name { get; } = string.Empty;
    public string Path { get; } = string.Empty;
    public long DirectCount { get; }
    public long TotalCount { get; }
    public bool HasChildren { get; }
    public bool IsPlaceholder { get; }

    /// <summary>How deep this row sits, which is the indent the flattened tree grid draws it at.</summary>
    public int Depth { get; }

    public Thickness IndentMargin => new(Depth * 15, 0, 0, 0);

    /// <summary>Files at or below a folder. Null on a file row, which leaves the cell blank.</summary>
    public long? ItemCount => IsFile || IsPlaceholder ? null : TotalCount;

    /// <summary>The per-file columns, all null on a folder row so its cells stay empty.</summary>
    public ArchiveEntryFileType? FileType => Entry?.FileType;
    public long? Size => Entry?.OriginalSize;
    public string PackageName => Entry?.Package ?? string.Empty;

    /// <summary>The archive entry this row stands for, or null when the row is a folder.</summary>
    public ArchiveEntryDto? Entry { get; }

    public bool IsFile => Entry is not null;
    public ObservableCollection<ArchiveFolderNodeViewModel> Children { get; } = [];

    public string Label => IsPlaceholder || IsFile ? Name : $"{Name} ({TotalCount:N0})";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
            {
                return;
            }
            if (!value || _childrenRequested || _context is null || IsFile)
            {
                return;
            }

            _childrenRequested = true;
            _ = LoadChildrenAsync();
        }
    }

    /// <summary>Every row visible below this one: its children when open, and theirs in turn.</summary>
    public IEnumerable<ArchiveFolderNodeViewModel> VisibleDescendants()
    {
        if (!IsExpanded)
        {
            yield break;
        }
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.VisibleDescendants())
            {
                yield return descendant;
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value && !IsPlaceholder)
            {
                _context?.Select(this);
            }
        }
    }

    /// <summary>
    /// Rebuilds the count suffix after a language change, which is the only thing about a loaded node
    /// that a new culture can alter.
    /// </summary>
    public void RefreshLabel()
    {
        OnPropertyChanged(nameof(Label));
        foreach (var child in Children)
        {
            child.RefreshLabel();
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_context is null)
        {
            return;
        }
        try
        {
            var folders = await _context.LoadChildrenAsync(Path).ConfigureAwait(true);
            var files = _context.IncludesFiles
                ? await _context.LoadFilesAsync(Path).ConfigureAwait(true)
                : [];
            if (folders.Count == 0 && files.Count == 0 && HasChildren)
            {
                // This folder was told it holds something, so an empty level means the load was
                // answered by a superseded generation rather than by the archive. Keep the stand-in
                // and stay open to trying again: clearing here is what leaves a row that can never
                // be opened, since nothing would ask a second time.
                _childrenRequested = false;
                return;
            }
            Children.Clear();
            foreach (var folder in folders)
            {
                Children.Add(Create(_context, folder, Depth + 1));
            }
            // Folders first, then the folder's own files, so a level reads the way a file manager
            // presents one rather than interleaving the two by name.
            foreach (var file in files)
            {
                Children.Add(new ArchiveFolderNodeViewModel(_context, file, Depth + 1));
            }
        }
        catch (OperationCanceledException)
        {
            // A newer archive session owns the tree; its own root load replaces this node.
        }
        catch (Exception exception)
        {
            // Leave the node collapsible and let the user retry rather than losing the whole tree.
            _childrenRequested = false;
            Children.Clear();
            _context.ReportFailure(exception);
        }
    }

    public static ArchiveFolderNodeViewModel Create(ArchiveFolderTreeContext context, ArchiveFolderNode node, int depth = 0)
    {
        var created = new ArchiveFolderNodeViewModel(
            context,
            node.Name,
            node.Path,
            node.DirectCount,
            node.TotalCount,
            node.HasChildren,
            depth);
        if (node.Children.Count > 0)
        {
            created.Children.Clear();
            created._childrenRequested = true;
            foreach (var child in node.Children)
            {
                created.Children.Add(Create(context, child, depth + 1));
            }
        }
        return created;
    }

    public static ArchiveFolderNodeViewModel CreateFile(ArchiveFolderTreeContext context, ArchiveEntryDto entry) =>
        new(context, entry, 0);

    /// <summary>
    /// Creates the row that releases the folder filter, shown above the top-level folders. It counts
    /// everything the tree covers, which is the whole archive until a filter narrows it.
    /// </summary>
    public static ArchiveFolderNodeViewModel CreateAllFolders(ArchiveFolderTreeContext context, long totalCount) =>
        new(context, LocalizationManager.Get("All"), string.Empty, 0, totalCount, hasChildren: false);
}

/// <summary>
/// A tree's link back to the Archive Browser: how to fetch a level, whether that level includes the
/// folder's own files, what to do when the user picks a row, and where a failed expansion is
/// reported. The folder navigator and the tree view differ only in whether files are included.
/// </summary>
public sealed class ArchiveFolderTreeContext(
    Func<string, Task<IReadOnlyList<ArchiveFolderNode>>> loadChildren,
    Func<string, Task<IReadOnlyList<ArchiveEntryDto>>> loadFiles,
    Action<ArchiveFolderNodeViewModel> select,
    Action<Exception> reportFailure,
    bool includesFiles)
{
    public bool IncludesFiles { get; } = includesFiles;

    public Task<IReadOnlyList<ArchiveFolderNode>> LoadChildrenAsync(string path) => loadChildren(path);

    public Task<IReadOnlyList<ArchiveEntryDto>> LoadFilesAsync(string path) => loadFiles(path);

    public void Select(ArchiveFolderNodeViewModel node) => select(node);

    public void ReportFailure(Exception exception) => reportFailure(exception);
}
