namespace Cdmw.ArchiveLite.Contracts;

public sealed record ArchiveEntryDto(
    long EntryId,
    string Path,
    string SourcePamt,
    string PazFile,
    int PazIndex,
    long Offset,
    long StoredSize,
    long OriginalSize,
    int Flags,
    string Extension,
    string Package,
    ArchiveEntryRole Role,
    bool IsPreviewable,
    string KnownName = "",
    string NameEvidence = "",
    ArchiveEntryFileType FileType = ArchiveEntryFileType.Other,
    ArchiveTextureUsage TextureUsage = ArchiveTextureUsage.None)
{
    public bool IsCompressed => StoredSize != OriginalSize;
    public int CompressionType => Flags & 0x0F;
    public bool IsEncrypted => (Flags >> 4) != 0;
    public int EncryptionType => (Flags >> 4) & 0x0F;
    public string Name => System.IO.Path.GetFileName(Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public string CompressionLabel => IsCompressed ? $"Type {CompressionType}" : "None";

    /// <summary>
    /// The single name to present for an entry: the item name the archive states outright when
    /// there is one, and otherwise the related-item evidence, which names a likely item rather
    /// than a certain one. <see cref="HasExactItemName"/> tells the two apart.
    /// </summary>
    public string ItemName => string.IsNullOrWhiteSpace(KnownName) ? NameEvidence : KnownName;

    public bool HasExactItemName => !string.IsNullOrWhiteSpace(KnownName);
}

public enum ArchiveEntryRole
{
    Other,
    Model,
    Animation,
    Physics,
    Metadata,
    Video,
    Audio,
    UserInterface,
    Impostor,
    Normal,
    Material,
    Image,
    Text,
}

public enum ArchiveEntryFileType
{
    Other,
    Model,
    Animation,
    Physics,
    Metadata,
    Video,
    Audio,
    UserInterface,
    Texture,
    Image,
    Text,
}

public enum ArchiveTextureUsage
{
    None,
    Unknown,
    Color,
    NormalMap,
    MaterialMap,
}

/// <summary>
/// How the entry list is arranged. The category navigator used to be two of these; it is a setting of
/// its own now, so those two are gone. The remaining values keep the numbers they have always had,
/// because settings files store this enum as a number and renumbering would silently move a saved
/// choice onto a different view.
/// </summary>
public enum ArchiveViewMode
{
    Folders = 0,
    Flat = 3,
    /// <summary>The entry list is itself the archive's tree, with files under their folders.</summary>
    Tree = 4,
}

public enum ArchiveSortField
{
    Path,
    Name,
    KnownName,
    NameEvidence,
    Extension,
    Package,
    OriginalSize,
    StoredSize,
    Compression,
    Role,
    FileType,
    TextureUsage,
}

public enum ArchiveCacheMode
{
    Persistent,
    SessionOnly,
}

public sealed record OpenArchiveRequest(
    string PackageRoot,
    bool ForceRefresh = false,
    ArchiveCacheMode CacheMode = ArchiveCacheMode.Persistent,
    bool AllowCacheBuild = true);

public sealed record OpenArchiveResult(
    string SessionId,
    string PackageRoot,
    string Fingerprint,
    long EntryCount,
    int IndexVersion,
    bool UsedCachedIndex,
    IReadOnlyList<string> Warnings,
    ArchiveCacheMode CacheMode = ArchiveCacheMode.Persistent);

public sealed record ArchiveQuerySpec(
    string SessionId,
    string? PathText = null,
    IReadOnlyList<string>? Extensions = null,
    string? Package = null,
    string? Folder = null,
    IReadOnlyList<ArchiveEntryRole>? Roles = null,
    long? MinimumSize = null,
    bool PreviewableOnly = false,
    ArchiveViewMode ViewMode = ArchiveViewMode.Flat,
    /// <summary>
    /// Whether the caller needs the per-role counts. The flat view can otherwise page straight out of
    /// the sorted index without visiting every entry, and that shortcut cannot count anything.
    /// </summary>
    bool IncludeCategoryFacets = false,
    ArchiveSortField SortField = ArchiveSortField.Path,
    bool SortDescending = false,
    int PageStart = 0,
    int PageSize = 256,
    IReadOnlyList<long>? EntryIds = null);

public sealed record ArchivePageResult(
    string SessionId,
    long Generation,
    long TotalMatches,
    int PageStart,
    IReadOnlyList<ArchiveEntryDto> Entries,
    IReadOnlyList<string> Folders,
    IReadOnlyDictionary<string, long> Categories);
