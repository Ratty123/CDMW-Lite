using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Cdmw.Archive.Content;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.App.ViewModels;
using Cdmw.ArchiveLite.Contracts;
using Cdmw.ArchiveLite.Core;
using Cdmw.ArchiveLite.Standalone;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Cdmw.ArchiveLite.Tests;

internal static class ArchiveLiteTestRunner
{
    public static async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("protocol serializes snake-case messages", TestProtocolAsync),
            ("English, German, and Spanish resources have identical keys", TestLocalizationResourcesAsync),
            ("compiled localization persists across later UI work", TestCompiledLocalizationAsync),
            ("read-only WPF text bindings are explicitly one-way", TestReadOnlyWpfBindingsAsync),
            ("fatal diagnostics are written to portable log and crash folders", TestFatalDiagnosticsAsync),
            ("portable settings retain filters, window placement, panes, and columns", TestPortableUiSettingsAsync),
            ("preview pane sizing stays inside the live workspace", TestWorkspacePaneSizingAsync),
            ("WPF themes expose the shared palette and safe progress bindings", TestWpfThemesAsync),
            ("Lite branding is distinct and embedded in both user-facing executables", TestApplicationBrandingAsync),
            ("modern shell exposes cache health, game detection, and Enter search", TestModernShellAsync),
            ("preview drawer and syntax colors stay readable across themes", TestPreviewPresentationAsync),
            ("archive grid exposes configurable sortable columns and categorized extensions", TestArchiveGridFeaturesAsync),
            ("associated assets resolve references and same-family companions read-only", TestAssociatedAssetsAsync),
            ("export paths reject traversal and roots", TestExportPathPolicyAsync),
            ("isolated cache maintenance is bounded and deterministic", TestCacheMaintenanceAsync),
            ("standalone payload extraction is atomic, reusable, and traversal-safe", TestStandaloneRuntimeAsync),
            ("double-click build launcher routes to the verified release pipeline", TestBuildLauncherSourceAsync),
            ("game discovery recognizes archive roots and Steam libraries", TestGameInstallDiscoveryAsync),
            ("archive cache health detects missing, current, and stale indexes", TestArchiveCacheHealthAsync),
            ("archive loading supports reusable and session-only indexes", TestArchiveCacheModesAsync),
            ("startup auto-loads current cache and recommends manual refresh after hash changes", TestStartupCacheAutoLoadAsync),
            ("asset metadata keeps HKX read-only and renderer-free", TestAssetMetadataAndHkxPreviewAsync),
            ("shared content manifest and semantic analyzers stay decoder-parity safe", TestSharedContentAnalyzersAsync),
            ("native preview core parses PAT LOD0 geometry", TestNativePatGeometryAsync),
            ("native model packages adapt safely and export Blender interchange formats", TestNativeModelPreviewPackageAsync),
            ("renderer warmup package is complete and loadable", TestRendererWarmupPackageAsync),
            ("native model previews start immediately and warm-cache hits stay delay-free", TestNativeModelPreviewCacheDwellAsync),
            ("known item names preserve exact matches and propagate related evidence", TestArchiveItemNamesAsync),
            ("native item-name discovery handles shifted records and semantic icon links", TestArchiveItemNameDiscoveryAsync),
            ("Item Finder shares the Full catalog contract and keeps icon work bounded", TestItemFinderCatalogAsync),
            ("Item Finder debounce, facet refresh, icons, close, and session changes stay latest-wins", TestItemFinderViewModelLifecycleAsync),
            ("DDS file type and terminal-suffix usage classification stay explicit", TestDdsTextureClassificationAsync),
            ("UTF-8, UTF-16, and Latin-1 text decode without Python codecs", TestTextDecodingAsync),
            ("native archive ABI scans and decodes synthetic PAMT/PAZ", TestNativeArchiveAsync),
            ("archive query, preview, and text search are read-only", TestArchiveServicesAsync),
            ("archive export is contained, atomic, and manifested", TestArchiveExportAsync),
            ("named-pipe worker opens and queries an archive", TestWorkerBoundaryAsync),
        };
        var failures = new List<string>();
        foreach (var test in tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{test.Name}: {exception}");
                Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
            }
        }
        if (failures.Count == 0)
        {
            Console.WriteLine($"CDMW Archive Lite focused tests: PASS ({tests.Length} scenarios)");
            return 0;
        }
        Console.Error.WriteLine($"CDMW Archive Lite focused tests: FAIL ({failures.Count}/{tests.Length})");
        return 1;
    }

    private static Task TestProtocolAsync()
    {
        var message = WorkerProtocol.Request(Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, WorkerProtocol.Ping, new PingRequest("1.0"));
        var json = System.Text.Json.JsonSerializer.Serialize(message, WorkerProtocol.JsonOptions);
        Require(json.Contains("\"protocol_version\":1", StringComparison.Ordinal), "protocol_version is not snake case");
        Require(WorkerProtocol.ReadPayload<PingRequest>(message)?.ClientVersion == "1.0", "protocol payload did not round-trip");
        var openMessage = WorkerProtocol.Request(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            8,
            WorkerProtocol.OpenArchive,
            new OpenArchiveRequest("C:\\game", CacheMode: ArchiveCacheMode.SessionOnly));
        var openJson = JsonSerializer.Serialize(openMessage, WorkerProtocol.JsonOptions);
        Require(openJson.Contains("\"cache_mode\":\"session_only\"", StringComparison.Ordinal), "cache_mode is not a snake-case protocol enum");
        Require(
            WorkerProtocol.ReadPayload<OpenArchiveRequest>(openMessage)?.CacheMode == ArchiveCacheMode.SessionOnly,
            "archive cache mode did not round-trip");
        var texturedPreview = new PreviewRequest("session", 17, IncludeModelTextures: true);
        var texturedPreviewJson = JsonSerializer.Serialize(texturedPreview, WorkerProtocol.JsonOptions);
        Require(
            texturedPreviewJson.Contains("\"include_model_textures\":true", StringComparison.Ordinal)
            && new PreviewRequest("session", 17).IncludeModelTextures == false,
            "opt-in model texture intent is not snake case or no longer defaults off");
        var cachedOnlyMessage = WorkerProtocol.Request(
            Guid.Parse("23232323-2323-2323-2323-232323232323"),
            8,
            WorkerProtocol.OpenArchive,
            new OpenArchiveRequest("C:\\game", CacheMode: ArchiveCacheMode.Persistent, AllowCacheBuild: false));
        var cachedOnlyJson = JsonSerializer.Serialize(cachedOnlyMessage, WorkerProtocol.JsonOptions);
        Require(cachedOnlyJson.Contains("\"allow_cache_build\":false", StringComparison.Ordinal), "cached-only startup intent is not serialized");
        var associationMessage = WorkerProtocol.Request(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            9,
            WorkerProtocol.FindAssociatedAssets,
            new FindAssociatedAssetsRequest("session", 42, 96));
        var associationJson = JsonSerializer.Serialize(associationMessage, WorkerProtocol.JsonOptions);
        Require(
            associationJson.Contains("\"maximum_results\":96", StringComparison.Ordinal),
            "associated-asset request is not snake case");
        Require(
            WorkerProtocol.ReadPayload<FindAssociatedAssetsRequest>(associationMessage)?.EntryId == 42,
            "associated-asset request did not round-trip");
        var textDocumentMessage = WorkerProtocol.Request(
            Guid.Parse("35353535-3535-3535-3535-353535353535"),
            10,
            WorkerProtocol.TextDocument,
            new TextDocumentRequest(TextSearchSourceKind.Archive, "session", "text/hello.txt", 17));
        var textDocumentJson = JsonSerializer.Serialize(textDocumentMessage, WorkerProtocol.JsonOptions);
        Require(
            textDocumentJson.Contains("\"entry_id\":17", StringComparison.Ordinal)
            && textDocumentJson.Contains("\"source_kind\":\"archive\"", StringComparison.Ordinal),
            "full text-document preview request is not snake case");
        var folderExportMessage = WorkerProtocol.Request(
            Guid.Parse("34343434-3434-3434-3434-343434343434"),
            10,
            WorkerProtocol.Export,
            new ExportPlanRequest(
                "session",
                ExportKind.FolderTree,
                "C:\\output",
                [],
                null,
                FolderPath: "character/model"));
        var folderExportJson = JsonSerializer.Serialize(folderExportMessage, WorkerProtocol.JsonOptions);
        Require(folderExportJson.Contains("\"folder_path\":\"character/model\"", StringComparison.Ordinal), "folder export scope is not serialized");
        var itemSearchMessage = WorkerProtocol.Request(
            Guid.Parse("45454545-4545-4545-4545-454545454545"),
            11,
            WorkerProtocol.SearchItemCatalog,
            new ItemCatalogSearchRequest("session", "sword steel", "Weapon", "Sword", "steel", 0, 72));
        var itemSearchJson = JsonSerializer.Serialize(itemSearchMessage, WorkerProtocol.JsonOptions);
        Require(
            itemSearchJson.Contains("\"kind\":\"search_item_catalog\"", StringComparison.Ordinal)
            && itemSearchJson.Contains("\"material_tag\":\"steel\"", StringComparison.Ordinal)
            && itemSearchJson.Contains("\"page_size\":72", StringComparison.Ordinal),
            "Item Finder request is not bounded or snake case");
        return Task.CompletedTask;
    }

    private static Task TestLocalizationResourcesAsync()
    {
        var resourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.App",
            "Resources");
        var resources = new[] { "Strings.resx", "Strings.de.resx", "Strings.es.resx" }
            .Select(filename => System.Xml.Linq.XDocument.Load(Path.Combine(resourceRoot, filename)))
            .Select(document => document.Root!.Elements("data").ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.Ordinal))
            .ToArray();
        var expected = resources[0].Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var resource in resources)
        {
            Require(resource.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected), "localized resource keys do not match");
            Require(resource.Values.All(static value => !string.IsNullOrWhiteSpace(value)), "localized resource contains an empty value");
        }
        return Task.CompletedTask;
    }

    private static Task TestCompiledLocalizationAsync()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var refreshCount = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.Equals(args.PropertyName, "Item[]", StringComparison.Ordinal))
            {
                refreshCount++;
            }
        };
        LocalizedStringSource.Instance.PropertyChanged += handler;
        try
        {
            var expectations = new[]
            {
                (Language: "en", Title: "Archive loading"),
                (Language: "de", Title: "Archiv laden"),
                (Language: "es", Title: "Carga del archivo"),
            };
            foreach (var expectation in expectations)
            {
                LocalizationManager.ApplyCulture(expectation.Language);
                Require(
                    string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, expectation.Language, StringComparison.Ordinal),
                    $"current UI culture did not switch to {expectation.Language}");
                Require(
                    string.Equals(CultureInfo.DefaultThreadCurrentUICulture?.TwoLetterISOLanguageName, expectation.Language, StringComparison.Ordinal),
                    $"default UI culture did not persist {expectation.Language} for later UI callbacks");
                Require(
                    string.Equals(LocalizationManager.Get("CacheChoiceTitle"), expectation.Title, StringComparison.Ordinal),
                    $"compiled {expectation.Language} cache-dialog resources fell back to another language");
                Require(
                    string.Equals(LocalizedStringSource.Instance["CacheChoiceTitle"], expectation.Title, StringComparison.Ordinal),
                    $"live localized binding source did not expose {expectation.Language}");
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                Require(
                    string.Equals(LocalizationManager.Get("CacheChoiceTitle"), expectation.Title, StringComparison.Ordinal),
                    $"compiled {expectation.Language} resources drifted with a later async culture context");
            }
            Require(refreshCount >= expectations.Length, "live localized bindings were not refreshed for each culture change");

            var locExtensionSource = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "src",
                "Cdmw.ArchiveLite.App",
                "Infrastructure",
                "LocExtension.cs"));
            Require(
                locExtensionSource.Contains("LocalizedStringSource.Instance", StringComparison.Ordinal)
                && locExtensionSource.Contains("BindingMode.OneWay", StringComparison.Ordinal),
                "XAML localization still resolves to a one-time static string");
        }
        finally
        {
            LocalizedStringSource.Instance.PropertyChanged -= handler;
            CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
        return Task.CompletedTask;
    }

    private static Task TestReadOnlyWpfBindingsAsync()
    {
        var windowPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.App",
            "MainWindow.xaml");
        var document = System.Xml.Linq.XDocument.Load(windowPath);
        var readOnlyTextBindings = document
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBox")
            .Where(element => string.Equals((string?)element.Attribute("IsReadOnly"), "True", StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("Text"))
            .Where(static value => value?.StartsWith("{Binding", StringComparison.Ordinal) == true)
            .ToArray();
        Require(readOnlyTextBindings.Length > 0, "MainWindow has no read-only TextBox binding to validate");
        Require(
            readOnlyTextBindings.All(static binding => binding!.Contains("Mode=OneWay", StringComparison.Ordinal)),
            "a read-only TextBox uses WPF's default TwoWay Text binding");
        var runTextBindings = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Run")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(static value => value?.StartsWith("{Binding", StringComparison.Ordinal) == true)
            .ToArray();
        Require(
            runTextBindings.All(static binding => binding!.Contains("Mode=OneWay", StringComparison.Ordinal)),
            "an inline Run uses WPF's write-back binding mode for a read-only row property");
        return Task.CompletedTask;
    }

    private static Task TestFatalDiagnosticsAsync()
    {
        var portableRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DATA_ROOT")!);
        var appAssembly = typeof(MainWindowViewModel).Assembly;
        var diagnosticLog = appAssembly.GetType("Cdmw.ArchiveLite.App.Services.DiagnosticLog")
            ?? throw new InvalidOperationException("DiagnosticLog type was not found");
        var writeFatal = diagnosticLog.GetMethod("WriteFatal", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("DiagnosticLog.WriteFatal was not found");
        writeFatal.Invoke(null, ["test-fatal", new InvalidOperationException("synthetic fatal diagnostic")]);
        var logPath = Path.Combine(portableRoot, "logs", "archive-lite.log");
        var crashFiles = Directory.GetFiles(Path.Combine(portableRoot, "crash"), "archive-lite-crash-*.log");
        Require(File.Exists(logPath), "fatal diagnostic did not reach the portable log folder");
        Require(crashFiles.Length > 0, "fatal diagnostic did not reach the portable crash folder");
        Require(
            File.ReadAllText(crashFiles[^1]).Contains("synthetic fatal diagnostic", StringComparison.Ordinal),
            "portable crash diagnostic omitted the underlying exception");
        return Task.CompletedTask;
    }

    private static async Task TestPortableUiSettingsAsync()
    {
        var defaults = new LiteSettings();
        Require(
            defaults.FontSize == "small" && defaults.LayoutDensity == "compact",
            "first-run appearance does not default to Small and Compact");

        var expected = new LiteSettings(
            Language: "de",
            ArchiveRoot: "C:\\game",
            Theme: "midnight",
            FontSize: "large",
            LayoutDensity: "compact",
            ArchiveSortField: ArchiveSortField.KnownName,
            ArchiveSortDescending: true,
            ArchiveVisibleColumns: ["Name", "Path"],
            ArchiveBrowser: new ArchiveBrowserSettings(
                PathFilter: "character/model",
                ExtensionFilter: ".pac;.pam",
                ViewMode: ArchiveViewMode.CategoriesAndFolders,
                FolderPath: "character/model/player",
                CollisionPolicy: ExportCollisionPolicy.Overwrite,
                ManifestFormat: ExportManifestFormat.Csv,
                ModelPreviewCameraInput: new ModelPreviewCameraInputSettings(
                    OrbitSensitivity: 0.35,
                    PanSensitivity: 1.15,
                    InvertOrbitX: true,
                    InvertOrbitY: false,
                    InvertPanX: false,
                    InvertPanY: true)),
            TextSearch: new TextSearchSettings(
                TextSearchSourceKind.LooseFolder,
                "C:\\loose",
                "material_name",
                "character",
                ".xml;.material",
                true,
                true),
            ItemFinder: new ItemFinderSettings("sword", "Weapon", "Sword", "steel", 1180, 760),
            WindowPlacement: new WindowPlacementSettings(120, 80, 1320, 790, true),
            WorkspaceLayout: new WorkspaceLayoutSettings(336, 488, 318, 452),
            ArchiveColumnLayout:
            [
                new GridColumnSettings("Name", 1, 240),
                new GridColumnSettings("Path", 0, 510),
            ],
            TextSearchColumnLayout:
            [
                new GridColumnSettings("Path", 0, 420),
                new GridColumnSettings("Context", 2, 560),
            ]);
        var settingsStore = typeof(MainWindowViewModel).Assembly.GetType("Cdmw.ArchiveLite.App.Services.SettingsStore")
            ?? throw new InvalidOperationException("SettingsStore type was not found");
        var saveMethod = settingsStore.GetMethod("SaveAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("SettingsStore.SaveAsync was not found");
        var loadMethod = settingsStore.GetMethod("LoadAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("SettingsStore.LoadAsync was not found");
        var saveTask = saveMethod.Invoke(null, [expected, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException("SettingsStore.SaveAsync did not return a task");
        await saveTask.ConfigureAwait(false);
        var loadTask = loadMethod.Invoke(null, [CancellationToken.None]) as Task
            ?? throw new InvalidOperationException("SettingsStore.LoadAsync did not return a task");
        await loadTask.ConfigureAwait(false);
        var actual = loadTask.GetType().GetProperty("Result")?.GetValue(loadTask) as LiteSettings
            ?? throw new InvalidOperationException("SettingsStore.LoadAsync did not return LiteSettings");

        Require(actual.ArchiveBrowser == expected.ArchiveBrowser, "archive filters did not round-trip through portable settings");
        Require(actual.TextSearch == expected.TextSearch, "text-search filters did not round-trip through portable settings");
        Require(actual.ItemFinder == expected.ItemFinder, "Item Finder filters and window size did not round-trip through portable settings");
        Require(actual.WindowPlacement == expected.WindowPlacement, "window placement did not round-trip through portable settings");
        Require(actual.WorkspaceLayout == expected.WorkspaceLayout, "split-pane widths did not round-trip through portable settings");
        Require(actual.FontSize == "large" && actual.LayoutDensity == "compact", "global font size and layout density did not round-trip through portable settings");
        Require(
            actual.ArchiveColumnLayout?.SequenceEqual(expected.ArchiveColumnLayout!) == true,
            "archive column widths/order did not round-trip through portable settings");
        Require(
            actual.TextSearchColumnLayout?.SequenceEqual(expected.TextSearchColumnLayout!) == true,
            "text-search column widths/order did not round-trip through portable settings");

        var portableRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DATA_ROOT")!);
        var settingsPath = Path.Combine(portableRoot, "settings.json");
        var json = await File.ReadAllTextAsync(settingsPath).ConfigureAwait(false);
        Require(
            json.Contains("\"archive_browser\"", StringComparison.Ordinal)
            && json.Contains("\"window_placement\"", StringComparison.Ordinal)
            && json.Contains("\"workspace_layout\"", StringComparison.Ordinal),
            "portable settings do not use the expected stable snake-case sections");

        await RunOnWpfDispatcherAsync(() =>
        {
            var browser = new ArchiveBrowserViewModel(
                null!,
                expected.ArchiveRoot,
                _ => { },
                (_, _) => ArchiveCacheMode.Persistent,
                expected.ArchiveSortField,
                expected.ArchiveSortDescending,
                actual.ArchiveBrowser);
            var search = new TextSearchViewModel(null!, () => null, _ => { }, actual.TextSearch);
            Require(browser.PathFilter == "character/model", "archive path filter was not restored into the view model");
            Require(browser.ExtensionFilter == ".pac;.pam", "archive extension filter was not restored into the view model");
            Require(browser.PackageFilter.Length == 0, "removed package filter still affects archive queries");
            Require(!browser.PreviewableOnly, "removed previewable-only filter still affects archive queries");
            Require(browser.ViewMode == ArchiveViewMode.CategoriesAndFolders, "archive view filter was not restored into the view model");
            Require(browser.SelectedFolder?.Path == "character/model/player", "folder filter was not ready before the first archive query");
            Require(browser.SelectedRole.Role is null, "removed role filter still affects archive queries");
            Require(browser.CollisionPolicy == ExportCollisionPolicy.Overwrite, "export collision policy was not restored");
            Require(browser.ManifestFormat == ExportManifestFormat.Csv, "export manifest format was not restored");
            Require(
                browser.ModelPreviewOrbitSensitivity == 0.35
                && browser.ModelPreviewPanSensitivity == 1.15
                && browser.ModelPreviewInvertOrbitX
                && !browser.ModelPreviewInvertOrbitY
                && !browser.ModelPreviewInvertPanX
                && browser.ModelPreviewInvertPanY,
                "model-preview camera input settings were not restored");
            Require(!browser.ShowModelTextures, "model textures must stay session-only and opt-in after startup");
            browser.ResetModelPreviewCameraInputCommand.Execute(null);
            Require(
                browser.ModelPreviewOrbitSensitivity == 0.22
                && browser.ModelPreviewPanSensitivity == 0.60
                && browser.ModelPreviewInvertOrbitX
                && browser.ModelPreviewInvertPanY,
                "camera-input reset did not restore Full's sensitivity defaults while preserving inversion choices");
            Require(search.SourceKind == TextSearchSourceKind.LooseFolder, "text-search source was not restored");
            Require(search.LooseFolder == "C:\\loose", "text-search loose folder was not restored");
            Require(search.Query == "material_name", "text-search query was not restored");
            Require(search.PathFilter == "character", "text-search path filter was not restored");
            Require(search.Extensions == ".xml;.material", "text-search extensions were not restored");
            Require(search.UseRegularExpression && search.CaseSensitive, "text-search boolean filters were not restored");
            browser.RequestShutdown();
            search.RequestShutdown();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static async Task TestWpfThemesAsync()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.App");
        var themeRoot = Path.Combine(appRoot, "Themes");
        var themePaths = Directory.GetFiles(themeRoot, "Theme.*.xaml", SearchOption.TopDirectoryOnly);
        Require(themePaths.Length == 6, "Archive Lite must ship all six selectable color themes");
        var themeDocuments = themePaths.ToDictionary(
            static path => Path.GetFileNameWithoutExtension(path),
            System.Xml.Linq.XDocument.Load,
            StringComparer.Ordinal);

        var requiredKeys = new[]
        {
            "WindowBackgroundBrush",
            "SurfaceBrush",
            "SurfaceRaisedBrush",
            "SurfaceHoverBrush",
            "SurfacePressedBrush",
            "InputBackgroundBrush",
            "TextBrush",
            "TextMutedBrush",
            "TextDisabledBrush",
            "AccentBrush",
            "AccentHoverBrush",
            "AccentPressedBrush",
            "AccentTextBrush",
            "BorderBrush",
            "SelectionBrush",
            "SelectionInactiveBrush",
            "AssociatedAssetModelBrush",
            "AssociatedAssetTextureBrush",
            "AssociatedAssetPhysicsBrush",
            "AssociatedAssetOtherBrush",
        };
        foreach (var themePath in themePaths)
        {
            var document = themeDocuments[Path.GetFileNameWithoutExtension(themePath)];
            var keys = document.Root!
                .Elements()
                .Select(element => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value)
                .Where(static key => key is not null)
                .ToHashSet(StringComparer.Ordinal);
            Require(
                requiredKeys.All(requiredKey => keys.Contains(requiredKey)),
                $"{Path.GetFileName(themePath)} is missing a shared theme resource");
        }

        var lightTheme = themeDocuments["Theme.Light"];
        var frostTheme = themeDocuments["Theme.Frost"];
        Require(
            RgbDistance(
                ThemeBrushColor(lightTheme, "WindowBackgroundBrush"),
                ThemeBrushColor(frostTheme, "WindowBackgroundBrush")) >= 18d
            && RgbDistance(
                ThemeBrushColor(lightTheme, "SurfaceBrush"),
                ThemeBrushColor(frostTheme, "SurfaceBrush")) >= 10d,
            "Frost is not visually distinct from the neutral Light theme");
        var activeButtonContrastPairs = new (string State, string Foreground, string Background)[]
        {
            ("secondary", "TextBrush", "SurfaceRaisedBrush"),
            ("secondary hover", "TextBrush", "SurfaceHoverBrush"),
            ("secondary pressed", "TextBrush", "SurfacePressedBrush"),
            ("secondary detail", "TextMutedBrush", "SurfaceRaisedBrush"),
            ("secondary detail hover", "TextMutedBrush", "SurfaceHoverBrush"),
            ("secondary detail pressed", "TextMutedBrush", "SurfacePressedBrush"),
            ("selected secondary detail", "TextMutedBrush", "SelectionInactiveBrush"),
            ("primary", "AccentTextBrush", "AccentBrush"),
            ("primary hover", "AccentTextBrush", "AccentHoverBrush"),
            ("primary pressed", "AccentTextBrush", "AccentPressedBrush"),
            ("selected navigation", "TextBrush", "SelectionInactiveBrush"),
        };
        foreach (var (themeName, theme) in themeDocuments)
        {
            var accentBackgrounds = new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush" };
            var blackWorstCase = accentBackgrounds.Min(backgroundKey =>
                ContrastRatio("#000000", ThemeBrushColor(theme, backgroundKey)));
            var whiteWorstCase = accentBackgrounds.Min(backgroundKey =>
                ContrastRatio("#FFFFFF", ThemeBrushColor(theme, backgroundKey)));
            var expectedAccentText = blackWorstCase >= whiteWorstCase ? "#000000" : "#FFFFFF";
            Require(
                string.Equals(ThemeBrushColor(theme, "AccentTextBrush"), expectedAccentText, StringComparison.OrdinalIgnoreCase),
                $"{themeName} does not use its maximum-contrast black/white accent foreground");

            foreach (var (state, foregroundKey, backgroundKey) in activeButtonContrastPairs)
            {
                var contrast = ContrastRatio(
                    ThemeBrushColor(theme, foregroundKey),
                    ThemeBrushColor(theme, backgroundKey));
                Require(
                    contrast >= 4.5d,
                    $"{themeName} {state} button contrast is only {contrast:F2}:1");
            }

            var disabledContrast = ContrastRatio(
                ThemeBrushColor(theme, "TextDisabledBrush"),
                ThemeBrushColor(theme, "SurfaceRaisedBrush"));
            Require(
                disabledContrast >= 3d,
                $"{themeName} disabled button contrast is only {disabledContrast:F2}:1");
        }

        await RunOnWpfDispatcherAsync(() =>
        {
            foreach (var themePath in themePaths)
            {
                var host = new System.Windows.Controls.Grid();
                host.Resources.MergedDictionaries.Add(LoadWpfResourceDictionary(themePath));
                host.Resources.MergedDictionaries.Add(LoadWpfResourceDictionary(Path.Combine(themeRoot, "Controls.xaml")));
                var button = new System.Windows.Controls.Button
                {
                    Content = "Search",
                    Style = (System.Windows.Style)host.Resources["PrimaryButtonStyle"],
                };
                host.Children.Add(button);
                host.Measure(new System.Windows.Size(200d, 80d));
                host.Arrange(new System.Windows.Rect(0d, 0d, 200d, 80d));
                host.UpdateLayout();

                var generatedLabel = FindVisualDescendant<System.Windows.Controls.TextBlock>(button)
                    ?? throw new InvalidOperationException("Primary button label was not created.");
                var actualForeground = generatedLabel.Foreground as System.Windows.Media.SolidColorBrush;
                var expectedForeground = host.Resources["AccentTextBrush"]
                    as System.Windows.Media.SolidColorBrush;
                Require(
                    actualForeground?.Color == expectedForeground?.Color,
                    $"{Path.GetFileNameWithoutExtension(themePath)} primary button renders "
                    + $"{actualForeground?.Color} instead of accent text {expectedForeground?.Color}");
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);

        var controls = System.Xml.Linq.XDocument.Load(Path.Combine(themeRoot, "Controls.xaml"));
        var defaultTextBlockStyle = controls.Root!.Elements().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals((string?)element.Attribute("TargetType"), "TextBlock", StringComparison.Ordinal)
            && !element.Attributes().Any(attribute => attribute.Name.LocalName == "Key"));
        Require(
            defaultTextBlockStyle.Elements().Any(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal)
                && ((string?)element.Attribute("Value"))?.Contains("TextBrush", StringComparison.Ordinal) == true),
            "ordinary text blocks do not retain the theme's normal readable foreground");
        var primaryButtonStyle = controls.Root!.Elements().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "PrimaryButtonStyle"));
        Require(
            primaryButtonStyle.Elements().Any(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal)
                && ((string?)element.Attribute("Value"))?.Contains("AccentTextBrush", StringComparison.Ordinal) == true),
            "primary buttons do not select the theme's accent-text foreground");
        var buttonContentPresenter = controls.Root!.Elements()
            .Single(element =>
                element.Name.LocalName == "Style"
                && string.Equals((string?)element.Attribute("TargetType"), "Button", StringComparison.Ordinal)
                && !element.Attributes().Any(attribute => attribute.Name.LocalName == "Key"))
            .Descendants()
            .Single(element => element.Name.LocalName == "ContentPresenter");
        Require(
            buttonContentPresenter.Attributes().Any(attribute =>
                attribute.Name.LocalName == "TextElement.Foreground"
                && attribute.Value.Contains("Binding Foreground", StringComparison.Ordinal)
                && attribute.Value.Contains("RelativeSource TemplatedParent", StringComparison.Ordinal)),
            "button templates do not bind their resolved foreground into generated label text");
        Require(
            buttonContentPresenter.Descendants().Any(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal)
                && ((string?)element.Attribute("Value"))?.Contains("AncestorType=Button", StringComparison.Ordinal) == true),
            "the global TextBlock style can override a button label's high-contrast foreground");
        var columnResizeThumbStyle = controls.Root!.Elements().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "ColumnHeaderResizeThumbStyle"));
        Require(
            columnResizeThumbStyle.Elements().Any(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "Background", StringComparison.Ordinal)
                && string.Equals((string?)element.Attribute("Value"), "Transparent", StringComparison.Ordinal))
            && columnResizeThumbStyle.Descendants().Any(element => element.Name.LocalName == "ControlTemplate"),
            "column resize handles can fall back to WPF's thick native Thumb chrome");
        var columnHeaderStyle = controls.Root!.Elements().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals((string?)element.Attribute("TargetType"), "DataGridColumnHeader", StringComparison.Ordinal));
        var columnHeaderGrippers = columnHeaderStyle.Descendants()
            .Where(element => element.Name.LocalName == "Thumb"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value is "PART_LeftHeaderGripper" or "PART_RightHeaderGripper"))
            .ToArray();
        Require(
            columnHeaderGrippers.Length == 2
            && columnHeaderGrippers.All(element =>
                ((string?)element.Attribute("Style"))?.Contains("ColumnHeaderResizeThumbStyle", StringComparison.Ordinal) == true),
            "column headers do not keep invisible resize hit targets over their one-pixel dividers");
        var gridSplitterStyle = controls.Root!.Elements().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals((string?)element.Attribute("TargetType"), "GridSplitter", StringComparison.Ordinal));
        var splitterLine = gridSplitterStyle.Descendants().Single(element =>
            element.Name.LocalName == "Border"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "SplitterLine"));
        Require(
            string.Equals((string?)splitterLine.Attribute("Width"), "1", StringComparison.Ordinal)
            && gridSplitterStyle.Descendants().Where(element => element.Name.LocalName == "Setter")
                .Any(element => string.Equals((string?)element.Attribute("TargetName"), "SplitterLine", StringComparison.Ordinal)),
            "pane splitters do not remain a one-pixel line while hovered or dragged");
        foreach (var styleKey in new[] { "WorkspaceNavigationButtonStyle", "TopNavigationActionButtonStyle" })
        {
            var contentPresenter = controls.Root!.Elements()
                .Single(element => element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key" && attribute.Value == styleKey))
                .Descendants()
                .Single(element => element.Name.LocalName == "ContentPresenter");
            Require(
                contentPresenter.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "TextElement.Foreground"
                    && attribute.Value.Contains("TemplateBinding Foreground", StringComparison.Ordinal)),
                $"{styleKey} does not pass its readable foreground into label text");
        }
        var buttonStyles = controls.Root!.Elements().Where(element =>
            element.Name.LocalName == "Style"
            && (string.Equals((string?)element.Attribute("TargetType"), "Button", StringComparison.Ordinal)
                || element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key" && attribute.Value == "WorkspaceNavigationButtonStyle")));
        Require(
            !buttonStyles.SelectMany(static style => style.Descendants()).Any(element => element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "Opacity", StringComparison.Ordinal)
                && element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "Trigger"
                    && string.Equals((string?)ancestor.Attribute("Property"), "IsEnabled", StringComparison.Ordinal)
                    && string.Equals((string?)ancestor.Attribute("Value"), "False", StringComparison.Ordinal))),
            "disabled button labels are faded after selecting an accessible foreground");

        var window = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        var progressBindings = window
            .Descendants()
            .Where(element => element.Name.LocalName == "ProgressBar")
            .SelectMany(element => new[] { (string?)element.Attribute("Value"), (string?)element.Attribute("IsIndeterminate") })
            .Where(static value => value?.StartsWith("{Binding", StringComparison.Ordinal) == true)
            .ToArray();
        Require(progressBindings.Length >= 2, "MainWindow has no bound progress indicator to validate");
        Require(
            progressBindings.All(static binding => binding!.Contains("Mode=OneWay", StringComparison.Ordinal)),
            "a read-only progress property uses WPF's default TwoWay binding");
    }

    private static Task TestApplicationBrandingAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Cdmw.ArchiveLite.App");
        var assetRoot = Path.Combine(appRoot, "Assets");
        var iconPath = Path.Combine(assetRoot, "ArchiveLite.ico");
        var pngPath = Path.Combine(assetRoot, "ArchiveLite.png");
        var svgPath = Path.Combine(assetRoot, "ArchiveLite.svg");

        var iconBytes = File.ReadAllBytes(iconPath);
        Require(
            iconBytes.Length > 6
            && BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(0, 2)) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(2, 2)) == 1,
            "Archive Lite application icon is not a valid ICO container");
        var frameCount = BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(4, 2));
        Require(frameCount >= 7 && iconBytes.Length >= 6 + (16 * frameCount), "application icon has too few size variants");
        var frameSizes = new HashSet<int>();
        for (var index = 0; index < frameCount; index++)
        {
            var entryOffset = 6 + (16 * index);
            var width = iconBytes[entryOffset] == 0 ? 256 : iconBytes[entryOffset];
            var height = iconBytes[entryOffset + 1] == 0 ? 256 : iconBytes[entryOffset + 1];
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(iconBytes.AsSpan(entryOffset + 8, 4));
            var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(iconBytes.AsSpan(entryOffset + 12, 4));
            Require(width == height, "application icon contains a non-square frame");
            Require(
                payloadLength > 8
                && payloadOffset <= (uint)iconBytes.Length
                && payloadLength <= (uint)iconBytes.Length - payloadOffset
                && iconBytes.AsSpan((int)payloadOffset, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "application icon contains an invalid PNG frame");
            frameSizes.Add(width);
        }
        Require(
            new[] { 16, 24, 32, 48, 64, 128, 256 }.All(frameSizes.Contains),
            "application icon does not cover standard Windows shell sizes");

        var pngBytes = File.ReadAllBytes(pngPath);
        Require(
            pngBytes.Length > 24
            && pngBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(16, 4)) == 1024
            && BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(20, 4)) == 1024,
            "Archive Lite branding preview is not a 1024-pixel PNG");

        var svg = System.Xml.Linq.XDocument.Load(svgPath);
        var liteMark = svg.Descendants().SingleOrDefault(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "id" && attribute.Value == "lite-mark"));
        Require(
            liteMark?.Elements().Count(element => element.Name.LocalName == "rect") == 3,
            "Archive Lite vector mark must retain its distinct three-cell L silhouette");

        var appProject = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "Cdmw.ArchiveLite.App.csproj"));
        var appIcon = appProject.Descendants().Single(element => element.Name.LocalName == "ApplicationIcon");
        Require(
            string.Equals(appIcon.Value.Replace('\\', '/'), "Assets/ArchiveLite.ico", StringComparison.Ordinal)
            && appIcon.Attributes().All(attribute => attribute.Name.LocalName != "Condition"),
            "WPF application icon is optional or points at the wrong asset");
        Require(
            appProject.Descendants().Any(element => element.Name.LocalName == "Resource"
                && string.Equals(
                    ((string?)element.Attribute("Include"))?.Replace('\\', '/'),
                    "Assets/ArchiveLite.ico",
                    StringComparison.Ordinal)),
            "WPF window icon is not embedded as an application resource");

        var standaloneProject = System.Xml.Linq.XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Standalone",
            "Cdmw.ArchiveLite.Standalone.csproj"));
        Require(
            standaloneProject.Descendants().Any(element => element.Name.LocalName == "ApplicationIcon"
                && element.Value.Replace('\\', '/').EndsWith("/Assets/ArchiveLite.ico", StringComparison.Ordinal)),
            "standalone launcher does not embed the Archive Lite icon");

        var controls = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        var windowStyle = controls.Root!.Elements().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals((string?)element.Attribute("TargetType"), "Window", StringComparison.Ordinal));
        Require(
            windowStyle.Elements().Any(element => element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "Icon", StringComparison.Ordinal)
                && ((string?)element.Attribute("Value"))?.EndsWith("/Assets/ArchiveLite.ico", StringComparison.Ordinal) == true),
            "WPF windows do not consistently use the Archive Lite icon");

        return Task.CompletedTask;
    }

    private static Task TestWorkspacePaneSizingAsync()
    {
        Require(
            WorkspacePaneSizing.CalculatePreviewMaximum(1900, 278, 410, 14, 350) == 1000,
            "wide workspaces do not retain the established preview-width ceiling");
        Require(
            WorkspacePaneSizing.CalculatePreviewMaximum(1200, 278, 410, 14, 350) == 498,
            "preview width is not reduced to the remaining live workspace width");
        Require(
            WorkspacePaneSizing.CalculatePreviewMaximum(900, 278, 410, 14, 350) == 350,
            "preview width can shrink below its usable minimum");
        return Task.CompletedTask;
    }

    private static Task TestModernShellAsync()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.App");
        var window = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        Require(
            string.Equals((string?)window.Root?.Attribute("WindowStyle"), "None", StringComparison.Ordinal),
            "MainWindow is still using the bare native window shell");
        Require(
            window.Descendants().Any(element => element.Name.LocalName == "WindowChrome"),
            "MainWindow has no resizable custom window chrome");
        Require(
            window.Descendants().Any(element => ((string?)element.Attribute("Command"))?.Contains("DetectGameCommand", StringComparison.Ordinal) == true),
            "MainWindow has no game-folder detection action");
        Require(
            !window.Descendants().Any(element => ((string?)element.Attribute("Command"))?.Contains("ExportMeshCommand", StringComparison.Ordinal) == true)
            && window.Descendants().Any(element => ((string?)element.Attribute("Command"))?.Contains("ExportSelectedCommand", StringComparison.Ordinal) == true),
            "selected mesh export is not unified under Export selected");
        Require(
            window.Descendants().Any(element => ((string?)element.Attribute("Text"))?.Contains("CacheHealthLabel", StringComparison.Ordinal) == true),
            "MainWindow does not expose archive cache health");

        var searchQuery = window.Descendants()
            .Single(element => element.Name.LocalName == "TextBox"
                && ((string?)element.Attribute("Text"))?.Contains("TextSearch.Query", StringComparison.Ordinal) == true);
        Require(
            searchQuery.Descendants().Any(element => element.Name.LocalName == "KeyBinding"
                && string.Equals((string?)element.Attribute("Key"), "Enter", StringComparison.Ordinal)
                && ((string?)element.Attribute("Command"))?.Contains("TextSearch.SearchCommand", StringComparison.Ordinal) == true),
            "the main text query does not start searching when Enter is pressed");

        var controls = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        var styleKeys = controls.Root!
            .Elements()
            .Select(element => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value)
            .Where(static key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        Require(styleKeys.Contains("WindowCaptionButtonStyle"), "custom chrome has no theme-aware caption button style");
        Require(styleKeys.Contains("TopBarComboBoxStyle"), "custom chrome has no compact top-bar selector style");
        Require(styleKeys.Contains("WorkspaceNavigationButtonStyle"), "title row has no shared workspace navigation style");
        Require(styleKeys.Contains("WorkspaceContentTabControlStyle"), "workspace content has no headerless tab host style");
        var topBarComboBoxStyle = controls.Root!.Elements().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "TopBarComboBoxStyle"));
        var topBarMinimumHeight = double.Parse(
            topBarComboBoxStyle.Elements().Single(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "MinHeight", StringComparison.Ordinal))
            .Attribute("Value")!.Value,
            CultureInfo.InvariantCulture);
        Require(
            topBarComboBoxStyle.Elements().Any(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string?)element.Attribute("Property"), "VerticalAlignment", StringComparison.Ordinal)
                && string.Equals((string?)element.Attribute("Value"), "Center", StringComparison.Ordinal)),
            "top-bar selectors are not vertically centered in the fixed-height title row");
        var shellGrid = window.Root!.Elements().Single(element => element.Name.LocalName == "Grid");
        var titleRowHeight = double.Parse(
            shellGrid.Elements().Single(element => element.Name.LocalName == "Grid.RowDefinitions")
                .Elements().First().Attribute("Height")!.Value,
            CultureInfo.InvariantCulture);
        var titleBarGrid = shellGrid.Elements().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "TitleBarGrid"));
        var topBarSelectors = titleBarGrid.Elements().Where(element =>
            element.Name.LocalName == "ComboBox"
            && ((string?)element.Attribute("Style"))?.Contains("TopBarComboBoxStyle", StringComparison.Ordinal) == true)
            .ToArray();
        Require(topBarSelectors.Length == 4, "title row does not expose all four appearance selectors");
        foreach (var selector in topBarSelectors)
        {
            var margin = ((string?)selector.Attribute("Margin"))?.Split(',')
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
            Require(
                margin?.Length == 4 && titleRowHeight - margin[1] - margin[3] >= topBarMinimumHeight + 1d,
                "a top-bar selector's vertical margins clip its rounded border below the style's minimum height");
        }
        var globallySizedControlTypes = new[]
        {
            "Window",
            "TextBlock",
            "Label",
            "Button",
            "TextBox",
            "ComboBox",
            "ComboBoxItem",
            "CheckBox",
            "GroupBox",
            "ListBox",
            "ListBoxItem",
            "DataGrid",
            "DataGridColumnHeader",
            "DataGridRow",
            "DataGridCell",
            "ToolTip",
            "Menu",
            "ContextMenu",
            "MenuItem",
        };
        foreach (var controlType in globallySizedControlTypes)
        {
            var style = controls.Root!.Elements()
                .SingleOrDefault(element => element.Name.LocalName == "Style"
                    && string.Equals((string?)element.Attribute("TargetType"), controlType, StringComparison.Ordinal)
                    && element.Attributes().All(attribute => attribute.Name.LocalName != "Key"));
            Require(
                style?.Elements().Any(element => element.Name.LocalName == "Setter"
                    && string.Equals((string?)element.Attribute("Property"), "FontSize", StringComparison.Ordinal)
                    && ((string?)element.Attribute("Value"))?.Contains("DynamicResource BaseFontSize", StringComparison.Ordinal) == true) == true,
                $"{controlType} does not follow the global font-size resource");
        }
        Require(
            window.Descendants().Any(element => ((string?)element.Attribute("ItemsSource"))?.Contains("FontSizes", StringComparison.Ordinal) == true)
            && window.Descendants().Any(element => ((string?)element.Attribute("ItemsSource"))?.Contains("LayoutDensities", StringComparison.Ordinal) == true),
            "title row has no global font-size and layout-density selectors");
        var navigationButtons = window.Descendants()
            .Where(element => element.Name.LocalName == "ToggleButton"
                && string.Equals((string?)element.Attribute("Click"), "OnWorkspaceNavigationClick", StringComparison.Ordinal))
            .ToArray();
        Require(navigationButtons.Length == 2, "Archive Browser and Text Search were not both moved into the title row");
        var topTabs = window.Descendants().Single(element => element.Name.LocalName == "TabControl");
        Require(
            string.Equals((string?)topTabs.Attribute("Margin"), "12,6,12,8", StringComparison.Ordinal)
            && ((string?)topTabs.Attribute("Style"))?.Contains("WorkspaceContentTabControlStyle", StringComparison.Ordinal) == true
            && topTabs.Elements().Where(element => element.Name.LocalName == "TabItem")
                .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "Grid"))
                .All(element => string.Equals((string?)element.Attribute("Margin"), "0", StringComparison.Ordinal)),
            "workspace content still stacks a separate tab row or duplicate page margins");
        Require(
            string.Equals((string?)topTabs.Attribute("SelectedIndex"), "0", StringComparison.Ordinal),
            "Archive Browser is not the explicit default startup workspace");
        var xamlText = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        Require(
            xamlText.Split("TextElement.FontSize=\"{DynamicResource BaseFontSize}\"", StringSplitOptions.None).Length >= 3,
            "custom Archive Browser menus do not follow the global font-size resource");
        Require(
            !xamlText.Contains("ProductSubtitle", StringComparison.Ordinal)
            && !xamlText.Contains("ReadOnlyWorkspace", StringComparison.Ordinal)
            && !xamlText.Contains("ArchiveBrowser.ReadOnlyCaption", StringComparison.Ordinal)
            && !xamlText.Contains(">CD<", StringComparison.Ordinal),
            "the compact title/archive header still contains the removed icon, subtitle, workspace pill, or read-only caption");
        Require(
            xamlText.Contains("x:Name=\"ArchiveExtensionFilterComboBox\"", StringComparison.Ordinal)
            && xamlText.Contains("SelectionChanged=\"OnArchiveExtensionSelectionChanged\"", StringComparison.Ordinal),
            "the Archive Browser extension catalogue is not wired to apply selected extensions");
        Require(
            xamlText.Contains("OperationProgressDetail", StringComparison.Ordinal)
            && xamlText.Contains("OperationProgressPercent", StringComparison.Ordinal),
            "archive operations do not expose real progress detail and a determinate progress value");
        var textSearchResultsGrid = window.Descendants().Single(element => element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == "TextSearchResultsGrid"));
        Require(
            textSearchResultsGrid.Attributes().Any(attribute =>
                attribute.Name.LocalName == "RoundedClip.Radius"
                && attribute.Name.NamespaceName.Contains("Infrastructure", StringComparison.Ordinal)
                && attribute.Value == "11"),
            "the Text Search results grid can paint square content over its card corners");
        var roundedClipMatchesBounds = false;
        var roundedClipThread = new Thread(() =>
        {
            var clipTarget = new System.Windows.Controls.Border { Width = 240d, Height = 120d };
            RoundedClip.SetRadius(clipTarget, 11d);
            clipTarget.Measure(new System.Windows.Size(240d, 120d));
            clipTarget.Arrange(new System.Windows.Rect(0d, 0d, 240d, 120d));
            clipTarget.UpdateLayout();
            roundedClipMatchesBounds = clipTarget.Clip is System.Windows.Media.RectangleGeometry geometry
                && geometry.Rect == new System.Windows.Rect(0d, 0d, 240d, 120d)
                && geometry.RadiusX == 11d
                && geometry.RadiusY == 11d;
        });
        roundedClipThread.SetApartmentState(ApartmentState.STA);
        roundedClipThread.Start();
        roundedClipThread.Join();
        Require(roundedClipMatchesBounds, "rounded clipping does not follow the rendered results-grid bounds");
        Require(
            xamlText.Contains("ArchiveBrowser.ExportFamilyCommand", StringComparison.Ordinal),
            "Export Options does not expose family export for the current selection");
        Require(
            !xamlText.Contains("ArchiveBrowser.PackageFilter", StringComparison.Ordinal)
            && !xamlText.Contains("ArchiveBrowser.PreviewableOnly", StringComparison.Ordinal)
            && !xamlText.Contains("ArchiveBrowser.SelectedRole", StringComparison.Ordinal),
            "removed Package, Previewable only, or Role controls still appear in Filters");
        Require(
            xamlText.Contains("AssociatedAssets.CloseDrawerCommand", StringComparison.Ordinal)
            && xamlText.Contains("Grid.RowSpan=\"7\"", StringComparison.Ordinal),
            "associated assets are not exposed as a compact preview-side drawer");
        Require(
            xamlText.Contains("x:Name=\"ArchiveTextPreviewEditor\"", StringComparison.Ordinal)
            && xamlText.Contains("x:Name=\"TextSearchPreviewEditor\"", StringComparison.Ordinal)
            && xamlText.Contains("AvalonEditBinding.Syntax", StringComparison.Ordinal)
            && xamlText.Contains("FindInFile", StringComparison.Ordinal),
            "text previews do not expose full-document syntax coloring and in-file search");
        var namedLayoutElements = window.Descendants()
            .SelectMany(element => element.Attributes()
                .Where(attribute => attribute.Name.LocalName == "Name")
                .Select(static attribute => attribute.Value))
            .ToHashSet(StringComparer.Ordinal);
        Require(
            new[]
            {
                "ArchiveFilterColumn",
                "ArchivePreviewColumn",
                "TextSearchFilterColumn",
                "TextSearchPreviewColumn",
                "TextSearchResultsGrid",
            }.All(namedLayoutElements.Contains),
            "user-resizable panes or result columns are not addressable for persistence");
        var previewColumns = window.Descendants()
            .Where(element => element.Name.LocalName == "ColumnDefinition"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && (attribute.Value == "ArchivePreviewColumn"
                        || attribute.Value == "TextSearchPreviewColumn")))
            .ToArray();
        Require(
            previewColumns.Length == 2
            && previewColumns.All(element =>
                double.TryParse(
                    (string?)element.Attribute("MaxWidth"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var maximum)
                && maximum == WorkspacePaneSizing.MaximumPreviewWidth),
            "preview splitters can grow their panes beyond the live workspace width policy");
        var windowSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
        Require(
            windowSource.Split("WorkspaceTabs.SelectedIndex = 0;", StringSplitOptions.None).Length >= 3,
            "startup does not defensively restore Archive Browser after XAML initialization and load");
        Require(
            windowSource.Contains("CaptureUiState();", StringComparison.Ordinal)
            && windowSource.Contains("CaptureWindowPlacement()", StringComparison.Ordinal)
            && windowSource.Contains("CaptureGridColumnLayout", StringComparison.Ordinal),
            "window, pane, or column resizing is not captured during orderly shutdown");
        Require(
            windowSource.Contains("OnArchiveExtensionSelectionChanged", StringComparison.Ordinal)
            && windowSource.Contains("ArchiveBrowser.ExtensionFilter = choice.Extension", StringComparison.Ordinal),
            "extension catalogue selection does not update the Archive Browser filter");
        var exportDialog = System.Xml.Linq.XDocument.Load(
            Path.Combine(appRoot, "Dialogs", "ExportSelectionDialog.xaml"));
        Require(
            exportDialog.Descendants().Count(element => element.Name.LocalName == "RadioButton") >= 3
            && exportDialog.Descendants().Any(element => element.Name.LocalName == "ComboBox"),
            "Export selected does not offer file-only, folder-structure, family, and model-format choices");
        var controlsSource = File.ReadAllText(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        var controlsDocument = System.Xml.Linq.XDocument.Parse(controlsSource);
        var workspaceTabStyle = controlsDocument.Descendants().Single(element =>
            element.Name.LocalName == "Style"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "WorkspaceContentTabControlStyle"));
        Require(
            !workspaceTabStyle.Descendants().Any(element => element.Name.LocalName == "TabPanel")
            && controlsSource.Contains("CornerRadius=\"9\"", StringComparison.Ordinal)
            && controlsSource.Contains("BorderThickness=\"{TemplateBinding BorderThickness}\"", StringComparison.Ordinal),
            "title-row navigation still uses the clipped secondary tab panel");

        var cacheDialog = System.Xml.Linq.XDocument.Load(
            Path.Combine(appRoot, "Dialogs", "ArchiveCacheChoiceDialog.xaml"));
        Require(
            string.Equals((string?)cacheDialog.Root?.Attribute("WindowStyle"), "None", StringComparison.Ordinal),
            "the cache choice still uses a bare native dialog shell");
        Require(
            cacheDialog.Descendants().Count(element => element.Name.LocalName == "Button"
                && ((string?)element.Attribute("Click") is "OnPersistentClick" or "OnSessionOnlyClick")) == 2,
            "the cache dialog does not expose both persistent and session-only choices");
        Require(
            cacheDialog.Descendants().Where(element => element.Name.LocalName == "TextBlock")
                .Any(element => string.Equals((string?)element.Attribute("TextWrapping"), "Wrap", StringComparison.Ordinal)),
            "cache choice explanations cannot wrap safely");
        Require(
            string.Equals((string?)cacheDialog.Root?.Attribute("AllowsTransparency"), "True", StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)cacheDialog.Root?.Attribute("Background"), "Transparent", StringComparison.OrdinalIgnoreCase),
            "cache dialog does not use a transparent rounded host window");
        Require(
            !cacheDialog.Descendants().Any(element => element.Name.LocalName == "WindowChrome"),
            "cache dialog still combines WindowChrome with rounded content and can expose white corner arcs");
        var cacheDialogFrame = cacheDialog.Descendants().Single(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "CacheDialogFrame"));
        Require(
            string.Equals((string?)cacheDialogFrame.Attribute("Margin"), "12", StringComparison.Ordinal)
            && string.Equals((string?)cacheDialogFrame.Attribute("CornerRadius"), "14", StringComparison.Ordinal),
            "cache dialog shadow and rounded frame are not inset safely from the window edge");

        var archiveViewModelSource = File.ReadAllText(
            Path.Combine(appRoot, "ViewModels", "ArchiveBrowserViewModel.cs"));
        Require(
            archiveViewModelSource.Contains("ChooseAndOpenArchiveAsync(false", StringComparison.Ordinal)
            && archiveViewModelSource.Contains("ChooseAndOpenArchiveAsync(true", StringComparison.Ordinal),
            "Load and Refresh do not share the cache-choice flow");
        return Task.CompletedTask;
    }

    private static Task TestPreviewPresentationAsync()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.App");
        var window = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        var previewLayout = window.Descendants().Single(element => element.Attributes().Any(
            attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ArchivePreviewCardLayout"));
        var associatedAssetsDrawer = previewLayout.Descendants().Single(element =>
            ((string?)element.Attribute("Visibility"))?.Contains(
                "ArchiveBrowser.AssociatedAssets.IsExpanded",
                StringComparison.Ordinal) == true);
        Require(
            associatedAssetsDrawer.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Grid.Column" && attribute.Value == "1")
            && !associatedAssetsDrawer.Attributes().Any(attribute => attribute.Name.LocalName == "Panel.ZIndex"),
            "associated assets still overlap the native preview instead of occupying the adjacent layout column");

        var textureToggle = window.Descendants().Single(element =>
            ((string?)element.Attribute("IsChecked"))?.Contains(
                "ArchiveBrowser.ShowModelTextures",
                StringComparison.Ordinal) == true);
        Require(
            textureToggle.Name.LocalName == "CheckBox"
            && ((string?)textureToggle.Attribute("Visibility"))?.Contains("IsModelSelection", StringComparison.Ordinal) == true,
            "the on-demand texture option is not a model-only checkbox beside the preview actions");
        Require(
            window.Descendants().Any(element =>
                string.Equals((string?)element.Attribute("Click"), "OnModelPreviewSettingsClick", StringComparison.Ordinal)
                && ((string?)element.Attribute("Visibility"))?.Contains("IsModelSelection", StringComparison.Ordinal) == true),
            "model previews do not expose the Preview Settings window");
        var modelPreviewHost = window.Descendants().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "ModelPreviewHost"));
        foreach (var binding in new[]
        {
            "ModelPreviewOrbitSensitivity",
            "ModelPreviewPanSensitivity",
            "ModelPreviewInvertOrbitX",
            "ModelPreviewInvertOrbitY",
            "ModelPreviewInvertPanX",
            "ModelPreviewInvertPanY",
        })
        {
            Require(
                modelPreviewHost.Attributes().Any(attribute => attribute.Value.Contains(binding, StringComparison.Ordinal)),
                $"model preview host is missing the live {binding} binding");
        }
        var settingsDialog = System.Xml.Linq.XDocument.Load(Path.Combine(
            appRoot,
            "Dialogs",
            "ModelPreviewSettingsDialog.xaml"));
        var settingsTab = settingsDialog.Descendants().Single(element =>
            element.Name.LocalName == "TabControl"
            && ((string?)element.Attribute("Name") ?? element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "Name")?.Value) == "SettingsTabs");
        var settingsSliders = settingsDialog.Descendants()
            .Where(element => element.Name.LocalName == "Slider")
            .ToArray();
        Require(
            settingsDialog.Descendants().Any(element =>
                element.Name.LocalName == "TabItem"
                && ((string?)element.Attribute("Header"))?.Contains("CameraInput", StringComparison.Ordinal) == true)
            && settingsSliders.Length == 2
            && settingsDialog.Descendants().Count(element =>
                element.Name.LocalName == "CheckBox"
                && ((string?)element.Attribute("IsChecked"))?.Contains("ModelPreviewInvert", StringComparison.Ordinal) == true) == 4,
            "Preview Settings does not provide the requested Orbit/Pan camera-input tab");
        Require(
            ((string?)settingsTab.Attribute("Style"))?.Contains("SettingsTabControlStyle", StringComparison.Ordinal) == true
            && settingsSliders.All(element =>
                ((string?)element.Attribute("Style"))?.Contains("SettingsSliderStyle", StringComparison.Ordinal) == true),
            "Preview Settings still falls back to the platform's white TabControl or Slider surfaces");
        var settingsControls = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        foreach (var styleKey in new[]
        {
            "SettingsTabItemStyle",
            "SettingsTabControlStyle",
            "SettingsSliderTrackButtonStyle",
            "SettingsSliderThumbStyle",
            "SettingsSliderStyle",
        })
        {
            Require(
                settingsControls.Descendants().Any(element => element.Name.LocalName == "Style"
                    && element.Attributes().Any(attribute =>
                        attribute.Name.LocalName == "Key" && attribute.Value == styleKey)),
                $"the dark Preview Settings control theme is missing {styleKey}");
        }

        var imagePreview = previewLayout.Descendants().Single(element =>
            element.Name.LocalName == "Image"
            && ((string?)element.Attribute("Source"))?.Contains(
                "ArchiveBrowser.PreviewImage",
                StringComparison.Ordinal) == true);
        Require(
            string.Equals((string?)imagePreview.Attribute("Stretch"), "Uniform", StringComparison.Ordinal)
            && string.Equals((string?)imagePreview.Parent?.Attribute("ClipToBounds"), "True", StringComparison.Ordinal)
            && !imagePreview.Ancestors().Any(element => element.Name.LocalName == "ScrollViewer"),
            "image previews are not constrained to an aspect-preserving, scrollbar-free viewport");

        var previewEditors = window.Descendants()
            .Where(element => element.Name.LocalName == "TextEditor")
            .ToArray();
        Require(
            previewEditors.Length == 2
            && previewEditors.All(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "LineNumbersForeground"
                && attribute.Value.Contains("TextMutedBrush", StringComparison.Ordinal))),
            "text-preview line numbers do not use the theme's readable muted foreground");

        var controls = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        var sectionTitleStyle = controls.Root!.Elements().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "SectionTitleStyle"));
        Require(
            sectionTitleStyle.Elements().Any(element =>
                string.Equals((string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal)
                && ((string?)element.Attribute("Value"))?.Contains("TextBrush", StringComparison.Ordinal) == true),
            "preview and associated-assets headings can inherit the platform's black default foreground");

        var highlightingSource = File.ReadAllText(Path.Combine(appRoot, "Infrastructure", "AvalonEditBinding.cs"));
        foreach (var color in new[]
        {
            "#D4D4D4", "#6A9955", "#569CD6", "#9CDCFE", "#CE9178", "#B5CEA8",
            "#1F1F1F", "#008000", "#0000FF", "#001080", "#A31515", "#098658",
        })
        {
            Require(
                highlightingSource.Contains(color, StringComparison.Ordinal),
                $"the common light/dark editor palette is missing {color}");
        }
        Require(
            highlightingSource.Contains("ThemeManager.Current.IsDark", StringComparison.Ordinal)
            && highlightingSource.Contains("HighlightingLoader.Load", StringComparison.Ordinal),
            "syntax definitions are not rebuilt against the active theme palette");

        var resolver = typeof(AvalonEditBinding).GetMethod(
            "ResolveHighlighting",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(AvalonEditBinding), "ResolveHighlighting");
        ThemeManager.Apply("graphite");
        var darkXml = ResolveTestHighlighting(resolver, ".xml", "dark XML");
        ThemeManager.Apply("light");
        var lightXml = ResolveTestHighlighting(resolver, ".xml", "light XML");
        ThemeManager.Apply("graphite");
        Require(
            HighlightingForeground(darkXml, "XmlTag").Contains("569CD6", StringComparison.OrdinalIgnoreCase)
            && HighlightingForeground(darkXml, "AttributeName").Contains("9CDCFE", StringComparison.OrdinalIgnoreCase)
            && HighlightingForeground(darkXml, "AttributeValue").Contains("CE9178", StringComparison.OrdinalIgnoreCase)
            && HighlightingForeground(darkXml, "Comment").Contains("6A9955", StringComparison.OrdinalIgnoreCase),
            "the runtime XML highlighter did not apply the readable Dark+ palette");
        Require(
            HighlightingForeground(lightXml, "XmlTag").Contains("0000FF", StringComparison.OrdinalIgnoreCase)
            && HighlightingForeground(lightXml, "AttributeName").Contains("001080", StringComparison.OrdinalIgnoreCase)
            && HighlightingForeground(lightXml, "AttributeValue").Contains("A31515", StringComparison.OrdinalIgnoreCase)
            && HighlightingForeground(lightXml, "Comment").Contains("008000", StringComparison.OrdinalIgnoreCase),
            "the runtime XML highlighter did not apply the readable Light+ palette");

        var windowSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
        Require(
            windowSource.Contains("AvalonEditBinding.RefreshSyntax(ArchiveTextPreviewEditor)", StringComparison.Ordinal)
            && windowSource.Contains("AvalonEditBinding.RefreshSyntax(TextSearchPreviewEditor)", StringComparison.Ordinal),
            "live theme changes do not refresh both text preview editors");
        return Task.CompletedTask;
    }

    private static string HighlightingForeground(IHighlightingDefinition definition, string colorName) =>
        definition.GetNamedColor(colorName)?.Foreground?.ToString() ?? string.Empty;

    private static IHighlightingDefinition ResolveTestHighlighting(MethodInfo resolver, string syntax, string label)
    {
        try
        {
            return resolver.Invoke(null, [syntax]) as IHighlightingDefinition
                ?? throw new InvalidDataException($"{label} highlighting did not load");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"{label} highlighting failed: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}",
                exception.InnerException);
        }
    }

    private static Task TestArchiveGridFeaturesAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.App");
        var window = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        var archiveGrid = window
            .Descendants()
            .Single(element => element.Name.LocalName == "DataGrid"
                && element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ArchiveGrid"));
        Require(
            string.Equals((string?)archiveGrid.Attribute("CanUserSortColumns"), "True", StringComparison.OrdinalIgnoreCase),
            "archive grid column sorting is disabled");
        Require(
            string.Equals((string?)archiveGrid.Attribute("CanUserReorderColumns"), "True", StringComparison.OrdinalIgnoreCase),
            "archive grid column reordering is disabled");
        Require(
            string.Equals((string?)archiveGrid.Attribute("SelectionMode"), "Extended", StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)archiveGrid.Attribute("SelectionChanged"), "OnArchiveGridSelectionChanged", StringComparison.Ordinal),
            "archive grid does not support exporting multiple selected files");
        Require(
            double.TryParse((string?)archiveGrid.Attribute("MinColumnWidth"), out var minimumColumnWidth)
            && minimumColumnWidth <= 48,
            "archive grid still prevents users from freely narrowing columns");
        Require(
            string.Equals((string?)archiveGrid.Attribute("CanUserResizeColumns"), "True", StringComparison.OrdinalIgnoreCase)
            && archiveGrid.Descendants().Where(element => element.Name.LocalName.EndsWith("Column", StringComparison.Ordinal)).All(element => element.Attribute("MinWidth") is null),
            "archive grid columns retain fixed per-column resize constraints");
        Require(
            archiveGrid.Attributes().Any(attribute =>
                attribute.Name.LocalName.EndsWith("HorizontalScrollBarVisibility", StringComparison.Ordinal)
                && string.Equals(attribute.Value, "Auto", StringComparison.Ordinal)),
            "archive grid has no horizontal overflow path for user-selected columns");
        var sortMembers = archiveGrid
            .Descendants()
            .Select(element => (string?)element.Attribute("SortMemberPath"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        var expectedSortMembers = new[]
        {
            nameof(ArchiveSortField.Name),
            nameof(ArchiveSortField.KnownName),
            nameof(ArchiveSortField.NameEvidence),
            nameof(ArchiveSortField.Extension),
            nameof(ArchiveSortField.FileType),
            nameof(ArchiveSortField.TextureUsage),
            nameof(ArchiveSortField.OriginalSize),
            nameof(ArchiveSortField.StoredSize),
            nameof(ArchiveSortField.Compression),
            nameof(ArchiveSortField.Package),
            nameof(ArchiveSortField.Path),
        };
        Require(expectedSortMembers.All(sortMembers.Contains), "archive grid is missing a sortable requested column");
        Require(
            archiveGrid.Descendants().Any(element => ((string?)element.Attribute("Binding"))?.Contains("ArchiveEntryFileTypeLabelConverter", StringComparison.Ordinal) == true)
            && archiveGrid.Descendants().Any(element => ((string?)element.Attribute("Binding"))?.Contains("ArchiveTextureUsageLabelConverter", StringComparison.Ordinal) == true),
            "archive grid does not present localized file type and DDS usage separately");
        Require(
            window.Descendants().Any(element => element.Attributes().Any(
                attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ArchiveColumnChooser")),
            "archive grid has no column chooser");
        foreach (var commandName in new[]
        {
            "ExportSelectedCommand",
            "ExportFolderCommand",
            "ExportFilteredCommand",
            "AssociatedAssets.ExportSelectedCommand",
            "AssociatedAssets.ExportFamilyCommand",
        })
        {
            Require(
                window.Descendants().Any(element => ((string?)element.Attribute("Command"))?.Contains(commandName, StringComparison.Ordinal) == true),
                $"Archive Browser does not expose {commandName}");
        }
        var associatedAssetsList = window.Descendants().Single(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "AssociatedAssetsList"));
        Require(
            string.Equals((string?)associatedAssetsList.Attribute("SelectionMode"), "Extended", StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)associatedAssetsList.Attribute("SelectionChanged"), "OnAssociatedAssetsSelectionChanged", StringComparison.Ordinal),
            "associated-assets export does not support multiple selected family rows");
        Require(
            associatedAssetsList.Descendants().Any(element => ((string?)element.Attribute("Text"))?.Contains("AssociatedAssetCategoryLabelConverter", StringComparison.Ordinal) == true)
            && associatedAssetsList.Descendants().Any(element => ((string?)element.Attribute("BorderBrush"))?.Contains("AssociatedAssetOtherBrush", StringComparison.Ordinal) == true),
            "associated assets are not grouped into localized color-coded sections");
        Require(
            window.Descendants()
                .Where(element => element.Name.LocalName == "ComboBox")
                .Any(element => ((string?)element.Attribute("ItemsSource"))?.Contains("ExtensionChoicesView", StringComparison.Ordinal) == true
                    && element.Descendants().Any(descendant => descendant.Name.LocalName == "GroupStyle")),
            "extension filter is not a categorized picker");
        var controlsSource = File.ReadAllText(Path.Combine(appRoot, "Themes", "Controls.xaml"));
        Require(
            controlsSource.Contains("x:Name=\"PART_LeftHeaderGripper\"", StringComparison.Ordinal)
            && controlsSource.Contains("x:Name=\"PART_RightHeaderGripper\"", StringComparison.Ordinal),
            "the shared column-header template removes WPF's functional resize handles");
        Require(
            controlsSource.Contains("<ItemsPresenter KeyboardNavigation.DirectionalNavigation=\"Contained\"", StringComparison.Ordinal)
            && !controlsSource.Contains("<StackPanel IsItemsHost=\"True\"", StringComparison.Ordinal)
            && controlsSource.Contains("<ScrollViewer CanContentScroll=\"False\"", StringComparison.Ordinal),
            "the shared ComboBox template cannot expand grouped extension children");
        Require(
            window.Descendants().Any(element => ((string?)element.Attribute("Text"))?.Contains("ItemCount", StringComparison.Ordinal) == true)
            && window.Descendants().Any(element => ((string?)element.Attribute("Text"))?.Contains("{Binding Label}", StringComparison.Ordinal) == true),
            "extension categories do not expose both their extensions and counts");
        Require(
            ArchiveEntryClassifier.Classify("audio/music.flac", ".flac") == ArchiveEntryRole.Audio
            && ArchiveEntryClassifier.Classify("movie/intro.webm", ".webm") == ArchiveEntryRole.Video
            && ArchiveEntryClassifier.ClassifyExtensionCategory(".wmv") == ArchiveExtensionCategory.AudioVideo,
            "common sound/movie formats are not routed into media preview");

        var archiveViewModelSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ArchiveBrowserViewModel.cs"));
        Require(
            archiveViewModelSource.Contains("ExtensionChoicesView.Refresh();", StringComparison.Ordinal)
            && !archiveViewModelSource.Contains("ExtensionChoicesView.DeferRefresh", StringComparison.Ordinal),
            "extension facets can mutate the collection while its WPF view refresh is deferred");
        Require(
            archiveViewModelSource.Contains("OnPropertyChanged(nameof(ViewMode));", StringComparison.Ordinal)
            && archiveViewModelSource.Contains("OnPropertyChanged(nameof(SortField));", StringComparison.Ordinal)
            && archiveViewModelSource.Contains("OnPropertyChanged(nameof(CollisionPolicy));", StringComparison.Ordinal)
            && archiveViewModelSource.Contains("OnPropertyChanged(nameof(ManifestFormat));", StringComparison.Ordinal),
            "live localization does not restore value-selected ComboBox selections after replacing their options");

        var windowSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
        Require(
            windowSource.Contains("LegacyDefaultArchiveColumns", StringComparison.Ordinal)
            && windowSource.Contains("MigrateArchiveColumnKeys", StringComparison.Ordinal)
            && windowSource.Contains("MigrateArchiveColumnLayout", StringComparison.Ordinal),
            "legacy Role visibility, ordering, and width are not migrated to File type and Usage");
        Require(
            windowSource.Contains("grid.MaxColumnWidth", StringComparison.Ordinal)
            && windowSource.Contains("column.MaxWidth", StringComparison.Ordinal)
            && !windowSource.Contains("Math.Clamp(setting.Width, minimum, 1600)", StringComparison.Ordinal),
            "saved catalog widths are still capped by an arbitrary restore limit");
        var migrateLayout = typeof(Cdmw.ArchiveLite.App.MainWindow).GetMethod(
            "MigrateArchiveColumnLayout",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("Archive column migration helper is missing");
        var migratedLayout = (IReadOnlyList<GridColumnSettings>)(migrateLayout.Invoke(
            null,
            [new GridColumnSettings[]
            {
                new("Name", 0, 180),
                new("Role", 1, 111),
                new("Path", 2, 420),
            }]) ?? throw new InvalidOperationException("Archive column migration returned no layout"));
        Require(
            migratedLayout.Select(static setting => setting.Key).SequenceEqual(["Name", "FileType", "TextureUsage", "Path"])
            && migratedLayout[1].Width == 111
            && migratedLayout[3].Width == 420,
            "legacy Role layout migration disturbed another saved column or lost the old width");

        var hostSource = File.ReadAllText(Path.Combine(appRoot, "Controls", "DotNetModelPreviewHost.cs"));
        Require(
            hostSource.Contains("--simple-preview", StringComparison.Ordinal)
            && hostSource.Contains("presentation_state_update", StringComparison.Ordinal)
            && hostSource.Contains("resident_presentation_state_v1", StringComparison.Ordinal)
            && hostSource.Contains("orbit_sensitivity = input.OrbitSensitivity", StringComparison.Ordinal)
            && hostSource.Contains("pan_sensitivity = input.PanSensitivity", StringComparison.Ordinal),
            "Archive Lite does not request the simple renderer surface with live Orbit/Pan input updates");
        var previewSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Core",
            "NativeModelPreviewService.cs"));
        Require(
            previewSource.Contains("[\"use_textures_by_default\"] = includeTextures", StringComparison.Ordinal)
            && previewSource.Contains("includeTextures ? TexturedPackageVersion : PackageVersion", StringComparison.Ordinal)
            && previewSource.Contains("var channelResolution = includeTextures", StringComparison.Ordinal)
            && previewSource.Contains("? ResolveChannels(root, batch)", StringComparison.Ordinal),
            "native PAC texture discovery is not isolated behind the explicit opt-in route");
        var rendererProgram = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dotnet_mesh_editor_experiment",
            "Program.cs"));
        Require(
            rendererProgram.Contains("_presentationGridVisible = scene.GridVisible", StringComparison.Ordinal)
            && rendererProgram.Contains("_presentationGizmoVisible = scene.GizmoVisible", StringComparison.Ordinal),
            "the renderer presentation contexts can restore the grid or gizmo over a hidden scene setting");
        Require(
            rendererProgram.Contains("ArchivePreviewTexturesEnabled ? \"textured\" : \"untextured_wire\"", StringComparison.Ordinal)
            && rendererProgram.Contains("Color.FromArgb(48, 60, 74)", StringComparison.Ordinal)
            && rendererProgram.Contains("new MeshOverlaySizing(1.0f", StringComparison.Ordinal),
            "the simple Archive Lite renderer does not switch between opt-in textures and the restrained default presentation");
        var residentPackageSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dotnet_mesh_editor_experiment",
            "MeshViewport.ResidentPackage.cs"));
        Require(
            residentPackageSource.Contains("preserveArchiveCamera", StringComparison.Ordinal)
            && residentPackageSource.Contains("ApplyArchivePreviewInitialCamera", StringComparison.Ordinal)
            && residentPackageSource.Contains("(_yaw, _pitch, _zoom, _panX, _panY) = previousCamera", StringComparison.Ordinal),
            "resident texture refreshes do not preserve the user's camera or apply the classified initial view");
        var displayModesSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dotnet_mesh_editor_experiment",
            "MeshViewport.DisplayModes.cs"));
        var renderPanesSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dotnet_mesh_editor_experiment",
            "D3D11MaterialViewport.Panes.cs"));
        Require(
            displayModesSource.Contains("\"untextured_wire\" => (true, true, false, false, false)", StringComparison.Ordinal)
            && renderPanesSource.Contains("string.Equals(mode, \"untextured_wire\"", StringComparison.Ordinal)
            && renderPanesSource.Contains("string.Equals(mode, \"textured_wire\"", StringComparison.Ordinal),
            "solid topology modes do not reach the D3D11 wire overlay without enabling textures");
        var materialShader = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dotnet_mesh_editor_experiment",
            "D3D11MaterialShaders.hlsl"));
        Require(
            materialShader.Contains("per-part tone shift", StringComparison.Ordinal)
            && materialShader.Contains("keyLight * 0.48f", StringComparison.Ordinal)
            && materialShader.Contains("rimShape * 0.025f", StringComparison.Ordinal),
            "textureless preview shading does not preserve form without the exaggerated rim glow");
        return Task.CompletedTask;
    }

    private static async Task TestAssociatedAssetsAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAssociatedAssetsAsync().ConfigureAwait(false);
        var beforePamt = await Sha256Async(fixture.Pamt).ConfigureAwait(false);
        var beforePaz = await Sha256Async(fixture.Paz).ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(
            new OpenArchiveRequest(fixture.Root, true, ArchiveCacheMode.SessionOnly),
            CancellationToken.None).ConfigureAwait(false);
        var query = new ArchiveQueryService(sessions);
        var modelPage = await query.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, PathText: "character/model/hero.pac"),
            1,
            CancellationToken.None).ConfigureAwait(false);
        var model = modelPage.Entries.Single(entry => entry.Path == "character/model/hero.pac");
        var progress = new List<ProgressUpdate>();
        var associations = new ArchiveAssociationService(sessions, native);
        var result = await associations.FindAsync(
            new FindAssociatedAssetsRequest(opened.SessionId, model.EntryId),
            update =>
            {
                progress.Add(update);
                return Task.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);

        Require(result.Assets.Count == 6, "the synthetic model family did not resolve all six companions");
        Require(!result.Truncated, "the bounded synthetic model family was unexpectedly truncated");
        Require(result.ScannedEntries == 0, "associated assets still scanned the full archive after the basename index was available");
        Require(progress.Any(update => update.Phase == "association_lookup"), "associated-asset indexed lookup progress was not published");
        Require(progress.All(update => update.Phase != "association_scan"), "associated assets fell back to a full archive scan");
        Require(
            result.Assets.Single(asset => asset.Entry.Path == "character/modelproperty/hero.pac_xml").Evidence
                == AssociationEvidence.ExactCompanion,
            "PAC material sidecar was not identified as an expected companion");
        Require(
            result.Assets.Count(asset => asset.Category == AssociatedAssetCategory.Texture
                && asset.Evidence == AssociationEvidence.ExplicitReference) == 2,
            "material-sidecar DDS references were not resolved as explicit textures");
        Require(
            result.Assets.Any(asset => asset.Category == AssociatedAssetCategory.Physics
                && asset.Entry.Path == "character/physics/hero.hkx"),
            "explicit HKX physics reference was not categorized");
        Require(
            result.Assets.Any(asset => asset.Category == AssociatedAssetCategory.MeshMetadata
                && asset.Entry.Path == "character/model/hero.meshinfo"),
            "same-family mesh metadata was not found");
        Require(
            result.Assets.Any(asset => asset.Category == AssociatedAssetCategory.PrefabMetadata
                && asset.Entry.Path == "character/model/hero.prefab"),
            "same-family prefab metadata was not found");
        Require(
            result.Assets.All(asset => asset.Entry.Path != "unrelated/other.dds"),
            "an unrelated texture leaked into the model family");
        Require(
            result.Assets.Single(asset => asset.Entry.Path.EndsWith(".pac_xml", StringComparison.Ordinal)).Entry.Role
                == ArchiveEntryRole.Text,
            "material sidecars are not previewable as text");

        var familyExportRoot = Path.Combine(fixture.OutputRoot, "family");
        var exportService = new ArchiveExportService(
            sessions,
            query,
            native,
            new NativeModelExportService(new NativeModelPreviewService()));
        var familyEntryIds = new[] { model.EntryId }
            .Concat(result.Assets.Select(static asset => asset.Entry.EntryId))
            .ToArray();
        var familyExport = await exportService.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.RawEntries,
                familyExportRoot,
                familyEntryIds,
                null),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(familyExport.Exported == 7 && familyExport.Failed == 0, "asset-family raw export did not include the source and six companions");
        Require(
            File.Exists(Path.Combine(familyExportRoot, "base", "character", "model", "hero.pac"))
            && File.Exists(Path.Combine(familyExportRoot, "base", "character", "texture", "hero_body_d.dds")),
            "asset-family export did not preserve the full-app package and virtual folder structure");

        var diffuse = result.Assets.Single(asset => asset.Entry.Path.EndsWith("hero_body_d.dds", StringComparison.Ordinal)).Entry;
        var reverse = await associations.FindAsync(
            new FindAssociatedAssetsRequest(opened.SessionId, diffuse.EntryId),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(reverse.ScannedEntries == 0, "a learned reverse family performed another full index scan");
        Require(reverse.Assets.Any(asset => asset.Entry.EntryId == model.EntryId), "DDS reverse lookup did not return its PAC model");
        Require(
            reverse.Assets.Any(asset => asset.Entry.Path == "character/modelproperty/hero.pac_xml"),
            "DDS reverse lookup did not return its material sidecar");

        var unrelatedPage = await query.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, PathText: "unrelated/other.dds"),
            2,
            CancellationToken.None).ConfigureAwait(false);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(() => associations.FindAsync(
                new FindAssociatedAssetsRequest(opened.SessionId, unrelatedPage.Entries.Single().EntryId),
                null,
                cancelled.Token)).ConfigureAwait(false);
        }

        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Cdmw.ArchiveLite.App");
        var windowSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        Require(
            windowSource.Contains("AssociatedAssets.AssetsView", StringComparison.Ordinal)
            && windowSource.Contains("AssociatedAssets.FindCommand", StringComparison.Ordinal)
            && windowSource.Contains("AssociatedAssets.ShowInBrowserCommand", StringComparison.Ordinal)
            && windowSource.Contains("AssociatedAssets.ExportSelectedCommand", StringComparison.Ordinal)
            && windowSource.Contains("AssociatedAssets.ExportFamilyCommand", StringComparison.Ordinal),
            "Archive Browser does not expose grouped find, open, and export associated-assets controls");
        Require(
            windowSource.Contains("AssociatedAssetCategoryLabelConverter", StringComparison.Ordinal)
            && !windowSource.Contains("Text=\"{Binding KnownName}\"", StringComparison.Ordinal),
            "associated-assets rows repeat path/name detail instead of showing a compact filename-only row");
        var viewModelSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "AssociatedAssetsViewModel.cs"));
        Require(
            viewModelSource.Contains("CancellationTokenSource.CreateLinkedTokenSource", StringComparison.Ordinal)
            && viewModelSource.Contains("IsCurrent(sessionId, source.EntryId, generation)", StringComparison.Ordinal)
            && viewModelSource.Contains("RequestShutdown()", StringComparison.Ordinal)
            && viewModelSource.Contains("CancelOperation(Interlocked.Exchange", StringComparison.Ordinal),
            "associated-asset UI work is missing cancellation, stale-result, or shutdown ownership");

        Require(await Sha256Async(fixture.Pamt).ConfigureAwait(false) == beforePamt, "associated-asset lookup changed PAMT bytes");
        Require(await Sha256Async(fixture.Paz).ConfigureAwait(false) == beforePaz, "associated-asset lookup changed PAZ bytes");
    }

    private static Task TestExportPathPolicyAsync()
    {
        Require(ExportPathPolicy.NormalizeVirtualPath("folder/file.txt") == "folder/file.txt", "safe path changed");
        RequireThrows<InvalidDataException>(() => ExportPathPolicy.NormalizeVirtualPath("../escape.txt"));
        RequireThrows<InvalidDataException>(() => ExportPathPolicy.NormalizeVirtualPath("C:/escape.txt"));
        RequireThrows<InvalidDataException>(() => ExportPathPolicy.NormalizeVirtualPath("//server/share.txt"));
        RequireThrows<InvalidDataException>(() => ExportPathPolicy.NormalizeVirtualPath("folder/bad. "));
        RequireThrows<InvalidDataException>(() => ExportPathPolicy.NormalizeVirtualPath("folder/CON.txt"));
        RequireThrows<InvalidDataException>(() => ExportPathPolicy.NormalizeVirtualPath("LPT1/report.txt"));
        return Task.CompletedTask;
    }

    private static async Task TestCacheMaintenanceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-cache-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            for (var index = 0; index < 3; index++)
            {
                var path = Path.Combine(root, $"cache-{index}.bin");
                await File.WriteAllBytesAsync(path, new byte[1_000]).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(index - 4));
            }
            var result = ArchiveLiteCacheMaintenance.Prune(root, 1_500);
            Require(result.BytesBefore == 3_000, "cache size accounting is wrong");
            Require(result.BytesAfter <= 1_350 && result.FilesRemoved == 2, "cache did not prune to its low-water mark");
            Require(File.Exists(Path.Combine(root, "cache-2.bin")), "cache pruning did not retain the newest entry");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task TestBuildLauncherSourceAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var launcherSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "BUILD-FRESH-EXE.bat"));
        Require(
            launcherSource.Contains(
                "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0scripts\\build_archive_lite.ps1\"",
                StringComparison.OrdinalIgnoreCase),
            "the double-click launcher bypasses the verified Archive Lite release builder");
        Require(
            launcherSource.Contains("-StandaloneOnly", StringComparison.OrdinalIgnoreCase),
            "the double-click launcher still requests the portable folder and ZIP outputs");
        Require(
            launcherSource.Contains("set \"BUILD_EXIT_CODE=%ERRORLEVEL%\"", StringComparison.OrdinalIgnoreCase)
            && launcherSource.Contains("if not \"%BUILD_EXIT_CODE%\"==\"0\" goto build_failed", StringComparison.OrdinalIgnoreCase)
            && launcherSource.Contains("exit /b %BUILD_EXIT_CODE%", StringComparison.OrdinalIgnoreCase),
            "the double-click launcher does not preserve and report build failures");
        Require(
            launcherSource.Contains("pause", StringComparison.OrdinalIgnoreCase)
            && launcherSource.Contains("%~dp0artifacts", StringComparison.OrdinalIgnoreCase),
            "the double-click launcher does not keep its result visible or identify the output folder");
        var buildSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "build_archive_lite.ps1"));
        var artifactGuardSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "verify_archive_lite_artifact.ps1"));
        var standaloneGuardSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "verify_archive_lite_standalone.ps1"));
        var dependencyBootstrapSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "ensure_vgmstream.ps1"));
        Require(
            buildSource.Contains("function Invoke-CheckedPowerShellScript", StringComparison.Ordinal)
            && buildSource.Contains("[switch]$StandaloneOnly", StringComparison.Ordinal)
            && buildSource.Contains("if (-not $StandaloneOnly)", StringComparison.Ordinal)
            && buildSource.Contains(
                "Invoke-CheckedPowerShellScript -Path (Join-Path $PSScriptRoot \"ensure_vgmstream.ps1\")",
                StringComparison.Ordinal)
            && buildSource.Contains(
                "Invoke-CheckedPowerShellScript -Path (Join-Path $PSScriptRoot \"verify_archive_lite_artifact.ps1\")",
                StringComparison.Ordinal)
            && buildSource.Contains(
                "Invoke-CheckedPowerShellScript -Path (Join-Path $PSScriptRoot \"verify_archive_lite_standalone.ps1\")",
                StringComparison.Ordinal),
            "the release builder can misattribute a child PowerShell script's retained native exit code");
        Require(
            dependencyBootstrapSource.Contains("$probeExitCode = $LASTEXITCODE", StringComparison.Ordinal)
            && dependencyBootstrapSource.Contains("if ($probeExitCode -ne 1)", StringComparison.Ordinal)
            && dependencyBootstrapSource.TrimEnd().EndsWith("exit 0", StringComparison.Ordinal),
            "the vgmstream bootstrap does not normalize its intentionally nonzero version-probe exit code");
        Require(
            buildSource.Contains("\"Cdmw.Archive.Content.dll\"", StringComparison.Ordinal)
            && artifactGuardSource.Contains("\"Cdmw.Archive.Content.dll\"", StringComparison.Ordinal)
            && standaloneGuardSource.Contains("\"Cdmw.Archive.Content.dll\"", StringComparison.Ordinal),
            "the shared archive-content decoder is not copied and required throughout portable/standalone packaging");
        Require(
            artifactGuardSource.Contains("logs\\archive-lite.log", StringComparison.Ordinal)
            && artifactGuardSource.Contains("No packaged application diagnostic log was written", StringComparison.Ordinal),
            "the artifact guard hides packaged application self-test diagnostics");
        return Task.CompletedTask;
    }

    private static async Task TestStandaloneRuntimeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-standalone-test-{Guid.NewGuid():N}");
        try
        {
            var payloadBytes = CreateStandaloneTestPayload();
            await using var firstPayload = new MemoryStream(payloadBytes, writable: false);
            var extracted = await StandaloneRuntime.EnsureExtractedAsync(
                firstPayload,
                root,
                CancellationToken.None).ConfigureAwait(false);
            var workerPath = Path.Combine(extracted, "CdmwArchiveLite.Worker.exe");
            var markerPath = Path.Combine(extracted, StandaloneRuntime.ReadyMarkerName);
            Require(File.Exists(Path.Combine(extracted, "CdmwArchiveLite.exe")), "standalone application was not extracted");
            Require(File.Exists(workerPath), "standalone worker was not extracted");
            Require(File.Exists(markerPath), "standalone ready marker was not published");
            var markerTimestamp = File.GetLastWriteTimeUtc(markerPath);
            var markerContents = await File.ReadAllTextAsync(markerPath).ConfigureAwait(false);

            await using var secondPayload = new MemoryStream(payloadBytes, writable: false);
            var reused = await StandaloneRuntime.EnsureExtractedAsync(
                secondPayload,
                root,
                CancellationToken.None).ConfigureAwait(false);
            Require(reused == extracted, "standalone runtime did not reuse its content-addressed cache");
            Require(File.GetLastWriteTimeUtc(markerPath) == markerTimestamp, "standalone cache reuse rewrote its ready marker");
            Require(await File.ReadAllTextAsync(markerPath).ConfigureAwait(false) == markerContents, "standalone cache marker changed during reuse");

            File.Delete(workerPath);
            await using var repairPayload = new MemoryStream(payloadBytes, writable: false);
            var repaired = await StandaloneRuntime.EnsureExtractedAsync(
                repairPayload,
                root,
                CancellationToken.None).ConfigureAwait(false);
            Require(repaired == extracted && File.Exists(workerPath), "standalone runtime did not rebuild a damaged cache");
            Require(
                Directory.GetDirectories(Path.Combine(root, "payloads"), "*.invalid-*").Length == 1,
                "damaged standalone runtime was not quarantined before replacement");

            var maliciousPayloadBytes = CreateStandaloneTraversalPayload();
            await using var maliciousPayload = new MemoryStream(maliciousPayloadBytes, writable: false);
            await RequireThrowsAsync<InvalidDataException>(() => StandaloneRuntime.EnsureExtractedAsync(
                maliciousPayload,
                root,
                CancellationToken.None)).ConfigureAwait(false);
            Require(!File.Exists(Path.Combine(root, "escape.txt")), "standalone payload escaped its runtime root");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateStandaloneTestPayload()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["CdmwArchiveLite.exe"] = Encoding.UTF8.GetBytes("test application"),
            ["CdmwArchiveLite.Worker.exe"] = Encoding.UTF8.GetBytes("test worker"),
            ["cdmw-archive-core.dll"] = Encoding.UTF8.GetBytes("test archive core"),
            ["preview/cdmw-preview-core.exe"] = Encoding.UTF8.GetBytes("test preview"),
            ["indexer/cdmw-archive-accelerator.exe"] = Encoding.UTF8.GetBytes("test indexer"),
            ["mesh/cdmw-mesh-core.exe"] = Encoding.UTF8.GetBytes("test mesh exporter"),
            ["renderer/cdmw-mesh-dotnet-editor.exe"] = Encoding.UTF8.GetBytes("test renderer"),
        };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(files.Select(file => new
        {
            path = file.Key,
            bytes = file.Value.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant(),
        }));

        using var payload = new MemoryStream();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                WriteZipEntry(archive, $"CDMW-Archive-Lite-win-x64/{file.Key}", file.Value);
            }
            WriteZipEntry(archive, "CDMW-Archive-Lite-win-x64/PACKAGE-CONTENTS.json", manifest);
        }
        return payload.ToArray();
    }

    private static byte[] CreateStandaloneTraversalPayload()
    {
        using var payload = new MemoryStream();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(archive, "CDMW-Archive-Lite-win-x64/../../escape.txt", Encoding.UTF8.GetBytes("unsafe"));
        }
        return payload.ToArray();
    }

    private static void WriteZipEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var output = entry.Open();
        output.Write(contents);
    }

    private static async Task TestGameInstallDiscoveryAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        Require(
            GameInstallDiscoveryService.LooksLikeArchivePackageRoot(Path.GetDirectoryName(fixture.Pamt)!),
            "synthetic archive root was not recognized as a game package root");
        Require(
            !GameInstallDiscoveryService.LooksLikeArchivePackageRoot(fixture.OutputRoot),
            "an ordinary output folder was misidentified as a game package root");

        const string vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" }
                "1" { "path" "D:\\Games\\Steam" }
            }
            """;
        var paths = GameInstallDiscoveryService.ParseSteamLibraryPaths(vdf);
        Require(paths.Contains(@"C:\Program Files (x86)\Steam", StringComparer.OrdinalIgnoreCase), "primary Steam library was not parsed");
        Require(paths.Contains(@"D:\Games\Steam", StringComparer.OrdinalIgnoreCase), "secondary Steam library was not parsed");
    }

    private static async Task TestArchiveCacheHealthAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var health = new ArchiveCacheHealthService();
        var missing = await health.InspectAsync(
            new ArchiveCacheHealthRequest(fixture.Root),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(missing.State == ArchiveCacheHealthState.Missing, "a never-opened archive cache was not reported missing");

        var native = new NativeArchiveCore();
        using (var sessions = new ArchiveSessionManager(native))
        {
            _ = await sessions.OpenAsync(
                new OpenArchiveRequest(fixture.Root, true),
                CancellationToken.None).ConfigureAwait(false);
        }

        var current = await health.InspectAsync(
            new ArchiveCacheHealthRequest(fixture.Root),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(current.State == ArchiveCacheHealthState.Current, "a verified archive cache was not reported current");

        var timestamp = File.GetLastWriteTimeUtc(fixture.Pamt);
        File.SetLastWriteTimeUtc(fixture.Pamt, timestamp.AddSeconds(2));
        var stale = await health.InspectAsync(
            new ArchiveCacheHealthRequest(fixture.Root),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(stale.State == ArchiveCacheHealthState.Stale, "changed archive source metadata did not mark the cache stale");
        Require(stale.ChangedSourceCount > 0, "stale cache did not report changed source files");
    }

    private static async Task TestArchiveCacheModesAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [fixture.Pamt] = await Sha256Async(fixture.Pamt).ConfigureAwait(false),
            [fixture.Paz] = await Sha256Async(fixture.Paz).ConfigureAwait(false),
            [fixture.Pathc] = await Sha256Async(fixture.Pathc).ConfigureAwait(false),
        };
        var native = new NativeArchiveCore();
        string persistentPath;
        string persistentBasenamePath;
        string persistentExtensionPath;
        string temporaryPath;
        string temporaryBasenamePath;
        string temporaryExtensionPath;
        using (var sessions = new ArchiveSessionManager(native))
        {
            var built = await sessions.OpenAsync(
                new OpenArchiveRequest(
                    fixture.Root,
                    ForceRefresh: true,
                    CacheMode: ArchiveCacheMode.Persistent),
                CancellationToken.None).ConfigureAwait(false);
            persistentPath = sessions.GetRequired(built.SessionId).Index.Path;
            persistentBasenamePath = Path.ChangeExtension(persistentPath, ".abi");
            persistentExtensionPath = Path.ChangeExtension(persistentPath, ".aex");
            Require(built.CacheMode == ArchiveCacheMode.Persistent, "persistent cache mode was not returned");
            Require(!built.UsedCachedIndex, "a forced persistent build incorrectly reported a cache hit");
            Require(File.Exists(persistentPath), "persistent archive index was not retained");
            Require(File.Exists(persistentBasenamePath), "persistent basename lookup index was not retained");
            Require(File.Exists(persistentExtensionPath), "persistent extension lookup index was not retained");

            var manifestPath = Directory.EnumerateFiles(ArchiveLiteDataPaths.IndexRootManifests, "*.json")
                .Single(path => File.ReadAllText(path).Contains(built.Fingerprint, StringComparison.OrdinalIgnoreCase));
            var manifestBeforeReuse = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);

            var reused = await sessions.OpenAsync(
                new OpenArchiveRequest(fixture.Root, CacheMode: ArchiveCacheMode.Persistent),
                CancellationToken.None).ConfigureAwait(false);
            Require(reused.UsedCachedIndex, "a verified persistent index was not reused");
            var manifestAfterReuse = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            Require(
                manifestBeforeReuse.AsSpan().SequenceEqual(manifestAfterReuse),
                "reusing an unchanged persistent cache rewrote its freshness manifest");

            var priorSessionIds = new List<string> { built.SessionId, reused.SessionId };
            for (var refresh = 0; refresh < 3; refresh++)
            {
                var refreshed = await sessions.OpenAsync(
                    new OpenArchiveRequest(
                        fixture.Root,
                        ForceRefresh: true,
                        CacheMode: ArchiveCacheMode.Persistent),
                    CancellationToken.None).ConfigureAwait(false);
                Require(!refreshed.UsedCachedIndex, "a forced refresh incorrectly reported a cache hit");
                Require(
                    Path.GetFullPath(sessions.GetRequired(refreshed.SessionId).Index.Path)
                        .Equals(Path.GetFullPath(persistentPath), StringComparison.OrdinalIgnoreCase),
                    "a forced refresh published to a different persistent cache path");
                foreach (var releasedSessionId in priorSessionIds)
                {
                    await RequireThrowsAsync<KeyNotFoundException>(() => Task.FromResult(sessions.GetRequired(releasedSessionId)))
                        .ConfigureAwait(false);
                }
                priorSessionIds = [refreshed.SessionId];
            }

            var originalPamt = await File.ReadAllBytesAsync(fixture.Pamt).ConfigureAwait(false);
            var originalTimestamp = File.GetLastWriteTimeUtc(fixture.Pamt);
            var changedPamt = originalPamt.ToArray();
            changedPamt[^1] ^= 0x01;
            await File.WriteAllBytesAsync(fixture.Pamt, changedPamt).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(fixture.Pamt, originalTimestamp);
            try
            {
                await RequireThrowsAsync<ArchiveCacheRefreshRequiredException>(() => sessions.OpenAsync(
                    new OpenArchiveRequest(
                        fixture.Root,
                        CacheMode: ArchiveCacheMode.Persistent,
                        AllowCacheBuild: false),
                    CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                await File.WriteAllBytesAsync(fixture.Pamt, originalPamt).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(fixture.Pamt, originalTimestamp);
            }

            var sessionOnly = await sessions.OpenAsync(
                new OpenArchiveRequest(fixture.Root, CacheMode: ArchiveCacheMode.SessionOnly),
                CancellationToken.None).ConfigureAwait(false);
            temporaryPath = sessions.GetRequired(sessionOnly.SessionId).Index.Path;
            temporaryBasenamePath = Path.ChangeExtension(temporaryPath, ".abi");
            temporaryExtensionPath = Path.ChangeExtension(temporaryPath, ".aex");
            Require(sessionOnly.CacheMode == ArchiveCacheMode.SessionOnly, "session-only cache mode was not returned");
            Require(!sessionOnly.UsedCachedIndex, "session-only loading reused a persistent index");
            Require(File.Exists(temporaryPath), "session-only index was not available for the live session");
            Require(File.Exists(temporaryBasenamePath), "session-only basename index was not available for the live session");
            Require(File.Exists(temporaryExtensionPath), "session-only extension index was not available for the live session");
            Require(
                !Path.GetFullPath(temporaryPath).Equals(Path.GetFullPath(persistentPath), StringComparison.OrdinalIgnoreCase),
                "session-only loading wrote into the persistent index path");
        }

        Require(File.Exists(persistentPath), "closing a session removed its persistent index");
        Require(File.Exists(persistentBasenamePath), "closing a session removed its persistent basename index");
        Require(File.Exists(persistentExtensionPath), "closing a session removed its persistent extension index");
        Require(!File.Exists(temporaryPath), "closing the worker session retained a one-time index");
        Require(!File.Exists(temporaryBasenamePath), "closing the worker session retained a one-time basename index");
        Require(!File.Exists(temporaryExtensionPath), "closing the worker session retained a one-time extension index");

        await File.WriteAllBytesAsync(persistentExtensionPath, [0x01, 0x02, 0x03]).ConfigureAwait(false);
        using (var repairedSessions = new ArchiveSessionManager(native))
        {
            var repaired = await repairedSessions.OpenAsync(
                new OpenArchiveRequest(fixture.Root, CacheMode: ArchiveCacheMode.Persistent),
                CancellationToken.None).ConfigureAwait(false);
            Require(repaired.UsedCachedIndex, "repairing a derived extension lookup rebuilt the primary archive index");
            var facets = await new ArchiveFacetsService(repairedSessions).LoadAsync(
                new ArchiveFacetsRequest(repaired.SessionId),
                null,
                CancellationToken.None).ConfigureAwait(false);
            Require(facets.Extensions.Count > 0, "the repaired extension lookup returned no facets");
            Require(new FileInfo(persistentExtensionPath).Length > 64, "the damaged extension lookup was not rebuilt");
        }
        foreach (var (sourcePath, expectedHash) in sourceHashes)
        {
            Require(
                string.Equals(await Sha256Async(sourcePath).ConfigureAwait(false), expectedHash, StringComparison.Ordinal),
                $"archive cache loading changed source bytes: {sourcePath}");
        }
    }

    private static async Task TestStartupCacheAutoLoadAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        await using var missingFixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using (var sessions = new ArchiveSessionManager(native))
        {
            _ = await sessions.OpenAsync(
                new OpenArchiveRequest(
                    fixture.Root,
                    ForceRefresh: true,
                    CacheMode: ArchiveCacheMode.Persistent),
                CancellationToken.None).ConfigureAwait(false);
        }

        var expectedPortableRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DATA_ROOT")!);
        var expectedCache = Path.Combine(expectedPortableRoot, "cache");
        Require(
            Path.GetFullPath(ArchiveLiteDataPaths.Root).Equals(expectedPortableRoot, StringComparison.OrdinalIgnoreCase),
            "worker data root did not honor the isolated portable root");
        Require(
            Path.GetFullPath(ArchiveLiteDataPaths.Cache).Equals(expectedCache, StringComparison.OrdinalIgnoreCase),
            "worker cache path did not honor the isolated portable cache root");
        var appDataPaths = typeof(MainWindowViewModel).Assembly.GetType("Cdmw.ArchiveLite.App.Services.AppDataPaths")
            ?? throw new InvalidOperationException("AppDataPaths type was not found");
        var appPortableRoot = appDataPaths.GetProperty("Root")?.GetValue(null) as string;
        Require(
            Path.GetFullPath(appPortableRoot!).Equals(expectedPortableRoot, StringComparison.OrdinalIgnoreCase),
            "application settings/log root did not honor the isolated portable root");
        foreach (var (propertyName, expectedPath) in new[]
        {
            ("Settings", Path.Combine(expectedPortableRoot, "settings.json")),
            ("Cache", expectedCache),
            ("Logs", Path.Combine(expectedPortableRoot, "logs")),
            ("Crash", Path.Combine(expectedPortableRoot, "crash")),
        })
        {
            var actualPath = appDataPaths.GetProperty(propertyName)?.GetValue(null) as string;
            Require(
                Path.GetFullPath(actualPath!).Equals(expectedPath, StringComparison.OrdinalIgnoreCase),
                $"application {propertyName.ToLowerInvariant()} path was not routed beside the executable");
        }

        await RunOnWpfDispatcherAsync(async () =>
        {
            var previousWorkerPath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_WORKER_PATH");
            Environment.SetEnvironmentVariable("CDMW_ARCHIVE_LITE_WORKER_PATH", FindWorkerOutputPath());
            WorkerProcessHost? worker = null;
            ArchiveBrowserViewModel? currentViewModel = null;
            ArchiveBrowserViewModel? changedViewModel = null;
            ArchiveBrowserViewModel? missingViewModel = null;
            byte[]? originalPamt = null;
            DateTime originalTimestamp = default;
            try
            {
                worker = await WorkerProcessHost.StartAsync(CancellationToken.None);
                var promptCount = 0;
                ArchiveCacheMode? CacheChoice(string _, bool __)
                {
                    promptCount++;
                    return ArchiveCacheMode.Persistent;
                }

                LocalizationManager.ApplyCulture("en");
                var missingPromptCount = 0;
                missingViewModel = new ArchiveBrowserViewModel(
                    worker,
                    missingFixture.Root,
                    _ => { },
                    (_, _) =>
                    {
                        missingPromptCount++;
                        return ArchiveCacheMode.Persistent;
                    });
                await missingViewModel.InitializeEnvironmentAsync(CancellationToken.None);
                Require(missingPromptCount == 1, "missing startup cache did not show the archive loading choice");
                Require(!string.IsNullOrWhiteSpace(missingViewModel.SessionId), "accepted missing-cache choice did not open the archive");

                currentViewModel = new ArchiveBrowserViewModel(worker, fixture.Root, _ => { }, CacheChoice);
                await currentViewModel.InitializeEnvironmentAsync(CancellationToken.None);
                Require(!string.IsNullOrWhiteSpace(currentViewModel.SessionId), "current persistent cache was not auto-loaded at startup");
                Require(currentViewModel.CacheHealthState == ArchiveCacheHealthState.Current, "auto-loaded cache was not reported current");
                Require(promptCount == 0, "startup auto-load displayed the manual cache-choice prompt");

                originalPamt = await File.ReadAllBytesAsync(fixture.Pamt);
                originalTimestamp = File.GetLastWriteTimeUtc(fixture.Pamt);
                var changedPamt = originalPamt.ToArray();
                changedPamt[^1] ^= 0x01;
                await File.WriteAllBytesAsync(fixture.Pamt, changedPamt);
                File.SetLastWriteTimeUtc(fixture.Pamt, originalTimestamp);

                changedViewModel = new ArchiveBrowserViewModel(worker, fixture.Root, _ => { }, CacheChoice);
                await changedViewModel.InitializeEnvironmentAsync(CancellationToken.None);
                Require(string.IsNullOrWhiteSpace(changedViewModel.SessionId), "hash-changed game files were silently reindexed at startup");
                Require(changedViewModel.CacheHealthState == ArchiveCacheHealthState.Stale, "hash change did not mark the startup cache stale");
                Require(changedViewModel.RefreshCommand.CanExecute(null), "manual Refresh was not enabled for a stale startup cache");
                Require(!changedViewModel.OpenCommand.CanExecute(null), "Open remained enabled for a stale startup cache");
                Require(
                    changedViewModel.CacheHealthDetail.Contains(LocalizationManager.Get("CacheRefreshRecommended"), StringComparison.Ordinal),
                    "stale startup cache did not recommend manual Refresh");
                Require(promptCount == 0, "stale startup inspection displayed a cache-choice prompt without user action");
            }
            finally
            {
                currentViewModel?.RequestShutdown();
                changedViewModel?.RequestShutdown();
                missingViewModel?.RequestShutdown();
                if (worker is not null)
                {
                    await worker.ShutdownAsync();
                }
                Environment.SetEnvironmentVariable("CDMW_ARCHIVE_LITE_WORKER_PATH", previousWorkerPath);
                if (originalPamt is not null)
                {
                    await File.WriteAllBytesAsync(fixture.Pamt, originalPamt);
                    File.SetLastWriteTimeUtc(fixture.Pamt, originalTimestamp);
                }
            }
        }).ConfigureAwait(false);
    }

    private static async Task TestNativeModelPreviewPackageAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-model-preview-test-{Guid.NewGuid():N}");
        try
        {
            var geometryRoot = Path.Combine(root, "geometry");
            Directory.CreateDirectory(geometryRoot);
            var geometryPath = Path.Combine(geometryRoot, "batch_000.bin");
            using (var writer = new BinaryWriter(File.Create(geometryPath), Encoding.UTF8, leaveOpen: false))
            {
                foreach (var position in new[]
                {
                    new[] { -0.5f, 0.0f, 0.0f },
                    new[] { 0.5f, 0.0f, 0.0f },
                    new[] { 0.0f, 1.0f, 0.0f },
                })
                {
                    var vertex = new float[23];
                    vertex[0] = position[0];
                    vertex[1] = position[1];
                    vertex[2] = position[2];
                    vertex[5] = 1.0f;
                    vertex[9] = position[0] + 0.5f;
                    vertex[10] = position[1];
                    foreach (var value in vertex) writer.Write(value);
                }
            }
            var textureRoot = Path.Combine(root, "textures");
            Directory.CreateDirectory(textureRoot);
            var baseTexturePath = Path.Combine(textureRoot, "base.dds");
            await File.WriteAllBytesAsync(baseTexturePath, BuildRgba8Dds(4, 4, 224, 112, 48)).ConfigureAwait(false);
            var materialTexturePath = Path.Combine(textureRoot, "material.dds");
            await File.WriteAllBytesAsync(materialTexturePath, BuildRgba8Dds(4, 4, 255, 128, 64)).ConfigureAwait(false);
            var specularTexturePath = Path.Combine(textureRoot, "specular.dds");
            await File.WriteAllBytesAsync(specularTexturePath, BuildRgba8Dds(4, 4, 192, 192, 192)).ConfigureAwait(false);
            var damageTexturePath = Path.Combine(textureRoot, "damage.dds");
            await File.WriteAllBytesAsync(damageTexturePath, BuildRgba8Dds(4, 4, 255, 0, 255)).ConfigureAwait(false);
            var layerTexturePath = Path.Combine(textureRoot, "detail-layer.dds");
            await File.WriteAllBytesAsync(layerTexturePath, BuildRgba8Dds(4, 4, 32, 208, 96)).ConfigureAwait(false);
            var layerMaskPath = Path.Combine(textureRoot, "detail-mask.dds");
            await File.WriteAllBytesAsync(
                layerMaskPath,
                BuildRgba8DdsRows(
                    4,
                    4,
                    top: (255, 255, 255, 255),
                    bottom: (0, 0, 0, 255))).ConfigureAwait(false);
            var manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schema_version = 8,
                    backend = "d3d11",
                    source_path = "character/model/01_weapon/sword/synthetic.pac",
                    batches = new[]
                    {
                        new
                        {
                            index = 0,
                            material_name = "synthetic_metal",
                            vertex_file = "geometry/batch_000.bin",
                            vertex_count = 3,
                            base_color = new[] { 0.3f, 0.45f, 0.6f },
                            roughness = 0.4f,
                            metalness = 0.8f,
                            specular = 0.5f,
                            base_tint_strength = 0.0f,
                            native_material_hints = new
                            {
                                roughness = 0.4f,
                                metalness = 0.8f,
                                specular = 0.5f,
                            },
                            material_category = "skin",
                            material_category_confidence = 0.9f,
                            material_response_promoted = true,
                            shader_family = "SkinnedMeshSkin",
                            shader_rule = "skin_sidecar",
                            normal_y_policy = "preserve",
                            texture_flip_vertical = true,
                            alpha_mode = "mask",
                            alpha_threshold = 0.25f,
                            two_sided = true,
                            dds_textures = new Dictionary<string, object>
                            {
                                ["base"] = new { source_path = "textures/base.dds", srgb_mode = "srgb" },
                                ["material"] = new
                                {
                                    source_path = "textures/material.dds",
                                    packed_channels = "r=occlusion,g=roughness,b=metalness",
                                    srgb_mode = "linear",
                                },
                                ["material_inputs"] = new object[]
                                {
                                    new
                                    {
                                        slot = "specular",
                                        source_path = "textures/specular.dds",
                                        semantic_type = "specular",
                                        layer_role = "specular_response",
                                        source_authority = "exact_sidecar",
                                    },
                                    new
                                    {
                                        slot = "specular",
                                        source_path = "textures/damage.dds",
                                        semantic_type = "specular",
                                        layer_role = "damage",
                                        source_authority = "exact_sidecar",
                                    },
                                },
                            },
                            material_layers = new object[]
                            {
                                new
                                {
                                    layer_role = "base",
                                    mask_channel = "r",
                                    blend_order = "base_then_layer",
                                    diffuse_source = "textures/base.dds",
                                    mask_source = "",
                                    weight = 1.0f,
                                    tint = new[] { 1.0f, 1.0f, 1.0f, 1.0f },
                                },
                                new
                                {
                                    layer_role = "detail",
                                    mask_channel = "r",
                                    blend_order = "base_then_detail",
                                    diffuse_source = "textures/detail-layer.dds",
                                    mask_source = "textures/detail-mask.dds",
                                    weight = 0.65f,
                                    tint = new[] { 1.0f, 0.8f, 0.6f, 1.0f },
                                },
                            },
                        },
                    },
                }),
                Encoding.UTF8).ConfigureAwait(false);

            var geometryHash = await Sha256Async(geometryPath).ConfigureAwait(false);
            var result = await NativePreviewPackageAdapter.PrepareAsync(root, "synthetic:test", CancellationToken.None).ConfigureAwait(false);
            Require(result.BatchCount == 1 && result.VertexCount == 3, "native preview package counts are wrong");
            Require(File.Exists(Path.Combine(root, "net_materials.json")), "renderer materials sidecar was not created");
            Require(File.Exists(Path.Combine(root, "dotnet_scene.json")), "renderer scene sidecar was not created");
            Require(File.Exists(Path.Combine(root, "mesh.cdmeta.json")), "renderer metadata sidecar was not created");
            Require(File.Exists(Path.Combine(root, "archive_lite_adapter_v6.json")), "renderer adapter version marker was not created");
            using (var scene = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "dotnet_scene.json")).ConfigureAwait(false)))
            {
                Require(!scene.RootElement.GetProperty("grid").GetProperty("visible").GetBoolean(), "read-only preview scene exposed the grid");
                Require(!scene.RootElement.GetProperty("gizmo").GetProperty("visible").GetBoolean(), "read-only preview scene exposed the edit gizmo");
                Require(scene.RootElement.GetProperty("interaction_mode").GetString() == "placement", "preview scene mode is wrong");
                var archivePreview = scene.RootElement.GetProperty("archive_preview");
                Require(!archivePreview.GetProperty("textures_enabled").GetBoolean(), "default native preview unexpectedly enables textures");
                Require(
                    archivePreview.GetProperty("camera").GetProperty("pitch_degrees").GetSingle() == -89.0f
                    && archivePreview.GetProperty("camera").GetProperty("yaw_degrees").GetSingle() == 0.0f
                    && Math.Abs(
                        archivePreview.GetProperty("camera").GetProperty("fit_relative_zoom").GetSingle()
                        - 0.9f) < 0.0001f
                    && archivePreview.GetProperty("camera").GetProperty("reason").GetString() == "archive_model_initial_overhead",
                    "weapon preview did not preserve its existing overhead camera");
            }
            using (var materials = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "net_materials.json")).ConfigureAwait(false)))
            {
                Require(materials.RootElement.GetProperty("submeshes").GetArrayLength() == 1, "renderer material sidecar count is wrong");
                var submesh = materials.RootElement.GetProperty("submeshes")[0];
                Require(submesh.GetProperty("resolved_channels").GetRawText() == "{}", "mesh-only preview retained resolved textures");
                Require(string.IsNullOrEmpty(submesh.GetProperty("texture").GetString()), "mesh-only preview retained a base texture");
            }

            await NativePreviewPackageAdapter.PrepareAsync(
                root,
                "synthetic:test:textured",
                CancellationToken.None,
                includeTextures: true).ConfigureAwait(false);
            using (var materials = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "net_materials.json")).ConfigureAwait(false)))
            {
                Require(
                    materials.RootElement.GetProperty("format").GetString() == "cdmw_mesh_dotnet_materials_v1"
                    && materials.RootElement.GetProperty("renderer_authority").GetString() == "dotnet_mesh_editor",
                    "Archive Lite did not emit the Full Mesh Editor material-sidecar contract");
                var submesh = materials.RootElement.GetProperty("submeshes")[0];
                var channels = submesh.GetProperty("resolved_channels");
                Require(
                    string.Equals(
                        channels.GetProperty("base").GetString(),
                        Path.GetFullPath(baseTexturePath),
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(submesh.GetProperty("texture").GetString(), Path.GetFullPath(baseTexturePath), StringComparison.OrdinalIgnoreCase),
                    "opt-in native preview did not carry the Full material graph's resolved base texture");
                Require(
                    string.Equals(channels.GetProperty("material").GetString(), Path.GetFullPath(materialTexturePath), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(channels.GetProperty("roughness").GetString(), Path.GetFullPath(materialTexturePath), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(channels.GetProperty("metallic").GetString(), Path.GetFullPath(materialTexturePath), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(channels.GetProperty("occlusion").GetString(), Path.GetFullPath(materialTexturePath), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(channels.GetProperty("specular").GetString(), Path.GetFullPath(specularTexturePath), StringComparison.OrdinalIgnoreCase)
                    && !channels.EnumerateObject().Any(channel =>
                        string.Equals(channel.Value.GetString(), Path.GetFullPath(damageTexturePath), StringComparison.OrdinalIgnoreCase)),
                    "the Full-compatible bridge lost packed material channels, primary specular input, or layer-only filtering");
                var resourceChannels = submesh.GetProperty("resource_channels");
                Require(
                    materials.RootElement.GetProperty("resources").GetArrayLength() == 5
                    && resourceChannels.EnumerateObject().Count() == 6
                    && resourceChannels.GetProperty("material").GetString() == resourceChannels.GetProperty("roughness").GetString()
                    && resourceChannels.GetProperty("material").GetString() == resourceChannels.GetProperty("metallic").GetString()
                    && resourceChannels.GetProperty("material").GetString() == resourceChannels.GetProperty("occlusion").GetString(),
                    "the Full-compatible bridge did not emit deduplicated resident texture resources");
                var materialLayers = submesh.GetProperty("material_layers");
                Require(
                    materialLayers.GetArrayLength() == 2
                    && materialLayers[0].GetProperty("layer_role").GetString() == "base"
                    && materialLayers[1].GetProperty("layer_role").GetString() == "detail"
                    && materialLayers[1].GetProperty("mask_channel").GetString() == "r"
                    && materialLayers[1].GetProperty("weight").GetSingle() == 0.65f
                    && !string.IsNullOrWhiteSpace(materialLayers[1].GetProperty("diffuse_resource_id").GetString())
                    && !string.IsNullOrWhiteSpace(materialLayers[1].GetProperty("mask_resource_id").GetString())
                    && submesh.GetProperty("material_layer_compiler").GetString()
                        == "archive_lite_managed_albedo_layer_compiler_v1",
                    "the adapter did not preserve Full's ordered masked material-layer contract");
                var components = submesh.GetProperty("channel_components");
                Require(
                    components.GetProperty("occlusion").GetString() == "r"
                    && components.GetProperty("roughness").GetString() == "g"
                    && components.GetProperty("metallic").GetString() == "b",
                    "packed material component routing does not match the native material graph");
                var parameters = submesh.GetProperty("parameters");
                Require(
                    parameters.GetProperty("base_tint_strength").GetSingle() == 0.0f
                    && parameters.GetProperty("roughness_hint").GetSingle() == 0.4f
                    && parameters.GetProperty("metalness_hint").GetSingle() == 0.8f
                    && parameters.GetProperty("specular_hint").GetSingle() == 0.5f
                    && !parameters.TryGetProperty("roughness", out _),
                    "native material hints were replaced by the flat fallback tint/scalar policy");
                Require(
                    submesh.GetProperty("shader_family").GetString() == "skin"
                    && submesh.GetProperty("shader_technique").GetString() == "SkinnedMeshSkin"
                    && submesh.GetProperty("normal_y_policy").GetString() == "preserve"
                    && submesh.GetProperty("texture_flip_vertical").GetBoolean()
                    && submesh.GetProperty("alpha_mode").GetString() == "cutout"
                    && submesh.GetProperty("alpha_cutoff").GetSingle() == 0.25f
                    && submesh.GetProperty("double_sided").GetBoolean()
                    && submesh.GetProperty("channel_color_spaces").GetProperty("base").GetString() == "srgb"
                    && submesh.GetProperty("channel_authorities").GetProperty("specular").GetString() == "exact_sidecar",
                    "the .NET sidecar did not preserve Full's shader, UV, alpha, sidedness, or channel authority contract");
            }
            var packedParser = typeof(NativePreviewPackageAdapter).GetMethod(
                "ParsePackedComponents",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("packed material parser is missing");
            var packedComponents = (Dictionary<string, string>)(packedParser.Invoke(
                null,
                ["r=occlusion,g=roughness,b=metalness,a=specular_response"])
                ?? throw new InvalidOperationException("packed material parser returned no map"));
            Require(
                packedComponents.GetValueOrDefault("specular") == "a",
                "specular_response is not routed from the packed material alpha channel");
            using (var scene = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "dotnet_scene.json")).ConfigureAwait(false)))
            {
                var initialCamera = scene.RootElement
                    .GetProperty("archive_preview")
                    .GetProperty("camera");
                Require(
                    scene.RootElement.GetProperty("archive_preview").GetProperty("textures_enabled").GetBoolean()
                    && Math.Abs(initialCamera.GetProperty("fit_relative_zoom").GetSingle() - 0.9f) < 0.0001f,
                    "textured native preview did not select the renderer's textured display mode and relaxed initial fit");
            }
            Require(
                ArchiveModelPreviewPolicy.UsesOverheadCamera("character/model/weapon/sword/example.pac")
                && ArchiveModelPreviewPolicy.UsesOverheadCamera("character/model/01_sword/example.pac")
                && !ArchiveModelPreviewPolicy.UsesOverheadCamera("character/model/body/example.pac")
                && ArchiveModelPreviewPolicy.InitialView("character/model/body/example.pac").YawDegrees == 180.0f
                && ArchiveModelPreviewPolicy.InitialView("character/model/body/example.pac").PitchDegrees == 0.0f
                && Math.Abs(
                    ArchiveModelPreviewPolicy.InitialView("character/model/body/example.pac").FitRelativeZoom
                    - 0.5f) < 0.0001f,
                "archive model camera policy no longer matches the preserved weapon and full-body front framing");

            var rendererPath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DOTNET_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(rendererPath))
            {
                var warmupRoot = await GetRendererWarmupPackageAsync().ConfigureAwait(false);
                await RunConfiguredResidentRendererSwitchAsync(
                    rendererPath,
                    warmupRoot,
                    Path.Combine(warmupRoot, "manifest.json"),
                    root,
                    expectTexturedReplacement: true).ConfigureAwait(false);
            }

            // Restore the default texture-free package before export and optional renderer smoke coverage.
            await NativePreviewPackageAdapter.PrepareAsync(root, "synthetic:test", CancellationToken.None).ConfigureAwait(false);

            var exportRoot = Path.Combine(root, "exports");
            Directory.CreateDirectory(exportRoot);
            var exporter = new NativeModelExportService(new NativeModelPreviewService());
            var progressUpdates = new List<ProgressUpdate>();
            Task CaptureProgress(ProgressUpdate update)
            {
                progressUpdates.Add(update);
                return Task.CompletedTask;
            }
            var glbPath = Path.Combine(exportRoot, "triangle.glb");
            await exporter.ExportPackageAsync(
                root,
                "models/triangle.pac",
                ExportKind.Glb,
                glbPath,
                overwrite: false,
                CaptureProgress,
                "models/triangle.pac",
                CancellationToken.None).ConfigureAwait(false);
            var glb = await File.ReadAllBytesAsync(glbPath).ConfigureAwait(false);
            Require(glb.AsSpan(0, 4).SequenceEqual("glTF"u8), "GLB export has no glTF container signature");
            Require(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4)) == 2, "GLB export version is not 2");
            Require(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4)) == glb.Length, "GLB declared length is wrong");
            var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
            using (var glbDocument = JsonDocument.Parse(glb.AsMemory(20, jsonLength)))
            {
                Require(glbDocument.RootElement.GetProperty("meshes")[0].GetProperty("primitives").GetArrayLength() == 1, "GLB export lost its mesh primitive");
                Require(glbDocument.RootElement.GetProperty("materials").GetArrayLength() == 1, "GLB export lost its material identity");
                var views = glbDocument.RootElement.GetProperty("bufferViews");
                var binaryOffset = checked(20 + jsonLength + 8);
                var firstPositionOffset = binaryOffset + views[0].GetProperty("byteOffset").GetInt32();
                var firstUvOffset = binaryOffset + views[2].GetProperty("byteOffset").GetInt32();
                Require(
                    BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(firstPositionOffset, 4)) == -0.5f,
                    "GLB export changed the first vertex position");
                Require(
                    BinaryPrimitives.ReadSingleLittleEndian(glb.AsSpan(firstUvOffset + 4, 4)) == 1.0f,
                    "GLB export did not apply the workbench UV convention");
            }

            var objPath = Path.Combine(exportRoot, "triangle.obj");
            await exporter.ExportPackageAsync(
                root,
                "models/triangle.pac",
                ExportKind.Obj,
                objPath,
                overwrite: false,
                CaptureProgress,
                "models/triangle.pac",
                CancellationToken.None).ConfigureAwait(false);
            var obj = await File.ReadAllTextAsync(objPath).ConfigureAwait(false);
            Require(obj.Contains("# Crimson Desert Mesh", StringComparison.Ordinal), "OBJ export did not use the workbench writer");
            Require(obj.Contains("f 1/1/1 2/2/2 3/3/3", StringComparison.Ordinal), "OBJ export has no triangle face");

            var fbxPath = Path.Combine(exportRoot, "triangle.fbx");
            await exporter.ExportPackageAsync(
                root,
                "models/triangle.pac",
                ExportKind.Fbx,
                fbxPath,
                overwrite: false,
                CaptureProgress,
                "models/triangle.pac",
                CancellationToken.None).ConfigureAwait(false);
            var fbx = await File.ReadAllBytesAsync(fbxPath).ConfigureAwait(false);
            Require(Encoding.ASCII.GetString(fbx, 0, 20) == "Kaydara FBX Binary  ", "FBX export is not a binary FBX file");
            Require(progressUpdates.Any(update => update.Phase == "mesh_export_prepare" && update.Total > 0), "mesh export did not report determinate preparation progress");
            Require(progressUpdates.Any(update => update.Phase == "mesh_export_write" && update.Completed == update.Total), "mesh export did not report completion progress");

            var preservedPath = Path.Combine(exportRoot, "preserved.glb");
            await File.WriteAllTextAsync(preservedPath, "preserve-me").ConfigureAwait(false);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await RequireThrowsAsync<OperationCanceledException>(() => exporter.ExportPackageAsync(
                    root,
                    "models/triangle.pac",
                    ExportKind.Glb,
                    preservedPath,
                    overwrite: true,
                    null,
                    null,
                    cancelled.Token)).ConfigureAwait(false);
            }
            Require(await File.ReadAllTextAsync(preservedPath).ConfigureAwait(false) == "preserve-me", "cancelled mesh export replaced an existing destination");

            var unsafeRoot = Path.Combine(root, "unsafe");
            Directory.CreateDirectory(unsafeRoot);
            await File.WriteAllTextAsync(
                Path.Combine(unsafeRoot, "manifest.json"),
                JsonSerializer.Serialize(new
                {
                    schema_version = 8,
                    batches = new[]
                    {
                        new { index = 0, vertex_file = "../geometry/batch_000.bin", vertex_count = 3 },
                    },
                }),
                Encoding.UTF8).ConfigureAwait(false);
            await RequireThrowsAsync<InvalidDataException>(() => NativePreviewPackageAdapter.PrepareAsync(
                unsafeRoot,
                "synthetic:unsafe",
                CancellationToken.None)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(rendererPath))
            {
                await RunConfiguredRendererSmokeAsync(rendererPath, root, manifestPath).ConfigureAwait(false);
            }
            Require(await Sha256Async(geometryPath).ConfigureAwait(false) == geometryHash, "preview preparation or rendering changed native geometry");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestRendererWarmupPackageAsync()
    {
        var packageRoot = await GetRendererWarmupPackageAsync().ConfigureAwait(false);
        var expectedCacheRoot = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DATA_ROOT")!,
            "cache"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Require(
            Path.GetFullPath(packageRoot).StartsWith(expectedCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "renderer warmup package escaped the isolated application cache");
        foreach (var required in new[] { "manifest.json", "mesh.cdmeta.json", "net_materials.json", "dotnet_scene.json" })
        {
            Require(File.Exists(Path.Combine(packageRoot, required)), $"renderer warmup package omitted {required}");
        }
        var geometryPath = Path.Combine(packageRoot, "geometry", "batch_000.bin");
        Require(new FileInfo(geometryPath).Length == 3 * 23 * sizeof(float), "renderer warmup geometry length is invalid");
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "manifest.json")).ConfigureAwait(false)))
        {
            Require(
                manifest.RootElement.GetProperty("schema_version").GetInt32() == 8
                && manifest.RootElement.GetProperty("batches")[0].GetProperty("vertex_count").GetInt32() == 3,
                "renderer warmup manifest is incompatible with the production package schema");
        }

        var rendererPath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DOTNET_PREVIEW_PATH");
        if (!string.IsNullOrWhiteSpace(rendererPath))
        {
            await RunConfiguredRendererSmokeAsync(
                rendererPath,
                packageRoot,
                Path.Combine(packageRoot, "manifest.json")).ConfigureAwait(false);
        }
    }

    private static byte[] BuildRgba8Dds(int width, int height, byte red, byte green, byte blue, byte alpha = 255)
    {
        Require(width > 0 && height > 0, "synthetic DDS dimensions must be positive");
        var pixelsLength = checked(width * height * 4);
        var dds = new byte[checked(128 + pixelsLength)];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(8, 4), 0x100F);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(20, 4), checked((uint)(width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(92, 4), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(96, 4), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(100, 4), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(104, 4), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(108, 4), 0x1000);
        for (var offset = 128; offset < dds.Length; offset += 4)
        {
            dds[offset] = red;
            dds[offset + 1] = green;
            dds[offset + 2] = blue;
            dds[offset + 3] = alpha;
        }
        return dds;
    }

    private static byte[] BuildRgba8DdsRows(
        int width,
        int height,
        (byte R, byte G, byte B, byte A) top,
        (byte R, byte G, byte B, byte A) bottom)
    {
        var dds = BuildRgba8Dds(width, height, 0, 0, 0);
        for (var y = 0; y < height; y++)
        {
            var color = y < height / 2 ? top : bottom;
            for (var x = 0; x < width; x++)
            {
                var offset = 128 + (((y * width) + x) * 4);
                dds[offset] = color.R;
                dds[offset + 1] = color.G;
                dds[offset + 2] = color.B;
                dds[offset + 3] = color.A;
            }
        }
        return dds;
    }

    private static async Task<string> GetRendererWarmupPackageAsync()
    {
        var warmupType = typeof(ArchiveBrowserViewModel).Assembly.GetType(
            "Cdmw.ArchiveLite.App.Services.PreviewRendererWarmupPackage")
            ?? throw new InvalidOperationException("PreviewRendererWarmupPackage was not found");
        var factory = warmupType.GetMethod(
            "GetOrCreateAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("renderer warmup package factory was not found");
        var task = factory.Invoke(null, [CancellationToken.None]) as Task<string>
            ?? throw new InvalidOperationException("renderer warmup package factory returned the wrong task type");
        return await task.ConfigureAwait(false);
    }

    private static async Task TestNativeModelPreviewCacheDwellAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAssociatedAssetsAsync().ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(
            new OpenArchiveRequest(fixture.Root, CacheMode: ArchiveCacheMode.SessionOnly),
            CancellationToken.None).ConfigureAwait(false);
        var session = sessions.GetRequired(opened.SessionId);
        var entry = session.Index.FindEntriesByPath("character/model/hero.pac").Single();
        var previewCorePath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_PREVIEW_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(previewCorePath))
        {
            await RunNativeDependencyTraceProbeAsync(previewCorePath, session, entry).ConfigureAwait(false);
        }
        var previews = new NativeModelPreviewService();
        using var coldTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var coldTimer = Stopwatch.StartNew();
        var destination = await previews.BuildAsync(
            session,
            entry,
            null,
            coldTimeout.Token).ConfigureAwait(false);
        coldTimer.Stop();
        var cacheManifestPath = Path.Combine(destination, "archive_lite_preview.json");
        var cacheManifestText = await File.ReadAllTextAsync(cacheManifestPath).ConfigureAwait(false);
        using (var cacheManifest = JsonDocument.Parse(cacheManifestText))
        {
            var root = cacheManifest.RootElement;
            Require(
                root.GetProperty("version").GetString() == "archive_lite_native_model_v4_lazy_prefab",
                "the default texture-free preview changed its established cache version");
            Require(root.GetProperty("validation_mode").GetString() == "dependency_v1", "native package cache fell back to whole-session invalidation");
            Require(
                root.GetProperty("dependencies").EnumerateArray().Any(dependency =>
                    dependency.GetProperty("path").GetString() == entry.Path
                    && dependency.GetProperty("raw_sha256").GetString()?.Length == 64),
                "native package cache omitted the selected PAC dependency or its stored-byte hash");
            Require(
                root.GetProperty("basename_queries").EnumerateArray().Any(query =>
                    query.GetProperty("basename").GetString() == "hero_s.prefab"
                    && query.GetProperty("candidates").GetArrayLength() == 0),
                "native package cache omitted the zero-result prefab dependency query");
        }
        var priorFingerprint = "different-unrelated-session-fingerprint";
        var crossSessionManifest = cacheManifestText.Replace(session.Fingerprint, priorFingerprint, StringComparison.Ordinal);
        Require(crossSessionManifest != cacheManifestText, "native package cache manifest omitted its source-session fingerprint");
        await File.WriteAllTextAsync(cacheManifestPath, crossSessionManifest).ConfigureAwait(false);

        var pazPath = Path.IsPathFullyQualified(entry.PazFile)
            ? Path.GetFullPath(entry.PazFile)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry.SourcePamt)!, entry.PazFile));
        var originalPaz = await File.ReadAllBytesAsync(pazPath).ConfigureAwait(false);
        var originalPazTimestamp = File.GetLastWriteTimeUtc(pazPath);
        var changedPaz = originalPaz.ToArray();
        changedPaz[checked((int)entry.Offset + 100)] ^= 0x5A;
        try
        {
            await File.WriteAllBytesAsync(pazPath, changedPaz).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(pazPath, originalPazTimestamp);
            using var cancelledByRawDependencyChange = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            await RequireThrowsAsync<OperationCanceledException>(() => previews.BuildAsync(
                    session,
                    entry,
                    null,
                    cancelledByRawDependencyChange.Token))
                .ConfigureAwait(false);
        }
        finally
        {
            await File.WriteAllBytesAsync(pazPath, originalPaz).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(pazPath, originalPazTimestamp);
        }

        var warmTimer = Stopwatch.StartNew();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            var cached = await previews.BuildAsync(
                session,
                entry,
                null,
                timeout.Token).ConfigureAwait(false);
            Require(cached == destination, "warm native model cache hit returned the wrong package");
        }
        warmTimer.Stop();
        Require(
            warmTimer.Elapsed < coldTimer.Elapsed,
            $"dependency-validated cache reuse ({warmTimer.Elapsed.TotalMilliseconds:N1} ms) was not faster than the cold native build ({coldTimer.Elapsed.TotalMilliseconds:N1} ms)");
        Console.WriteLine(
            $"INFO: native model cache cold={coldTimer.Elapsed.TotalMilliseconds:N1}ms cross-session-warm={warmTimer.Elapsed.TotalMilliseconds:N1}ms");

        var adapterMarker = Path.Combine(destination, "archive_lite_adapter_v6.json");
        File.Delete(adapterMarker);
        var migrationProgress = new List<ProgressUpdate>();
        var migrated = await previews.BuildAsync(
            session,
            entry,
            update =>
            {
                migrationProgress.Add(update);
                return Task.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        Require(
            migrated == destination
            && File.Exists(adapterMarker)
            && migrationProgress.Any(update => update.Phase == "model_preview_adapt")
            && migrationProgress.All(update => update.Phase != "model_preview_native"),
            "legacy texture-free cache metadata did not upgrade in place without rerunning native PAC preparation");

        var unknownValidationManifest = crossSessionManifest.Replace(
            "dependency_v1",
            "unknown",
            StringComparison.Ordinal);
        Require(unknownValidationManifest != crossSessionManifest, "native package cache manifest omitted its validation mode");
        try
        {
            await File.WriteAllTextAsync(cacheManifestPath, unknownValidationManifest).ConfigureAwait(false);
            using var cancelledByUnknownValidation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            await RequireThrowsAsync<OperationCanceledException>(() => previews.BuildAsync(
                    session,
                    entry,
                    null,
                    cancelledByUnknownValidation.Token))
                .ConfigureAwait(false);
        }
        finally
        {
            await File.WriteAllTextAsync(cacheManifestPath, crossSessionManifest).ConfigureAwait(false);
        }

        await fixture.AddSingleEntryPackageAsync(
            "added-package",
            "bin__/prefab/hero_s.prefab",
            Encoding.UTF8.GetBytes("new cross-package prefab candidate")).ConfigureAwait(false);
        using (var changedSessions = new ArchiveSessionManager(native))
        {
            var changedOpen = await changedSessions.OpenAsync(
                new OpenArchiveRequest(fixture.Root, CacheMode: ArchiveCacheMode.SessionOnly),
                CancellationToken.None).ConfigureAwait(false);
            var changedSession = changedSessions.GetRequired(changedOpen.SessionId);
            var unchangedEntry = changedSession.Index.FindEntriesByPath(entry.Path).Single();
            using var cancelledByDependencyChange = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
            await RequireThrowsAsync<OperationCanceledException>(() => previews.BuildAsync(
                changedSession,
                unchangedEntry,
                null,
                cancelledByDependencyChange.Token)).ConfigureAwait(false);
        }

        Directory.Delete(destination, recursive: true);
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(5)))
        {
            await RequireThrowsAsync<OperationCanceledException>(() => previews.BuildAsync(
                session,
                entry,
                null,
                cancelled.Token)).ConfigureAwait(false);
        }
        Require(!Directory.Exists(destination), "cancelled cold native model request published a partial package");

        var nonDurableProbe = Path.Combine(ArchiveLiteDataPaths.PreviewCache, "staged-cache-write-probe.json");
        await AtomicFile.WriteAsync(
            nonDurableProbe,
            async (stream, token) => await stream.WriteAsync("cache"u8.ToArray(), token).ConfigureAwait(false),
            CancellationToken.None,
            flushToDisk: false).ConfigureAwait(false);
        Require(await File.ReadAllTextAsync(nonDurableProbe).ConfigureAwait(false) == "cache", "non-durable staged cache write was not published atomically");

        var repositoryRoot = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.App",
            "ViewModels",
            "ArchiveBrowserViewModel.cs"));
        var previewServiceSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Core",
            "ArchivePreviewService.cs"));
        var modelPreviewSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Core",
            "NativeModelPreviewService.cs"));
        var rendererHostSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.App",
            "Controls",
            "DotNetModelPreviewHost.cs"));
        var nativeProtocolSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "native",
            "cdmw_preview_core",
            "src",
            "owners",
            "protocol_json.cpp"));
        var nativeLookupSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "native",
            "cdmw_preview_core",
            "src",
            "owners",
            "material_archive_lookup.cpp"));
        var nativeReportSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "native",
            "cdmw_preview_core",
            "src",
            "owners",
            "preview_report.cpp"));
        var buildSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "build_archive_lite.ps1"));
        Require(
            viewModelSource.Contains("if (!isNativeModel)", StringComparison.Ordinal)
            && viewModelSource.Contains("await Task.Delay(90, operation.Token)", StringComparison.Ordinal),
            "native model selections still pay the UI preview debounce");
        Require(
            !previewServiceSource.Contains("ColdModelPreviewDelay", StringComparison.Ordinal)
            && modelPreviewSource.Contains("ColdBuildCoalesceDelay = TimeSpan.FromMilliseconds(35)", StringComparison.Ordinal),
            "native model preparation still retains the old fixed cold-build dwell");
        Require(
            modelPreviewSource.Contains("[\"archive_index_path\"] = session.Index.Path", StringComparison.Ordinal)
            && modelPreviewSource.Contains("[\"archive_basename_index_path\"] = session.BasenameIndex.Path", StringComparison.Ordinal),
            "native model jobs do not carry the compact cross-package lookup indexes");
        Require(
            modelPreviewSource.Contains("NativeModelPreviewCache.ComputeKey(packageVersion, session, entry, companion)", StringComparison.Ordinal)
            && modelPreviewSource.Contains("includeTextures ? TexturedPackageVersion : PackageVersion", StringComparison.Ordinal)
            && modelPreviewSource.Contains("PackageVersion = \"archive_lite_native_model_v4_lazy_prefab\"", StringComparison.Ordinal)
            && modelPreviewSource.Contains("TexturedPackageVersion = \"archive_lite_native_model_v5_textured_lazy_prefab\"", StringComparison.Ordinal)
            && modelPreviewSource.Contains("NativeModelPreviewCache.IsReusableAsync", StringComparison.Ordinal)
            && !modelPreviewSource.Contains("PackageVersion,\n            session.Fingerprint", StringComparison.Ordinal),
            "native model packages do not preserve the fast default cache while isolating textured packages");
        Require(
            modelPreviewSource.Contains("[\"enabled_prefab_component_paths\"] = Array.Empty<string>()", StringComparison.Ordinal)
            && nativeProtocolSource.Contains("\"enabled_prefab_component_paths\"", StringComparison.Ordinal)
            && nativeLookupSource.Contains("prefab_component_enabled_for_job(component, job)", StringComparison.Ordinal)
            && nativeLookupSource.Contains("job.extension == \".pac\" && !job.enabled_prefab_component_paths.empty()", StringComparison.Ordinal)
            && nativeReportSource.Contains("if (!prefab_component_enabled_for_job(component, job)) continue;", StringComparison.Ordinal)
            && nativeReportSource.Contains("none loaded until explicitly enabled", StringComparison.Ordinal),
            "native model jobs still auto-compose optional prefab geometry and material sidecars");
        Require(
            rendererHostSource.Contains("resident.LoadPackageAsync(packagePath, generation", StringComparison.Ordinal)
            && rendererHostSource.Contains("The old scene remains live while a fresh-process fallback starts", StringComparison.Ordinal)
            && rendererHostSource.Contains("prior is { IsAlive: true }", StringComparison.Ordinal)
            && rendererHostSource.Contains("BeginCameraInputUpdate();", StringComparison.Ordinal)
            && !rendererHostSource.Contains("await resident.ApplyCameraInputAsync", StringComparison.Ordinal),
            "Archive Lite does not reuse the resident renderer with generation and rollback guards");
        Require(
            rendererHostSource.Contains("PreviewRendererWarmupPackage.GetOrCreateAsync", StringComparison.Ordinal)
            && rendererHostSource.Contains("!session.SupportsResidentPackageLoad", StringComparison.Ordinal)
            && rendererHostSource.Contains("session.SupportsResidentHostAttach", StringComparison.Ordinal)
            && rendererHostSource.Contains("AttachToHostAsync(visibleHost", StringComparison.Ordinal)
            && rendererHostSource.Contains("OnWindowPositionChanged", StringComparison.Ordinal)
            && rendererHostSource.Contains("TryResizeAttachedRenderer(_hostHandle, width, height)", StringComparison.Ordinal)
            && rendererHostSource.Contains("Volatile.Read(ref _hostPixelWidth)", StringComparison.Ordinal)
            && rendererHostSource.Contains("WsPopup | WsVisible | WsClipChildren", StringComparison.Ordinal)
            && viewModelSource.Contains("ShouldPrewarmModelRenderer = true", StringComparison.Ordinal)
            && viewModelSource.Contains("NativeModelExtension(entry.Extension)", StringComparison.Ordinal),
            "Archive Lite does not safely prewarm, attach, and resize the resident renderer after first-page readiness");
        Require(
            buildSource.Contains("-p:PublishSingleFile=false", StringComparison.Ordinal)
            && !buildSource.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", StringComparison.Ordinal),
            "the already-contained renderer still incurs a nested single-file extraction launch");
    }

    private static async Task RunNativeDependencyTraceProbeAsync(
        string previewCorePath,
        ArchiveSession session,
        ArchiveEntryDto entry)
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-dependency-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeRoot);
        try
        {
            var outputRoot = Path.Combine(probeRoot, "package");
            var jobPath = Path.Combine(probeRoot, "job.json");
            var reportPath = Path.Combine(probeRoot, "report.json");
            var basenameIndexPath = Path.ChangeExtension(session.Index.Path, ".abi");
            await File.WriteAllTextAsync(
                jobPath,
                JsonSerializer.Serialize(new
                {
                    version = 1,
                    backend = "cdmw_preview_core_0.1",
                    renderer_backend = "d3d11",
                    schema_version = 8,
                    package_root = session.PackageRoot,
                    archive_index_path = session.Index.Path,
                    archive_basename_index_path = basenameIndexPath,
                    cache_root = Path.Combine(probeRoot, "cache"),
                    output_root = outputRoot,
                    entry = new
                    {
                        path = entry.Path,
                        basename = entry.Name,
                        extension = entry.Extension,
                        pamt_path = entry.SourcePamt,
                        paz_file = entry.PazFile,
                        offset = entry.Offset,
                        comp_size = entry.StoredSize,
                        orig_size = entry.OriginalSize,
                        flags = entry.Flags,
                        paz_index = entry.PazIndex,
                        compression_type = entry.CompressionType,
                    },
                    companion_entry = new { },
                    render_settings = new
                    {
                        visible_texture_mode = "mesh_base_first",
                        d3d11_view_mode = "lit",
                        use_textures_by_default = false,
                        high_quality_by_default = true,
                    },
                })).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(previewCorePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("preview-job");
            startInfo.ArgumentList.Add(jobPath);
            startInfo.ArgumentList.Add(reportPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("native dependency-trace probe could not start preview-core");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdoutText = await stdout.ConfigureAwait(false);
            var stderrText = await stderr.ConfigureAwait(false);
            Require(process.ExitCode == 0, $"native dependency-trace probe failed: {stderrText}{stdoutText}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath).ConfigureAwait(false));
            var root = report.RootElement;
            Require(root.GetProperty("cache_dependency_schema").GetInt32() == 1, "native report omitted the cache dependency schema");
            Require(
                root.GetProperty("cache_dependency_entries").EnumerateArray().Any(candidate =>
                    candidate.GetProperty("path").GetString() == entry.Path),
                "native report did not record the selected PAC dependency");
            var dependencyQueries = root.GetProperty("cache_dependency_queries");
            Require(
                dependencyQueries.EnumerateArray().Any(query =>
                    query.GetProperty("basename").GetString() == "hero_s.prefab"
                    && query.GetProperty("scope").GetString() == "global_index"
                    && query.GetProperty("maximum_results").GetInt32() == 8),
                $"native report did not retain a zero-result global prefab query: {dependencyQueries.GetRawText()}");
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }
        }
    }

    private static async Task TestAssetMetadataAndHkxPreviewAsync()
    {
        var dds = new byte[148 + 16];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12, 4), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16, 4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(20, 4), 524288);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28, 4), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80, 4), 0x4);
        "DX10"u8.CopyTo(dds.AsSpan(84, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(128, 4), 98);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(132, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(140, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(144, 4), 3);
        var ddsMetadata = AssetMetadataInspector.Describe(".dds", dds);
        Require(
            ddsMetadata.Contains($"{1024:N0}", StringComparison.Ordinal)
            && ddsMetadata.Contains($"{512:N0}", StringComparison.Ordinal)
            && ddsMetadata.Contains("BC7_UNORM", StringComparison.Ordinal)
            && ddsMetadata.Contains("Mip levels: 6", StringComparison.Ordinal)
            && ddsMetadata.Contains("alpha opaque", StringComparison.Ordinal),
            "DDS metadata omits dimensions, DXGI format, mip levels, or alpha mode");

        var hkx = BuildSyntheticSkeletonHkx();
        var hkxMetadata = AssetMetadataInspector.Describe(".hkx", hkx);
        Require(
            hkxMetadata.Contains("Havok tagfile", StringComparison.Ordinal)
            && hkxMetadata.Contains("20240200", StringComparison.Ordinal)
            && hkxMetadata.Contains("ITEM", StringComparison.Ordinal),
            "HKX metadata does not identify its SDK and tagfile sections");

        await using var fixture = await SyntheticArchiveFixture.CreateAssociatedAssetsAsync().ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(
            new OpenArchiveRequest(fixture.Root, CacheMode: ArchiveCacheMode.SessionOnly),
            CancellationToken.None).ConfigureAwait(false);
        var session = sessions.GetRequired(opened.SessionId);
        var hkxEntry = session.Index.FindEntriesByPath("character/physics/hero.hkx").Single();
        var preview = await new ArchivePreviewService(sessions, native).BuildAsync(
            new PreviewRequest(opened.SessionId, hkxEntry.EntryId),
            CancellationToken.None).ConfigureAwait(false);
        Require(
            preview.Kind == PreviewKind.StructuredData
            && preview.ArtifactPath?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true
            && preview.Text?.Contains("Havok container analysis", StringComparison.Ordinal) == true
            && preview.Text.Contains("object graphs are not reconstructed", StringComparison.Ordinal)
            && File.Exists(preview.ArtifactPath),
            "HKX preview did not remain on the readable, renderer-free analysis path");

        var previewSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.Core",
            "ArchivePreviewService.cs"));
        Require(
            previewSource.Contains("BuildSemanticResultAsync", StringComparison.Ordinal)
            && previewSource.Contains("ArchiveContentPreviewService", StringComparison.Ordinal)
            && !previewSource.Contains("NativeHkxPreviewService", StringComparison.Ordinal),
            "archive preview still contains an HKX visual-rendering route");
        var hostSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.App",
            "Controls",
            "DotNetModelPreviewHost.cs"));
        Require(
            !hostSource.Contains("archive_lite_hkx_preview", StringComparison.Ordinal)
            && !hostSource.Contains("requested HKX structure view", StringComparison.Ordinal),
            "Archive Lite still contains an HKX-specific renderer handshake");
    }

    private static byte[] BuildSyntheticSkeletonHkx()
    {
        var typeNames = Encoding.ASCII.GetBytes("char\0HavokShapeNameProperty\0hkQsTransform\0hkBone\0hkInt16\0hkSkeleton\0hknpMaterial\0").Concat([byte.MaxValue]).ToArray();
        byte[] tna1 = [8, 0, 0, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0];
        var data = new byte[480];
        Encoding.ASCII.GetBytes("Bone_Test\0").CopyTo(data, 0);
        WriteU32(data, 32 + 0x20, 1);
        for (var row = 0; row < 2; row++)
        {
            var offset = 80 + row * 48;
            foreach (var (component, value) in new[] { (0, (float)row), (1, 1.0f), (2, 2.0f), (3, 1.0f) })
            {
                WriteF32(data, offset + component * 4, value);
            }
            foreach (var (component, value) in new[] { (0, 0.0f), (1, 0.0f), (2, 0.0f), (3, 1.0f) })
            {
                WriteF32(data, offset + 16 + component * 4, value);
            }
            foreach (var component in Enumerable.Range(0, 4)) WriteF32(data, offset + 32 + component * 4, 1.0f);
        }
        WriteU32(data, 176, 1);
        WriteU32(data, 184, uint.MaxValue);
        WriteU32(data, 192, 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(208, 2), -1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(210, 2), 0);
        foreach (var (offset, value) in new[]
        {
            (224 + 0x18, 176u), (224 + 0x1C, 2u),
            (224 + 0x28, 208u), (224 + 0x2C, 2u),
            (224 + 0x38, 80u), (224 + 0x3C, 2u),
        })
        {
            WriteU32(data, offset, value);
        }

        var items = new List<byte>(12 + 7 * 12);
        items.AddRange(new byte[12]);
        foreach (var (typeFlags, offset, count) in new[]
        {
            (0x1000_0001u, 0u, 11u),
            (0x1000_0002u, 32u, 1u),
            (0x2000_0003u, 80u, 2u),
            (0x2000_0004u, 176u, 2u),
            (0x2000_0005u, 208u, 2u),
            (0x1000_0006u, 224u, 1u),
            (0x2000_0007u, 320u, 2u),
        })
        {
            items.AddRange(BitConverter.GetBytes(typeFlags));
            items.AddRange(BitConverter.GetBytes(offset));
            items.AddRange(BitConverter.GetBytes(count));
        }

        var body = new List<byte>(1024);
        body.AddRange("TAG0"u8.ToArray());
        body.AddRange(BuildHkxTagItem("SDKV", "20240200"u8.ToArray()));
        body.AddRange(BuildHkxTagItem("DATA", data));
        body.AddRange(BuildHkxTagItem("TST1", typeNames));
        body.AddRange(BuildHkxTagItem("TNA1", tna1));
        var itemLength = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(itemLength, checked((uint)(8 + items.Count)));
        body.AddRange(itemLength);
        body.AddRange("ITEM"u8.ToArray());
        body.AddRange(items);
        var output = new byte[body.Count + 4];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), checked((uint)output.Length));
        body.CopyTo(output, 4);
        return output;
    }

    private static byte[] BuildHkxTagItem(string marker, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), 0x4000_0000u | checked((uint)result.Length));
        Encoding.ASCII.GetBytes(marker).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteF32(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);

    private static Task TestArchiveItemNamesAsync()
    {
        var names = ArchiveItemNameIndex.FromMappings(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cd_phm_01_sword_0016"] = "Gilded Longsword",
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cd_phm_02_sword_0042"] = "Ashen Greatsword",
                ["cd_m1234_01_ashen"] = "Ashen Outfit",
            });
        var exact = names.Enrich(CreateArchiveEntry("equipment/cd_phm_01_sword_0016.pac"));
        Require(exact.KnownName == "Gilded Longsword", "exact localized name was not attached");
        Require(exact.NameEvidence == "Exact localization", "exact localized name evidence is wrong");

        var related = names.Enrich(CreateArchiveEntry("equipment/cd_phm_02_sword_0042_in.pac"));
        Require(string.IsNullOrEmpty(related.KnownName), "related family hint was presented as an exact name");
        Require(related.NameEvidence == "Ashen Greatsword", "related model-variant evidence was not attached directly");
        var icon = names.Enrich(CreateArchiveEntry("ui/itemicon/itemicon_prefab_cd_phm_02_sword_0042_n.dds"));
        Require(icon.NameEvidence == "Ashen Greatsword", "item-icon texture name evidence was not propagated");
        var texture = names.Enrich(CreateArchiveEntry("character/texture/cd_phm_02_sword_0042_base_color.dds"));
        Require(texture.NameEvidence == "Ashen Greatsword", "texture-family name evidence was not propagated");
        var sidecar = names.Enrich(CreateArchiveEntry("equipment/cd_phm_02_sword_0042.pac.xml"));
        Require(sidecar.NameEvidence == "Ashen Greatsword", "compound sidecar name evidence was not propagated");
        var component = names.Enrich(CreateArchiveEntry("equipment/cd_m1234_01_ashen_ub_0001.pac"));
        Require(component.NameEvidence == "Ashen Outfit", "equipment-component name evidence was not propagated");
        return Task.CompletedTask;
    }

    private static async Task TestArchiveItemNameDiscoveryAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateNameIndexAsync().ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(
            new OpenArchiveRequest(fixture.Root, ForceRefresh: true),
            CancellationToken.None).ConfigureAwait(false);
        Directory.CreateDirectory(ArchiveLiteDataPaths.NameIndexCache);
        var staleCachePath = Path.Combine(ArchiveLiteDataPaths.NameIndexCache, $"{opened.Fingerprint}.json");
        await File.WriteAllTextAsync(
            staleCachePath,
            """{"schema_version":2,"exact_names":{"stale":"Stale Name"},"related_names":{},"items":[]}""")
            .ConfigureAwait(false);
        var service = new ArchiveItemNameIndexService(sessions, native);
        var result = await service.BuildAsync(
            new BuildNameIndexRequest(opened.SessionId),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(!result.UsedCache, "the pre-discovery name cache schema was reused instead of rebuilding");
        Require(result.Available && result.ExactNameCount > 0 && result.RelatedNameCount > 0,
            $"synthetic item-name discovery did not publish both mapping kinds: {result.Warning}");

        var session = sessions.GetRequired(opened.SessionId);
        var queries = new ArchiveQueryService(sessions);
        async Task<ArchiveEntryDto> ReadAsync(string path)
        {
            var entry = session.Index.FindEntriesByPath(path).Single();
            var page = await queries.QueryAsync(
                new ArchiveQuerySpec(opened.SessionId, EntryIds: [entry.EntryId]),
                1,
                CancellationToken.None).ConfigureAwait(false);
            return page.Entries.Single();
        }

        var exact = await ReadAsync("character/model/cd_test_01_sword.pac").ConfigureAwait(false);
        Require(
            exact.KnownName == "Synthetic Blade" && exact.NameEvidence == "Exact localization",
            "shifted ItemInfo localization or the later bounded prefab list did not recover the exact name");
        var related = await ReadAsync("character/model/cd_marni_laser_hel_0001_index01.pac").ConfigureAwait(false);
        Require(
            string.IsNullOrEmpty(related.KnownName) && related.NameEvidence == "Synthetic Blade",
            "alternate StringInfo prefix and semantic item/model tokens did not recover related evidence");
        var icon = await ReadAsync("ui/itemicon/itemicon_prefab_cd_marni_laser_hel_0001_n.dds").ConfigureAwait(false);
        Require(
            string.IsNullOrEmpty(icon.KnownName) && icon.NameEvidence == "Synthetic Blade",
            "recovered related evidence did not propagate to the derived item-icon texture");
    }

    private static async Task TestItemFinderCatalogAsync()
    {
        var catalog = ArchiveItemCatalog.FromRecords(
        [
            new ArchiveItemCatalogRecord(
                101,
                "OneHandSword_Gilded",
                "Gilded Longsword",
                ["Gilded Longsword", "Vergoldetes Langschwert"],
                [0x11223344],
                ["cd_phm_01_sword_0016"],
                ["cd_phm_01_sword_0016.pac"],
                ["ui/icon/item/cd_phm_01_sword_0016.dds"],
                ["steel", "gold"]),
            new ArchiveItemCatalogRecord(
                202,
                "Helmet_Ashen",
                "Ashen Helm",
                ["Ashen Helm"],
                [0x55667788],
                ["cd_phm_hel_0042"],
                ["cd_phm_hel_0042.pac"],
                ["ui/icon/item/cd_phm_hel_0042.dds"],
                ["cloth"]),
            new ArchiveItemCatalogRecord(
                102,
                "OneHandSword_Gilded_+1",
                "Gilded Longsword (+1)",
                ["Gilded Longsword (+1)"],
                [0x11223345],
                ["cd_phm_01_sword_0016_l"],
                ["cd_phm_01_sword_0016_l.pac"],
                ["ui/icon/item/cd_phm_01_sword_0016.dds"],
                ["steel", "gold"]),
            new ArchiveItemCatalogRecord(
                303,
                "QuestJournal",
                "Old Journal",
                ["Old Journal"],
                [],
                ["quest_journal_01"],
                ["quest_journal_01.pac"],
                [],
                []),
        ]);

        Require(catalog.Count == 3, "Item Finder discarded valid native catalog rows");
        var sword = catalog.Search("gilded steel", "Weapon", "Sword", "steel", 0, 72);
        Require(sword.TotalMatches == 1 && sword.Items[0].ItemId == 101, "Item Finder did not search across names and material tags");
        Require(sword.Items[0].VariantCount == 2, "Item Finder did not group enhancement/model variants like Full");
        Require(sword.Items[0].CategoryEvidence.Contains("Recovered", StringComparison.Ordinal), "Item Finder category evidence is missing");
        var localized = catalog.Search("vergoldetes", null, null, null, 0, 72);
        Require(localized.TotalMatches == 1 && localized.Items[0].ItemId == 101, "localized Item Finder search did not match");
        var byId = catalog.Search("202", null, null, null, 0, 72);
        Require(byId.TotalMatches == 1 && byId.Items[0].Category == "Armor", "numeric item-id search or category recovery failed");
        Require(catalog.Search("102", null, null, null, 0, 72).TotalMatches == 1, "grouped secondary item IDs are not searchable");
        var paged = catalog.Search(string.Empty, null, null, null, 1, 1);
        Require(paged.TotalMatches == 3 && paged.Items.Count == 1, "Item Finder search is not predictably paged");
        Require(catalog.CategoryFacets.Any(facet => facet.Category == "Weapon" && facet.Group == "Sword"), "Item Finder category facets omit weapons");
        Require(catalog.MaterialFacets.Any(facet => facet.Value == "steel" && facet.Count == 1), "Item Finder material facets omit native tags");
        var cachedRowJson = JsonSerializer.Serialize(catalog.Items.Single(item => item.ItemId == 101), WorkerProtocol.JsonOptions);
        Require(!cachedRowJson.Contains("search_text", StringComparison.Ordinal), "Item Finder persisted its rebuildable search text");
        var cachedRow = JsonSerializer.Deserialize<ArchiveItemCatalogRecord>(cachedRowJson, WorkerProtocol.JsonOptions)
            ?? throw new InvalidDataException("Item Finder cache row did not deserialize");
        Require(
            ArchiveItemCatalog.FromRecords([cachedRow]).Search("gilded", null, null, null, 0, 72).TotalMatches == 1,
            "Item Finder did not rebuild search text after a persistent-cache load");

        var extensionFacets = Enumerable.Range(0, 12)
            .Select(index => new ArchiveExtensionFacet(
                $".{(char)('a' + index)}",
                index is 9 or 10 ? 50 : 100 - index,
                ArchiveExtensionCategory.Other))
            .ToArray();
        var mostCommon = ArchiveExtensionFacetSelection.MostCommon(extensionFacets);
        Require(mostCommon.Count == 10, "the common-extension picker did not cap its active set at ten");
        Require(
            mostCommon.Zip(mostCommon.Skip(1)).All(pair =>
                pair.First.Count > pair.Second.Count
                || (pair.First.Count == pair.Second.Count
                    && StringComparer.Ordinal.Compare(pair.First.Extension, pair.Second.Extension) < 0)),
            "common-extension ordering is not count-descending with deterministic name ties");
        Require(
            ArchiveExtensionFacetSelection.MostCommon(extensionFacets.Take(3)).Count == 3,
            "a small archive padded the common-extension picker with nonexistent entries");

        var scopedFacets = ArchiveExtensionFacetBuilder.Build(
        [
            CreateArchiveEntry("scope/one.dds") with { EntryId = 1 },
            CreateArchiveEntry("scope/two.DDS") with { EntryId = 2, Extension = ".DDS" },
            CreateArchiveEntry("scope/model.pac") with { EntryId = 3 },
        ]);
        Require(
            scopedFacets.Single(facet => facet.Extension == ".dds").Count == 2
            && scopedFacets.Single(facet => facet.Extension == ".pac").Count == 1,
            "an Item Finder scope did not expose extension counts from only its resolved rows");
        var previewSelector = typeof(ArchiveBrowserViewModel).GetMethod(
            "SelectItemPreviewEntry",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Item Finder preview selection policy was not found");
        var selectedPreviewEntry = previewSelector.Invoke(
            null,
            [
                new[]
                {
                    CreateArchiveEntry("ui/icon/item.dds") with { EntryId = 1 },
                    CreateArchiveEntry("equipment/item.pac") with { EntryId = 2 },
                    CreateArchiveEntry("equipment/item.xml") with { EntryId = 3, IsPreviewable = false },
                },
            ]) as ArchiveEntryDto;
        Require(
            selectedPreviewEntry?.EntryId == 2,
            "Item Finder activation did not prefer a directly linked model for preview");

        var repositoryRoot = FindRepositoryRoot();
        var acceleratorSource = File.ReadAllText(Path.Combine(repositoryRoot, "native", "cdmw_archive_accelerator", "src", "main.cpp"));
        var liteCatalogSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Core",
            "ArchiveItemNameIndexService.cs"));
        var workerRuntimeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Worker",
            "WorkerRuntime.cs"));
        Require(
            acceleratorSource.Contains("\\\"catalog_schema\\\":1", StringComparison.Ordinal)
            && liteCatalogSource.Contains("item-index-job", StringComparison.Ordinal),
            "Lite does not consume its versioned native item catalog");
        Require(
            workerRuntimeSource.Contains("WorkerProtocol.WarmItemIcons or WorkerProtocol.BuildNameIndex", StringComparison.Ordinal)
            && liteCatalogSource.Contains("WaitForForegroundAsync(yieldToForeground", StringComparison.Ordinal),
            "background item-catalog construction does not yield to foreground archive work");

        var appRoot = Path.Combine(repositoryRoot, "src", "Cdmw.ArchiveLite.App");
        var mainWindow = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var mainWindowSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
        var mainWindowDocument = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        var itemFinderDialog = File.ReadAllText(Path.Combine(appRoot, "Dialogs", "ItemFinderDialog.xaml"));
        var itemFinderDialogDocument = System.Xml.Linq.XDocument.Load(Path.Combine(appRoot, "Dialogs", "ItemFinderDialog.xaml"));
        var itemFinderDialogSource = File.ReadAllText(Path.Combine(appRoot, "Dialogs", "ItemFinderDialog.xaml.cs"));
        var itemFinderViewModel = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ItemFinderViewModel.cs"));
        var archiveBrowserViewModel = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "ArchiveBrowserViewModel.cs"));
        var iconService = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Cdmw.ArchiveLite.Core",
            "ArchiveItemIconService.cs"));
        Require(
            mainWindow.Contains("OnItemFinderClick", StringComparison.Ordinal)
            && itemFinderDialog.Contains("ItemsSource=\"{Binding Items}\"", StringComparison.Ordinal),
            "Archive Lite does not expose the Item Finder dialog");
        Require(
            itemFinderDialog.Contains("MouseDoubleClick=\"OnItemGridMouseDoubleClick\"", StringComparison.Ordinal)
            && itemFinderDialogSource.Contains("ShowExactLinksCommand.Execute(null)", StringComparison.Ordinal)
            && archiveBrowserViewModel.Contains("SelectedEntry = SelectItemPreviewEntry(Entries)", StringComparison.Ordinal)
            && mainWindowSource.Contains("dialog.ShowDialog() == true", StringComparison.Ordinal)
            && mainWindowSource.Contains("WorkspaceTabs.SelectedIndex = 0", StringComparison.Ordinal),
            "Item Finder double-click does not load the exact item into the Archive Browser preview");
        var itemFinderLauncher = mainWindowDocument.Descendants()
            .Single(element => element.Name.LocalName == "Button"
                && element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ItemFinderNavigationButton"));
        Require(
            itemFinderLauncher.Ancestors().Any(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "TitleBarGrid")),
            "Item Finder is not a top-navigation action");
        Require(
            !mainWindowDocument.Descendants()
                .Where(element => element.Name.LocalName == "GroupBox"
                    && ((string?)element.Attribute("Header"))?.Contains("ReadOnlyTools", StringComparison.Ordinal) == true)
                .SelectMany(static element => element.Descendants())
                .Any(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Click" && attribute.Value == "OnItemFinderClick")),
            "Item Finder is still nested under Export Options");
        Require(
            itemFinderDialogDocument.Descendants().Any(element => element.Name.LocalName == "WindowChrome")
            && itemFinderDialog.Contains("ResizeMode=\"CanResize\"", StringComparison.Ordinal)
            && itemFinderDialog.Contains("BorderThickness=\"0,0,0,1\"", StringComparison.Ordinal)
            && itemFinderDialogSource.Contains("ThemedWindowChrome.Apply(this)", StringComparison.Ordinal),
            "Item Finder does not share the resizable themed chrome while retaining its title divider");
        Require(
            itemFinderViewModel.Contains("MaximumMemoryIcons = 96", StringComparison.Ordinal)
            && itemFinderViewModel.Contains("FilterDebounce = TimeSpan.FromMilliseconds(220)", StringComparison.Ordinal)
            && itemFinderViewModel.Contains("_suppressFilterSearch", StringComparison.Ordinal)
            && itemFinderViewModel.Contains("StartPageIconLoading", StringComparison.Ordinal)
            && !itemFinderDialogSource.Contains("DispatcherTimer", StringComparison.Ordinal)
            && itemFinderViewModel.Contains("WarmItemIconsRequest", StringComparison.Ordinal)
            && itemFinderViewModel.Contains("ShowRelatedSetCommand", StringComparison.Ordinal)
            && archiveBrowserViewModel.Contains("ItemCatalogReady?.Invoke", StringComparison.Ordinal)
            && archiveBrowserViewModel.Contains("ShowItemScopeAsync", StringComparison.Ordinal)
            && archiveBrowserViewModel.Contains("ClearItemScopeCommand", StringComparison.Ordinal)
            && archiveBrowserViewModel.Contains("scope.Extensions ?? []", StringComparison.Ordinal)
            && archiveBrowserViewModel.Contains("MostCommonExtensionChoices", StringComparison.Ordinal)
            && iconService.Contains("WaitForVisibleRequestsAsync", StringComparison.Ordinal)
            && iconService.Contains("WaitForForegroundAsync", StringComparison.Ordinal)
            && iconService.Contains("BuildThumbnailAsync", StringComparison.Ordinal),
            "Item Finder icon loading is not memory-bounded, persistent, and visible-first");

        var workPriority = new ArchiveWorkPriority();
        var lease = workPriority.EnterForeground();
        var backgroundWait = workPriority.WaitForForegroundAsync(CancellationToken.None);
        await Task.Delay(70).ConfigureAwait(false);
        Require(!backgroundWait.IsCompleted, "background icon preload did not yield to foreground archive work");
        lease.Dispose();
        await backgroundWait.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    private static async Task TestItemFinderViewModelLifecycleAsync()
    {
        var iconPath = Path.Combine(Path.GetTempPath(), $"cdmw-item-finder-icon-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(
            iconPath,
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")).ConfigureAwait(false);
        try
        {
            await RunOnWpfDispatcherAsync(async () =>
            {
                var fakeWorker = new FakeItemFinderWorker(iconPath)
                {
                    IconDelay = TimeSpan.FromMilliseconds(120),
                };
                var sessionId = "session-a";
                var scopes = new List<(int ItemId, bool IncludeRelated)>();
                var viewModel = new ItemFinderViewModel(
                    fakeWorker,
                    () => sessionId,
                    _ => { },
                    (itemId, _, includeRelated, _) =>
                    {
                        scopes.Add((itemId, includeRelated));
                        return Task.FromResult(true);
                    });
                var closeRequests = 0;
                viewModel.CloseRequested += (_, _) => closeRequests++;
                var collectionChanges = 0;
                viewModel.Items.CollectionChanged += (_, _) => collectionChanges++;

                await viewModel.ActivateAsync(CancellationToken.None).ConfigureAwait(true);
                Require(fakeWorker.SearchCount == 1 && viewModel.Items.Count == 1, "Item Finder activation did not publish one search page");
                var initialRow = viewModel.Items.Single();
                var iconChanges = 0;
                initialRow.PropertyChanged += (_, eventArgs) =>
                {
                    if (eventArgs.PropertyName == nameof(ItemFinderRowViewModel.Icon))
                    {
                        iconChanges++;
                    }
                };
                await WaitUntilAsync(() => initialRow.Icon is not null, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                Require(iconChanges == 1 && fakeWorker.IconCount == 1, "an Item Finder tile did not transition to its icon exactly once");
                viewModel.ShowExactLinksCommand.Execute(null);
                await WaitUntilAsync(() => scopes.Count == 1 && !viewModel.IsBusy, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                Require(
                    scopes.Single() == (initialRow.ItemId, false) && closeRequests == 1,
                    "Item Finder exact activation did not apply the selected item and close the dialog");

                var changesBeforeCategory = collectionChanges;
                viewModel.SelectedCategory = viewModel.CategoryOptions.Single(option => option.Category == "Weapon");
                await WaitUntilAsync(() => fakeWorker.SearchCount == 2 && !viewModel.IsBusy, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                await Task.Delay(320).ConfigureAwait(true);
                Require(fakeWorker.SearchCount == 2, "a programmatic facet selection scheduled an extra Item Finder search");
                Require(collectionChanges - changesBeforeCategory == 2, "one category change recreated the Item Finder page more than once");
                Require(fakeWorker.IconCount == 1, "a cached Item Finder icon was loaded from the worker again");

                viewModel.Query = "g";
                await Task.Delay(35).ConfigureAwait(true);
                viewModel.Query = "gi";
                await Task.Delay(35).ConfigureAwait(true);
                viewModel.Query = "gilded";
                await WaitUntilAsync(() => fakeWorker.SearchCount == 3 && !viewModel.IsBusy, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                await Task.Delay(320).ConfigureAwait(true);
                Require(
                    fakeWorker.SearchCount == 3 && fakeWorker.SearchRequests.Last().Query == "gilded",
                    "rapid Item Finder changes did not collapse into one latest debounced request");

                viewModel.RefreshLocalization();
                await Task.Delay(320).ConfigureAwait(true);
                Require(fakeWorker.SearchCount == 3, "programmatic localized facet refresh triggered a search");

                fakeWorker.SearchDelay = TimeSpan.FromMilliseconds(350);
                fakeWorker.IgnoreSearchCancellation = true;
                viewModel.Query = "late-session";
                await WaitUntilAsync(() => fakeWorker.SearchCount == 4, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                sessionId = "session-b";
                viewModel.NotifyArchiveSessionChanged();
                Require(viewModel.Items.Count == 0, "session change did not clear the prior Item Finder page");
                await Task.Delay(430).ConfigureAwait(true);
                Require(viewModel.Items.Count == 0, "a late prior-session Item Finder result repopulated the page");

                viewModel.Query = "late-close";
                await WaitUntilAsync(() => fakeWorker.SearchCount == 5, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                viewModel.Deactivate();
                await Task.Delay(430).ConfigureAwait(true);
                Require(viewModel.Items.Count == 0, "a late Item Finder result published after dialog close");
                viewModel.RequestShutdown();
            }).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(iconPath);
        }
    }

    private static Task TestDdsTextureClassificationAsync()
    {
        var colorPaths = new[] { "stone_d.dds", "stone_diffuse.dds", "stone_albedo.dds", "stone_basecolor.dds" };
        var normalPaths = new[] { "stone_n.dds", "stone_normal.dds", "stone_nrm.dds" };
        var materialPaths = new[]
        {
            "stone_m.dds", "stone_ma.dds", "stone_mg.dds", "stone_mask.dds", "stone_orm.dds", "stone_mra.dds",
            "stone_ao.dds", "stone_roughness.dds", "stone_metallic.dds", "stone_specular.dds",
        };
        Require(
            colorPaths.All(path => ArchiveContentClassification.ClassifyTextureUsage(path) == ArchiveTextureUsageKind.Color),
            "a terminal color suffix was not classified as Color");
        Require(
            normalPaths.All(path => ArchiveContentClassification.ClassifyTextureUsage(path) == ArchiveTextureUsageKind.NormalMap),
            "a terminal normal suffix was not classified as Normal map");
        Require(
            materialPaths.All(path => ArchiveContentClassification.ClassifyTextureUsage(path) == ArchiveTextureUsageKind.MaterialMap),
            "a terminal packed/material suffix was not classified as Material map");
        Require(
            ArchiveContentClassification.ClassifyTextureUsage("metal_gate_d.dds") == ArchiveTextureUsageKind.Color
            && ArchiveContentClassification.ClassifyRole("texture/metal_gate_d.dds", ".dds") == "image",
            "a word such as metal elsewhere in the filename overrode its terminal color suffix");
        Require(
            ArchiveContentClassification.ClassifyTextureUsage("metal_gate_detail.dds") == ArchiveTextureUsageKind.Unknown
            && ArchiveEntryClassifier.ClassifyTextureUsage("metal_gate_detail.dds", ".dds") == ArchiveTextureUsage.Unknown,
            "an unrecognized DDS filename did not remain explicitly Unknown");
        Require(
            Enum.GetValues<ArchiveEntryRole>().All(role => ArchiveEntryClassifier.ClassifyFileType(".dds", role) == ArchiveEntryFileType.Texture),
            "a DDS row can still acquire a non-Texture file type from its legacy role");
        Require(
            ArchiveEntryClassifier.ClassifyTextureUsage("stone_n.png", ".png") == ArchiveTextureUsage.None,
            "non-DDS images were assigned an inferred DDS usage");
        var legacyEntryJson = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(CreateArchiveEntry("legacy/file.dds"), WorkerProtocol.JsonOptions))!
            .AsObject();
        legacyEntryJson.Remove("file_type");
        legacyEntryJson.Remove("texture_usage");
        var legacyEntry = JsonSerializer.Deserialize<ArchiveEntryDto>(legacyEntryJson, WorkerProtocol.JsonOptions)
            ?? throw new InvalidDataException("legacy ArchiveEntryDto did not deserialize");
        Require(
            legacyEntry is { FileType: ArchiveEntryFileType.Other, TextureUsage: ArchiveTextureUsage.None },
            "defaulted Type/Usage fields broke an older archive-row payload");
        var legacyScopeJson = """
            {"session_id":"legacy","item_id":7,"include_related":true,"entry_ids":[1,2],"direct_count":1,"truncated":false}
            """;
        var legacyScope = JsonSerializer.Deserialize<ItemCatalogScopeResult>(legacyScopeJson, WorkerProtocol.JsonOptions)
            ?? throw new InvalidDataException("legacy ItemCatalogScopeResult did not deserialize");
        Require(legacyScope.Extensions is null, "defaulted scoped extension facets broke an older protocol payload");
        return Task.CompletedTask;
    }

    private static ArchiveEntryDto CreateArchiveEntry(string path) => new(
        EntryId: 0,
        Path: path,
        SourcePamt: "synthetic.pamt",
        PazFile: "synthetic.paz",
        PazIndex: 0,
        Offset: 0,
        StoredSize: 1,
        OriginalSize: 1,
        Flags: 0,
        Extension: Path.GetExtension(path),
        Package: "synthetic",
        Role: ArchiveEntryRole.Model,
        IsPreviewable: true);

    private static async Task RunConfiguredRendererSmokeAsync(string rendererPath, string packageRoot, string manifestPath)
    {
        var resolvedRenderer = Path.GetFullPath(rendererPath);
        Require(File.Exists(resolvedRenderer), $"configured .NET preview renderer was not found: {resolvedRenderer}");
        var runtimeRoot = Path.Combine(packageRoot, "headless-smoke");
        Directory.CreateDirectory(runtimeRoot);
        var statusPath = Path.Combine(runtimeRoot, "status.json");
        var outputRoot = Path.Combine(runtimeRoot, "output");
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedRenderer,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(resolvedRenderer)!,
        };
        foreach (var argument in new[]
        {
            "--input-package", packageRoot,
            "--mesh", manifestPath,
            "--metadata", Path.Combine(packageRoot, "mesh.cdmeta.json"),
            "--status", statusPath,
            "--output", outputRoot,
            "--edit-operations", Path.Combine(runtimeRoot, "edit_operations.json"),
            "--evaluation", Path.Combine(runtimeRoot, "evaluation.md"),
            "--headless-smoke",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("configured .NET preview renderer could not be started");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException("configured .NET preview renderer did not finish its synthetic package smoke test");
        }
        var stdoutText = await stdout.ConfigureAwait(false);
        var stderrText = await stderr.ConfigureAwait(false);
        Require(process.ExitCode == 0, $"configured .NET preview renderer failed: {stderrText}{stdoutText}");
        var exportedMeshPath = Path.Combine(outputRoot, "mesh.obj");
        Require(File.Exists(exportedMeshPath), "configured .NET preview renderer did not load and export the native manifest");
        await RequireNativeRendererUvConventionAsync(packageRoot, manifestPath, exportedMeshPath).ConfigureAwait(false);
        using var status = JsonDocument.Parse(await File.ReadAllTextAsync(statusPath).ConfigureAwait(false));
        Require(status.RootElement.GetProperty("event").GetString() == "saved", "configured .NET preview renderer did not report a successful smoke result");
    }

    private static async Task RequireNativeRendererUvConventionAsync(
        string packageRoot,
        string manifestPath,
        string exportedMeshPath)
    {
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
        var relativeGeometryPath = manifest.RootElement
            .GetProperty("batches")[0]
            .GetProperty("vertex_file")
            .GetString()
            ?? throw new InvalidDataException("native renderer smoke manifest omitted its first geometry path");
        var geometryPath = Path.GetFullPath(Path.Combine(
            packageRoot,
            relativeGeometryPath.Replace('/', Path.DirectorySeparatorChar)));
        await using var geometry = File.OpenRead(geometryPath);
        geometry.Position = 9 * sizeof(float);
        using var reader = new BinaryReader(geometry, Encoding.UTF8, leaveOpen: true);
        var nativeU = reader.ReadSingle();
        var nativeV = reader.ReadSingle();

        var firstUv = File.ReadLines(exportedMeshPath)
            .FirstOrDefault(line => line.StartsWith("vt ", StringComparison.Ordinal))
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require(firstUv is { Length: >= 3 }, "configured .NET preview renderer exported no native UV coordinates");
        var exportedU = float.Parse(firstUv![1], CultureInfo.InvariantCulture);
        var exportedV = float.Parse(firstUv[2], CultureInfo.InvariantCulture);
        Require(
            Math.Abs(exportedU - nativeU) < 0.00001f
            && Math.Abs(exportedV - (1.0f - nativeV)) < 0.00001f,
            "native renderer import did not enter the Wavefront UV convention before the shared upload restores native V");
    }

    private static async Task RunConfiguredResidentRendererSwitchAsync(
        string rendererPath,
        string initialPackageRoot,
        string initialManifestPath,
        string? replacementPackageRoot = null,
        bool expectTexturedReplacement = false)
    {
        replacementPackageRoot ??= initialPackageRoot;
        using var parentHost = new TestRendererHost(-32000, -32000, 640, 480);
        using var secondParentHost = new TestRendererHost(-31000, -31000, 720, 540);
        var parentHandle = parentHost.Handle;
        var secondParentHandle = secondParentHost.Handle;
        var runtimeRoot = Path.Combine(replacementPackageRoot, "resident-switch-smoke");
        Directory.CreateDirectory(runtimeRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(rendererPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(rendererPath))!,
        };
        foreach (var argument in new[]
        {
            "--input-package", initialPackageRoot,
            "--mesh", initialManifestPath,
            "--metadata", Path.Combine(initialPackageRoot, "mesh.cdmeta.json"),
            "--status", Path.Combine(runtimeRoot, "status.json"),
            "--output", Path.Combine(runtimeRoot, "output"),
            "--edit-operations", Path.Combine(runtimeRoot, "edit_operations.json"),
            "--evaluation", Path.Combine(runtimeRoot, "evaluation.md"),
            "--embedded",
            "--simple-preview",
            "--parent-hwnd", parentHandle.ToInt64().ToString(CultureInfo.InvariantCulture),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("resident .NET preview renderer could not be started");
        process.StandardInput.AutoFlush = true;
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            long rendererWindowHandle;
            using (var protocolReady = await ReadRendererEventAsync(process.StandardOutput, "protocol_ready", timeout.Token).ConfigureAwait(false))
            {
                Require(
                    protocolReady.RootElement.GetProperty("capabilities").EnumerateArray()
                        .Any(value => value.GetString() == "resident_package_load_v1"),
                    "renderer did not advertise resident package loading");
                Require(
                    protocolReady.RootElement.GetProperty("capabilities").EnumerateArray()
                        .Any(value => value.GetString() == "resident_host_attach_v1"),
                    "renderer did not advertise resident host attachment");
                rendererWindowHandle = protocolReady.RootElement.GetProperty("window_handle").GetInt64();
                Require(rendererWindowHandle > 0, "renderer did not publish its embedded window handle");
            }
            using (var metrics = await ReadRendererEventAsync(process.StandardOutput, "metrics", timeout.Token).ConfigureAwait(false))
            {
                Require(
                    metrics.RootElement.GetProperty("renderer").GetProperty("backend").GetString() == "d3d11_vortice_shader",
                    "resident renderer did not initialize the production backend");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "host_attach_request",
                request_id = 1,
                parent_hwnd = secondParentHandle.ToInt64(),
                activate = false,
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var attached = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "host_attach_applied",
                       timeout.Token).ConfigureAwait(false))
            {
                Require(attached.RootElement.GetProperty("request_id").GetInt64() == 1, "renderer acknowledged the wrong host attachment");
                Require(attached.RootElement.GetProperty("parent_hwnd").GetInt64() == secondParentHandle.ToInt64(), "renderer attached to the wrong hidden host");
                Require(attached.RootElement.GetProperty("process_id").GetInt32() == process.Id, "renderer host attachment changed process");
                Require(GetParent(new IntPtr(rendererWindowHandle)) == secondParentHandle, "renderer protocol acknowledged host attachment before reparenting");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "host_attach_request",
                request_id = 2,
                parent_hwnd = parentHandle.ToInt64(),
                activate = false,
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var reattached = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "host_attach_applied",
                       timeout.Token).ConfigureAwait(false))
            {
                Require(reattached.RootElement.GetProperty("request_id").GetInt64() == 2, "renderer acknowledged the wrong return host attachment");
                Require(GetParent(new IntPtr(rendererWindowHandle)) == parentHandle, "renderer did not return to its original host");
            }

            var residentSessionId = string.Empty;
            for (var generation = 2; generation <= 3; generation++)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    @event = "package_load_request",
                    request_id = generation,
                    generation,
                    package_path = replacementPackageRoot,
                })).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var applied = await ReadRendererEventAsync(
                    process.StandardOutput,
                    "package_load_applied",
                    timeout.Token).ConfigureAwait(false);
                Require(applied.RootElement.GetProperty("request_id").GetInt64() == generation, "resident renderer acknowledged the wrong request");
                Require(applied.RootElement.GetProperty("process_id").GetInt32() == process.Id, "resident package load changed renderer process");
                residentSessionId = applied.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
                Require(residentSessionId.Length > 0, "resident package load did not publish its presentation session identity");
                Require(
                    applied.RootElement.GetProperty("resident_scene_load_count").GetInt64() == generation,
                    "resident renderer did not replace the D3D11 scene in place");
                Require(
                    applied.RootElement.GetProperty("renderer").GetProperty("backend").GetString() == "d3d11_vortice_shader",
                    "resident package switch left the production backend");
                if (expectTexturedReplacement)
                {
                    var renderer = applied.RootElement.GetProperty("renderer");
                    Require(
                        renderer.GetProperty("textures_enabled").GetBoolean()
                        && renderer.GetProperty("display_mode").GetString() == "textured"
                        && renderer.GetProperty("material_parameter_state_count").GetInt32() == 1
                        && renderer.GetProperty("resolved_texture_references").GetInt32() == 3
                        && renderer.GetProperty("existing_texture_files").GetInt32() == 3
                        && renderer.GetProperty("dds_resources").GetInt32() == 5
                        && renderer.GetProperty("native_dds_resources").GetInt32() == 5
                        && renderer.GetProperty("managed_material_layer_composites").GetInt64() == 1
                        && renderer.GetProperty("texture_load_failures").GetInt32() == 0,
                        $"resident Vortice renderer did not load the synthetic Full-compatible DDS material graph: {renderer.GetRawText()}");
                }
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "presentation_state_update",
                session_id = residentSessionId,
                request_id = 39,
                base_revision = 0,
                process_generation = 1,
                protocol_version = 2,
                presentation_generation = 1,
                display = new
                {
                    quality = new
                    {
                        orbit_sensitivity = 0.35,
                        pan_sensitivity = 1.15,
                        invert_orbit_x = true,
                        invert_orbit_y = false,
                        invert_pan_x = false,
                        invert_pan_y = true,
                    },
                },
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var initialCameraApplied = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "presentation_state_update_ack",
                       timeout.Token).ConfigureAwait(false))
            {
                var presentation = initialCameraApplied.RootElement.GetProperty("presentation");
                var quality = presentation.GetProperty("quality_state");
                var camera = presentation
                    .GetProperty("view_contexts")
                    .EnumerateArray()
                    .Single(view => view.GetProperty("id").GetString() == "editable")
                    .GetProperty("camera");
                var boundsMinimum = camera.GetProperty("bounds_minimum");
                var boundsMaximum = camera.GetProperty("bounds_maximum");
                var sceneSize = Math.Max(
                    boundsMaximum[0].GetDouble() - boundsMinimum[0].GetDouble(),
                    Math.Max(
                        boundsMaximum[1].GetDouble() - boundsMinimum[1].GetDouble(),
                        boundsMaximum[2].GetDouble() - boundsMinimum[2].GetDouble()));
                var fittedZoom = sceneSize > 0.0001 ? 380.0 / sceneSize : 220.0;
                Require(
                    initialCameraApplied.RootElement.GetProperty("status").GetString() == "applied"
                    && Math.Abs(camera.GetProperty("yaw_degrees").GetDouble()) < 0.01
                    && Math.Abs(camera.GetProperty("pitch_degrees").GetDouble() + 89.0) < 0.01
                    && Math.Abs(camera.GetProperty("zoom").GetDouble() - (fittedZoom * 0.9)) < 0.01
                    && Math.Abs(quality.GetProperty("orbit_sensitivity").GetDouble() - 0.35) < 0.001
                    && Math.Abs(quality.GetProperty("pan_sensitivity").GetDouble() - 1.15) < 0.001
                    && quality.GetProperty("invert_orbit_x").GetBoolean()
                    && quality.GetProperty("invert_pan_y").GetBoolean(),
                    "resident renderer did not apply the relaxed weapon-overhead camera and live input settings");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "presentation_state_update",
                session_id = residentSessionId,
                request_id = 40,
                base_revision = 0,
                process_generation = 1,
                protocol_version = 2,
                presentation_generation = 2,
                camera = new
                {
                    role = "editable",
                    yaw = 23.0,
                    pitch = -40.0,
                    zoom_factor = 1.25,
                    pan = new[] { 0.20, -0.10 },
                    command_generation = 1,
                },
                display = new
                {
                    quality = new
                    {
                        orbit_sensitivity = 0.35,
                        pan_sensitivity = 1.15,
                        invert_orbit_x = true,
                        invert_orbit_y = false,
                        invert_pan_x = false,
                        invert_pan_y = true,
                    },
                },
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            var cameraZoomBeforeRefresh = 0.0;
            using (var cameraInputApplied = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "presentation_state_update_ack",
                       timeout.Token).ConfigureAwait(false))
            {
                var quality = cameraInputApplied.RootElement
                    .GetProperty("presentation")
                    .GetProperty("quality_state");
                var camera = cameraInputApplied.RootElement
                    .GetProperty("presentation")
                    .GetProperty("view_contexts")
                    .EnumerateArray()
                    .Single(view => view.GetProperty("id").GetString() == "editable")
                    .GetProperty("camera");
                cameraZoomBeforeRefresh = camera.GetProperty("zoom").GetDouble();
                var pan = camera.GetProperty("pan");
                Require(
                    cameraInputApplied.RootElement.GetProperty("status").GetString() == "applied"
                    && Math.Abs(quality.GetProperty("orbit_sensitivity").GetDouble() - 0.35) < 0.001
                    && Math.Abs(quality.GetProperty("pan_sensitivity").GetDouble() - 1.15) < 0.001
                    && quality.GetProperty("invert_orbit_x").GetBoolean()
                    && quality.GetProperty("invert_pan_y").GetBoolean()
                    && Math.Abs(camera.GetProperty("yaw_degrees").GetDouble() - 23.0) < 0.01
                    && Math.Abs(camera.GetProperty("pitch_degrees").GetDouble() + 40.0) < 0.01
                    && Math.Abs(pan[0].GetDouble() - 0.20) < 0.001
                    && Math.Abs(pan[1].GetDouble() + 0.10) < 0.001,
                    "resident renderer did not apply the live Orbit/Pan camera settings");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "package_load_request",
                request_id = 4,
                generation = 4,
                package_path = replacementPackageRoot,
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var refreshed = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "package_load_applied",
                       timeout.Token).ConfigureAwait(false))
            {
                residentSessionId = refreshed.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
                Require(
                    refreshed.RootElement.GetProperty("resident_scene_load_count").GetInt64() == 4
                    && residentSessionId.Length > 0,
                    "same-model resident refresh did not complete in place");
            }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "presentation_state_update",
                session_id = residentSessionId,
                request_id = 41,
                base_revision = 0,
                process_generation = 1,
                protocol_version = 2,
                presentation_generation = 3,
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var refreshedCamera = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "presentation_state_update_ack",
                       timeout.Token).ConfigureAwait(false))
            {
                var camera = refreshedCamera.RootElement
                    .GetProperty("presentation")
                    .GetProperty("view_contexts")
                    .EnumerateArray()
                    .Single(view => view.GetProperty("id").GetString() == "editable")
                    .GetProperty("camera");
                var pan = camera.GetProperty("pan");
                Require(
                    refreshedCamera.RootElement.GetProperty("status").GetString() == "applied"
                    && Math.Abs(camera.GetProperty("yaw_degrees").GetDouble() - 23.0) < 0.01
                    && Math.Abs(camera.GetProperty("pitch_degrees").GetDouble() + 40.0) < 0.01
                    && Math.Abs(camera.GetProperty("zoom").GetDouble() - cameraZoomBeforeRefresh) < 0.001
                    && Math.Abs(pan[0].GetDouble() - 0.20) < 0.001
                    && Math.Abs(pan[1].GetDouble() + 0.10) < 0.001,
                    "same-model resident refresh reset the user's orbit, pan, or zoom");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "package_load_request",
                request_id = 5,
                generation = 5,
                package_path = runtimeRoot,
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var failed = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "package_load_failed",
                       timeout.Token).ConfigureAwait(false))
            {
                Require(failed.RootElement.GetProperty("request_id").GetInt64() == 5, "resident renderer rejected the wrong package request");
                Require(failed.RootElement.GetProperty("process_id").GetInt32() == process.Id, "failed package load changed renderer process");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                @event = "package_load_request",
                request_id = 6,
                generation = 6,
                package_path = replacementPackageRoot,
            })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            using (var recovered = await ReadRendererEventAsync(
                       process.StandardOutput,
                       "package_load_applied",
                       timeout.Token).ConfigureAwait(false))
            {
                Require(recovered.RootElement.GetProperty("process_id").GetInt32() == process.Id, "resident renderer restarted after a rejected package");
                Require(
                    recovered.RootElement.GetProperty("resident_scene_load_count").GetInt64() == 5,
                    "failed resident package load changed or lost the prior D3D11 scene");
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            _ = await stderrTask.ConfigureAwait(false);
            throw;
        }
    }

    private sealed class TestRendererHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;

        public TestRendererHost(int x, int y, int width, int height)
        {
            var ready = new TaskCompletionSource<(IntPtr Handle, Dispatcher Dispatcher)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    handle = CreateWindowExW(
                        0,
                        "STATIC",
                        string.Empty,
                        0x10000000 | 0x02000000 | 0x04000000,
                        x,
                        y,
                        width,
                        height,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        GetModuleHandleW(null),
                        IntPtr.Zero);
                    if (handle == IntPtr.Zero)
                    {
                        throw new InvalidOperationException($"Test renderer host could not be created (Win32 {Marshal.GetLastWin32Error()}).");
                    }
                    ready.TrySetResult((handle, dispatcher));
                    Dispatcher.Run();
                }
                catch (Exception exception)
                {
                    ready.TrySetException(exception);
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                    {
                        _ = DestroyWindow(handle);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ArchiveLite renderer host smoke",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            var state = ready.Task.GetAwaiter().GetResult();
            Handle = state.Handle;
            _dispatcher = state.Dispatcher;
        }

        public IntPtr Handle { get; }

        public void Dispose()
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<JsonDocument> ReadRendererEventAsync(
        StreamReader reader,
        string expectedEvent,
        CancellationToken cancellationToken)
    {
        var observed = new List<string>();
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException($"Renderer exited before '{expectedEvent}'.");
                try
                {
                    var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("event", out var eventName)
                        && string.Equals(eventName.GetString(), expectedEvent, StringComparison.Ordinal))
                    {
                        return document;
                    }
                    if (string.Equals(expectedEvent, "package_load_applied", StringComparison.Ordinal)
                        && string.Equals(eventName.GetString(), "package_load_failed", StringComparison.Ordinal))
                    {
                        var message = document.RootElement.TryGetProperty("message", out var failureMessage)
                            ? failureMessage.GetString()
                            : "unknown resident package load failure";
                        document.Dispose();
                        throw new InvalidDataException(message);
                    }
                    if (eventName.ValueKind == JsonValueKind.String && observed.Count < 16)
                    {
                        observed.Add(eventName.GetString() ?? string.Empty);
                    }
                    document.Dispose();
                }
                catch (JsonException)
                {
                    // Non-protocol diagnostics remain available through the process output.
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Renderer did not emit '{expectedEvent}' before the resident-switch timeout; observed: {string.Join(", ", observed)}.",
                exception);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr child);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    private static Task TestTextDecodingAsync()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Crimson UTF-16")).ToArray();
        Require(TextDecoding.LooksTextual(utf16), "UTF-16 text was classified as binary");
        Require(TextDecoding.Decode(utf16) == "Crimson UTF-16", "UTF-16 decode is wrong");
        var latin1 = new byte[] { (byte)'C', (byte)'a', (byte)'f', 0xE9 };
        Require(TextDecoding.Decode(latin1) == "Café", "Latin-1 fallback decode is wrong");
        return Task.CompletedTask;
    }

    private static async Task TestNativeArchiveAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var native = new NativeArchiveCore();
        native.EnsureCompatible();
        var singlePamtFingerprint = await ArchiveFingerprint.ComputeAsync(fixture.Pamt, CancellationToken.None).ConfigureAwait(false);
        Require(singlePamtFingerprint.SourceFiles.Contains(fixture.Paz, StringComparer.OrdinalIgnoreCase), "single-PAMT fingerprint omitted its PAZ source");
        Require(singlePamtFingerprint.SourceFiles.Contains(fixture.Pathc, StringComparer.OrdinalIgnoreCase), "single-PAMT fingerprint omitted PATHC metadata");
        var indexPath = Path.Combine(fixture.Root, "native-index.ali");
        var nativeProgress = new List<ProgressUpdate>();
        var count = native.BuildIndex(fixture.Root, indexPath, nativeProgress.Add, CancellationToken.None);
        Require(count == 4, "native index count is wrong");
        Require(
            nativeProgress.Any(update => update.Phase == "index_parse" && update.Total == 1 && update.Completed == 1)
            && nativeProgress.Any(update => update.Phase == "index_sort" && update.Total == 4 && update.Completed == 4)
            && nativeProgress.Any(update => update.Phase == "index_write" && update.Total == 4 && update.Completed == 4)
            && nativeProgress.Any(update => update.Phase == "index_publish" && update.Total == 1 && update.Completed == 1),
            "native index build did not report real parse, sort, write, and publish totals");
        using var index = ArchiveIndex.Open(indexPath);
        Require(index.EntryCount == 4, "managed index count is wrong");
        var pathMatches = index.FindEntriesByPath("TEXT\\HELLO.TXT");
        Require(pathMatches.Count == 1 && pathMatches[0].Path == "text/hello.txt", "indexed companion-path lookup is wrong");
        var entries = Enumerable.Range(0, 4).Select(id => index.ReadEntry(id)).ToArray();
        var text = entries.Single(entry => entry.Path == "text/hello.txt");
        var decoded = native.Decode(text);
        Require(Encoding.UTF8.GetString(decoded.Bytes).Contains("Crimson", StringComparison.Ordinal), "raw text decode failed");
        var lz4 = entries.Single(entry => entry.Path == "materials/sample.material");
        var lz4Decoded = native.Decode(lz4);
        Require(Encoding.UTF8.GetString(lz4Decoded.Bytes) == "material alpha", "LZ4 text decode failed");
        Require(lz4Decoded.Note == "LZ4", "LZ4 diagnostic note is missing");
        var partialDds = native.Decode(entries.Single(entry => entry.Path == "texture/test.dds"));
        Require(partialDds.Bytes.Length == 0x88 && partialDds.Bytes.AsSpan(0, 4).SequenceEqual("DDS "u8), "managed PATHC DDS decode failed");
        Require(partialDds.Note == "PartialDDS+PATHC", "managed PATHC DDS diagnostic note is missing");
    }

    private static async Task TestArchiveServicesAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var beforePamt = await Sha256Async(fixture.Pamt).ConfigureAwait(false);
        var beforePaz = await Sha256Async(fixture.Paz).ConfigureAwait(false);
        var beforePathc = await Sha256Async(fixture.Pathc).ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var openProgress = new List<ProgressUpdate>();
        var opened = await sessions.OpenAsync(
            new OpenArchiveRequest(fixture.Root, true),
            CancellationToken.None,
            update =>
            {
                openProgress.Add(update);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        Require(openProgress.Any(update => update.Phase == "fingerprint"), "archive open did not publish fingerprint progress");
        Require(openProgress.Any(update => update.Phase == "index_build"), "archive open did not publish index-build progress");
        Require(openProgress.Any(update => update.Phase == "validate"), "archive open did not publish validation progress");
        var queries = new ArchiveQueryService(sessions);
        var directPage = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, PageSize: 2),
            8,
            CancellationToken.None).ConfigureAwait(false);
        Require(directPage.TotalMatches == 4 && directPage.Entries.Count == 2, "direct flat-path page is wrong");
        Require(directPage.Folders.Count == 0 && directPage.Categories.Count == 0, "direct flat-path page performed navigation aggregation");
        var scopedPage = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, EntryIds: [directPage.Entries[1].EntryId]),
            81,
            CancellationToken.None).ConfigureAwait(false);
        Require(
            scopedPage.TotalMatches == 1 && scopedPage.Entries.Single().EntryId == directPage.Entries[1].EntryId,
            "Item Finder exact-entry scope leaked unrelated archive rows");
        var emptyScope = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, EntryIds: []),
            82,
            CancellationToken.None).ConfigureAwait(false);
        Require(emptyScope.TotalMatches == 0, "an empty Item Finder scope incorrectly showed the full archive");
        await RequireThrowsAsync<InvalidDataException>(() => queries.QueryAsync(
            new ArchiveQuerySpec(
                opened.SessionId,
                EntryIds: Enumerable.Range(0, 1025).Select(static value => (long)value).ToArray()),
            83,
            CancellationToken.None)).ConfigureAwait(false);
        var page = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, Extensions: [".txt", ".material"]),
            9,
            CancellationToken.None).ConfigureAwait(false);
        Require(page.TotalMatches == 2, "archive extension query count is wrong");
        Require(page.Generation == 9, "query generation was not retained");
        var facetProgress = new List<ProgressUpdate>();
        var facets = await new ArchiveFacetsService(sessions).LoadAsync(
            new ArchiveFacetsRequest(opened.SessionId),
            update =>
            {
                facetProgress.Add(update);
                return Task.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        Require(facets.Extensions.Count == 4, "extension catalogue count is wrong");
        Require(
            facets.Extensions.Single(item => item.Extension == ".dds").Category == ArchiveExtensionCategory.TextureImage,
            "DDS extension category is wrong");
        Require(
            facets.Extensions.Single(item => item.Extension == ".material").Category == ArchiveExtensionCategory.MaterialMetadata,
            "material extension category is wrong");
        Require(
            facets.Extensions.Single(item => item.Extension == ".txt").Category == ArchiveExtensionCategory.UserInterfaceText,
            "text extension category is wrong");
        Require(facetProgress.Any(update => update.Phase == "extension_scan"), "extension catalogue did not report progress");
        var sortedPage = await queries.QueryAsync(
            new ArchiveQuerySpec(
                opened.SessionId,
                SortField: ArchiveSortField.OriginalSize,
                SortDescending: true,
                PageSize: 2),
            10,
            CancellationToken.None).ConfigureAwait(false);
        Require(sortedPage.Entries.Count == 2, "bounded sorted page size is wrong");
        Require(sortedPage.Entries[0].OriginalSize >= sortedPage.Entries[1].OriginalSize, "server-side descending sort is wrong");
        var reversePathPage = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, SortDescending: true, PageSize: 2),
            11,
            CancellationToken.None).ConfigureAwait(false);
        Require(StringComparer.OrdinalIgnoreCase.Compare(reversePathPage.Entries[0].Path, reversePathPage.Entries[1].Path) >= 0, "descending path paging is wrong");
        var previewService = new ArchivePreviewService(sessions, native);
        var textEntry = page.Entries.Single(entry => entry.Extension == ".txt");
        var preview = await previewService.BuildAsync(
            new PreviewRequest(opened.SessionId, textEntry.EntryId),
            CancellationToken.None).ConfigureAwait(false);
        Require(
            preview.Kind == PreviewKind.Text
            && preview.ArtifactPath is not null
            && preview.Syntax == ".txt",
            "full text preview artifact is missing");
        Require(
            (await File.ReadAllTextAsync(preview.ArtifactPath!).ConfigureAwait(false)).Contains("Crimson", StringComparison.Ordinal),
            "full text preview artifact has the wrong content");
        var imagePage = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, Extensions: [".dds"]),
            12,
            CancellationToken.None).ConfigureAwait(false);
        Require(
            imagePage.Entries.Single() is { FileType: ArchiveEntryFileType.Texture, TextureUsage: ArchiveTextureUsage.Unknown },
            "an unrecognized DDS archive row did not expose Type=Texture and Usage=Unknown");
        var fileTypePage = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, SortField: ArchiveSortField.FileType, PageSize: 4),
            121,
            CancellationToken.None).ConfigureAwait(false);
        Require(
            fileTypePage.Entries.Zip(fileTypePage.Entries.Skip(1)).All(pair => pair.First.FileType.CompareTo(pair.Second.FileType) <= 0),
            "server-side file-type sorting is not deterministic");
        var usagePage = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, SortField: ArchiveSortField.TextureUsage, PageSize: 4),
            122,
            CancellationToken.None).ConfigureAwait(false);
        Require(
            usagePage.Entries.Zip(usagePage.Entries.Skip(1)).All(pair => pair.First.TextureUsage.CompareTo(pair.Second.TextureUsage) <= 0),
            "server-side texture-usage sorting is not deterministic");
        var imagePreview = await previewService.BuildAsync(
            new PreviewRequest(opened.SessionId, imagePage.Entries.Single().EntryId),
            CancellationToken.None).ConfigureAwait(false);
        Require(imagePreview.Kind == PreviewKind.Image && imagePreview.ArtifactPath is not null, "DDS preview artifact is missing");
        var imageBytes = await File.ReadAllBytesAsync(imagePreview.ArtifactPath!).ConfigureAwait(false);
        Require(
            imageBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "DDS preview was not decoded to a displayable PNG");
        var imageEntry = imagePage.Entries.Single();
        var thumbnailInput = Path.Combine(Path.GetTempPath(), $"cdmw-item-icon-{Guid.NewGuid():N}.dds");
        try
        {
            await File.WriteAllBytesAsync(thumbnailInput, native.Decode(imageEntry).Bytes).ConfigureAwait(false);
            var texturePreviews = new NativeTexturePreviewService();
            var thumbnail = await texturePreviews.BuildThumbnailAsync(
                sessions.GetRequired(opened.SessionId),
                imageEntry,
                thumbnailInput,
                120,
                CancellationToken.None).ConfigureAwait(false);
            File.Delete(thumbnailInput);
            var warmThumbnail = await texturePreviews.BuildThumbnailAsync(
                sessions.GetRequired(opened.SessionId),
                imageEntry,
                thumbnailInput,
                120,
                CancellationToken.None).ConfigureAwait(false);
            Require(
                Path.GetFullPath(thumbnail).Equals(Path.GetFullPath(warmThumbnail), StringComparison.OrdinalIgnoreCase)
                && texturePreviews.TryGetCachedThumbnail(sessions.GetRequired(opened.SessionId), imageEntry, 120) == thumbnail,
                "a warm Item Finder icon reran input work instead of reusing the persistent thumbnail");
        }
        finally
        {
            File.Delete(thumbnailInput);
        }
        var search = new TextSearchService(sessions, native);
        var result = await search.SearchAsync(
            new TextSearchRequest(
                TextSearchSourceKind.Archive,
                opened.SessionId,
                "crimson",
                false,
                false,
                null,
                [".txt"]),
            CancellationToken.None).ConfigureAwait(false);
        Require(result.Matches.Count == 1, "literal archive search did not find one match");
        Require(result.Matches[0].Line == 1, "search line number is wrong");
        var regex = await search.SearchAsync(
            new TextSearchRequest(
                TextSearchSourceKind.Archive,
                opened.SessionId,
                "line\\s+2",
                true,
                false,
                null,
                [".txt"]),
            CancellationToken.None).ConfigureAwait(false);
        Require(regex.Matches.Count == 1, "regex archive search did not find one match");
        Require(await Sha256Async(fixture.Pamt).ConfigureAwait(false) == beforePamt, "PAMT changed during read-only services");
        Require(await Sha256Async(fixture.Paz).ConfigureAwait(false) == beforePaz, "PAZ changed during read-only services");
        Require(await Sha256Async(fixture.Pathc).ConfigureAwait(false) == beforePathc, "PATHC changed during read-only services");
    }

    private static async Task TestArchiveExportAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(new OpenArchiveRequest(fixture.Root, true), CancellationToken.None).ConfigureAwait(false);
        var queries = new ArchiveQueryService(sessions);
        var page = await queries.QueryAsync(
            new ArchiveQuerySpec(opened.SessionId, PathText: "hello"),
            1,
            CancellationToken.None).ConfigureAwait(false);
        var exportRoot = fixture.OutputRoot;
        var service = new ArchiveExportService(
            sessions,
            queries,
            native,
            new NativeModelExportService(new NativeModelPreviewService()));
        await RequireThrowsAsync<InvalidDataException>(() => service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.RawEntries,
                fixture.Root,
                [page.Entries.Single().EntryId],
                null),
            null,
            CancellationToken.None)).ConfigureAwait(false);
        await RequireThrowsAsync<NotSupportedException>(() => service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.Wav,
                exportRoot,
                [page.Entries.Single().EntryId],
                null),
            null,
            CancellationToken.None)).ConfigureAwait(false);
        await RequireThrowsAsync<InvalidDataException>(() => service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.Obj,
                exportRoot,
                [page.Entries.Single().EntryId],
                null,
                ManifestFormat: ExportManifestFormat.None,
                SingleOutputPath: Path.Combine(fixture.Root, "unsafe.obj")),
            null,
            CancellationToken.None)).ConfigureAwait(false);
        var unsupportedMesh = await service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.Obj,
                Path.Combine(exportRoot, "unsupported-mesh"),
                [page.Entries.Single().EntryId],
                null,
                ManifestFormat: ExportManifestFormat.None),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(
            unsupportedMesh.Failed == 1 && unsupportedMesh.Exported == 0,
            "non-model OBJ export did not fail closed");
        Require(
            !File.Exists(Path.Combine(exportRoot, "unsupported-mesh", "base", "text", "hello.obj")),
            "non-model OBJ export silently wrote a differently formatted file");
        var result = await service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.RawEntries,
                exportRoot,
                [page.Entries.Single().EntryId],
                null),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(result.Exported == 1 && result.Failed == 0, "archive export result is wrong");
        var output = Path.Combine(exportRoot, "base", "text", "hello.txt");
        Require(File.Exists(output), "archive export did not preserve the package and virtual path");
        Require(await File.ReadAllTextAsync(output).ConfigureAwait(false) == "Hello Crimson\nline 2", "archive export bytes are wrong");
        Require(result.ManifestPath is not null && File.Exists(result.ManifestPath), "JSON manifest was not written");

        var flatRoot = Path.Combine(fixture.OutputRoot, "files-only");
        var flatResult = await service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.RawEntries,
                flatRoot,
                [page.Entries.Single().EntryId],
                null,
                PathLayout: ExportPathLayout.FilesOnly),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(flatResult.Exported == 1 && flatResult.Failed == 0, "file-only archive export failed");
        Require(
            File.Exists(Path.Combine(flatRoot, "hello.txt"))
            && !File.Exists(Path.Combine(flatRoot, "base", "text", "hello.txt")),
            "file-only archive export did not flatten the package and virtual path");

        var singleRoot = Path.Combine(fixture.OutputRoot, "single-selected");
        var singlePath = Path.Combine(singleRoot, "hello-original.txt");
        var singleRaw = await service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.RawEntries,
                singleRoot,
                [page.Entries.Single().EntryId],
                null,
                ManifestFormat: ExportManifestFormat.None,
                SingleOutputPath: singlePath),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(
            singleRaw.Exported == 1
            && await File.ReadAllTextAsync(singlePath).ConfigureAwait(false) == "Hello Crimson\nline 2",
            "unified Export selected cannot save one entry to an explicit original-format file");

        var folderExportRoot = Path.Combine(fixture.OutputRoot, "folder-tree");
        var folderExport = await service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.FolderTree,
                folderExportRoot,
                [],
                null,
                FolderPath: "text"),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(folderExport.Exported == 1 && folderExport.Failed == 0, "folder-tree export did not resolve the selected archive folder");
        Require(
            File.Exists(Path.Combine(folderExportRoot, "base", "text", "hello.txt")),
            "folder-tree export did not preserve the full-app package folder structure");

        var collision = await service.ExportAsync(
            new ExportPlanRequest(
                opened.SessionId,
                ExportKind.RawEntries,
                exportRoot,
                [page.Entries.Single().EntryId],
                null,
                CollisionPolicy: ExportCollisionPolicy.Skip),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(collision.Skipped == 1, "skip collision policy did not preserve the destination");

        var looseRoot = Path.Combine(fixture.Root, "loose-source");
        Directory.CreateDirectory(Path.Combine(looseRoot, "notes"));
        await File.WriteAllTextAsync(Path.Combine(looseRoot, "notes", "result.txt"), "Crimson loose result").ConfigureAwait(false);
        var textSearch = new TextSearchService(sessions, native);
        var looseSearch = await textSearch.SearchAsync(
            new TextSearchRequest(
                TextSearchSourceKind.LooseFolder,
                looseRoot,
                "loose",
                false,
                false,
                null,
                [".txt"]),
            CancellationToken.None).ConfigureAwait(false);
        Require(looseSearch.Matches.Count == 1, "loose-folder search did not find its text file");
        var loosePreview = await textSearch.BuildPreviewAsync(
            new TextDocumentRequest(
                TextSearchSourceKind.LooseFolder,
                looseRoot,
                looseSearch.Matches[0].Path),
            CancellationToken.None).ConfigureAwait(false);
        Require(
            loosePreview.Kind == PreviewKind.Text
            && loosePreview.ArtifactPath is not null
            && await File.ReadAllTextAsync(loosePreview.ArtifactPath!).ConfigureAwait(false) == "Crimson loose result",
            "loose text-search preview did not publish the complete file");
        await RequireThrowsAsync<InvalidDataException>(() => service.ExportAsync(
            new ExportPlanRequest(
                null,
                ExportKind.RawEntries,
                Path.Combine(looseRoot, "unsafe-output"),
                [],
                [looseSearch.Matches[0].Path],
                looseRoot),
            null,
            CancellationToken.None)).ConfigureAwait(false);
        var looseExport = await service.ExportAsync(
            new ExportPlanRequest(
                null,
                ExportKind.RawEntries,
                Path.Combine(fixture.OutputRoot, "loose-search"),
                [],
                [looseSearch.Matches[0].Path],
                looseRoot),
            null,
            CancellationToken.None).ConfigureAwait(false);
        Require(looseExport.Exported == 1, "loose search-result export failed");
        await using var looseManifestStream = File.OpenRead(looseExport.ManifestPath!);
        var looseManifest = await JsonSerializer.DeserializeAsync<ArchiveLiteManifest>(
            looseManifestStream,
            WorkerProtocol.JsonOptions).ConfigureAwait(false);
        Require(looseManifest?.LooseFiles.Count == 1, "loose export manifest did not record its source");
    }

    private static async Task TestWorkerBoundaryAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAsync().ConfigureAwait(false);
        var beforePamt = await Sha256Async(fixture.Pamt).ConfigureAwait(false);
        var beforePaz = await Sha256Async(fixture.Paz).ConfigureAwait(false);
        var workerPath = FindWorkerOutputPath();
        Require(File.Exists(workerPath), $"worker output was not found: {workerPath}");

        var pipeName = $"cdmw-archive-lite-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = $"--pipe \"{pipeName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
        }) ?? throw new InvalidOperationException("worker test process could not be started");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            var ping = await ExchangeAsync(writer, reader, WorkerProtocol.Ping, 1, new PingRequest("test"), timeout.Token).ConfigureAwait(false);
            var pingResult = WorkerProtocol.ReadPayload<PingResult>(ping);
            Require(pingResult?.ProtocolVersion == WorkerProtocol.Version, "worker ping protocol version is wrong");

            var workerProgress = new List<ProgressUpdate>();
            var openedMessage = await ExchangeAsync(
                writer,
                reader,
                WorkerProtocol.OpenArchive,
                2,
                new OpenArchiveRequest(fixture.Root, true),
                timeout.Token,
                workerProgress).ConfigureAwait(false);
            var opened = WorkerProtocol.ReadPayload<OpenArchiveResult>(openedMessage)
                ?? throw new InvalidDataException("worker open response is missing");
            Require(opened.EntryCount == 4, "worker archive count is wrong");
            Require(workerProgress.Any(update => update.Phase == "fingerprint"), "worker did not forward archive-open progress");

            var queryMessage = await ExchangeAsync(
                writer,
                reader,
                WorkerProtocol.QueryArchive,
                3,
                new ArchiveQuerySpec(opened.SessionId, PathText: "hello"),
                timeout.Token).ConfigureAwait(false);
            var page = WorkerProtocol.ReadPayload<ArchivePageResult>(queryMessage)
                ?? throw new InvalidDataException("worker query response is missing");
            Require(page.TotalMatches == 1 && page.Entries.Single().Path == "text/hello.txt", "worker query result is wrong");

            var documentMessage = await ExchangeAsync(
                writer,
                reader,
                WorkerProtocol.TextDocument,
                4,
                new TextDocumentRequest(
                    TextSearchSourceKind.Archive,
                    opened.SessionId,
                    page.Entries.Single().Path,
                    page.Entries.Single().EntryId),
                timeout.Token).ConfigureAwait(false);
            var documentPreview = WorkerProtocol.ReadPayload<PreviewResult>(documentMessage)
                ?? throw new InvalidDataException("worker text-document response is missing");
            Require(
                documentPreview.ArtifactPath is not null
                && await File.ReadAllTextAsync(documentPreview.ArtifactPath, timeout.Token).ConfigureAwait(false) == "Hello Crimson\nline 2",
                "worker did not publish the complete selected text file");

            var associationProgress = new List<ProgressUpdate>();
            var associationMessage = await ExchangeAsync(
                writer,
                reader,
                WorkerProtocol.FindAssociatedAssets,
                5,
                new FindAssociatedAssetsRequest(opened.SessionId, page.Entries.Single().EntryId),
                timeout.Token,
                associationProgress).ConfigureAwait(false);
            var associations = WorkerProtocol.ReadPayload<FindAssociatedAssetsResult>(associationMessage)
                ?? throw new InvalidDataException("worker associated-assets response is missing");
            Require(associations.Assets.Count == 0, "worker invented associations for an isolated text file");
            Require(
                associationProgress.Any(update => update.Phase == "association_lookup"),
                "worker did not forward associated-asset progress");

            var healthMessage = await ExchangeAsync(
                writer,
                reader,
                WorkerProtocol.InspectArchiveCache,
                6,
                new ArchiveCacheHealthRequest(fixture.Root),
                timeout.Token).ConfigureAwait(false);
            var health = WorkerProtocol.ReadPayload<ArchiveCacheHealthResult>(healthMessage)
                ?? throw new InvalidDataException("worker cache-health response is missing");
            Require(health.State == ArchiveCacheHealthState.Current, "worker did not report the freshly opened archive cache as current");

            await ExchangeAsync(writer, reader, WorkerProtocol.Shutdown, 7, new { }, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            Require(process.ExitCode == 0, "worker did not exit cleanly");
        }
        catch (Exception exception)
        {
            var stderr = process.HasExited ? await stderrTask.ConfigureAwait(false) : string.Empty;
            throw new InvalidOperationException($"Worker boundary failed. {stderr}", exception);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
        }

        Require(await Sha256Async(fixture.Pamt).ConfigureAwait(false) == beforePamt, "worker changed the PAMT source");
        Require(await Sha256Async(fixture.Paz).ConfigureAwait(false) == beforePaz, "worker changed the PAZ source");
    }

    private static async Task<WorkerMessage> ExchangeAsync<T>(
        StreamWriter writer,
        StreamReader reader,
        string kind,
        long generation,
        T payload,
        CancellationToken cancellationToken,
        ICollection<ProgressUpdate>? progress = null)
    {
        var request = WorkerProtocol.Request(Guid.NewGuid(), generation, kind, payload);
        var json = JsonSerializer.Serialize(request, WorkerProtocol.JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("worker disconnected before replying");
            Require(Encoding.UTF8.GetByteCount(line) <= WorkerProtocol.MaximumMessageBytes, "worker response exceeds the protocol limit");
            var response = JsonSerializer.Deserialize<WorkerMessage>(line, WorkerProtocol.JsonOptions)
                ?? throw new InvalidDataException("worker response could not be decoded");
            if (response.RequestId != request.RequestId)
            {
                continue;
            }
            if (response.Status == WorkerMessageStatus.Progress)
            {
                if (WorkerProtocol.ReadPayload<ProgressUpdate>(response) is { } update)
                {
                    progress?.Add(update);
                }
                continue;
            }
            if (response.Status == WorkerMessageStatus.Started)
            {
                continue;
            }
            if (response.Status == WorkerMessageStatus.Error)
            {
                throw new InvalidOperationException(response.Error?.Message ?? "worker request failed");
            }
            Require(response.Status == WorkerMessageStatus.Result, $"unexpected worker terminal status: {response.Status}");
            return response;
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Cdmw.ArchiveLite.slnx"))
                    && Directory.Exists(Path.Combine(current.FullName, "src", "Cdmw.ArchiveLite.App")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("repository root could not be located");
    }

    private static string FindWorkerOutputPath()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cdmw.ArchiveLite.Worker",
            "bin",
            FindBuildConfiguration(),
            "net10.0-windows",
            "win-x64",
            "CdmwArchiveLite.Worker.exe");
    }

    private static string FindBuildConfiguration()
    {
        return AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
    }

    private static Task RunOnWpfDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ArchiveLiteStartupCacheTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static Task TestSharedContentAnalyzersAsync()
    {
        Require(ArchiveContentRegistry.All.Count >= 100, "shared content manifest is unexpectedly small");
        Require(
            ArchiveEntryClassifier.Classify("models/tree.obj", ".obj") == ArchiveEntryRole.Text,
            "textual model formats must not be routed to hex/model decoding");
        Require(
            ArchiveEntryClassifier.ClassifyExtensionCategory(".pae") == ArchiveExtensionCategory.AnimationScene,
            ".pae must stay in the animation/effect category");
        Require(
            ArchiveEntryClassifier.ClassifyExtensionCategory(".paschedulepath") == ArchiveExtensionCategory.AnimationScene,
            ".paschedulepath must use the canonical extension spelling");

        var analyzer = new ArchiveContentAnalyzer();
        var meshInfoBytes = Encoding.ASCII.GetBytes(
            "MeshBounds\0SocketRoot\0BreakablePart\0physics/collision.hkx\0texture/tree_color.dds\0");
        var meshInfo = analyzer.Analyze(".meshinfo", "tree.meshinfo", meshInfoBytes);
        Require(meshInfo.ContentKind == "meshinfo", "MeshInfo did not use the semantic analyzer");
        Require(meshInfo.References.Any(reference => reference.Value.EndsWith("tree_color.dds", StringComparison.OrdinalIgnoreCase)),
            "MeshInfo asset references were not recovered");
        Require(meshInfo.ToReadableText().Contains("Candidate", StringComparison.Ordinal),
            "MeshInfo inferred values must remain visibly labeled as candidates");

        var pat = analyzer.Analyze(".pat", "tree.pat", BuildSyntheticPat());
        var patModel = pat.Model ?? throw new InvalidOperationException("PAT structural tables did not decode");
        Require(patModel is { LodCount: 1, VertexCount: 3, IndexCount: 3, DrawCount: 1 },
            "PAT structural tables did not decode");
        Require(patModel.MaterialCount == 1, "PAT material strings did not decode");
        using var json = JsonDocument.Parse(ArchiveContentJson.Serialize(pat));
        Require(json.RootElement.GetProperty("schema_version").GetInt32() == 1,
            "semantic JSON schema version was not serialized");
        return Task.CompletedTask;
    }

    private static async Task TestNativePatGeometryAsync()
    {
        Require(NativeModelPreviewService.Supports(".pat"), "Archive Lite does not route PAT through native model preview");
        var executable = Path.Combine(
            FindRepositoryRoot(),
            "native",
            "cdmw_preview_core",
            "build",
            FindBuildConfiguration(),
            "cdmw-preview-core.exe");
        Require(File.Exists(executable), $"{FindBuildConfiguration()} native preview core is not built");
        var testRoot = Path.Combine(ArchiveLiteDataPaths.Root, "native-pat");
        Directory.CreateDirectory(testRoot);
        var input = Path.Combine(testRoot, "tree.pat");
        var report = Path.Combine(testRoot, "report.json");
        await File.WriteAllBytesAsync(input, BuildSyntheticPat()).ConfigureAwait(false);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "mesh-parse-job", input, report, "tree.pat" },
        }) ?? throw new InvalidOperationException("Native preview core did not start");
        var standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        Require(process.ExitCode == 0, $"native PAT parse failed: {standardError}");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(report).ConfigureAwait(false));
        var root = result.RootElement;
        Require(root.GetProperty("status").GetString() == "ok", "native PAT report was not successful");
        Require(root.GetProperty("format").GetString() == "pat", "native PAT report format is wrong");
        Require(root.GetProperty("parser").GetString() == "native_pat_lod0", "native PAT parser identity is wrong");
        Require(root.GetProperty("submesh_count").GetInt32() == 1, "native PAT draw was not emitted");
        Require(root.GetProperty("vertex_count").GetInt32() == 3, "native PAT vertex count is wrong");
        Require(root.GetProperty("face_count").GetInt32() == 1, "native PAT face count is wrong");
    }

    private static byte[] BuildSyntheticPat()
    {
        const int vertexStart = 52;
        const int vertexEnd = vertexStart + 3 * 32;
        const int indexStart = vertexEnd + 8;
        const int indexEnd = indexStart + 6;
        const int drawStart = indexEnd + 8;
        const int drawEnd = drawStart + 16;
        var tail = Encoding.ASCII.GetBytes("oak_mat\0oak_color.dds\0");
        var bytes = new byte[drawEnd + tail.Length];
        "PAR "u8.CopyTo(bytes);
        WriteSingle(bytes, 16, -1f);
        WriteSingle(bytes, 20, -2f);
        WriteSingle(bytes, 24, -3f);
        WriteSingle(bytes, 28, 4f);
        WriteSingle(bytes, 32, 5f);
        WriteSingle(bytes, 36, 6f);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(vertexStart + 32), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(vertexStart + 64 + 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(vertexEnd), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(vertexEnd + 4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(indexStart), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(indexStart + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(indexStart + 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(indexEnd), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(indexEnd + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(drawStart), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(drawStart + 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(drawStart + 8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(drawStart + 12), 3);
        tail.CopyTo(bytes.AsSpan(drawEnd));
        return bytes;
    }

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(value));

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static string ThemeBrushColor(System.Xml.Linq.XDocument theme, string key) =>
        theme.Root!.Elements().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == key))
            .Attribute("Color")?.Value
        ?? throw new InvalidDataException($"Theme brush {key} has no color.");

    private static System.Windows.ResourceDictionary LoadWpfResourceDictionary(string path)
    {
        using var stream = File.OpenRead(path);
        return (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    private static T? FindVisualDescendant<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static double RgbDistance(string first, string second)
    {
        var firstRgb = ParseRgb(first);
        var secondRgb = ParseRgb(second);
        return Math.Sqrt(
            Math.Pow(firstRgb.Red - secondRgb.Red, 2d)
            + Math.Pow(firstRgb.Green - secondRgb.Green, 2d)
            + Math.Pow(firstRgb.Blue - secondRgb.Blue, 2d));
    }

    private static double ContrastRatio(string first, string second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05d)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05d);
    }

    private static double RelativeLuminance(string color)
    {
        var rgb = ParseRgb(color);
        return 0.2126d * Linearize(rgb.Red)
            + 0.7152d * Linearize(rgb.Green)
            + 0.0722d * Linearize(rgb.Blue);

        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045d
                ? value / 12.92d
                : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }
    }

    private static (byte Red, byte Green, byte Blue) ParseRgb(string color)
    {
        if (color.Length != 7 || color[0] != '#')
        {
            throw new FormatException($"Expected #RRGGBB, received {color}.");
        }

        return (
            Convert.ToByte(color.Substring(1, 2), 16),
            Convert.ToByte(color.Substring(3, 2), 16),
            Convert.ToByte(color.Substring(5, 2), 16));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException("The expected asynchronous state was not reached in time.");
            }
            await Task.Delay(15).ConfigureAwait(true);
        }
    }

    private sealed class FakeItemFinderWorker(string iconPath) : IWorkerRequestClient
    {
        private readonly object _requestGate = new();
        private readonly List<ItemCatalogSearchRequest> _searchRequests = [];
        private int _searchCount;
        private int _iconCount;

        public TimeSpan SearchDelay { get; set; }
        public TimeSpan IconDelay { get; set; }
        public bool IgnoreSearchCancellation { get; set; }
        public int SearchCount => Volatile.Read(ref _searchCount);
        public int IconCount => Volatile.Read(ref _iconCount);
        public IReadOnlyList<ItemCatalogSearchRequest> SearchRequests
        {
            get
            {
                lock (_requestGate)
                {
                    return _searchRequests.ToArray();
                }
            }
        }

        public async Task<TResult> SendAsync<TRequest, TResult>(
            string kind,
            long generation,
            TRequest payload,
            CancellationToken cancellationToken,
            IProgress<ProgressUpdate>? progress = null)
        {
            if (kind == WorkerProtocol.SearchItemCatalog && payload is ItemCatalogSearchRequest search)
            {
                Interlocked.Increment(ref _searchCount);
                lock (_requestGate)
                {
                    _searchRequests.Add(search);
                }
                if (SearchDelay > TimeSpan.Zero)
                {
                    if (IgnoreSearchCancellation)
                    {
                        await Task.Delay(SearchDelay).ConfigureAwait(true);
                    }
                    else
                    {
                        await Task.Delay(SearchDelay, cancellationToken).ConfigureAwait(true);
                    }
                }
                var categories = search.Query == "gilded"
                    ? new ItemCatalogCategoryFacet[]
                    {
                        new("Weapon", "Sword", 1),
                        new("Armor", "Helmet", 1),
                    }
                    : [new ItemCatalogCategoryFacet("Weapon", "Sword", 1)];
                var result = new ItemCatalogSearchResult(
                    search.SessionId,
                    1,
                    search.PageStart,
                    search.PageSize,
                    [new ItemCatalogRow(
                        101,
                        "OneHandSword_Gilded",
                        "Gilded Longsword",
                        "Weapon",
                        "Sword",
                        "Recovered category",
                        ["equipment/sword.pac"],
                        ["equipment/sword"],
                        ["ui/icon/item/sword_d.dds"],
                        ["Gilded Longsword"],
                        ["steel"],
                        1,
                        "Synthetic regression row")],
                    categories,
                    [new ItemCatalogValueFacet("steel", 1)]);
                return (TResult)(object)result;
            }
            if (kind == WorkerProtocol.LoadItemIcons && payload is ItemIconBatchRequest icons)
            {
                Interlocked.Increment(ref _iconCount);
                if (IconDelay > TimeSpan.Zero)
                {
                    await Task.Delay(IconDelay, cancellationToken).ConfigureAwait(true);
                }
                return (TResult)(object)new ItemIconBatchResult(
                    icons.SessionId,
                    icons.ItemIds.Select(itemId => new ItemIconResult(itemId, iconPath, "ui/icon/item/sword_d.dds")).ToArray());
            }
            if (kind == WorkerProtocol.WarmItemIcons && payload is WarmItemIconsRequest warmup)
            {
                return (TResult)(object)new WarmItemIconsResult(warmup.SessionId, 1, 1, 0, 0);
            }
            throw new InvalidOperationException($"Unexpected fake Item Finder request: {kind}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }
}
