using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.App.ViewModels;

public sealed class CharacterFinderViewModel : ObservableObject
{
    private const int PageSize = 72;
    private readonly IWorkerRequestClient _worker;
    private readonly Func<string?> _getSession;
    private readonly Func<string, string, bool, CancellationToken, Task<bool>> _showScope;
    private CancellationTokenSource? _activation, _search, _selection, _thumbnails, _debounce;
    private long _searchGeneration, _selectionGeneration;
    private string? _session, _relatedKey;
    private bool _active, _suppress, _isBusy;
    private int _pageStart, _total;
    private string _query = "", _view = "assets", _tab = "bodies";
    private string _role = "", _source = "", _family = "", _resolution = "";
    private string _status = "", _previewStatus = "", _warnings = "";
    private string _previewTitle = "";
    private string? _package;
    private CharacterFinderRow? _selectedRow;
    private CharacterCatalogDetailResult? _detail;
    private IReadOnlyList<CharacterCatalogFacet> _facets = [];

    public CharacterFinderViewModel(IWorkerRequestClient worker, Func<string?> getSession,
        Func<string, string, bool, CancellationToken, Task<bool>> showScope, CharacterFinderSettings? settings = null)
    {
        _worker = worker; _getSession = getSession; _showScope = showScope;
        if (settings is not null)
        {
            _query = settings.Query; _view = settings.View is "appearances" ? "appearances" : "assets";
            _tab = settings.Tab is "faces" ? "faces" : "bodies";
            _role = settings.Role; _source = settings.SourceGroup; _family = settings.BodyFamily; _resolution = settings.Resolution;
        }
        WindowWidth = double.IsFinite(settings?.Width ?? 0) ? Math.Clamp(settings?.Width ?? 1320, 1040, 2400) : 1320;
        WindowHeight = double.IsFinite(settings?.Height ?? 0) ? Math.Clamp(settings?.Height ?? 840, 680, 1600) : 840;
        SearchCommand = new AsyncCommand(token => { Cancel(ref _debounce); return SearchAsync(true, token); }, () => IsAvailable);
        PreviousCommand = new AsyncCommand(token => MovePageAsync(-PageSize, token), () => IsAvailable && _pageStart > 0 && !IsBusy);
        NextCommand = new AsyncCommand(token => MovePageAsync(PageSize, token), () => IsAvailable && _pageStart + PageSize < _total && !IsBusy);
        ClearCommand = new RelayCommand(ClearFilters, () => IsAvailable);
        CancelCommand = new RelayCommand(CancelPending, () => IsBusy);
        ShowExactCommand = new AsyncCommand(token => ShowScopeAsync(false, token), () => CanScope);
        ShowRelatedFilesCommand = new AsyncCommand(token => ShowScopeAsync(true, token), () => CanScope);
        ShowRelatedRowsCommand = new RelayCommand(ShowRelatedRows, () => _detail?.Related.Count > 0 && !IsBusy);
        RefreshLocalization();
    }

