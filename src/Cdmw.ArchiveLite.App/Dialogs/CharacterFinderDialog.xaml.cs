using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.App.ViewModels;

namespace Cdmw.ArchiveLite.App.Dialogs;

public partial class CharacterFinderDialog : Window
{
    private readonly CharacterFinderViewModel _viewModel;
    public CharacterFinderDialog(CharacterFinderViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent(); DataContext = viewModel;
        Width = viewModel.WindowWidth; Height = viewModel.WindowHeight;
        Loaded += OnLoaded; Closing += OnClosing; Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        ThemeManager.ThemeChanged += OnThemeChanged;
        viewModel.CloseRequested += OnCloseRequested;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        SearchBox.Focus();
        await _viewModel.ActivateAsync(CancellationToken.None).ConfigureAwait(true);
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        var size = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
        _viewModel.UpdateWindowSize(size.Width, size.Height);
        _viewModel.Deactivate();
    }
    private async void OnClosed(object? sender, EventArgs args)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _viewModel.CloseRequested -= OnCloseRequested;
        await ModelPreview.ShutdownAsync().ConfigureAwait(true);
    }
    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        ThemedWindowChrome.Apply(this);
        MaximizedWindowBounds.Apply(this);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var bounds = NativeWindowPlacement.Capture(handle)?.Restored ?? default;
        var monitors = NativeWindowPlacement.Monitors();
        if (monitors.Count == 0) return;
        var work = monitors.OrderByDescending(monitor => monitor.Bounds.IntersectionArea(bounds)).First().WorkArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var maxWidth = Math.Max(1, work.Width / dpi.DpiScaleX - 24);
        var maxHeight = Math.Max(1, work.Height / dpi.DpiScaleY - 48);
        MinWidth = Math.Min(MinWidth, maxWidth); MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth); Height = Math.Min(Height, maxHeight);
    }
    private void OnThemeChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.CheckAccess()) ThemedWindowChrome.Apply(this);
        else _ = Dispatcher.BeginInvoke(() => ThemedWindowChrome.Apply(this));
    }
    private void OnCloseRequested(object? sender, EventArgs args) => DialogResult = true;
    private void OnCloseClick(object sender, RoutedEventArgs args) => Close();
    private void OnCopyPathClick(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedRow is { } row) Clipboard.SetText(row.Source.Path);
    }
    private void OnSearchKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        _viewModel.SearchCommand.Execute(null); args.Handled = true;
    }
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left || args.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultGrid, source) is not ListBoxItem) return;
        if (_viewModel.ShowExactCommand.CanExecute(null)) _viewModel.ShowExactCommand.Execute(null);
    }
    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (args.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
