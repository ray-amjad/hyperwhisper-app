using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Services;
using HyperWhisper.Statistics;
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

    // The stats strip is backed by HyperWhisper.Statistics, which Windows
    // Application Control can block on an individual machine. Naming
    // HomeStatisticsService here made the CLR load that assembly while it
    // PREPARED this handler, so the FileLoadException was thrown before the `try`
    // below was entered — the catch never ran, and the home page crashed the app
    // (HYPERWHISPER-YF). Every reference now sits behind a NoInlining method that
    // is called only when the guard says the assembly loads.
    // See Services/OptionalAssemblyGuard.cs.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!OptionalAssemblyGuard.IsAvailable(OptionalAssemblyGuard.StatisticsAssembly))
        {
            StatsBar.Visibility = Visibility.Collapsed;
            LoggingService.Warn(
                $"HomePage: stats strip skipped (stage=home_page_loaded, " +
                $"assembly={OptionalAssemblyGuard.StatisticsAssembly}, reason=unavailable)");
            return;
        }

        try
        {
            await LoadStatsBarAsync();
        }
        catch (Exception ex) when (OptionalAssemblyGuard.IsLoadFailure(ex))
        {
            // Never `ex.Message` here: a FileLoadException message carries the
            // installed path, and that path holds the user's account name.
            OptionalAssemblyGuard.MarkUnavailable(
                OptionalAssemblyGuard.StatisticsAssembly, ex, "home_page_loaded");
            StatsBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"HomePage: OnLoaded failed: {ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_statsViewModel == null)
        {
            return;
        }

        DetachStatsViewModel();
    }

    /// <summary>
    /// Warning: do not inline this method and do not name
    /// <c>HomeStatisticsService</c> in its callers. Both put the optional
    /// assembly back into a method that must stay preparable on a machine that
    /// blocks it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task LoadStatsBarAsync()
    {
        // Loaded can re-fire on the same Page instance; detach any prior
        // view-model so we don't leak HistoryService event subscriptions.
        _statsViewModel?.Detach();

        _statsViewModel = new HomeStatsBarViewModel(
            new HomeStatisticsService(new WindowsStatisticsTranscriptProvider()),
            SettingsService.Instance);
        StatsBar.DataContext = _statsViewModel;
        StatsBar.Visibility = Visibility.Visible;
        await _statsViewModel.RecomputeAsync();
    }

    /// <summary>
    /// Warning: do not inline this method. See <see cref="LoadStatsBarAsync"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DetachStatsViewModel()
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