    public ObservableCollection<CharacterFinderRow> Rows { get; } = [];
    public ObservableCollection<FinderOption> Roles { get; } = [];
    public ObservableCollection<FinderOption> Sources { get; } = [];
    public ObservableCollection<FinderOption> Families { get; } = [];
    public ObservableCollection<FinderOption> Resolutions { get; } = [];
    public ObservableCollection<CharacterCatalogRow> RelatedRows { get; } = [];
    public IReadOnlyList<FinderOption> Views => [new("assets", L("CharacterUniqueAssets")), new("appearances", L("CharacterAppearances"))];
    public IReadOnlyList<FinderOption> Tabs => [new("bodies", L("CharacterBodies")), new("faces", L("CharacterFaces"))];
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand PreviousCommand { get; }
    public AsyncCommand NextCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand ShowExactCommand { get; }
    public AsyncCommand ShowRelatedFilesCommand { get; }
    public RelayCommand ShowRelatedRowsCommand { get; }
    public event EventHandler? CloseRequested;
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_getSession());
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    public double WindowWidth { get; private set; }
    public double WindowHeight { get; private set; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string PreviewStatus { get => _previewStatus; private set => SetProperty(ref _previewStatus, value); }
    public string PreviewTitle { get => _previewTitle; private set => SetProperty(ref _previewTitle, value); }
    public string Warnings { get => _warnings; private set => SetProperty(ref _warnings, value); }
    public string? PackagePath { get => _package; private set => SetProperty(ref _package, value); }
    public string Query { get => _query; set { if (SetProperty(ref _query, value ?? "")) ScheduleSearch(); } }
    public string View { get => _view; set { if (SetProperty(ref _view, value ?? "assets")) ScheduleSearch(); } }
    public string Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value ?? "bodies")) return;
            _role = ""; OnPropertyChanged(nameof(Role));
            RebuildFacets(); ScheduleSearch();
        }
    }
    public string Role { get => _role; set { if (SetProperty(ref _role, value ?? "")) ScheduleSearch(); } }
    public string SourceGroup { get => _source; set { if (SetProperty(ref _source, value ?? "")) ScheduleSearch(); } }
    public string BodyFamily { get => _family; set { if (SetProperty(ref _family, value ?? "")) ScheduleSearch(); } }
    public string Resolution { get => _resolution; set { if (SetProperty(ref _resolution, value ?? "")) ScheduleSearch(); } }
    public CharacterFinderRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            Cancel(ref _selection); Interlocked.Increment(ref _selectionGeneration);
            _detail = null; RelatedRows.Clear(); OnPropertyChanged(nameof(Details)); RaiseCommands();
            if (value is null) { PackagePath = null; PreviewStatus = ""; PreviewTitle = ""; }
            else if (_active) _ = LoadSelectionAsync(value);
        }
    }
    public string PageSummary => _total == 0 ? L("ItemFinderNoMatches")
        : LocalizationManager.Format("ItemFinderPageSummary", _pageStart + 1, Math.Min(_total, _pageStart + PageSize), _total);
    public string Details => _detail is null ? "" : string.Join(Environment.NewLine,
        _detail.Characters.Select(owner => $"{owner.DisplayName}  {owner.InternalName}  #{owner.CharacterId}")
            .Concat(_detail.Components.Select(component => $"{CharacterFinderLabels.Value(component.Role)}: {component.Name} ({component.Resolution})"))
            .Concat(_detail.Evidence)
            .Append(_detail.Truncated ? L("CharacterDetailsBounded") : ""));
    public bool HasRelatedFilter => _relatedKey is not null;
    private bool CanScope => _active && !IsBusy && SelectedRow is not null && _detail is not null;

    public async Task ActivateAsync(CancellationToken token)
    {
        if (_active) return;
        _active = true; _session = _getSession();
        _activation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await SearchAsync(true, _activation.Token).ConfigureAwait(true);
    }

    public void Deactivate()
    {
        _active = false; CancelPending();
        var activation = _activation; Cancel(ref _activation); activation?.Dispose(); PackagePath = null;
    }

    public void NotifyArchiveSessionChanged()
    {
        Deactivate(); Rows.Clear(); SelectedRow = null; _session = _getSession(); _relatedKey = null;
        _pageStart = _total = 0; _facets = []; RebuildFacets(); Status = L("CharacterFinderReady");
        OnPropertyChanged(nameof(IsAvailable)); OnPropertyChanged(nameof(PageSummary)); RaiseCommands();
    }

    public void RequestShutdown() => Deactivate();
    public CharacterFinderSettings CaptureSettings() => new(Query, View, Tab, Role, SourceGroup, BodyFamily, Resolution, WindowWidth, WindowHeight);
    public void UpdateWindowSize(double width, double height)
    {
        if (double.IsFinite(width)) WindowWidth = Math.Clamp(width, 1040, 2400);
        if (double.IsFinite(height)) WindowHeight = Math.Clamp(height, 680, 1600);
    }

    public void RefreshLocalization()
    {
        var previous = _suppress; _suppress = true;
        var view = View; var tab = Tab; var role = Role;
        OnPropertyChanged(nameof(Views)); OnPropertyChanged(nameof(Tabs));
        View = view; Tab = tab; Role = role; RebuildFacets(); _suppress = previous;
        OnPropertyChanged(nameof(PageSummary)); OnPropertyChanged(nameof(Details));
        foreach (var row in Rows) row.RefreshLocalization();
        if (!IsBusy) Status = PageSummary;
    }

    private void ScheduleSearch()
    {
        if (_suppress || !_active) return;
        CancelPending();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_activation?.Token ?? CancellationToken.None);
        _debounce = operation;
        _ = DebouncedSearchAsync(operation);
    }

    private async Task DebouncedSearchAsync(CancellationTokenSource operation)
    {
        try
        {
            await Task.Delay(220, operation.Token).ConfigureAwait(true);
            if (ReferenceEquals(_debounce, operation)) await SearchAsync(true, operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        finally { Release(ref _debounce, operation); }
    }

    private async Task SearchAsync(bool resetPage, CancellationToken token)
    {
        var session = _session;
        if (!_active || string.IsNullOrWhiteSpace(session)) { Status = L("ItemFinderOpenArchive"); return; }
        Cancel(ref _search); Cancel(ref _selection); Cancel(ref _thumbnails);
        Interlocked.Increment(ref _selectionGeneration);
        if (resetPage) _pageStart = 0;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _activation?.Token ?? CancellationToken.None);
        _search = operation;
        var generation = Interlocked.Increment(ref _searchGeneration);
        IsBusy = true; Status = L("CharacterFinderLoading");
        try
        {
            var progress = new Progress<ProgressUpdate>(update =>
            {
                if (Current(session, generation)) Status = L("CharacterFinderLoading") + (update.Total > 0 ? $" {update.Completed:N0} / {update.Total:N0}" : "");
            });
            var result = await _worker.SendAsync<CharacterCatalogSearchRequest, CharacterCatalogSearchResult>(
                WorkerProtocol.SearchCharacterCatalog, generation,
                new(session, Query, View, Tab, Role, SourceGroup, BodyFamily, Resolution, _relatedKey, _pageStart, PageSize),
                operation.Token, progress).ConfigureAwait(true);
            if (!Current(session, generation)) return;
            _facets = result.Facets; RebuildFacets();
            Rows.Clear();
            foreach (var row in result.Rows) Rows.Add(new(row));
            _pageStart = result.PageStart; _total = result.TotalMatches;
            Warnings = string.Join(Environment.NewLine, result.Warnings);
            OnPropertyChanged(nameof(PageSummary)); Status = PageSummary; IsBusy = false;
            SelectedRow = Rows.FirstOrDefault();
            var thumbnails = CancellationTokenSource.CreateLinkedTokenSource(_activation?.Token ?? CancellationToken.None);
            _thumbnails = thumbnails;
            _ = LoadThumbnailsAsync(Rows.ToArray(), session, generation, thumbnails);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current(session, generation)) Status = error.Message; }
        finally
        {
            if (Current(session, generation)) IsBusy = false;
            Release(ref _search, operation);
        }
    }

    private async Task LoadSelectionAsync(CharacterFinderRow row)
    {
        var session = _session!;
        var generation = Interlocked.Increment(ref _selectionGeneration);
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_activation?.Token ?? CancellationToken.None);
        _selection = operation;
        bool CurrentSelection() => _active && _session == session && generation == _selectionGeneration && ReferenceEquals(SelectedRow, row);
        PreviewStatus = L("CharacterPreviewLoading");
        try
        {
            await Task.Delay(100, operation.Token).ConfigureAwait(true);
            var detail = await _worker.SendAsync<CharacterCatalogDetailRequest, CharacterCatalogDetailResult>(
                WorkerProtocol.CharacterCatalogDetail, generation, new(session, row.Source.Key), operation.Token).ConfigureAwait(true);
            if (!CurrentSelection()) return;
            _detail = detail; RelatedRows.Clear();
            foreach (var related in detail.Related) RelatedRows.Add(related);
            OnPropertyChanged(nameof(Details)); RaiseCommands();
            var preview = await _worker.SendAsync<CharacterPreviewRequest, CharacterPreviewResult>(
                WorkerProtocol.PreviewCharacter, generation, new(session, row.Source.Key), operation.Token).ConfigureAwait(true);
            if (!CurrentSelection()) return;
            PackagePath = preview.PackagePath;
            PreviewTitle = row.Label;
            PreviewStatus = string.Join(Environment.NewLine, preview.Warnings);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (CurrentSelection()) PreviewStatus = error.Message; }
        finally { Release(ref _selection, operation); }
    }

    private async Task LoadThumbnailsAsync(CharacterFinderRow[] rows, string session, long generation, CancellationTokenSource operation)
    {
        try
        {
            var eligible = rows.Where(row => row.Source.ModelCount > 0 && row.Source.Resolution != "ambiguous").ToArray();
            for (var offset = 0; offset < eligible.Length; offset += 4)
            {
                operation.Token.ThrowIfCancellationRequested();
                var batch = eligible.Skip(offset).Take(4).ToDictionary(row => row.Source.Key);
                var result = await _worker.SendAsync<CharacterThumbnailRequest, CharacterThumbnailResult>(
                    WorkerProtocol.CharacterThumbnails, generation, new(session, batch.Keys.ToArray()), operation.Token).ConfigureAwait(true);
                if (!Current(session, generation)) return;
                foreach (var thumbnail in result.Items)
                {
                    if (!batch.TryGetValue(thumbnail.Key, out var row)) continue;
                    if (thumbnail.PngPath is not null)
                    {
                        var bitmap = await PreviewImageLoader.LoadFrozenAsync(thumbnail.PngPath, operation.Token).ConfigureAwait(true);
                        if (!Current(session, generation)) return;
                        row.Thumbnail = bitmap;
                    }
                    else row.ThumbnailWarning = thumbnail.Warning ?? "";
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current(session, generation)) Warnings = error.Message; }
        finally { Release(ref _thumbnails, operation); }
    }

    private async Task MovePageAsync(int delta, CancellationToken token)
    {
        _pageStart = Math.Max(0, _pageStart + delta);
        await SearchAsync(false, token).ConfigureAwait(true);
    }

    private void ClearFilters()
    {
        _suppress = true;
        Query = ""; Role = ""; SourceGroup = ""; BodyFamily = ""; Resolution = ""; _relatedKey = null;
        _suppress = false; OnPropertyChanged(nameof(HasRelatedFilter)); ScheduleSearch();
    }

    private void ShowRelatedRows()
    {
        if (_detail is null || SelectedRow is null) return;
        var key = SelectedRow.Source.Key;
        _suppress = true;
        View = View == "assets" ? "appearances" : "assets";
        Query = ""; Role = ""; SourceGroup = ""; BodyFamily = ""; Resolution = "";
        _relatedKey = key;
        _suppress = false; OnPropertyChanged(nameof(HasRelatedFilter)); ScheduleSearch();
    }

    private async Task ShowScopeAsync(bool related, CancellationToken token)
    {
        var row = SelectedRow;
        if (row is null || !_active) return;
        var session = _session;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _activation?.Token ?? CancellationToken.None);
        try
        {
            IsBusy = true;
            if (await _showScope(row.Source.Key, row.Source.Label, related, linked.Token).ConfigureAwait(true)
                && _active && _session == session) CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_active && _session == session) Status = error.Message; }
        finally { if (_active && _session == session) IsBusy = false; }
    }

    private void RebuildFacets()
    {
        var previous = _suppress; _suppress = true;
        // WPF clears SelectedValue during a collection rebuild; retain canonical values explicitly.
        var role = Role; var source = SourceGroup; var family = BodyFamily; var resolution = Resolution;
        Fill(Roles, "role", Tab == "faces" ? CharacterFinderLabels.Value("head") : L("CharacterAllRoles"), role);
        Fill(Sources, "source_group", L("CharacterAllTypes"), source);
        Fill(Families, "body_family", L("CharacterAllFamilies"), family);
        Fill(Resolutions, "resolution", L("CharacterAllStates"), resolution);
        Role = role; SourceGroup = source; BodyFamily = family; Resolution = resolution;
        _suppress = previous;
    }

    private void Fill(ObservableCollection<FinderOption> options, string field, string all, string current)
    {
        options.Clear(); options.Add(new("", all));
        foreach (var facet in _facets.Where(facet => facet.Field == field))
            options.Add(new(facet.Value, $"{CharacterFinderLabels.Value(facet.Value)} ({facet.Count:N0})"));
        if (current.Length > 0 && !options.Any(option => option.Value == current)) options.Add(new(current, CharacterFinderLabels.Value(current)));
    }

    private bool Current(string session, long generation) => _active && _session == session && generation == _searchGeneration;
    private void CancelPending()
    {
        Interlocked.Increment(ref _searchGeneration); Interlocked.Increment(ref _selectionGeneration);
        Cancel(ref _search); Cancel(ref _selection); Cancel(ref _thumbnails); Cancel(ref _debounce);
        ShowExactCommand.Cancel(); ShowRelatedFilesCommand.Cancel();
        IsBusy = false;
    }
    private void RaiseCommands()
    {
        SearchCommand.RaiseCanExecuteChanged(); PreviousCommand.RaiseCanExecuteChanged(); NextCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); ShowExactCommand.RaiseCanExecuteChanged();
        ShowRelatedFilesCommand.RaiseCanExecuteChanged(); ShowRelatedRowsCommand.RaiseCanExecuteChanged();
    }
    private static void Cancel(ref CancellationTokenSource? field)
    {
        var operation = Interlocked.Exchange(ref field, null);
        if (operation is null) return;
        try { operation.Cancel(); } catch (ObjectDisposedException) { }
        // Each operation disposes its source after the awaited work unwinds.
    }
    private static void Release(ref CancellationTokenSource? field, CancellationTokenSource operation)
    {
        Interlocked.CompareExchange(ref field, null, operation); operation.Dispose();
    }
    private static string L(string key) => LocalizationManager.Get(key);
}

public sealed record FinderOption(string Value, string Label);

public sealed class CharacterFinderRow(CharacterCatalogRow source) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private string _thumbnailWarning = "";
    public CharacterCatalogRow Source { get; } = source;
    public string Label => Source.Label;
    public string Subtitle => CharacterFinderLabels.Value(Source.Role) + " · " + CharacterFinderLabels.Value(Source.Resolution);
    public BitmapSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
    public string ThumbnailWarning { get => _thumbnailWarning; set => SetProperty(ref _thumbnailWarning, value); }
    public void RefreshLocalization() => OnPropertyChanged(nameof(Subtitle));
}
