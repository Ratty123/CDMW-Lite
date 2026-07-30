using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Cdmw.ArchiveLite.App.ViewModels;

/// <summary>
/// The folder tree flattened to the rows a grid can show, kept in step as folders open and close.
/// </summary>
/// <remarks>
/// WPF has no tree that can align columns, so the tree view is a grid and this is what turns a tree
/// into its rows. Opening a folder splices its visible rows in after it and closing one takes that
/// run back out, rather than rebuilding the list: a folder can hold a thousand files, and replacing
/// every row to reveal one level throws away the grid's containers and its scroll position with them.
/// </remarks>
public sealed class ArchiveEntryTreeRows
{
    private readonly HashSet<ArchiveFolderNodeViewModel> _tracked = [];

    public ObservableCollection<ArchiveFolderNodeViewModel> Rows { get; } = [];

    /// <summary>Starts again from a new set of roots.</summary>
    public void Reset(IEnumerable<ArchiveFolderNodeViewModel> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Clear();
        foreach (var root in roots)
        {
            Track(root);
            Rows.Add(root);
            foreach (var descendant in root.VisibleDescendants())
            {
                Track(descendant);
                Rows.Add(descendant);
            }
        }
    }

    public void Clear()
    {
        foreach (var node in _tracked)
        {
            node.PropertyChanged -= OnNodeChanged;
            node.Children.CollectionChanged -= OnChildrenChanged;
        }
        _tracked.Clear();
        Rows.Clear();
    }

    private void Track(ArchiveFolderNodeViewModel node)
    {
        if (!_tracked.Add(node))
        {
            return;
        }
        node.PropertyChanged += OnNodeChanged;
        node.Children.CollectionChanged += OnChildrenChanged;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ArchiveFolderNodeViewModel.IsExpanded)
            && sender is ArchiveFolderNodeViewModel node)
        {
            Refresh(node);
        }
    }

    /// <summary>
    /// A level has arrived from the worker, so whatever the folder was showing is replaced by what it
    /// actually holds. The sender is the collection, so its owner is found among the rows.
    /// </summary>
    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (sender is not ObservableCollection<ArchiveFolderNodeViewModel> children)
        {
            return;
        }
        var owner = _tracked.FirstOrDefault(node => ReferenceEquals(node.Children, children));
        if (owner is not null)
        {
            Refresh(owner);
        }
    }

    /// <summary>Replaces the run of rows below one folder with what it now shows.</summary>
    private void Refresh(ArchiveFolderNodeViewModel node)
    {
        var index = Rows.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        // Everything deeper than this folder and directly after it belongs to it.
        var end = index + 1;
        while (end < Rows.Count && Rows[end].Depth > node.Depth)
        {
            end++;
        }
        for (var removal = end - 1; removal > index; removal--)
        {
            Rows.RemoveAt(removal);
        }

        var insertion = index + 1;
        foreach (var descendant in node.VisibleDescendants())
        {
            Track(descendant);
            Rows.Insert(insertion++, descendant);
        }
    }
}
