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
        _viewModel.UpdateWindowSize(ActualWidth, ActualHeight);
        _viewModel.Deactivate();
    }
    private async void OnClosed(object? sender, EventArgs args)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _viewModel.CloseRequested -= OnCloseRequested;
        await ModelPreview.ShutdownAsync().ConfigureAwait(true);
    }
    private void OnSourceInitialized(object? sender, EventArgs args) => ThemedWindowChrome.Apply(this);
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
