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
        // Loaded can re-fire on the same Page instance, and a failed load leaves a
        // view-model subscribed to HistoryService. Release any prior one FIRST, so
        // a blocked assembly cannot leave a subscription alive that recomputes —
        // and fails again — on every later transcript.
        ReleaseStatsViewModel();

        OptionalAssemblyOutcome outcome;
        try
        {
            outcome = await OptionalAssemblyGuard.TryRunAsync(
                OptionalAssemblyGuard.StatisticsAssembly,
                "home_page_loaded",
                LoadStatsBarAsync);
        }
        catch (Exception ex)
        {
            // An ordinary failure inside the stats strip. A load failure never
            // reaches here; TryRunAsync answers LoadFailed for that.
            LoggingService.Warn($"HomePage: OnLoaded failed: {ex.Message}");
            ReleaseStatsViewModel();
            return;
        }

        if (outcome == OptionalAssemblyOutcome.Completed)
        {
            return;
        }

        // Unavailable or LoadFailed: hide the strip and leave nothing subscribed.
        StatsBar.Visibility = Visibility.Collapsed;
        ReleaseStatsViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ReleaseStatsViewModel();

    /// <summary>
    /// Releases the stats view-model when there is one.
    /// </summary>
    /// <remarks>
    /// The null test is load-bearing, not defensive: it keeps
    /// <see cref="DetachStatsViewModel"/> unprepared on a machine that blocks
    /// HyperWhisper.Statistics. On such a machine the field can only ever be null,
    /// because nothing could construct the view-model in the first place.
    /// </remarks>
    private void ReleaseStatsViewModel()
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
        _statsViewModel = new HomeStatsBarViewModel(
            // ast-grep-ignore: no-unguarded-optional-assembly-use -- this IS the guarded boundary, reached only through OptionalAssemblyGuard.TryRunAsync.
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
