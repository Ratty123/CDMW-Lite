using System.Windows;
using System.Windows.Input;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.App.ViewModels;

namespace Cdmw.ArchiveLite.App.Dialogs;

public partial class ModelPreviewSettingsDialog : Window
{
    private const int CameraInputTabIndex = 0;
    private const int AppearanceTabIndex = 1;

    public ModelPreviewSettingsDialog(ArchiveBrowserViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += OnClosed;
    }

    public void ShowCameraInputTab() => ShowTab(CameraInputTabIndex);

    public void ShowAppearanceTab() => ShowTab(AppearanceTabIndex);

    private void ShowTab(int tabIndex)
    {
        SettingsTabs.SelectedIndex = tabIndex;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs) => ThemedWindowChrome.Apply(this);

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        if (Dispatcher.CheckAccess())
        {
            ThemedWindowChrome.Apply(this);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => ThemedWindowChrome.Apply(this));
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs) => Close();

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
