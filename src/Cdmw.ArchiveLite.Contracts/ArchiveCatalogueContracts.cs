namespace Cdmw.ArchiveLite.Contracts;

public sealed record ArchiveFacetsRequest(string SessionId);

public sealed record ArchiveFacetsResult(
    string SessionId,
    IReadOnlyList<ArchiveExtensionFacet> Extensions);

public sealed record ArchiveExtensionFacet(
    string Extension,
    long Count,
    ArchiveExtensionCategory Category);

public enum ArchiveExtensionCategory
{
    ModelMeshPhysics,
    TextureImage,
    MaterialMetadata,
    AnimationScene,
    AudioVideo,
    UserInterfaceText,
    Other,
}

/// <summary>
/// Asks for the direct children of one archive folder. The navigator expands a level at a time so a
/// deep archive never has to fit its whole directory structure inside one protocol message.
/// </summary>
public sealed record ArchiveFolderTreeRequest(
    string SessionId,
    string? Path = null,
    int Depth = 1,
    ArchiveEntryFilter? Filter = null);

public sealed record ArchiveFolderTreeResult(
    string SessionId,
    string? Path,
    long DirectCount,
    long TotalCount,
    IReadOnlyList<ArchiveFolderNode> Nodes,
    bool Truncated = false);

/// <summary>
/// One folder in the archive. <paramref name="DirectCount"/> counts the files stored in the folder
/// itself; <paramref name="TotalCount"/> counts every file at or below it.
/// </summary>
public sealed record ArchiveFolderNode(
    string Name,
    string Path,
    long DirectCount,
    long TotalCount,
    bool HasChildren,
    IReadOnlyList<ArchiveFolderNode> Children);

public sealed record BuildNameIndexRequest(string SessionId);

public sealed record BuildNameIndexResult(
    string SessionId,
    bool Available,
    bool UsedCache,
    long ExactNameCount,
    long RelatedNameCount,
    string? Warning = null,
    long ItemCount = 0);

public sealed record ItemCatalogSearchRequest(
    string SessionId,
    string Query = "",
    string? Category = null,
    string? Group = null,
    string? MaterialTag = null,
    int PageStart = 0,
    int PageSize = 72);

public sealed record ItemCatalogSearchResult(
    string SessionId,
    long TotalMatches,
    int PageStart,
    int PageSize,
    IReadOnlyList<ItemCatalogRow> Items,
    IReadOnlyList<ItemCatalogCategoryFacet> Categories,
    IReadOnlyList<ItemCatalogValueFacet> MaterialTags,
    string? Warning = null);

public sealed record ItemCatalogRow(
    int ItemId,
    string InternalName,
    string DisplayName,
    string Category,
    string Group,
    string CategoryEvidence,
    IReadOnlyList<string> PacFiles,
    IReadOnlyList<string> ModelStems,
    IReadOnlyList<string> IconPaths,
    IReadOnlyList<string> LocalizedNames,
    IReadOnlyList<string> MaterialTags,
    int VariantCount,
    string Evidence,
    string Description = "");

public sealed record ItemCatalogCategoryFacet(
    string Category,
    string Group,
    long Count);

public sealed record ItemCatalogValueFacet(
    string Value,
    long Count);

public sealed record ItemIconBatchRequest(
    string SessionId,
    IReadOnlyList<int> ItemIds,
    int ThumbnailSize = 120);

public sealed record ItemIconBatchResult(
    string SessionId,
    IReadOnlyList<ItemIconResult> Items);

public sealed record ItemIconResult(
    int ItemId,
    string? PngPath,
    string? SourcePath,
    string? Warning = null);

public sealed record WarmItemIconsRequest(
    string SessionId,
    IReadOnlyList<int>? PrioritizedItemIds = null,
    int MaximumIcons = 0,
    int ThumbnailSize = 120);

public sealed record WarmItemIconsResult(
    string SessionId,
    long Considered,
    long Ready,
    long Missing,
    long Failed);

public sealed record ItemCatalogScopeRequest(
    string SessionId,
    int ItemId,
    bool IncludeRelated,
    int MaximumResults = 512);

public sealed record ItemCatalogScopeResult(
    string SessionId,
    int ItemId,
    bool IncludeRelated,
    IReadOnlyList<long> EntryIds,
    long DirectCount,
    bool Truncated,
    IReadOnlyList<ArchiveExtensionFacet>? Extensions = null);
