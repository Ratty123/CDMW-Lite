using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.App.Services;

public sealed record LiteSettings(
    string Language = "en",
    string? ArchiveRoot = null,
    string? ExportRoot = null,
    string Theme = "graphite",
    string FontSize = "small",
    string LayoutDensity = "compact",
    ArchiveSortField ArchiveSortField = ArchiveSortField.Path,
    bool ArchiveSortDescending = false,
    string[]? ArchiveVisibleColumns = null,
    ArchiveBrowserSettings? ArchiveBrowser = null,
    TextSearchSettings? TextSearch = null,
    ItemFinderSettings? ItemFinder = null,
    WindowPlacementSettings? WindowPlacement = null,
    WorkspaceLayoutSettings? WorkspaceLayout = null,
    GridColumnSettings[]? ArchiveColumnLayout = null,
    GridColumnSettings[]? TextSearchColumnLayout = null,
    int ArchiveColumnDefaultsRevision = 0);

/// <summary>
/// Tracks which shipped catalog column defaults a settings file was written against. A file left at
/// an older revision is re-seeded with the current defaults once, so a changed default reaches
/// existing portable installs instead of only first runs.
/// </summary>
public static class ArchiveColumnDefaults
{
    public const int Revision = 1;
}

public sealed record ArchiveBrowserSettings(
    string PathFilter = "",
    string ExtensionFilter = "",
    ArchiveViewMode ViewMode = ArchiveViewMode.Flat,
    string? FolderPath = null,
    ExportCollisionPolicy CollisionPolicy = ExportCollisionPolicy.Skip,
    ExportManifestFormat ManifestFormat = ExportManifestFormat.Json,
    ModelPreviewCameraInputSettings? ModelPreviewCameraInput = null,
    PreviewBackgroundSettings? PreviewBackground = null,
    bool ShowCategories = false);

/// <summary>
/// The colour drawn behind a preview. <see cref="PreviewBackgroundChoice.Theme"/> keeps the surface
/// the active theme paints; every other choice is a fixed sRGB colour so a texture's own tone cannot
/// be mistaken for the surface it sits on.
/// </summary>
public sealed record PreviewBackgroundSettings(
    PreviewBackgroundChoice Choice = PreviewBackgroundChoice.Theme,
    string CustomColor = "#202020");

public enum PreviewBackgroundChoice
{
    Theme,
    Black,
    Charcoal,
    MidGray,
    LightGray,
    White,
    Magenta,
    Custom,
}

public sealed record ModelPreviewCameraInputSettings(
    double OrbitSensitivity = 0.22,
    double PanSensitivity = 0.60,
    bool InvertOrbitX = false,
    bool InvertOrbitY = false,
    bool InvertPanX = false,
    bool InvertPanY = false);

public sealed record TextSearchSettings(
    TextSearchSourceKind SourceKind = TextSearchSourceKind.Archive,
    string LooseFolder = "",
    string Query = "",
    string PathFilter = "",
    string Extensions = ".xml;.txt;.json;.cfg;.ini;.lua;.material;.shader;.yaml;.yml",
    bool UseRegularExpression = false,
    bool CaseSensitive = false);

public sealed record ItemFinderSettings(
    string Query = "",
    string? Category = null,
    string? Group = null,
    double Width = 1240,
    double Height = 800);

/// <summary>
/// Where the main window was left, in physical screen pixels.
/// </summary>
/// <remarks>
/// These were once WPF device-independent units, which are only meaningful next to the scale of the
/// display they were measured on: a window remembered on a 100% monitor came back half again as
/// large on a 150% one. The pixel names are deliberately new, so a settings file written by an
/// older build supplies nothing here and opens at the default size rather than at a figure whose
/// units are no longer known.
/// </remarks>
public sealed record WindowPlacementSettings(
    int? PixelLeft = null,
    int? PixelTop = null,
    int? PixelWidth = null,
    int? PixelHeight = null,
    bool IsMaximized = false)
{
    public bool HasRestoredBounds =>
        PixelLeft is not null && PixelTop is not null && PixelWidth > 0 && PixelHeight > 0;
}

public sealed record WorkspaceLayoutSettings(
    double ArchiveFilterWidth = 278,
    double ArchivePreviewWidth = 420,
    double TextSearchFilterWidth = 300,
    double TextSearchPreviewWidth = 420,
    double ArchiveFolderWidth = 240);

public sealed record GridColumnSettings(
    string Key,
    int DisplayIndex,
    double Width);
