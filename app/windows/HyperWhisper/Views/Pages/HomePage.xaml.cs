using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Services;
using HyperWhisper.ViewModels;
using HyperWhisper.Views.Windows;

namespace HyperWhisper.Views.Pages;

public partial class HomePage : Page
{
    private HomeStatsBarViewModel? _statsViewModel;

    public HomePage()
    {
        InitializeComponent();
        // Disable Frame journal caching so navigating away always unloads this
        // Page — guarantees Unloaded fires and our HistoryService subscriptions
        // can be released. Without this, the Frame can retain evicted Pages.
        System.Windows.Navigation.JournalEntry.SetKeepAlive(this, false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Loaded can re-fire on the same Page instance; detach any prior
            // view-model so we don't leak HistoryService event subscriptions.
            _statsViewModel?.Detach();

            _statsViewModel = new HomeStatsBarViewModel(StatisticsService.Instance, SettingsService.Instance);
            StatsBar.DataContext = _statsViewModel;
            await _statsViewModel.RecomputeAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"HomePage: OnLoaded failed: {ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statsViewModel?.Detach();
        _statsViewModel = null;
    }

    private void OpenShortcutSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.NavigateToSettingsSection("Shortcuts");
        }
    }
}
