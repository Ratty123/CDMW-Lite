using System.Collections.ObjectModel;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.App.ViewModels;

/// <summary>
/// One folder in the Archive Browser's folder pane, or the stand-in shown while a level loads.
/// Children are fetched the first time the user expands a folder, so opening an archive never has to
/// carry its whole structure across the worker protocol or into memory on the interface side.
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
        long totalCount,
        bool hasChildren)
    {
        _context = context;
        Name = name;
        Path = path;
        TotalCount = totalCount;
        HasChildren = hasChildren;
        if (HasChildren)
        {
            // WPF only draws an expander when an item already has children, so an unexpanded folder
            // needs one stand-in row to stay expandable before its real level has been fetched.
            Children.Add(new ArchiveFolderNodeViewModel(LocalizationManager.Get("FolderTreeLoading")));
        }
    }

    /// <summary>Creates the non-selectable placeholder row shown while children are loading.</summary>
    private ArchiveFolderNodeViewModel(string label)
    {
        Name = label;
        Path = string.Empty;
        IsPlaceholder = true;
    }

    public string Name { get; } = string.Empty;
    public string Path { get; } = string.Empty;
    public long TotalCount { get; }
    public bool HasChildren { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<ArchiveFolderNodeViewModel> Children { get; } = [];

    public string Label => IsPlaceholder ? Name : $"{Name} ({TotalCount:N0})";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
            {
                return;
            }
            if (!value || _childrenRequested || _context is null)
            {
                return;
            }

            _childrenRequested = true;
            _ = LoadChildrenAsync();
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
            if (folders.Count == 0 && HasChildren)
            {
                // This folder was told it holds something, so an empty level means the load was
                // answered by a superseded generation rather than by the archive. Keep the stand-in
                // and stay open to trying again: clearing here is what leaves a row that can never be
                // opened, since nothing would ask a second time.
                _childrenRequested = false;
                return;
            }
            Children.Clear();
            foreach (var folder in folders)
            {
                Children.Add(Create(_context, folder));
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

    public static ArchiveFolderNodeViewModel Create(ArchiveFolderTreeContext context, ArchiveFolderNode node)
    {
        var created = new ArchiveFolderNodeViewModel(
            context,
            node.Name,
            node.Path,
            node.TotalCount,
            node.HasChildren);
        if (node.Children.Count > 0)
        {
            created.Children.Clear();
            created._childrenRequested = true;
            foreach (var child in node.Children)
            {
                created.Children.Add(Create(context, child));
            }
        }
        return created;
    }

    /// <summary>
    /// Creates the row that releases the folder filter, shown above the top-level folders. It counts
    /// everything the tree covers, which is the whole archive until a filter narrows it.
    /// </summary>
    public static ArchiveFolderNodeViewModel CreateAllFolders(ArchiveFolderTreeContext context, long totalCount) =>
        new(context, LocalizationManager.Get("All"), string.Empty, totalCount, hasChildren: false);
}

/// <summary>
/// The folder pane's link back to the Archive Browser: how to fetch a level, what to do when the user
/// picks a folder, and where a failed expansion is reported.
/// </summary>
public sealed class ArchiveFolderTreeContext(
    Func<string, Task<IReadOnlyList<ArchiveFolderNode>>> loadChildren,
    Action<ArchiveFolderNodeViewModel> select,
    Action<Exception> reportFailure)
{
    public Task<IReadOnlyList<ArchiveFolderNode>> LoadChildrenAsync(string path) => loadChildren(path);

    public void Select(ArchiveFolderNodeViewModel node) => select(node);

    public void ReportFailure(Exception exception) => reportFailure(exception);
}
