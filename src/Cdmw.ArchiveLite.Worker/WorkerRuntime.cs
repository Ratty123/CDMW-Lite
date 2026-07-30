using Cdmw.ArchiveLite.Contracts;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Worker;

internal sealed class WorkerRuntime : IDisposable
{
    private const int MaximumForwardedTextureFailures = 512;
    private static int _forwardedTextureFailures;
    private readonly NativeArchiveCore _native = new();
    private readonly ArchiveSessionManager _sessions;
    private readonly ArchiveQueryService _queries;
    private readonly ArchiveCacheHealthService _cacheHealth;
    private readonly GameInstallDiscoveryService _gameDiscovery;
    private readonly ArchiveFacetsService _facets;
    private readonly ArchiveFolderTreeService _folderTree;
    private readonly ArchiveItemNameIndexService _nameIndex;
    private readonly ArchiveItemCatalogService _itemCatalog;
    private readonly ArchiveItemIconService _itemIcons;
    private readonly ArchiveItemCatalogScopeService _itemScopes;
    private readonly ArchiveAssociationService _associations;
    private readonly ArchivePreviewService _previews;
    private readonly TextSearchService _textSearch;
    private readonly ArchiveExportService _exports;
    private readonly ArchiveWorkPriority _workPriority = new();

    public WorkerRuntime()
    {
        // Texture decode failures are recorded in the worker but read by the user in the client's
        // log, so forward each one over the standard error the client already drains. A warm-up
        // across a damaged archive can fail on every icon, so the stream is capped rather than
        // allowed to fill the log.
        TexturePreviewDiagnostics.Sink = static failure =>
        {
            var forwarded = Interlocked.Increment(ref _forwardedTextureFailures);
            if (forwarded < MaximumForwardedTextureFailures)
            {
                Console.Error.WriteLine(TexturePreviewDiagnostics.Describe(failure));
            }
            else if (forwarded == MaximumForwardedTextureFailures)
            {
                Console.Error.WriteLine(
                    $"texture decode failures beyond {MaximumForwardedTextureFailures} are no longer being reported; "
                    + "the most recent are retained in the worker.");
            }
        };
        ArchiveLiteCacheMaintenance.Prune(ArchiveLiteDataPaths.Cache, ArchiveLiteCacheMaintenance.DefaultCacheMaximumBytes);
        // No session holds an index yet, so startup is where a set abandoned by an earlier crash or
        // by a root the user no longer opens can be reclaimed without contending with a reader.
        ArchiveIndexCacheReclamation.ReclaimSuperseded();
        _native.EnsureCompatible();
        _sessions = new ArchiveSessionManager(_native);
        _queries = new ArchiveQueryService(_sessions);
        _cacheHealth = new ArchiveCacheHealthService();
        _gameDiscovery = new GameInstallDiscoveryService();
        _facets = new ArchiveFacetsService(_sessions);
        _folderTree = new ArchiveFolderTreeService(_sessions);
        _nameIndex = new ArchiveItemNameIndexService(_sessions, _native, _workPriority);
        _itemCatalog = new ArchiveItemCatalogService(_sessions, _nameIndex);
        _associations = new ArchiveAssociationService(_sessions, _native);
        _itemScopes = new ArchiveItemCatalogScopeService(_sessions, _nameIndex, _associations);
        var modelPreviews = new NativeModelPreviewService();
        var texturePreviews = new NativeTexturePreviewService();
        _itemIcons = new ArchiveItemIconService(_sessions, _nameIndex, _native, texturePreviews, _workPriority);
        _previews = new ArchivePreviewService(_sessions, _native, modelPreviews, texturePreviews);
        _textSearch = new TextSearchService(_sessions, _native);
        _exports = new ArchiveExportService(
            _sessions,
            _queries,
            _native,
            new NativeModelExportService(modelPreviews));
    }

