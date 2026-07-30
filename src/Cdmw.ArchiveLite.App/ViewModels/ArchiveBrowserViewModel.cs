using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.Contracts;
using Microsoft.Win32;

namespace Cdmw.ArchiveLite.App.ViewModels;

public sealed class ArchiveBrowserViewModel : ObservableObject
{
    private const int PageSize = 256;

    private readonly WorkerProcessHost _worker;
    private readonly Action<string> _setShellStatus;
    private readonly Func<string, bool, ArchiveCacheMode?> _chooseCacheMode;
    private readonly Func<int, bool, bool, ExportSelection?> _chooseExportSelection;
    private CancellationTokenSource? _foregroundOperation;
    private CancellationTokenSource? _previewOperation;
    private CancellationTokenSource? _catalogueOperation;
    private CancellationTokenSource? _environmentOperation;
    private CancellationTokenSource? _folderTreeOperation;
    private long _foregroundGeneration;
    private long _previewGeneration;
    private long _catalogueGeneration;
    private long _environmentGeneration;
    private string _archiveRoot;
    private string? _sessionId;
    private string _pathFilter = string.Empty;
    private string _extensionFilter = string.Empty;
    private string _packageFilter = string.Empty;
    private bool _previewableOnly;
    private bool _showCategories;
    private ArchiveViewMode _viewMode = ArchiveViewMode.Flat;
    private ArchiveSortField _sortField = ArchiveSortField.Path;
    private bool _sortDescending;
    private ArchiveFolderFilter? _selectedFolder;
    private ArchiveFolderTreeContext? _folderTreeContext;
    private bool _isFolderTreeBusy;
    private long _folderTreeGeneration;
    private string? _folderTreeFilterKey;
    private long _folderTreeTotalCount;
    private bool _restoringCategorySelection;
    private ArchiveRoleFilter _selectedRole = null!;
    private ArchiveCategoryCount? _selectedCategory;
    private ArchiveEntryDto? _selectedEntry;
    /// <summary>
    /// How to rebuild the settled catalogue line, kept so a later language change can re-resolve it.
    /// Storing the formatted text alone left the counts frozen in whichever language produced them.
    /// </summary>
    private Func<string>? _catalogueStatusSource;
    private IReadOnlyList<long> _selectedEntryIds = [];
    private string _previewTitle = LocalizationManager.Get("Preview");
    private string _previewMetadata = string.Empty;
    private string _previewText = LocalizationManager.Get("PreviewEmpty");
    private string _previewSyntax = string.Empty;
    private string _previewWarnings = string.Empty;
    private BitmapSource? _previewImage;
    private Uri? _previewMediaSource;
    private string? _modelPreviewPackagePath;
    private bool _showModelTextures;
    private double _modelPreviewOrbitSensitivity;
    private double _modelPreviewPanSensitivity;
    private bool _modelPreviewInvertOrbitX;
    private bool _modelPreviewInvertOrbitY;
    private bool _modelPreviewInvertPanX;
    private bool _modelPreviewInvertPanY;
    private PreviewBackgroundChoice _previewBackgroundChoice = PreviewBackgroundChoice.Theme;
    private string _previewBackgroundCustomColor = PreviewBackgroundPalette.DefaultCustomColor;
    private PreviewKind _previewKind = PreviewKind.Metadata;
    private bool _isPreviewBusy;
    private bool _shouldPrewarmModelRenderer;
    private string _previewProgressText = string.Empty;
    private long _totalMatches;
    private int _pageStart;
    private bool _isBusy;
    private string _operationProgressText = string.Empty;
    private string _operationProgressDetail = string.Empty;
    private double _operationProgressPercent;
    private bool _isOperationProgressIndeterminate = true;
    private ExportCollisionPolicy _collisionPolicy = ExportCollisionPolicy.Skip;
    private ExportManifestFormat _manifestFormat = ExportManifestFormat.Json;
    private bool _isExtensionCatalogBusy;
    private bool _isNameIndexBusy;
    private string _catalogueStatus = string.Empty;
    private IReadOnlyList<long>? _itemScopeEntryIds;
    private string _itemScopeStatus = string.Empty;
    private IReadOnlyList<ArchiveExtensionFacet> _globalExtensionFacets = [];
    private IReadOnlyList<ArchiveExtensionFacet> _activeExtensionFacets = [];
    private bool _suppressPreviewSelection;
    private ArchiveQuerySpec? _lastAppliedQuery;
    private bool _isEnvironmentBusy;
    private ArchiveCacheHealthState _cacheHealthState = ArchiveCacheHealthState.Unknown;
    private string _cacheHealthDetail = LocalizationManager.Get("CacheNotChecked");
    private string _cacheHealthRoot = string.Empty;
    private string _environmentStatus = string.Empty;
    private IReadOnlyList<LocalizedOption<ArchiveViewMode>> _viewModes = [];
    private IReadOnlyList<LocalizedOption<ArchiveSortField>> _sortFields = [];
    private IReadOnlyList<ArchiveRoleFilter> _roleFilters = [];
    private IReadOnlyList<LocalizedOption<ExportCollisionPolicy>> _collisionPolicies = [];
    private IReadOnlyList<LocalizedOption<ExportManifestFormat>> _manifestFormats = [];
    private IReadOnlyList<LocalizedOption<PreviewBackgroundChoice>> _previewBackgrounds = [];

