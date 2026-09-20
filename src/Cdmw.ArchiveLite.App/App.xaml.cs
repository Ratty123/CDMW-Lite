using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cdmw.ArchiveLite.App.Dialogs;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.App.ViewModels;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.App;

public partial class App : Application
{
    private WorkerProcessHost? _worker;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDataPaths.EnsureCreated();
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            await RunSelfTestAsync().ConfigureAwait(true);
            return;
        }

        var settings = await SettingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        LocalizationManager.ApplyCulture(settings.Language);
        ThemeManager.Apply(settings.Theme);
        UiPreferencesManager.Apply(settings.FontSize, settings.LayoutDensity);

        try
        {
            _worker = await WorkerProcessHost.StartAsync(CancellationToken.None).ConfigureAwait(true);
            var viewModel = new MainWindowViewModel(_worker, settings);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await DiagnosticLog.WriteAsync("startup", exception.ToString(), CancellationToken.None).ConfigureAwait(true);
            MessageBox.Show(
                LocalizationManager.Format("StartupFailureMessage", exception.Message),
                LocalizationManager.Get("StartupFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunSelfTestAsync()
    {
        try
        {
            LocalizationManager.ApplyCulture("en");
            ThemeManager.Apply("graphite");
            UiPreferencesManager.Apply("medium", "comfortable");
            _worker = await WorkerProcessHost.StartAsync(CancellationToken.None).ConfigureAwait(true);
            var result = await _worker.SendAsync<PingRequest, PingResult>(
                WorkerProtocol.Ping,
                1,
                new PingRequest(typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0"),
                CancellationToken.None).ConfigureAwait(true);
            if (result.ProtocolVersion != WorkerProtocol.Version)
            {
                throw new InvalidDataException("Worker protocol self-test failed.");
            }
            var viewModel = new MainWindowViewModel(_worker, new LiteSettings());
            var window = new MainWindow(viewModel);
            // Realize templates without saving synthetic layout/settings when the test exits.
            window.Closing -= window.OnClosing;
            window.Opacity = 0; window.ShowInTaskbar = false; window.ShowActivated = false;
            window.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.ArchiveFilterColumn.Width = new GridLength(409);
            window.ArchivePreviewColumn.Width = new GridLength(1000);
            var characters = new CharacterFinderDialog(viewModel.CharacterFinder);
            var items = new ItemFinderDialog(viewModel.ItemFinder);
            viewModel.CharacterFinder.Rows.Add(new CharacterFinderRow(new("layout", "assets", "body",
                "A long character name for layout validation", "layout", "character/model/layout.pac", "1_pc", "1_phm", "resolved", "ready", 1, 1, "layout")));
            var item = new ItemFinderRowViewModel(new(1, "layout", "A long item name for layout validation", "Weapon", "Sword", "",
                ["layout.pac"], ["layout"], [], ["Layout"], 1, "layout"));
            viewModel.ItemFinder.Items.Add(item); viewModel.ItemFinder.SelectedItem = item;
            foreach (var theme in ThemeManager.AvailableThemes)
            {
                ThemeManager.Apply(theme.Id);
                foreach (var size in new[] { new Size(1440, 880), new Size(1200, 720) })
                {
                    ValidateHiddenLayout(window, size, $"theme {theme.Id}");
                }
            }
            ThemeManager.Apply("graphite");
            foreach (var fontSize in UiPreferencesManager.AvailableFontSizes)
            {
                foreach (var density in UiPreferencesManager.AvailableLayoutDensities)
                {
                    UiPreferencesManager.Apply(fontSize.Id, density.Id);
                    var size = new Size(1200, 720);
                    ValidateHiddenLayout(window, size, $"appearance {fontSize.Id}/{density.Id}");
                }
            }
            // The title row packs fixed chrome next to translated labels. German and Russian run long
            // and CJK swaps in a different font stack, so sweep every shipped language at the
            // narrowest supported width rather than trusting that English fits for all of them.
            UiPreferencesManager.Apply("small", "compact");
            foreach (var language in LanguageCatalog.Languages)
            {
                LocalizationManager.ApplyCulture(language.Code);
                foreach (var font in UiPreferencesManager.AvailableFontSizes)
                {
                    UiPreferencesManager.Apply(font.Id, "spacious");
                    var variant = $"{language.Code}/{font.Id}";
                    ValidateHiddenLayout(window, new Size(1200, 720), variant);
                    ValidateFinderLayout(characters, new Size(1040, 680), variant);
                    ValidateFinderLayout(items, new Size(940, 640), variant);
                }
            }
            LocalizationManager.ApplyCulture("en");
            await _worker.ShutdownAsync().ConfigureAwait(true);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            await DiagnosticLog.WriteAsync("self-test", exception.ToString(), CancellationToken.None).ConfigureAwait(true);
            Shutdown(1);
        }
    }

    private static void ValidateHiddenLayout(MainWindow window, Size size, string variant)
    {
        window.Width = size.Width; window.Height = size.Height;
        window.ApplyTemplate();
        window.Measure(size);
        window.Arrange(new Rect(new Point(), size));
        window.UpdateLayout();
        if (!double.IsFinite(window.DesiredSize.Width) || !double.IsFinite(window.DesiredSize.Height))
        {
            throw new InvalidDataException($"The {variant} produced an invalid window layout.");
        }

        const double tolerance = 0.5;
        foreach (var child in window.TitleBarGrid.Children.OfType<FrameworkElement>().Where(static child => child.Visibility == Visibility.Visible))
        {
            var origin = child.TranslatePoint(new Point(), window.TitleBarGrid);
            if (!double.IsFinite(origin.X)
                || origin.X < -tolerance
                || origin.X + child.ActualWidth > size.Width + tolerance)
            {
                throw new InvalidDataException($"The {variant} places a title-row control outside the available window width.");
            }
        }
        if (window.ArchiveWorkspaceGrid.ColumnDefinitions.Sum(column => column.ActualWidth) > window.ArchiveWorkspaceGrid.ActualWidth + tolerance)
            throw new InvalidDataException($"The {variant} restores workspace panes wider than the available window.");
        foreach (var control in window.ArchivePreviewCardLayout.Children.OfType<WrapPanel>()
            .SelectMany(panel => panel.Children.OfType<FrameworkElement>())
            .Concat(((Panel)((FrameworkElement)window.ArchivePreviewFindBox.Parent).Parent).Children.OfType<Button>())
            .Append(window.ArchivePreviewFindBox)
            .Where(control => control.Visibility == Visibility.Visible && control.ActualWidth > 0))
        {
            var origin = control.TranslatePoint(new Point(), window);
            if (origin.X + control.ActualWidth > size.Width + tolerance)
                throw new InvalidDataException($"The {variant} clips a preview control outside the available window width.");
        }
    }

    private static void ValidateFinderLayout(Window window, Size size, string variant)
    {
        window.Width = size.Width; window.Height = size.Height;
        window.ApplyTemplate(); window.Measure(size); window.Arrange(new Rect(new Point(), size)); window.UpdateLayout();
        void Check(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && element.Visibility == Visibility.Visible && element.ActualWidth > 0
                    && child is ButtonBase or ComboBox or TextBox)
                {
                    var origin = element.TranslatePoint(new Point(), window);
                    if (origin.X < -0.5 || origin.X + element.ActualWidth > size.Width + 0.5)
                        throw new InvalidDataException($"{window.Title}: {variant} clips {element.GetType().Name} {element.Name} horizontally.");
                }
                Check(child);
            }
        }
        Check(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (_worker is not null)
        {
            _worker.DisposeImmediately();
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs) =>
        DiagnosticLog.WriteFatal("dispatcher-unhandled", eventArgs.Exception);

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs eventArgs)
    {
        var exception = eventArgs.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled non-exception object: {eventArgs.ExceptionObject}");
        DiagnosticLog.WriteFatal("domain-unhandled", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        DiagnosticLog.WriteFatal("task-unobserved", eventArgs.Exception);
        eventArgs.SetObserved();
    }
}
