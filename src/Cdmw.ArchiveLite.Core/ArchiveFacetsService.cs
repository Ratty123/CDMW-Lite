using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

public sealed class ArchiveFacetsService(ArchiveSessionManager sessions)
{
    public async Task<ArchiveFacetsResult> LoadAsync(
        ArchiveFacetsRequest request,
        Func<ProgressUpdate, Task>? publishProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = sessions.GetRequired(request.SessionId);
        if (session.TryGetExtensionFacets(out var cached) && cached is not null)
        {
            return new ArchiveFacetsResult(session.Id, cached);
        }

        var total = session.Index.EntryCount;
        if (publishProgress is not null)
        {
            await publishProgress(new ProgressUpdate(0, total, "extension_scan")).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var facets = session.ExtensionIndex.GetFacets();
        session.SetExtensionFacets(facets);
        if (publishProgress is not null)
        {
            await publishProgress(new ProgressUpdate(total, total, "extension_scan")).ConfigureAwait(false);
        }
        return new ArchiveFacetsResult(session.Id, facets);
    }
}