    public ArchiveBrowserViewModel(
        WorkerProcessHost worker,
        string? archiveRoot,
        Action<string> setShellStatus,
        Func<string, bool, ArchiveCacheMode?> chooseCacheMode,
        ArchiveSortField initialSortField = ArchiveSortField.Path,
        bool initialSortDescending = false,
        ArchiveBrowserSettings? initialSettings = null,
        Func<int, bool, bool, ExportSelection?>? chooseExportSelection = null)
    {
        var browserSettings = initialSettings ?? new ArchiveBrowserSettings();
        _worker = worker;
        _setShellStatus = setShellStatus;
        _chooseCacheMode = chooseCacheMode ?? throw new ArgumentNullException(nameof(chooseCacheMode));
        _chooseExportSelection = chooseExportSelection
            ?? (static (_, _, _) => new ExportSelection(ExportSelectionMode.PreserveStructure, ExportKind.RawEntries));
        _archiveRoot = archiveRoot ?? string.Empty;
        _pathFilter = browserSettings.PathFilter ?? string.Empty;
        _extensionFilter = browserSettings.ExtensionFilter ?? string.Empty;
        // The category navigator used to be two view modes of its own, numbered 1 and 2. A settings
        // file still naming one of those means the user had the navigator on, so the checkbox that
        // replaced them starts on and the view falls back to the plain list it was paired with.
        var storedViewMode = (int)browserSettings.ViewMode;
        _showCategories = browserSettings.ShowCategories || storedViewMode is 1 or 2;
        _viewMode = Enum.IsDefined(browserSettings.ViewMode)
            ? browserSettings.ViewMode
            : storedViewMode == 2 ? ArchiveViewMode.Folders : ArchiveViewMode.Flat;
        _sortField = Enum.IsDefined(initialSortField) ? initialSortField : ArchiveSortField.Path;
        if (_sortField == ArchiveSortField.Role)
        {
            _sortField = ArchiveSortField.FileType;
        }
        if (_sortField == ArchiveSortField.NameEvidence)
        {
            _sortField = ArchiveSortField.KnownName;
        }
        _sortDescending = initialSortDescending;
        _selectedFolder = string.IsNullOrWhiteSpace(browserSettings.FolderPath)
            ? null
            : new ArchiveFolderFilter(browserSettings.FolderPath, browserSettings.FolderPath);
        _collisionPolicy = Enum.IsDefined(browserSettings.CollisionPolicy)
            ? browserSettings.CollisionPolicy
            : ExportCollisionPolicy.Skip;
        _manifestFormat = Enum.IsDefined(browserSettings.ManifestFormat)
            ? browserSettings.ManifestFormat
            : ExportManifestFormat.Json;
        var cameraInput = browserSettings.ModelPreviewCameraInput ?? new ModelPreviewCameraInputSettings();
        _modelPreviewOrbitSensitivity = Math.Clamp(cameraInput.OrbitSensitivity, 0.05, 1.0);
        _modelPreviewPanSensitivity = Math.Clamp(cameraInput.PanSensitivity, 0.05, 3.0);
        _modelPreviewInvertOrbitX = cameraInput.InvertOrbitX;
        _modelPreviewInvertOrbitY = cameraInput.InvertOrbitY;
        _modelPreviewInvertPanX = cameraInput.InvertPanX;
        _modelPreviewInvertPanY = cameraInput.InvertPanY;
        var previewBackground = browserSettings.PreviewBackground ?? new PreviewBackgroundSettings();
        _previewBackgroundChoice = Enum.IsDefined(previewBackground.Choice)
            ? previewBackground.Choice
            : PreviewBackgroundChoice.Theme;
        _previewBackgroundCustomColor = PreviewBackgroundPalette.NormalizeCustomColor(previewBackground.CustomColor);
        AssociatedAssets = new AssociatedAssetsViewModel(
            worker,
            ShowAssociatedAssetInBrowserAsync,
            ExportAssociatedEntriesAsync,
            () => !IsBusy && !IsEnvironmentBusy,
            setShellStatus);
        BrowseCommand = new AsyncCommand(BrowseAsync, () => !IsBusy && !IsEnvironmentBusy);
        DetectGameCommand = new AsyncCommand(
            token => DetectAndInspectEnvironmentAsync(preferDetectedRoot: true, token),
            () => !IsBusy && !IsEnvironmentBusy);
        OpenCommand = new AsyncCommand(token => ChooseAndOpenArchiveAsync(false, token), CanOpenArchive);
        RefreshCommand = new AsyncCommand(
            token => ChooseAndOpenArchiveAsync(true, token),
            CanRefreshArchive);
        ApplyFilterCommand = new AsyncCommand(token => QueryAsync(0, token), () => !string.IsNullOrWhiteSpace(SessionId) && !IsBusy);
        ClearItemScopeCommand = new AsyncCommand(
            ClearItemScopeAndRefreshAsync,
            () => HasItemScope && !string.IsNullOrWhiteSpace(SessionId) && !IsBusy);
        PreviousPageCommand = new AsyncCommand(token => QueryAsync(Math.Max(0, PageStart - PageSize), token), () => PageStart > 0 && !IsBusy);
        NextPageCommand = new AsyncCommand(token => QueryAsync(PageStart + PageSize, token), () => PageStart + Entries.Count < TotalMatches && !IsBusy);
        CancelCommand = new RelayCommand(CancelForeground, () => IsBusy);
        ResetModelPreviewCameraInputCommand = new RelayCommand(ResetModelPreviewCameraInput);
        ExportSelectedCommand = new AsyncCommand(ExportSelectedAsync, () => CanExportSelectedEntries() && !IsBusy);
        ExportFamilyCommand = new AsyncCommand(
            token => AssociatedAssets.ExportCurrentFamilyAsync(token),
            () => SelectedEntry is not null && !IsBusy && !IsEnvironmentBusy);
        ExportFolderCommand = new AsyncCommand(
            ExportFolderAsync,
            () => !string.IsNullOrWhiteSpace(SessionId) && !string.IsNullOrWhiteSpace(SelectedFolder?.Path) && !IsBusy);
        ExportFilteredCommand = new AsyncCommand(ExportFilteredAsync, () => TotalMatches > 0 && !IsBusy);
        CopyFileNameCommand = new RelayCommand(CopySelectedFileName, () => SelectedEntry is not null);
        RebuildLocalizedOptions(null);
        ExtensionChoices.Add(ArchiveExtensionChoice.AllFiles(LocalizationManager.Get("AllFiles"), LocalizationManager.Get("ExtensionGroupAll")));
        MostCommonExtensionChoices.Add(ArchiveExtensionChoice.AllFiles(LocalizationManager.Get("AllFiles"), LocalizationManager.Get("ExtensionGroupAll")));
        ExtensionChoicesView = CollectionViewSource.GetDefaultView(ExtensionChoices);
        ExtensionChoicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ArchiveExtensionChoice.Group)));
        // The flattened rows follow the roots rather than being cleared alongside them by hand. A row
        // that outlives the load it came from is a row whose every request is answered by a
        // superseded generation, so it opens onto nothing; making that impossible by construction
        // beats remembering to clear two collections at each of the places one of them is replaced.
    }

    public ObservableCollection<ArchiveEntryDto> Entries { get; } = [];
    public ObservableCollection<ArchiveFolderFilter> Folders { get; } = [];
    public ObservableCollection<ArchiveFolderNodeViewModel> FolderTree { get; } = [];

    /// <summary>
    /// The folder tree flattened to the rows that are currently visible, each carrying its own depth.
    /// WPF has no tree that can align columns, so the tree view is a grid of these rows and the
    /// indent and expander live inside the first column.
    /// </summary>
    public ObservableCollection<ArchiveCategoryCount> Categories { get; } = [];
    public ObservableCollection<ArchiveExtensionChoice> ExtensionChoices { get; } = [];
    public ObservableCollection<ArchiveExtensionChoice> MostCommonExtensionChoices { get; } = [];
    public ICollectionView ExtensionChoicesView { get; }
    public AssociatedAssetsViewModel AssociatedAssets { get; }

    public IReadOnlyList<LocalizedOption<ArchiveViewMode>> ViewModes => _viewModes;
    public IReadOnlyList<LocalizedOption<ArchiveSortField>> SortFields => _sortFields;
    public IReadOnlyList<ArchiveRoleFilter> RoleFilters => _roleFilters;
    public IReadOnlyList<LocalizedOption<ExportCollisionPolicy>> CollisionPolicies => _collisionPolicies;
    public IReadOnlyList<LocalizedOption<ExportManifestFormat>> ManifestFormats => _manifestFormats;

    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand DetectGameCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ApplyFilterCommand { get; }
    public AsyncCommand ClearItemScopeCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ResetModelPreviewCameraInputCommand { get; }
    public AsyncCommand ExportSelectedCommand { get; }
    public AsyncCommand ExportFamilyCommand { get; }
    public AsyncCommand ExportFolderCommand { get; }
    public AsyncCommand ExportFilteredCommand { get; }
    public RelayCommand CopyFileNameCommand { get; }

    public event EventHandler? SessionChanged;
    public event EventHandler<ItemCatalogReadyEventArgs>? ItemCatalogReady;

    public void RefreshLocalization()
    {
        RebuildLocalizedOptions(SelectedRole.Role);
        RefreshNavigationLabels();
        RefreshExtensionLabels();
        AssociatedAssets.RefreshLocalization();
        OnPropertyChanged(nameof(CacheHealthLabel));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(TotalMatchesLabel));

        CacheHealthDetail = CacheHealthState switch
        {
            ArchiveCacheHealthState.Checking => LocalizationManager.Get("CacheChecking"),
            ArchiveCacheHealthState.Current => LocalizationManager.Get("CacheCurrent"),
            ArchiveCacheHealthState.SessionOnly => LocalizationManager.Get("CacheSessionOnly"),
            ArchiveCacheHealthState.Missing => LocalizationManager.Get("CacheMissing"),
            ArchiveCacheHealthState.Stale => LocalizationManager.Get("CacheRefreshRecommended"),
            ArchiveCacheHealthState.Invalid => LocalizationManager.Get("CacheInvalid"),
            _ => LocalizationManager.Get("CacheNotChecked"),
        };
        if (!string.IsNullOrWhiteSpace(EnvironmentStatus))
        {
            EnvironmentStatus = string.IsNullOrWhiteSpace(ArchiveRoot)
                ? LocalizationManager.Get("GameNotFound")
                : LocalizationManager.Format("GameFolderReady", ArchiveRoot);
        }
        if (IsExtensionCatalogBusy)
        {
            CatalogueStatus = LocalizationManager.Get("ExtensionCatalogLoading");
        }
        else if (IsNameIndexBusy)
        {
            CatalogueStatus = LocalizationManager.Get("NameIndexLoading");
        }
        else if (_catalogueStatusSource is not null)
        {
            CatalogueStatus = _catalogueStatusSource();
        }

        // Grid cells resolve their labels through value converters bound to the row's own DTO, so
        // nothing in the row tells them the language moved. Refreshing the view regenerates the
        // rows and re-runs the converters; the selection survives because the items are the same
        // instances.
        var selectedEntry = SelectedEntry;
        System.Windows.Data.CollectionViewSource.GetDefaultView(Entries)?.Refresh();
        if (selectedEntry is not null && Entries.Contains(selectedEntry))
        {
            SelectedEntry = selectedEntry;
        }

        if (SelectedEntry is null)
        {
            PreviewTitle = LocalizationManager.Get("Preview");
            PreviewText = LocalizationManager.Get("PreviewEmpty");
        }
        if (IsPreviewBusy)
        {
            PreviewProgressText = LocalizationManager.Get("PreviewProgressPreparing");
        }
        if (IsBusy)
        {
            OperationProgressText = LocalizationManager.Get("ProgressWorking");
        }
    }

    public string ArchiveRoot
    {
        get => _archiveRoot;
        set
        {
            if (SetProperty(ref _archiveRoot, value))
            {
                if (!PathsEqual(_cacheHealthRoot, value))
                {
                    _cacheHealthRoot = string.Empty;
                    CacheHealthState = ArchiveCacheHealthState.Unknown;
                    CacheHealthDetail = LocalizationManager.Get("CacheNotChecked");
                }
                RaiseCommandStates();
            }
        }
    }

    public bool IsEnvironmentBusy
    {
        get => _isEnvironmentBusy;
        private set
        {
            if (SetProperty(ref _isEnvironmentBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ArchiveCacheHealthState CacheHealthState
    {
        get => _cacheHealthState;
        private set
        {
            if (SetProperty(ref _cacheHealthState, value))
            {
                OnPropertyChanged(nameof(CacheHealthLabel));
                RaiseCommandStates();
            }
        }
    }

    public string CacheHealthLabel => LocalizationManager.Get(CacheHealthState switch
    {
        ArchiveCacheHealthState.Checking => "CacheChecking",
        ArchiveCacheHealthState.Current => "CacheCurrent",
        ArchiveCacheHealthState.SessionOnly => "CacheSessionOnly",
        ArchiveCacheHealthState.Missing => "CacheMissing",
        ArchiveCacheHealthState.Stale => "CacheStale",
        ArchiveCacheHealthState.Invalid => "CacheInvalid",
        _ => "CacheNotChecked",
    });

    public string CacheHealthDetail
    {
        get => _cacheHealthDetail;
        private set => SetProperty(ref _cacheHealthDetail, value);
    }

    public string EnvironmentStatus
    {
        get => _environmentStatus;
        private set => SetProperty(ref _environmentStatus, value);
    }

    public string? SessionId
    {
        get => _sessionId;
        private set
        {
            if (SetProperty(ref _sessionId, value))
            {
                ClearItemScope();
                AssociatedAssets.SelectSource(value, null);
                RaiseCommandStates();
                SessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string PathFilter
    {
        get => _pathFilter;
        set => SetProperty(ref _pathFilter, value);
    }

    public string ExtensionFilter
    {
        get => _extensionFilter;
        set => SetProperty(ref _extensionFilter, value);
    }

    public string PackageFilter
    {
        get => _packageFilter;
        set => SetProperty(ref _packageFilter, value);
    }

    public bool PreviewableOnly
    {
        get => _previewableOnly;
        set => SetProperty(ref _previewableOnly, value);
    }

    public ArchiveViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetProperty(ref _viewMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowFolderNavigator));
            // The folder filter's only controls are the folder navigator and the tree view, so it
            // must not outlive them; it would otherwise keep narrowing every later result with
            // nothing on screen able to release it.
            if (!ShowFolderNavigator)
            {
                SelectedFolder = Folders.FirstOrDefault(static folder => folder.Path is null);
            }
            EnsureFolderTreeLoaded();
        }
    }

    /// <summary>
    /// Whether the category navigator is shown. It is a setting of its own rather than a view mode,
    /// so any arrangement of the entry list can have it.
    /// </summary>
    public bool ShowCategories
    {
        get => _showCategories;
        set
        {
            if (!SetProperty(ref _showCategories, value))
            {
                return;
            }

            if (!value)
            {
                // Lite exposes no role control of its own, so the navigator is the only way in and out
                // of the role filter. Turning it off has to release what it applied.
                SelectedCategory = null;
                SelectedRole = RoleFilters.First(static role => role.Role is null);
            }
            // The counts come from the query, and whether they are gathered at all is decided when it
            // runs, so the navigator would sit empty until the user happened to search again.
            if (ApplyFilterCommand.CanExecute(null))
            {
                ApplyFilterCommand.Execute(null);
            }
        }
    }

    public bool IsFolderTreeBusy
    {
        get => _isFolderTreeBusy;
        private set => SetProperty(ref _isFolderTreeBusy, value);
    }

    /// <summary>Whether the folder tree has its own pane beside the entry list.</summary>
    public bool ShowFolderNavigator => ViewMode is ArchiveViewMode.Folders;

    /// <summary>Whether the entry list is itself a tree of folders and the files inside them.</summary>


    /// <summary>
    /// What kind of row a tree is sitting on, which decides what its context menu offers. A folder
    /// has no family and no file name to copy; a file is not a folder anyone can export.
    /// </summary>


    public ArchiveSortField SortField
    {
        get => _sortField;
        set => SetProperty(ref _sortField, value);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set => SetProperty(ref _sortDescending, value);
    }

    public bool IsExtensionCatalogBusy
    {
        get => _isExtensionCatalogBusy;
        private set => SetProperty(ref _isExtensionCatalogBusy, value);
    }

    public bool IsNameIndexBusy
    {
        get => _isNameIndexBusy;
        private set => SetProperty(ref _isNameIndexBusy, value);
    }

    public string CatalogueStatus
    {
        get => _catalogueStatus;
        // Any direct assignment is a transient line (loading, progress, cleared) that a later
        // language change must not overwrite with a settled result, so it drops the stored source.
        private set
        {
            _catalogueStatusSource = null;
            SetProperty(ref _catalogueStatus, value);
        }
    }

    /// <summary>
    /// Publishes a catalogue line that survives a language change, by keeping the resolver rather
    /// than the resolved text.
    /// </summary>
    private void SetCatalogueStatus(Func<string> localized)
    {
        CatalogueStatus = localized();
        _catalogueStatusSource = localized;
    }

    public string ItemScopeStatus
    {
        get => _itemScopeStatus;
        private set => SetProperty(ref _itemScopeStatus, value);
    }

    public bool HasItemScope => _itemScopeEntryIds is not null;

    public void ApplyCommonExtension(ArchiveExtensionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (IsBusy || string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }
        ExtensionFilter = choice.Extension;
        ApplyFilterCommand.Execute(null);
    }

    public void ApplyColumnSort(ArchiveSortField field)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }
        if (SortField == field)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortField = field;
            SortDescending = false;
        }
        ApplyFilterCommand.Execute(null);
    }

    public void SetSelectedEntries(IEnumerable<ArchiveEntryDto> entries)
    {
        _selectedEntryIds = entries
            .Select(static entry => entry.EntryId)
            .Distinct()
            .ToArray();
        ExportSelectedCommand.RaiseCanExecuteChanged();
        ExportFamilyCommand.RaiseCanExecuteChanged();
    }

    public ArchiveFolderFilter? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                ExportFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ArchiveRoleFilter SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (value is not null) SetProperty(ref _selectedRole, value);
        }
    }

    public ArchiveCategoryCount? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            // WPF clears a list selection while its items are being repopulated, so a transient null
            // must not touch the role filter. Only a real choice moves it, and the "All" row - whose
            // name does not parse as a role - is what clears it again.
            if (!SetProperty(ref _selectedCategory, value) || value is null)
            {
                return;
            }

            SelectedRole = Enum.TryParse<ArchiveEntryRole>(value.Name, out var role)
                ? RoleFilters.First(option => option.Role == role)
                : RoleFilters.First(static option => option.Role is null);

            // Choosing a category is choosing a filter, so it applies itself. The restore that
            // follows every query goes through this same setter, and re-querying from there would
            // never stop, so only a choice the user made reaches this.
            if (!_restoringCategorySelection && ApplyFilterCommand.CanExecute(null))
            {
                ApplyFilterCommand.Execute(null);
            }
        }
    }

    public ArchiveEntryDto? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                if (value is null)
                {
                    _selectedEntryIds = [];
                }
                AssociatedAssets.SelectSource(SessionId, value);
                OnPropertyChanged(nameof(IsModelSelection));
                OnPropertyChanged(nameof(CanOpenPreviewSettings));
                ExportSelectedCommand.RaiseCanExecuteChanged();
                ExportFamilyCommand.RaiseCanExecuteChanged();
                CopyFileNameCommand.RaiseCanExecuteChanged();
                if (!_suppressPreviewSelection)
                {
                    _ = LoadPreviewLatestAsync(value);
                }
            }
        }
    }

    public string PreviewTitle
    {
        get => _previewTitle;
        private set => SetProperty(ref _previewTitle, value);
    }

    public string PreviewMetadata
    {
        get => _previewMetadata;
        private set => SetProperty(ref _previewMetadata, value);
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public string PreviewSyntax
    {
        get => _previewSyntax;
        private set => SetProperty(ref _previewSyntax, value);
    }

    public string PreviewWarnings
    {
        get => _previewWarnings;
        private set => SetProperty(ref _previewWarnings, value);
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public Uri? PreviewMediaSource
    {
        get => _previewMediaSource;
        private set => SetProperty(ref _previewMediaSource, value);
    }

    public PreviewKind PreviewKind
    {
        get => _previewKind;
        private set
        {
            if (SetProperty(ref _previewKind, value))
            {
                OnPropertyChanged(nameof(IsImagePreview));
                OnPropertyChanged(nameof(IsMediaPreview));
                OnPropertyChanged(nameof(IsModelPreview));
                OnPropertyChanged(nameof(IsTextPreview));
                OnPropertyChanged(nameof(CanOpenPreviewSettings));
            }
        }
    }

    public string? ModelPreviewPackagePath
    {
        get => _modelPreviewPackagePath;
        private set
        {
            if (SetProperty(ref _modelPreviewPackagePath, value))
            {
                OnPropertyChanged(nameof(IsModelPreview));
                OnPropertyChanged(nameof(IsTextPreview));
            }
        }
    }

    public bool IsPreviewBusy
    {
        get => _isPreviewBusy;
        private set => SetProperty(ref _isPreviewBusy, value);
    }

    public bool ShowModelTextures
    {
        get => _showModelTextures;
        set
        {
            if (!SetProperty(ref _showModelTextures, value)
                || !IsModelSelection
                || SelectedEntry is not { } entry)
            {
                return;
            }
            _ = LoadPreviewLatestAsync(entry);
        }
    }

    public double ModelPreviewOrbitSensitivity
    {
        get => _modelPreviewOrbitSensitivity;
        set => SetProperty(ref _modelPreviewOrbitSensitivity, Math.Clamp(value, 0.05, 1.0));
    }

    public double ModelPreviewPanSensitivity
    {
        get => _modelPreviewPanSensitivity;
        set => SetProperty(ref _modelPreviewPanSensitivity, Math.Clamp(value, 0.05, 3.0));
    }

    public bool ModelPreviewInvertOrbitX
    {
        get => _modelPreviewInvertOrbitX;
        set => SetProperty(ref _modelPreviewInvertOrbitX, value);
    }

    public bool ModelPreviewInvertOrbitY
    {
        get => _modelPreviewInvertOrbitY;
        set => SetProperty(ref _modelPreviewInvertOrbitY, value);
    }

    public bool ModelPreviewInvertPanX
    {
        get => _modelPreviewInvertPanX;
        set => SetProperty(ref _modelPreviewInvertPanX, value);
    }

    public bool ModelPreviewInvertPanY
    {
        get => _modelPreviewInvertPanY;
        set => SetProperty(ref _modelPreviewInvertPanY, value);
    }

    public PreviewBackgroundChoice PreviewBackgroundChoice
    {
        get => _previewBackgroundChoice;
        set
        {
            if (SetProperty(ref _previewBackgroundChoice, Enum.IsDefined(value) ? value : PreviewBackgroundChoice.Theme))
            {
                OnPreviewBackgroundChanged();
            }
        }
    }

    /// <summary>The #RRGGBB used when <see cref="PreviewBackgroundChoice.Custom"/> is selected.</summary>
    public string PreviewBackgroundCustomColor
    {
        get => _previewBackgroundCustomColor;
        set
        {
            // Keep the raw text so a half-typed colour is still editable; only a complete
            // #RRGGBB reaches the preview surface.
            if (SetProperty(ref _previewBackgroundCustomColor, value ?? string.Empty))
            {
                OnPreviewBackgroundChanged();
            }
        }
    }

    public bool IsCustomPreviewBackground => PreviewBackgroundChoice == PreviewBackgroundChoice.Custom;

    /// <summary>Null when the theme owns the surface, so the themed brush behind it stays visible.</summary>
    public System.Windows.Media.Brush? PreviewBackgroundBrush
    {
        get
        {
            if (!PreviewBackgroundPalette.TryResolve(PreviewBackgroundChoice, PreviewBackgroundCustomColor, out var color))
            {
                return null;
            }
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>The renderer's clear colour, or empty to keep the renderer's own default.</summary>
    public string PreviewBackgroundColorHex =>
        PreviewBackgroundPalette.TryResolve(PreviewBackgroundChoice, PreviewBackgroundCustomColor, out var color)
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : string.Empty;

    public IReadOnlyList<LocalizedOption<PreviewBackgroundChoice>> PreviewBackgrounds => _previewBackgrounds;

    public PreviewBackgroundSettings CapturePreviewBackgroundSettings() => new(
        PreviewBackgroundChoice,
        PreviewBackgroundPalette.NormalizeCustomColor(PreviewBackgroundCustomColor));

    private void OnPreviewBackgroundChanged()
    {
        OnPropertyChanged(nameof(IsCustomPreviewBackground));
        OnPropertyChanged(nameof(PreviewBackgroundBrush));
        OnPropertyChanged(nameof(PreviewBackgroundColorHex));
    }

    public bool ShouldPrewarmModelRenderer
    {
        get => _shouldPrewarmModelRenderer;
        private set => SetProperty(ref _shouldPrewarmModelRenderer, value);
    }

    public string PreviewProgressText
    {
        get => _previewProgressText;
        private set => SetProperty(ref _previewProgressText, value);
    }

    public bool IsImagePreview => PreviewKind == PreviewKind.Image && PreviewImage is not null;
    public bool IsMediaPreview => PreviewKind is PreviewKind.Audio or PreviewKind.Video && PreviewMediaSource is not null;
    public bool IsModelPreview => PreviewKind == PreviewKind.Model && !string.IsNullOrWhiteSpace(ModelPreviewPackagePath);
    public bool IsModelSelection => SelectedEntry is { Extension: var extension }
        && NativeModelExtension(extension);
    public bool IsTextPreview => !IsImagePreview && !IsMediaPreview && !IsModelPreview;

    /// <summary>
    /// Preview Settings carries both the model camera input and the preview background, so it is
    /// reachable from a decoded texture as well as from a model selection.
    /// </summary>
    public bool CanOpenPreviewSettings => IsModelSelection || IsImagePreview;

    public ModelPreviewCameraInputSettings CaptureModelPreviewCameraInputSettings() => new(
        ModelPreviewOrbitSensitivity,
        ModelPreviewPanSensitivity,
        ModelPreviewInvertOrbitX,
        ModelPreviewInvertOrbitY,
        ModelPreviewInvertPanX,
        ModelPreviewInvertPanY);

    public long TotalMatches
    {
        get => _totalMatches;
        private set
        {
            if (SetProperty(ref _totalMatches, value))
            {
                OnPropertyChanged(nameof(PageSummary));
                OnPropertyChanged(nameof(TotalMatchesLabel));
                RaiseCommandStates();
            }
        }
    }

    public int PageStart
    {
        get => _pageStart;
        private set
        {
            if (SetProperty(ref _pageStart, value))
            {
                OnPropertyChanged(nameof(PageSummary));
                RaiseCommandStates();
            }
        }
    }

    public string PageSummary => TotalMatches == 0
        ? "0"
        : $"{PageStart + 1:N0}-{Math.Min(TotalMatches, PageStart + Entries.Count):N0} / {TotalMatches:N0}";

    public string TotalMatchesLabel => LocalizationManager.Format("EntriesCount", TotalMatches);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsOperationProgressVisible));
                RaiseCommandStates();
            }
        }
    }

    public bool IsOperationProgressVisible => IsBusy;

    public string OperationProgressText
    {
        get => _operationProgressText;
        private set => SetProperty(ref _operationProgressText, value);
    }

    public string OperationProgressDetail
    {
        get => _operationProgressDetail;
        private set => SetProperty(ref _operationProgressDetail, value);
    }

    public double OperationProgressPercent
    {
        get => _operationProgressPercent;
        private set => SetProperty(ref _operationProgressPercent, value);
    }

    public bool IsOperationProgressIndeterminate
    {
        get => _isOperationProgressIndeterminate;
        private set => SetProperty(ref _isOperationProgressIndeterminate, value);
    }

    public ExportCollisionPolicy CollisionPolicy
    {
        get => _collisionPolicy;
        set => SetProperty(ref _collisionPolicy, value);
    }

    public ExportManifestFormat ManifestFormat
    {
        get => _manifestFormat;
        set => SetProperty(ref _manifestFormat, value);
    }

    public void RequestShutdown()
    {
        CancelEnvironment();
        CancelForeground();
        Interlocked.Increment(ref _previewGeneration);
        CancelOperation(Interlocked.Exchange(ref _previewOperation, null));
        Interlocked.Increment(ref _catalogueGeneration);
        CancelOperation(Interlocked.Exchange(ref _catalogueOperation, null));
        AssociatedAssets.RequestShutdown();
    }

    public async Task InitializeEnvironmentAsync(CancellationToken cancellationToken)
    {
        await DetectAndInspectEnvironmentAsync(preferDetectedRoot: false, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (CacheHealthState == ArchiveCacheHealthState.Current &&
            string.IsNullOrWhiteSpace(SessionId) &&
            !string.IsNullOrWhiteSpace(ArchiveRoot))
        {
            await OpenArchiveAsync(
                forceRefresh: false,
                ArchiveCacheMode.Persistent,
                cancellationToken,
                allowCacheBuild: false).ConfigureAwait(true);
        }
        else if (CacheHealthState == ArchiveCacheHealthState.Missing &&
                 string.IsNullOrWhiteSpace(SessionId) &&
                 !string.IsNullOrWhiteSpace(ArchiveRoot))
        {
            await ChooseAndOpenArchiveAsync(forceRefresh: false, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Get("ArchiveRoot"),
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            ArchiveRoot = dialog.FolderName;
            await InspectSelectedRootAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private bool CanOpenArchive() =>
        !string.IsNullOrWhiteSpace(ArchiveRoot) &&
        CacheHealthState != ArchiveCacheHealthState.Stale &&
        !IsBusy &&
        !IsEnvironmentBusy;

    private bool CanRefreshArchive() =>
        !string.IsNullOrWhiteSpace(ArchiveRoot) && !IsBusy && !IsEnvironmentBusy;

    private async Task DetectAndInspectEnvironmentAsync(
        bool preferDetectedRoot,
        CancellationToken cancellationToken)
    {
        using var operation = BeginEnvironmentOperation(cancellationToken);
        var generation = Interlocked.Increment(ref _environmentGeneration);
        try
        {
            EnvironmentStatus = LocalizationManager.Get("DetectingGame");
            var discovery = await _worker.SendAsync<GameInstallDiscoveryRequest, GameInstallDiscoveryResult>(
                WorkerProtocol.DiscoverGameRoots,
                generation,
                new GameInstallDiscoveryRequest(),
                operation.Token).ConfigureAwait(true);
            if (!EnvironmentIsCurrent(generation))
            {
                return;
            }

            var configuredRoot = ArchiveRoot.Trim();
            var configuredExists = Path.Exists(configuredRoot);
            var selectedRoot = preferDetectedRoot && discovery.PreferredRoot is not null
                ? discovery.PreferredRoot
                : configuredExists
                    ? configuredRoot
                    : discovery.PreferredRoot ?? (configuredRoot.Length > 0 ? configuredRoot : null);

            if (string.IsNullOrWhiteSpace(selectedRoot))
            {
                EnvironmentStatus = LocalizationManager.Get("GameNotFound");
                CacheHealthState = ArchiveCacheHealthState.Unknown;
                CacheHealthDetail = LocalizationManager.Get("CacheNotChecked");
                return;
            }

            ArchiveRoot = selectedRoot;
            EnvironmentStatus = LocalizationManager.Format(
                PathsEqual(selectedRoot, discovery.PreferredRoot) ? "GameDetected" : "GameFolderReady",
                selectedRoot);
            await InspectCacheCoreAsync(selectedRoot, generation, operation.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (EnvironmentIsCurrent(generation))
            {
                EnvironmentStatus = exception.Message;
                CacheHealthState = ArchiveCacheHealthState.Invalid;
                CacheHealthDetail = exception.Message;
            }
        }
        finally
        {
            EndEnvironmentOperation(operation);
        }
    }

    private async Task InspectSelectedRootAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ArchiveRoot))
        {
            return;
        }

        using var operation = BeginEnvironmentOperation(cancellationToken);
        var generation = Interlocked.Increment(ref _environmentGeneration);
        try
        {
            EnvironmentStatus = LocalizationManager.Format("GameFolderReady", ArchiveRoot);
            await InspectCacheCoreAsync(ArchiveRoot, generation, operation.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (EnvironmentIsCurrent(generation))
            {
                CacheHealthState = ArchiveCacheHealthState.Invalid;
                CacheHealthDetail = exception.Message;
                EnvironmentStatus = exception.Message;
            }
        }
        finally
        {
            EndEnvironmentOperation(operation);
        }
    }

    private async Task InspectCacheCoreAsync(
        string archiveRoot,
        long generation,
        CancellationToken cancellationToken)
    {
        CacheHealthState = ArchiveCacheHealthState.Checking;
        CacheHealthDetail = LocalizationManager.Get("CacheChecking");
        var result = await _worker.SendAsync<ArchiveCacheHealthRequest, ArchiveCacheHealthResult>(
            WorkerProtocol.InspectArchiveCache,
            generation,
            new ArchiveCacheHealthRequest(archiveRoot),
            cancellationToken).ConfigureAwait(true);
        if (!EnvironmentIsCurrent(generation) || !PathsEqual(ArchiveRoot, result.PackageRoot))
        {
            return;
        }

        _cacheHealthRoot = result.PackageRoot;
        CacheHealthState = result.State;
        CacheHealthDetail = result.State == ArchiveCacheHealthState.Stale
            ? $"{result.Reason} {LocalizationManager.Get("CacheRefreshRecommended")}"
            : result.Reason;
    }

    private async Task ChooseAndOpenArchiveAsync(bool forceRefresh, CancellationToken commandToken)
    {
        commandToken.ThrowIfCancellationRequested();
        var cacheMode = _chooseCacheMode(ArchiveRoot.Trim(), forceRefresh);
        if (cacheMode is null)
        {
            return;
        }
        await OpenArchiveAsync(forceRefresh, cacheMode.Value, commandToken).ConfigureAwait(true);
    }

    private async Task OpenArchiveAsync(
        bool forceRefresh,
        ArchiveCacheMode cacheMode,
        CancellationToken commandToken,
        bool allowCacheBuild = true)
    {
        using var operation = BeginForegroundOperation(commandToken);
        var generation = Interlocked.Increment(ref _foregroundGeneration);
        ClearItemScope();
        CancelCatalogue();
        CancelPreviewAndClear();
        try
        {
            _setShellStatus(LocalizationManager.Format("OpeningArchive", ArchiveRoot));
            SetOperationProgress(LocalizationManager.Get("ProgressDiscovering"));
            var progress = new Progress<ProgressUpdate>(update => ApplyForegroundProgress(generation, update));
            var result = await _worker.SendAsync<OpenArchiveRequest, OpenArchiveResult>(
                WorkerProtocol.OpenArchive,
                generation,
                new OpenArchiveRequest(ArchiveRoot, forceRefresh, cacheMode, allowCacheBuild),
                operation.Token,
                progress).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _foregroundGeneration))
            {
                return;
            }

            SessionId = result.SessionId;
            _cacheHealthRoot = result.PackageRoot;
            CacheHealthState = result.CacheMode == ArchiveCacheMode.SessionOnly
                ? ArchiveCacheHealthState.SessionOnly
                : ArchiveCacheHealthState.Current;
            CacheHealthDetail = LocalizationManager.Get(result.CacheMode == ArchiveCacheMode.SessionOnly
                ? "CacheSessionOnlyLoaded"
                : (result.UsedCachedIndex ? "CacheReused" : "CacheRebuilt"));
            _setShellStatus(LocalizationManager.Format("OpenedEntries", result.EntryCount));
            SetOperationProgress(LocalizationManager.Get("ProgressLoadingEntries"));
            await QueryPageCoreAsync(0, generation, operation.Token).ConfigureAwait(true);
            ShouldPrewarmModelRenderer = true;
            EnsureFolderTreeLoaded();
            StartCatalogueLoad(result.SessionId);
        }
        catch (WorkerRequestException exception) when (exception.Error.Code == "cache_refresh_required")
        {
            _cacheHealthRoot = ArchiveRoot;
            CacheHealthState = ArchiveCacheHealthState.Stale;
            CacheHealthDetail = LocalizationManager.Get("CacheRefreshRecommended");
            _setShellStatus(CacheHealthDetail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _setShellStatus(exception.Message);
        }
        finally
        {
            EndForegroundOperation(operation);
        }
    }

    private async Task QueryAsync(int pageStart, CancellationToken commandToken)
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        using var operation = BeginForegroundOperation(commandToken);
        var generation = Interlocked.Increment(ref _foregroundGeneration);
        try
        {
            SetOperationProgress(LocalizationManager.Get("ProgressLoadingEntries"));
            await QueryPageCoreAsync(pageStart, generation, operation.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _setShellStatus(exception.Message);
        }
        finally
        {
            EndForegroundOperation(operation);
        }
    }

    public async Task<bool> ShowItemScopeAsync(
        int itemId,
        string displayName,
        bool includeRelated,
        CancellationToken commandToken)
    {
        var sessionId = SessionId;
        if (IsBusy || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        var applied = false;
        using var operation = BeginForegroundOperation(commandToken);
        var generation = Interlocked.Increment(ref _foregroundGeneration);
        try
        {
            SetOperationProgress(LocalizationManager.Get("ItemFinderScopeLoading"));
            var progress = new Progress<ProgressUpdate>(update =>
            {
                if (generation == Volatile.Read(ref _foregroundGeneration))
                {
                    SetOperationProgress(LocalizationManager.Get("ItemFinderScopeLoading"));
                    OperationProgressDetail = update.Total > 0
                        ? $"{update.Completed:N0} / {update.Total:N0}"
                        : update.CurrentItem ?? string.Empty;
                }
            });
            var scope = await _worker.SendAsync<ItemCatalogScopeRequest, ItemCatalogScopeResult>(
                WorkerProtocol.ScopeItemCatalog,
                generation,
                new ItemCatalogScopeRequest(sessionId, itemId, includeRelated),
                operation.Token,
                progress).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _foregroundGeneration)
                || !string.Equals(SessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            _itemScopeEntryIds = scope.EntryIds;
            OnPropertyChanged(nameof(HasItemScope));
            PathFilter = string.Empty;
            ExtensionFilter = string.Empty;
            PackageFilter = string.Empty;
            PreviewableOnly = false;
            ViewMode = ArchiveViewMode.Flat;
            SortField = ArchiveSortField.Path;
            SortDescending = false;
            SelectedFolder = Folders.FirstOrDefault(static folder => folder.Path is null);
            SelectedRole = RoleFilters.First(static role => role.Role is null);
            SelectedCategory = null;
            ItemScopeStatus = LocalizationManager.Format(
                includeRelated ? "ItemFinderRelatedScopeApplied" : "ItemFinderExactScopeApplied",
                displayName,
                scope.EntryIds.Count,
                scope.DirectCount);
            ApplyActiveExtensionFacets(scope.Extensions ?? []);
            await QueryPageCoreAsync(0, generation, operation.Token).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _foregroundGeneration)
                || !string.Equals(SessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }
            SelectedEntry = SelectItemPreviewEntry(Entries);
            _setShellStatus(ItemScopeStatus);
            applied = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _setShellStatus(exception.Message);
        }
        finally
        {
            EndForegroundOperation(operation);
        }
        return applied;
    }

    private async Task QueryPageCoreAsync(int pageStart, long generation, CancellationToken cancellationToken)
    {
        var sessionId = SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var request = CreateQuerySpec(sessionId, pageStart);
        var result = await _worker.SendAsync<ArchiveQuerySpec, ArchivePageResult>(
            WorkerProtocol.QueryArchive,
            generation,
            request,
            cancellationToken).ConfigureAwait(true);
        if (generation != Volatile.Read(ref _foregroundGeneration))
        {
            return;
        }
        _lastAppliedQuery = request;

        CancelPreviewAndClear();
        SelectedEntry = null;
        Entries.Clear();
        foreach (var entry in result.Entries)
        {
            Entries.Add(entry);
        }

        ApplyFolderFacets(result.Folders);

        ApplyCategoryFacets(result.Categories);
        RefreshFolderTreeForFilters();

        PageStart = result.PageStart;
        TotalMatches = result.TotalMatches;
        OnPropertyChanged(nameof(PageSummary));
        RaiseCommandStates();
        _setShellStatus(LocalizationManager.Format("ShowingEntries", PageSummary));
    }

    private async Task ShowAssociatedAssetInBrowserAsync(
        string sessionId,
        ArchiveEntryDto target,
        CancellationToken commandToken)
    {
        if (IsBusy || !string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        ClearItemScope();
        PathFilter = target.Path;
        ExtensionFilter = string.Empty;
        PackageFilter = string.Empty;
        PreviewableOnly = false;
        ViewMode = ArchiveViewMode.Flat;
        SortField = ArchiveSortField.Path;
        SortDescending = false;
        SelectedFolder = Folders.FirstOrDefault(static folder => folder.Path is null);
        SelectedRole = RoleFilters.First(static role => role.Role is null);
        SelectedCategory = null;

        using var operation = BeginForegroundOperation(commandToken);
        var generation = Interlocked.Increment(ref _foregroundGeneration);
        try
        {
            SetOperationProgress(LocalizationManager.Get("ProgressLoadingEntries"));
            await QueryPageCoreAsync(0, generation, operation.Token).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _foregroundGeneration))
            {
                return;
            }

            var located = Entries.FirstOrDefault(entry => entry.EntryId == target.EntryId);
            if (located is null)
            {
                _setShellStatus(LocalizationManager.Get("AssociatedAssetNotLocated"));
                return;
            }
            SelectedEntry = located;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _setShellStatus(exception.Message);
        }
        finally
        {
            EndForegroundOperation(operation);
        }
    }

    private ArchiveQuerySpec CreateQuerySpec(string sessionId, int pageStart)
    {
        var extensions = ExtensionFilter.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ArchiveQuerySpec(
            sessionId,
            PathFilter,
            extensions,
            PackageFilter,
            SelectedFolder?.Path,
            SelectedRole.Role is { } role ? [role] : null,
            PreviewableOnly: PreviewableOnly,
            ViewMode: ViewMode,
            IncludeCategoryFacets: ShowCategories,
            IncludeFolderTree: ShowFolderNavigator,
            SortField: SortField,
            SortDescending: SortDescending,
            PageStart: pageStart,
            PageSize: PageSize,
            EntryIds: _itemScopeEntryIds);
    }

    private async Task ClearItemScopeAndRefreshAsync(CancellationToken cancellationToken)
    {
        ClearItemScope();
        await QueryAsync(0, cancellationToken).ConfigureAwait(true);
    }

    private void ClearItemScope()
    {
        var hadScope = _itemScopeEntryIds is not null;
        _itemScopeEntryIds = null;
        ItemScopeStatus = string.Empty;
        if (hadScope)
        {
            OnPropertyChanged(nameof(HasItemScope));
        }
        ApplyActiveExtensionFacets(_globalExtensionFacets);
        ClearItemScopeCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Starts whichever tree the current view mode shows. Deriving a tree costs a pass over every
    /// archive path, so the flat and category views - which show neither the folder pane nor the
    /// tree view - never pay for it.
    /// </summary>
    private void EnsureFolderTreeLoaded()
    {
        if (!ShowFolderNavigator)
        {
            return;
        }
        if (SessionId is not { } sessionId || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }
        // A filter that moved while the pane was off screen is picked up here, so coming back to it
        // never shows rows the filters have moved past.
        if (_folderTreeContext is not null
            && string.Equals(_folderTreeFilterKey, CreateTreeFilter().CacheKey, StringComparison.Ordinal))
        {
            return;
        }
        StartFolderTreeLoad(sessionId);
    }

    /// <summary>
    /// Loads the archive's top-level folders. Deeper levels arrive when the user expands them, and the
    /// worker derives the whole structure once per session, so the first expansion pays for the scan
    /// and every later one is served from the resident tree.
    /// </summary>
    private void StartFolderTreeLoad(string sessionId)
    {
        var generation = Interlocked.Increment(ref _folderTreeGeneration);
        var operation = new CancellationTokenSource();
        CancelOperation(Interlocked.Exchange(ref _folderTreeOperation, operation));
        FolderTree.Clear();
        var filter = CreateTreeFilter();
        _folderTreeFilterKey = filter.CacheKey;
        _folderTreeContext = new ArchiveFolderTreeContext(
            path => LoadFolderChildrenAsync(sessionId, generation, path, filter),
            SelectFolderNode,
            exception => _setShellStatus(exception.Message));
        _ = LoadFolderTreeRootAsync(sessionId, generation);
    }

    /// <summary>
    /// The filters the tree narrows itself with. The folder filter is left out on purpose: the tree
    /// is how a folder is chosen, so applying it would collapse the tree to the folder already
    /// chosen and leave no way to reach any other.
    /// </summary>
    private ArchiveEntryFilter CreateTreeFilter() => new(
        PathFilter,
        ExtensionFilter.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        PackageFilter,
        Folder: null,
        SelectedRole.Role is { } role ? [role] : null,
        MinimumSize: null,
        PreviewableOnly);

    /// <summary>
    /// Rebuilds the tree when the filters move, since its rows and its counts are the filtered ones.
    /// </summary>
    private void RefreshFolderTreeForFilters()
    {
        // Only when a tree is actually on screen. Rebuilding one costs a pass over the archive, and a
        // role filter - which is what choosing a category applies - has no index to shorten it. Doing
        // that on every category click for a tree nobody can see is the expensive kind of nothing.
        // Coming back to a tree view re-reads the filter, so it cannot be left showing stale rows.
        if (!ShowFolderNavigator)
        {
            return;
        }
        if (_folderTreeContext is null || SessionId is not { } sessionId || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }
        if (string.Equals(_folderTreeFilterKey, CreateTreeFilter().CacheKey, StringComparison.Ordinal))
        {
            return;
        }
        StartFolderTreeLoad(sessionId);
    }

    private async Task LoadFolderTreeRootAsync(string sessionId, long generation)
    {
        var context = _folderTreeContext;
        if (context is null)
        {
            return;
        }
        try
        {
            IsFolderTreeBusy = true;
            // Through the context so the root is fetched with the same filter its levels will be.
            var children = await context.LoadChildrenAsync(string.Empty).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _folderTreeGeneration))
            {
                return;
            }
            FolderTree.Clear();
            // The tree has no row for the archive itself, so without this there is no way back to
            // every folder once one is chosen - the picker that used to do it is gone.
            FolderTree.Add(ArchiveFolderNodeViewModel.CreateAllFolders(context, _folderTreeTotalCount));
            foreach (var child in children)
            {
                FolderTree.Add(ArchiveFolderNodeViewModel.Create(context, child));
            }
        }
        catch (OperationCanceledException)
        {
            // A newer archive session owns the folder tree.
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _folderTreeGeneration))
            {
                _setShellStatus(exception.Message);
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _folderTreeGeneration))
            {
                IsFolderTreeBusy = false;
            }
        }
    }

    private async Task<IReadOnlyList<ArchiveFolderNode>> LoadFolderChildrenAsync(
        string sessionId,
        long generation,
        string path,
        ArchiveEntryFilter filter)
    {
        var operation = _folderTreeOperation;
        if (operation is null || generation != Volatile.Read(ref _folderTreeGeneration))
        {
            return [];
        }
        var result = await _worker.SendAsync<ArchiveFolderTreeRequest, ArchiveFolderTreeResult>(
            WorkerProtocol.ArchiveFolderTree,
            generation,
            new ArchiveFolderTreeRequest(sessionId, path, Filter: filter),
            operation.Token).ConfigureAwait(true);
        if (generation != Volatile.Read(ref _folderTreeGeneration)
            || !string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            return [];
        }
        if (string.IsNullOrEmpty(path))
        {
            // The root's own total is what the "All" row counts, and only this reply carries it.
            _folderTreeTotalCount = result.TotalCount;
        }
        if (result.Truncated)
        {
            _setShellStatus(LocalizationManager.Format("FolderTreeTruncated", result.Nodes.Count));
        }
        return result.Nodes;
    }

    /// <summary>
    /// Applies the folder the user picked in the tree. The "All" row releases the filter.
    /// </summary>
    private void SelectFolderNode(ArchiveFolderNodeViewModel node)
    {
        if (string.IsNullOrEmpty(node.Path))
        {
            ApplyFolderSelection(Folders.FirstOrDefault(static folder => folder.Path is null));
            return;
        }
        var existing = Folders.FirstOrDefault(folder =>
            string.Equals(folder.Path, node.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ArchiveFolderFilter(node.Path, node.Path);
            Folders.Insert(Folders.Count > 0 ? 1 : 0, existing);
        }
        ApplyFolderSelection(existing);
    }

    private void ApplyFolderSelection(ArchiveFolderFilter? folder)
    {
        if (folder is null || Equals(SelectedFolder, folder))
        {
            return;
        }
        SelectedFolder = folder;
        ApplyFilterCommand.Execute(null);
    }

    /// <summary>
    /// Rebuilds the folder filter list, keeping the active folder present. A folder filter narrows the
    /// result to that folder's contents, so the folder itself drops out of the rebuilt facet list
    /// whenever nothing is stored directly in it - which would otherwise release the filter silently.
    /// </summary>
    private void ApplyFolderFacets(IReadOnlyList<string> folders)
    {
        var previousPath = SelectedFolder?.Path;
        Folders.Clear();
        Folders.Add(new ArchiveFolderFilter(null, LocalizationManager.Get("All")));
        foreach (var folder in folders)
        {
            Folders.Add(new ArchiveFolderFilter(folder, folder));
        }
        if (!string.IsNullOrEmpty(previousPath)
            && !Folders.Any(folder => string.Equals(folder.Path, previousPath, StringComparison.OrdinalIgnoreCase)))
        {
            Folders.Insert(1, new ArchiveFolderFilter(previousPath, previousPath));
        }

        _selectedFolder = null;
        SelectedFolder = Folders.FirstOrDefault(folder =>
            string.Equals(folder.Path, previousPath, StringComparison.OrdinalIgnoreCase))
            ?? Folders[0];
    }

    private void StartCatalogueLoad(string sessionId)
    {
        var generation = Interlocked.Increment(ref _catalogueGeneration);
        var operation = new CancellationTokenSource();
        CancelOperation(Interlocked.Exchange(ref _catalogueOperation, operation));
        _ = LoadCatalogueLatestAsync(sessionId, generation, operation);
    }

    private async Task LoadCatalogueLatestAsync(
        string sessionId,
        long generation,
        CancellationTokenSource operation)
    {
        try
        {
            IsExtensionCatalogBusy = true;
            CatalogueStatus = LocalizationManager.Get("ExtensionCatalogLoading");
            var facetProgress = new Progress<ProgressUpdate>(update =>
            {
                if (generation == Volatile.Read(ref _catalogueGeneration))
                {
                    CatalogueStatus = update.Total > 0
                        ? LocalizationManager.Format("ExtensionCatalogProgress", update.Completed, update.Total)
                        : LocalizationManager.Get("ExtensionCatalogLoading");
                }
            });
            var facets = await _worker.SendAsync<ArchiveFacetsRequest, ArchiveFacetsResult>(
                WorkerProtocol.ArchiveFacets,
                generation,
                new ArchiveFacetsRequest(sessionId),
                operation.Token,
                facetProgress).ConfigureAwait(true);
            if (!CatalogueIsCurrent(sessionId, generation))
            {
                return;
            }
            ApplyGlobalExtensionFacets(facets.Extensions);
            IsExtensionCatalogBusy = false;

            IsNameIndexBusy = true;
            CatalogueStatus = LocalizationManager.Get("NameIndexLoading");
            var nameProgress = new Progress<ProgressUpdate>(update => ApplyNameIndexProgress(sessionId, generation, update));
            var names = await _worker.SendAsync<BuildNameIndexRequest, BuildNameIndexResult>(
                WorkerProtocol.BuildNameIndex,
                generation,
                new BuildNameIndexRequest(sessionId),
                operation.Token,
                nameProgress).ConfigureAwait(true);
            if (!CatalogueIsCurrent(sessionId, generation))
            {
                return;
            }

            var exactNameCount = names.ExactNameCount;
            var relatedNameCount = names.RelatedNameCount;
            var itemCount = names.ItemCount;
            var warning = names.Warning;
            SetCatalogueStatus(names.Available
                ? () => LocalizationManager.Format("NameIndexReady", exactNameCount, relatedNameCount, itemCount)
                : () => warning ?? LocalizationManager.Get("NameIndexUnavailable"));
            IsNameIndexBusy = false;
            if (names.Available)
            {
                ItemCatalogReady?.Invoke(this, new ItemCatalogReadyEventArgs(sessionId, names.ItemCount));
                await RefreshCurrentPageAfterNameIndexAsync(sessionId, generation, operation.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer archive session or application shutdown owns catalogue work.
        }
        catch (Exception exception)
        {
            if (CatalogueIsCurrent(sessionId, generation))
            {
                var reason = exception.Message;
                SetCatalogueStatus(() => LocalizationManager.Format("NameIndexFailed", reason));
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _catalogueGeneration))
            {
                IsExtensionCatalogBusy = false;
                IsNameIndexBusy = false;
            }
            Interlocked.CompareExchange(ref _catalogueOperation, null, operation);
            operation.Dispose();
        }
    }

    private void ApplyGlobalExtensionFacets(IReadOnlyList<ArchiveExtensionFacet> facets)
    {
        var canonical = CanonicalizeExtensionFacets(facets);
        if (!_globalExtensionFacets.SequenceEqual(canonical))
        {
            _globalExtensionFacets = canonical;
        }
        if (!HasItemScope)
        {
            ApplyActiveExtensionFacets(_globalExtensionFacets);
        }
    }

    private void ApplyActiveExtensionFacets(IReadOnlyList<ArchiveExtensionFacet> facets, bool force = false)
    {
        var canonical = CanonicalizeExtensionFacets(facets);
        if (!force && _activeExtensionFacets.SequenceEqual(canonical))
        {
            return;
        }
        _activeExtensionFacets = canonical;
        ExtensionChoices.Clear();
        ExtensionChoices.Add(ArchiveExtensionChoice.AllFiles(
            LocalizationManager.Get("AllFiles"),
            LocalizationManager.Get("ExtensionGroupAll"),
            canonical.Sum(static facet => facet.Count)));
        foreach (var facet in canonical)
        {
            ExtensionChoices.Add(new ArchiveExtensionChoice(
                facet.Extension,
                facet.Count,
                LocalizationManager.Get($"ExtensionGroup{facet.Category}"),
                facet.Category));
        }
        ExtensionChoicesView.Refresh();

        MostCommonExtensionChoices.Clear();
        MostCommonExtensionChoices.Add(ArchiveExtensionChoice.AllFiles(
            LocalizationManager.Get("AllFiles"),
            LocalizationManager.Get("ExtensionGroupAll"),
            canonical.Sum(static facet => facet.Count)));
        foreach (var facet in ArchiveExtensionFacetSelection.MostCommon(canonical))
        {
            MostCommonExtensionChoices.Add(new ArchiveExtensionChoice(
                facet.Extension,
                facet.Count,
                LocalizationManager.Get($"ExtensionGroup{facet.Category}"),
                facet.Category));
        }
    }

    private static IReadOnlyList<ArchiveExtensionFacet> CanonicalizeExtensionFacets(
        IEnumerable<ArchiveExtensionFacet> facets) => facets
        .Where(static facet => !string.IsNullOrWhiteSpace(facet.Extension))
        .Select(static facet => facet with { Extension = facet.Extension.Trim().ToLowerInvariant() })
        .OrderBy(static facet => facet.Category)
        .ThenByDescending(static facet => facet.Count)
        .ThenBy(static facet => facet.Extension, StringComparer.Ordinal)
        .ToArray();

    private void RebuildLocalizedOptions(ArchiveEntryRole? selectedRole = null)
    {
        _viewModes =
        [
            new LocalizedOption<ArchiveViewMode>(ArchiveViewMode.Folders, LocalizationManager.Get("FoldersView")),
            new LocalizedOption<ArchiveViewMode>(ArchiveViewMode.Flat, LocalizationManager.Get("FlatView")),
        ];
        _sortFields = Enum.GetValues<ArchiveSortField>()
            // The evidence is folded into the item name the grid shows, so it is no longer a sort
            // of its own; a persisted selection of it is migrated to the merged name.
            .Where(static field => field != ArchiveSortField.NameEvidence)
            .Select(field => new LocalizedOption<ArchiveSortField>(field, LocalizationManager.Get($"Sort{field}")))
            .ToArray();
        _collisionPolicies = Enum.GetValues<ExportCollisionPolicy>()
            .Select(policy => new LocalizedOption<ExportCollisionPolicy>(policy, LocalizationManager.Get($"Collision{policy}")))
            .ToArray();
        _manifestFormats = Enum.GetValues<ExportManifestFormat>()
            .Select(format => new LocalizedOption<ExportManifestFormat>(format, LocalizationManager.Get($"Manifest{format}")))
            .ToArray();
        _previewBackgrounds = Enum.GetValues<PreviewBackgroundChoice>()
            .Select(choice => new LocalizedOption<PreviewBackgroundChoice>(choice, LocalizationManager.Get($"PreviewBackground{choice}")))
            .ToArray();
        _roleFilters =
        [
            new ArchiveRoleFilter(null, LocalizationManager.Get("All")),
            .. Enum.GetValues<ArchiveEntryRole>()
                .Select(role => new ArchiveRoleFilter(role, LocalizationManager.Get($"Role{role}"))),
        ];
        _selectedRole = RoleFilters.First(option => option.Role == selectedRole);

        OnPropertyChanged(nameof(ViewModes));
        OnPropertyChanged(nameof(SortFields));
        OnPropertyChanged(nameof(CollisionPolicies));
        OnPropertyChanged(nameof(ManifestFormats));
        OnPropertyChanged(nameof(PreviewBackgrounds));
        OnPropertyChanged(nameof(RoleFilters));
        // Replacing a ComboBox ItemsSource can temporarily clear its selection.
        // Reassert the stable enum values after the localized options are visible
        // so a live language switch cannot leave a blank, invalid selection.
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(CollisionPolicy));
        OnPropertyChanged(nameof(ManifestFormat));
        OnPropertyChanged(nameof(PreviewBackgroundChoice));
        OnPropertyChanged(nameof(SelectedRole));
    }

    private void RefreshNavigationLabels()
    {
        if (Folders.Count > 0)
        {
            ApplyFolderFacets(Folders
                .Select(static folder => folder.Path)
                .OfType<string>()
                .ToArray());
        }

        foreach (var node in FolderTree)
        {
            node.RefreshLabel();
        }

        if (Categories.Count > 0)
        {
            ApplyCategoryFacets(Categories
                .Where(static category => category.Name is not null)
                .ToDictionary(static category => category.Name!, static category => category.Count, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Rebuilds the category navigator from a facet count per role, keeping whichever row the user
    /// had chosen. The leading "All" row is what releases the role filter the navigator applies, so
    /// the list is never a one-way funnel into the category that happens to be selected.
    /// </summary>
    private void ApplyCategoryFacets(IReadOnlyDictionary<string, long> facets)
    {
        var previousName = SelectedCategory?.Name;
        Categories.Clear();
        Categories.Add(new ArchiveCategoryCount(null, LocalizationManager.Get("All"), facets.Values.Sum()));
        foreach (var category in facets.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var roleLabel = Enum.TryParse<ArchiveEntryRole>(category.Key, out var parsedRole)
                ? LocalizationManager.Get($"Role{parsedRole}")
                : category.Key;
            Categories.Add(new ArchiveCategoryCount(category.Key, roleLabel, category.Value));
        }

        // Clearing the collection already pushed a null selection through the binding. Dropping the
        // backing field as well keeps the restore below from being swallowed as "no change" when the
        // rebuilt row compares equal to the old one, which would leave the list visually unselected.
        _selectedCategory = null;
        _restoringCategorySelection = true;
        try
        {
            SelectedCategory = Categories.FirstOrDefault(category =>
                string.Equals(category.Name, previousName, StringComparison.OrdinalIgnoreCase))
                ?? Categories[0];
        }
        finally
        {
            _restoringCategorySelection = false;
        }
    }

    private void RefreshExtensionLabels()
    {
        ApplyActiveExtensionFacets(_activeExtensionFacets, force: true);
    }

    private void ApplyNameIndexProgress(string sessionId, long generation, ProgressUpdate update)
    {
        if (!CatalogueIsCurrent(sessionId, generation))
        {
            return;
        }
        CatalogueStatus = update.Phase switch
        {
            "name_scan" when update.Total > 0 => LocalizationManager.Format("NameIndexScanProgress", update.Completed, update.Total),
            "name_extract" when !string.IsNullOrWhiteSpace(update.CurrentItem) => LocalizationManager.Format("NameIndexExtracting", update.CurrentItem),
            "name_build" => LocalizationManager.Get("NameIndexResolving"),
            "name_publish" => LocalizationManager.Get("NameIndexPublishing"),
            _ => LocalizationManager.Get("NameIndexLoading"),
        };
    }

    private async Task RefreshCurrentPageAfterNameIndexAsync(
        string sessionId,
        long catalogueGeneration,
        CancellationToken cancellationToken)
    {
        var foregroundGeneration = Volatile.Read(ref _foregroundGeneration);
        var request = _lastAppliedQuery;
        if (IsBusy
            || request is null
            || !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }
        var result = await _worker.SendAsync<ArchiveQuerySpec, ArchivePageResult>(
            WorkerProtocol.QueryArchive,
            foregroundGeneration,
            request,
            cancellationToken).ConfigureAwait(true);
        if (!CatalogueIsCurrent(sessionId, catalogueGeneration)
            || foregroundGeneration != Volatile.Read(ref _foregroundGeneration))
        {
            return;
        }

        _lastAppliedQuery = request;
        var selectedEntryId = SelectedEntry?.EntryId;
        _suppressPreviewSelection = true;
        try
        {
            Entries.Clear();
            foreach (var entry in result.Entries)
            {
                Entries.Add(entry);
            }
            SelectedEntry = selectedEntryId is { } entryId
                ? Entries.FirstOrDefault(entry => entry.EntryId == entryId)
                : null;
        }
        finally
        {
            _suppressPreviewSelection = false;
        }

        ApplyFolderFacets(result.Folders);

        ApplyCategoryFacets(result.Categories);
        RefreshFolderTreeForFilters();
        PageStart = result.PageStart;
        TotalMatches = result.TotalMatches;
        OnPropertyChanged(nameof(PageSummary));
        RaiseCommandStates();
    }

    private bool CatalogueIsCurrent(string sessionId, long generation) =>
        generation == Volatile.Read(ref _catalogueGeneration)
        && string.Equals(SessionId, sessionId, StringComparison.Ordinal);

    private void CancelCatalogue()
    {
        Interlocked.Increment(ref _catalogueGeneration);
        CancelOperation(Interlocked.Exchange(ref _catalogueOperation, null));
        IsExtensionCatalogBusy = false;
        IsNameIndexBusy = false;
        CatalogueStatus = string.Empty;
        _globalExtensionFacets = [];
        ApplyActiveExtensionFacets([], force: true);
        CancelFolderTree();
    }

    private void CancelFolderTree()
    {
        Interlocked.Increment(ref _folderTreeGeneration);
        CancelOperation(Interlocked.Exchange(ref _folderTreeOperation, null));
        _folderTreeContext = null;
        IsFolderTreeBusy = false;
        FolderTree.Clear();
    }

    private async Task LoadPreviewLatestAsync(ArchiveEntryDto? entry)
    {
        var sessionId = SessionId;
        var generation = Interlocked.Increment(ref _previewGeneration);
        using var operation = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _previewOperation, operation);
        CancelOperation(prior);
        if (entry is null || string.IsNullOrWhiteSpace(sessionId))
        {
            ClearPreview();
            Interlocked.CompareExchange(ref _previewOperation, null, operation);
            return;
        }

        try
        {
            var isNativeModel = NativeModelExtension(entry.Extension);
            if (!isNativeModel)
            {
                await Task.Delay(90, operation.Token).ConfigureAwait(true);
            }
            if (generation != Volatile.Read(ref _previewGeneration))
            {
                return;
            }
            IsPreviewBusy = true;
            PreviewProgressText = LocalizationManager.Get("PreviewProgressPreparing");
            var progress = new Progress<ProgressUpdate>(update => ApplyPreviewProgress(generation, update));
            var result = await _worker.SendAsync<PreviewRequest, PreviewResult>(
                WorkerProtocol.Preview,
                generation,
                new PreviewRequest(
                    sessionId,
                    entry.EntryId,
                    IncludeModelTextures: isNativeModel && ShowModelTextures),
                operation.Token,
                progress).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _previewGeneration))
            {
                return;
            }

            await PresentPreviewAsync(result, generation, operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer selection owns the preview lane.
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _previewGeneration))
            {
                ClearPreview();
                PreviewTitle = entry.Name;
                PreviewText = exception.Message;
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _previewGeneration))
            {
                IsPreviewBusy = false;
                PreviewProgressText = string.Empty;
            }
            Interlocked.CompareExchange(ref _previewOperation, null, operation);
        }
    }

    private void ApplyPreviewProgress(long generation, ProgressUpdate update)
    {
        if (generation != Volatile.Read(ref _previewGeneration))
        {
            return;
        }
        IsPreviewBusy = true;
        PreviewProgressText = update.Phase switch
        {
            "model_preview_native" => LocalizationManager.Get("PreviewProgressNative"),
            "model_preview_adapt" => LocalizationManager.Get("PreviewProgressAdapting"),
            // Kept in step with NativeTexturePreviewService.DecodePhase by a focused test; the
            // worker protocol crosses a project boundary, so phases are literals on both sides.
            "texture_preview_decode" =>
                LocalizationManager.Format("PreviewProgressDecodingTexture", update.Completed, update.Total),
            _ => string.IsNullOrWhiteSpace(update.CurrentItem) ? update.Phase : update.CurrentItem,
        };
    }

    private async Task PresentPreviewAsync(PreviewResult result, long generation, CancellationToken cancellationToken)
    {
        BitmapSource? image = null;
        var text = string.IsNullOrWhiteSpace(result.Text) ? result.Metadata : result.Text;
        var warnings = result.Warnings?.ToList() ?? [];
        if (result.Kind == PreviewKind.Image && !string.IsNullOrWhiteSpace(result.ArtifactPath))
        {
            try
            {
                image = await PreviewImageLoader.LoadFrozenAsync(result.ArtifactPath, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            {
                warnings.Add($"Image decoder: {exception.Message}");
            }
        }
        if (result.Kind == PreviewKind.Text && !string.IsNullOrWhiteSpace(result.ArtifactPath))
        {
            try
            {
                text = await PreviewTextLoader.LoadAsync(result.ArtifactPath, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException)
            {
                warnings.Add($"Text decoder: {exception.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _previewGeneration))
        {
            return;
        }

        PreviewTitle = result.Title;
        PreviewMetadata = result.Metadata;
        PreviewText = text;
        PreviewSyntax = result.Syntax ?? string.Empty;
        PreviewWarnings = string.Join(Environment.NewLine, warnings);
        PreviewImage = image;
        PreviewMediaSource = result.Kind is PreviewKind.Audio or PreviewKind.Video && !string.IsNullOrWhiteSpace(result.ArtifactPath)
            ? new Uri(result.ArtifactPath, UriKind.Absolute)
            : null;
        ModelPreviewPackagePath = result.Kind == PreviewKind.Model && !string.IsNullOrWhiteSpace(result.ArtifactPath)
            ? result.ArtifactPath
            : null;
        PreviewKind = result.Kind;
        OnPropertyChanged(nameof(IsImagePreview));
        OnPropertyChanged(nameof(IsMediaPreview));
        OnPropertyChanged(nameof(IsModelPreview));
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(CanOpenPreviewSettings));
    }

    private void ClearPreview()
    {
        PreviewTitle = LocalizationManager.Get("Preview");
        PreviewMetadata = string.Empty;
        PreviewText = LocalizationManager.Get("PreviewEmpty");
        PreviewSyntax = string.Empty;
        PreviewWarnings = string.Empty;
        PreviewImage = null;
        PreviewMediaSource = null;
        ModelPreviewPackagePath = null;
        PreviewKind = PreviewKind.Metadata;
        IsPreviewBusy = false;
        PreviewProgressText = string.Empty;
        OnPropertyChanged(nameof(IsImagePreview));
        OnPropertyChanged(nameof(IsMediaPreview));
        OnPropertyChanged(nameof(IsModelPreview));
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(CanOpenPreviewSettings));
    }

    private void CancelPreviewAndClear()
    {
        Interlocked.Increment(ref _previewGeneration);
        var operation = Interlocked.Exchange(ref _previewOperation, null);
        CancelOperation(operation);
        ClearPreview();
    }

    private void ResetModelPreviewCameraInput()
    {
        ModelPreviewOrbitSensitivity = 0.22;
        ModelPreviewPanSensitivity = 0.60;
    }

    private static ArchiveEntryDto? SelectItemPreviewEntry(IEnumerable<ArchiveEntryDto> entries)
    {
        ArchiveEntryDto? first = null;
        ArchiveEntryDto? firstPreviewable = null;
        foreach (var entry in entries)
        {
            first ??= entry;
            if (entry.IsPreviewable)
            {
                firstPreviewable ??= entry;
            }
            if (NativeModelExtension(entry.Extension))
            {
                return entry;
            }
        }
        return firstPreviewable ?? first;
    }

    private static bool NativeModelExtension(string extension) =>
        extension.Equals(".pac", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pam", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pamlod", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pat", StringComparison.OrdinalIgnoreCase);

    private async Task ExportSelectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        var entryIds = _selectedEntryIds.Count > 0
            ? _selectedEntryIds
            : SelectedEntry is not null
                ? [SelectedEntry.EntryId]
                : [];
        if (entryIds.Count == 0)
        {
            return;
        }

        var focusedEntry = entryIds.Count == 1
            && SelectedEntry is { } selected
            && selected.EntryId == entryIds[0]
                ? selected
                : null;
        var supportsModelExport = focusedEntry?.Extension.ToLowerInvariant() is ".pac" or ".pam" or ".pamlod";
        var selection = _chooseExportSelection(entryIds.Count, supportsModelExport, focusedEntry is not null);
        if (selection is null)
        {
            return;
        }

        if (selection.Mode == ExportSelectionMode.Family)
        {
            await AssociatedAssets.ExportCurrentFamilyAsync(cancellationToken).ConfigureAwait(true);
            return;
        }
        if (selection.Mode == ExportSelectionMode.FilesOnly
            && selection.Kind != ExportKind.RawEntries
            && focusedEntry is not null)
        {
            await ExportSelectedModelAsync(focusedEntry, selection.Kind, cancellationToken).ConfigureAwait(true);
            return;
        }
        if (selection.Mode == ExportSelectionMode.FilesOnly && focusedEntry is not null)
        {
            await ExportSelectedRawFileAsync(focusedEntry, cancellationToken).ConfigureAwait(true);
            return;
        }

        var destination = PickExportFolder();
        if (destination is null)
        {
            return;
        }

        await RunExportAsync(
            entryIds,
            destination,
            ExportKind.RawEntries,
            cancellationToken,
            pathLayout: selection.Mode == ExportSelectionMode.FilesOnly
                ? ExportPathLayout.FilesOnly
                : ExportPathLayout.PreserveStructure).ConfigureAwait(true);
    }

    private async Task ExportSelectedRawFileAsync(ArchiveEntryDto selectedEntry, CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.Get("ExportSelected"),
            FileName = selectedEntry.Name,
            DefaultExt = selectedEntry.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = $"Original archive file (*{selectedEntry.Extension})|*{selectedEntry.Extension}",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var destination = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }
        await RunExportAsync(
            [selectedEntry.EntryId],
            destination,
            ExportKind.RawEntries,
            cancellationToken,
            singleOutputPath: dialog.FileName,
            manifestFormat: ExportManifestFormat.None,
            pathLayout: ExportPathLayout.FilesOnly).ConfigureAwait(true);
    }

    private async Task ExportSelectedModelAsync(
        ArchiveEntryDto selectedEntry,
        ExportKind exportKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(selectedEntry.Name);
        var (extension, filter) = exportKind switch
        {
            ExportKind.Glb => (".glb", "glTF Binary (*.glb)|*.glb"),
            ExportKind.Obj => (".obj", "Wavefront OBJ (*.obj)|*.obj"),
            ExportKind.Fbx => (".fbx", "Autodesk FBX (*.fbx)|*.fbx"),
            _ => throw new InvalidDataException("The selected model export format is not supported."),
        };
        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.Get("ExportSelected"),
            FileName = $"{baseName}{extension}",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = filter,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var outputPath = Path.ChangeExtension(dialog.FileName, extension);
        var destination = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        await RunExportAsync(
            [selectedEntry.EntryId],
            destination,
            exportKind,
            cancellationToken,
            singleOutputPath: outputPath,
            manifestFormat: ExportManifestFormat.None,
            pathLayout: ExportPathLayout.FilesOnly).ConfigureAwait(true);
    }

    private async Task ExportFilteredAsync(CancellationToken cancellationToken)
    {
        var destination = PickExportFolder();
        if (destination is null)
        {
            return;
        }

        await RunExportAsync([], destination, ExportKind.FilteredEntries, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Puts the selected entry's file name on the clipboard. The clipboard is owned by whichever
    /// process last set it and can be held open by another one, so a refusal is reported rather than
    /// thrown at the user as a crash.
    /// </summary>
    private void CopySelectedFileName()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(entry.Name);
            _setShellStatus(LocalizationManager.Format("CopiedFileName", entry.Name));
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _setShellStatus(exception.Message);
        }
    }

    private async Task ExportFolderAsync(CancellationToken cancellationToken)
    {
        var folderPath = SelectedFolder?.Path;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }
        var destination = PickExportFolder();
        if (destination is null)
        {
            return;
        }
        await RunExportAsync(
            [],
            destination,
            ExportKind.FolderTree,
            cancellationToken,
            folderPath: folderPath).ConfigureAwait(true);
    }

    private async Task ExportAssociatedEntriesAsync(
        IReadOnlyList<long> entryIds,
        CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0)
        {
            return;
        }
        var destination = PickExportFolder();
        if (destination is null)
        {
            return;
        }
        await RunExportAsync(entryIds, destination, ExportKind.RawEntries, cancellationToken).ConfigureAwait(true);
    }

    private async Task RunExportAsync(
        IReadOnlyList<long> entryIds,
        string destination,
        ExportKind kind,
        CancellationToken cancellationToken,
        string? singleOutputPath = null,
        ExportManifestFormat? manifestFormat = null,
        string? folderPath = null,
        ExportPathLayout pathLayout = ExportPathLayout.PreserveStructure)
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        using var operation = BeginForegroundOperation(cancellationToken);
        var generation = Interlocked.Increment(ref _foregroundGeneration);
        try
        {
            SetOperationProgress(LocalizationManager.Get("ProgressExporting"));
            var progress = new Progress<ProgressUpdate>(update => ApplyForegroundProgress(generation, update));
            var result = await _worker.SendAsync<ExportPlanRequest, ExportPlanResult>(
                WorkerProtocol.Export,
                generation,
                new ExportPlanRequest(
                    SessionId,
                    kind,
                    destination,
                    entryIds,
                    null,
                    CollisionPolicy: CollisionPolicy,
                    ManifestFormat: manifestFormat ?? ManifestFormat,
                    SingleOutputPath: singleOutputPath,
                    FolderPath: folderPath,
                    PathLayout: pathLayout),
                operation.Token,
                progress).ConfigureAwait(true);
            if (generation == Volatile.Read(ref _foregroundGeneration))
            {
                _setShellStatus(LocalizationManager.Format("ExportSummary", result.Exported, result.Skipped, result.Failed));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _setShellStatus(exception.Message);
        }
        finally
        {
            EndForegroundOperation(operation);
        }
    }

    private static string? PickExportFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Get("ExportSelected"),
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private CancellationTokenSource BeginForegroundOperation(CancellationToken commandToken)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        var prior = Interlocked.Exchange(ref _foregroundOperation, operation);
        CancelOperation(prior);
        IsBusy = true;
        return operation;
    }

    private CancellationTokenSource BeginEnvironmentOperation(CancellationToken commandToken)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        var prior = Interlocked.Exchange(ref _environmentOperation, operation);
        CancelOperation(prior);
        IsEnvironmentBusy = true;
        return operation;
    }

    private bool EnvironmentIsCurrent(long generation) =>
        generation == Volatile.Read(ref _environmentGeneration);

    private void EndEnvironmentOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _environmentOperation, null, operation), operation))
        {
            IsEnvironmentBusy = false;
        }
    }

    private void CancelEnvironment()
    {
        Interlocked.Increment(ref _environmentGeneration);
        CancelOperation(Interlocked.Exchange(ref _environmentOperation, null));
        IsEnvironmentBusy = false;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        }
        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyForegroundProgress(long generation, ProgressUpdate update)
    {
        if (generation != Volatile.Read(ref _foregroundGeneration) || !IsBusy)
        {
            return;
        }

        var phase = update.Phase switch
        {
            "discover" => LocalizationManager.Get("ProgressDiscovering"),
            "fingerprint" => LocalizationManager.Get("ProgressFingerprinting"),
            "index_cache" => LocalizationManager.Get("ProgressOpeningIndex"),
            "index_build" => LocalizationManager.Get("ProgressBuildingIndex"),
            "index_parse" => LocalizationManager.Get("ProgressParsingPackages"),
            "index_sort" => LocalizationManager.Get("ProgressSortingIndex"),
            "index_write" => LocalizationManager.Get("ProgressWritingIndex"),
            "index_publish" => LocalizationManager.Get("ProgressPublishingIndex"),
            "lookup_index" or "lookup_index_build" or "extension_index" or "extension_index_build" => LocalizationManager.Get("ProgressBuildingIndex"),
            "validate" => LocalizationManager.Get("ProgressValidating"),
            "export" => LocalizationManager.Get("ProgressExporting"),
            "mesh_export_prepare" => LocalizationManager.Get("ProgressPreparingMesh"),
            "mesh_export_write" => LocalizationManager.Get("ProgressWritingMesh"),
            "complete" => LocalizationManager.Get("ProgressFinishing"),
            _ => LocalizationManager.Get("ProgressWorking"),
        };
        OperationProgressText = string.IsNullOrWhiteSpace(update.CurrentItem) || update.CurrentItem == "complete"
            ? phase
            : $"{phase}  -  {update.CurrentItem}";
        IsOperationProgressIndeterminate = update.Total <= 0;
        OperationProgressPercent = update.Total <= 0
            ? 0
            : Math.Clamp(update.Completed * 100.0 / update.Total, 0, 100);
        OperationProgressDetail = update.Total <= 0
            ? string.Empty
            : $"{Math.Min(update.Completed, update.Total):N0} / {update.Total:N0}  ({OperationProgressPercent:N0}%)";
    }

    private void SetOperationProgress(string text)
    {
        OperationProgressText = text;
        OperationProgressDetail = string.Empty;
        OperationProgressPercent = 0;
        IsOperationProgressIndeterminate = true;
    }

    private void EndForegroundOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _foregroundOperation, null, operation), operation))
        {
            IsBusy = false;
        }
    }

    private void CancelForeground()
    {
        Interlocked.Increment(ref _foregroundGeneration);
        CancelOperation(Interlocked.Exchange(ref _foregroundOperation, null));
        IsBusy = false;
    }

    private static void CancelOperation(CancellationTokenSource? operation)
    {
        if (operation is null)
        {
            return;
        }
        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion may dispose a latest-wins operation immediately after ownership is exchanged.
        }
    }

    private void RaiseCommandStates()
    {
        BrowseCommand.RaiseCanExecuteChanged();
        DetectGameCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        ApplyFilterCommand.RaiseCanExecuteChanged();
        ClearItemScopeCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportSelectedCommand.RaiseCanExecuteChanged();
        ExportFamilyCommand.RaiseCanExecuteChanged();
        CopyFileNameCommand.RaiseCanExecuteChanged();
        ExportFolderCommand.RaiseCanExecuteChanged();
        ExportFilteredCommand.RaiseCanExecuteChanged();
        AssociatedAssets.RaiseCommandStates();
    }

    private bool CanExportSelectedEntries() =>
        !string.IsNullOrWhiteSpace(SessionId)
        && (_selectedEntryIds.Count > 0 || SelectedEntry is not null);
}

public sealed record ItemCatalogReadyEventArgs(string SessionId, long ItemCount);

public sealed record ArchiveRoleFilter(ArchiveEntryRole? Role, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One row of the category navigator. A null <paramref name="Name"/> is the "All" row, which carries
/// the combined count and releases the role filter rather than naming a role.
/// </summary>
public sealed record ArchiveCategoryCount(string? Name, string Label, long Count)
{
    public override string ToString() => $"{Label} ({Count:N0})";
}

public sealed record ArchiveFolderFilter(string? Path, string Label)
{
    public override string ToString() => Label;
}

public sealed record ArchiveExtensionChoice(
    string Extension,
    long Count,
    string Group,
    ArchiveExtensionCategory? Category = null,
    string? DisplayLabel = null)
{
    public string Label => DisplayLabel ?? Extension;

    public static ArchiveExtensionChoice AllFiles(string label, string group, long count = 0) => new(string.Empty, count, group, null, label);

    public override string ToString() => Extension;
}