    public async Task<WorkerMessage> HandleAsync(
        WorkerMessage request,
        Func<ProgressUpdate, Task> publishProgress,
        CancellationToken cancellationToken)
    {
        using var foregroundLease = request.Kind is WorkerProtocol.WarmItemIcons or WorkerProtocol.BuildNameIndex
            ? null
            : _workPriority.EnterForeground();
        switch (request.Kind)
        {
            case WorkerProtocol.OpenArchive:
                {
                    var payload = RequirePayload<OpenArchiveRequest>(request);
                    var result = await _sessions.OpenAsync(payload, cancellationToken, publishProgress).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.QueryArchive:
                {
                    var payload = RequirePayload<ArchiveQuerySpec>(request);
                    var result = await _queries.QueryAsync(payload, request.Generation, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.InspectArchiveCache:
                {
                    var payload = RequirePayload<ArchiveCacheHealthRequest>(request);
                    var result = await _cacheHealth.InspectAsync(payload, publishProgress, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.DiscoverGameRoots:
                {
                    _ = RequirePayload<GameInstallDiscoveryRequest>(request);
                    var result = await _gameDiscovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.ArchiveFacets:
                {
                    var payload = RequirePayload<ArchiveFacetsRequest>(request);
                    var result = await _facets.LoadAsync(payload, publishProgress, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.ArchiveFolderTree:
                {
                    var payload = RequirePayload<ArchiveFolderTreeRequest>(request);
                    var result = await _folderTree.LoadAsync(payload, publishProgress, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.BuildNameIndex:
                {
                    var payload = RequirePayload<BuildNameIndexRequest>(request);
                    var result = await _nameIndex.BuildAsync(
                        payload,
                        publishProgress,
                        cancellationToken,
                        yieldToForeground: true).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.SearchItemCatalog:
                {
                    var payload = RequirePayload<ItemCatalogSearchRequest>(request);
                    var result = await _itemCatalog.SearchAsync(payload, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.LoadItemIcons:
                {
                    var payload = RequirePayload<ItemIconBatchRequest>(request);
                    var result = await _itemIcons.LoadAsync(payload, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.WarmItemIcons:
                {
                    var payload = RequirePayload<WarmItemIconsRequest>(request);
                    var result = await _itemIcons.WarmAsync(payload, publishProgress, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.ScopeItemCatalog:
                {
                    var payload = RequirePayload<ItemCatalogScopeRequest>(request);
                    var result = await _itemScopes.ResolveAsync(payload, publishProgress, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.FindAssociatedAssets:
                {
                    var payload = RequirePayload<FindAssociatedAssetsRequest>(request);
                    var result = await _associations.FindAsync(payload, publishProgress, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.Preview:
                {
                    var payload = RequirePayload<PreviewRequest>(request);
                    var result = await _previews.BuildAsync(payload, cancellationToken, publishProgress).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.TextSearch:
                {
                    var payload = RequirePayload<TextSearchRequest>(request);
                    var result = await _textSearch.SearchAsync(payload, cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.TextDocument:
                {
                    var payload = RequirePayload<TextDocumentRequest>(request);
                    PreviewResult result;
                    if (payload.SourceKind == TextSearchSourceKind.Archive)
                    {
                        if (payload.EntryId is not { } entryId)
                        {
                            throw new InvalidDataException("Archive text-document preview requires an entry id.");
                        }
                        result = await _previews.BuildAsync(
                            new PreviewRequest(payload.Source, entryId),
                            cancellationToken,
                            publishProgress).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await _textSearch.BuildPreviewAsync(payload, cancellationToken).ConfigureAwait(false);
                    }
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            case WorkerProtocol.Export:
                {
                    var payload = RequirePayload<ExportPlanRequest>(request);
                    var result = await _exports.ExportAsync(
                        payload,
                        publishProgress,
                        cancellationToken).ConfigureAwait(false);
                    return WorkerProtocol.Response(request, WorkerMessageStatus.Result, result);
                }
            default:
                return WorkerProtocol.Failure(request, "unsupported_request", $"Unsupported request kind '{request.Kind}'.");
        }
    }

    public void Dispose() => _sessions.Dispose();

    private static T RequirePayload<T>(WorkerMessage request)
    {
        return WorkerProtocol.ReadPayload<T>(request)
            ?? throw new InvalidDataException($"Worker request '{request.Kind}' has no valid payload.");
    }
}
