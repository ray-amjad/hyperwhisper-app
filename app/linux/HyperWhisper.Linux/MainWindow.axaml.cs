using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;

namespace HyperWhisper.Linux;

public partial class MainWindow : Window
{
    private readonly LinuxDesktopServices _platformServices;
    private readonly ApplicationDb _database;
    private readonly ApplicationShellViewModel _viewModel;
    private Task? _initialization;

    public MainWindow() : this(new LinuxDesktopServices())
    {
        Closed += (_, _) => _platformServices.Dispose();
    }

    internal MainWindow(LinuxDesktopServices platformServices)
    {
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        _database = new ApplicationDb(_platformServices.Paths);
        var settings = new PortableSettingsService(_platformServices.PrivateFiles, _platformServices.Paths);
        _viewModel = new ApplicationShellViewModel(_database, settings);
        InitializeComponent();
        DataContext = _viewModel;
        PlatformStatusText.Text = $"Linux platform connected · {_platformServices.Paths.DataDirectory}";
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e) => await EnsureInitializedAsync();

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }

    private Task EnsureInitializedAsync() => _initialization ??= _viewModel.InitializeAsync();

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is ListBoxItem { Tag: string pageId })
            _viewModel.Navigate(pageId);
    }

    internal async Task<int> RunSmokeTestAsync()
    {
        try
        {
            await EnsureInitializedAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (Bounds.Width <= 0 || Bounds.Height <= 0 || !IsVisible) return 2;
            if (!Path.IsPathFullyQualified(_platformServices.Paths.DataDirectory)
                || _platformServices.PrivateFiles is null
                || _platformServices.GlobalShortcuts is null
                || !_platformServices.ProbeSharedCore()) return 4;
            if (_viewModel.Status.HasError) return 5;

            await SeedSmokeDataAsync();
            var expectedControls = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["home"] = "HomeRefreshButton",
                ["modes"] = "ModeSaveButton",
                ["history"] = "HistoryDeleteButton",
                ["vocabulary"] = "VocabularyAddButton",
                ["settings"] = "SettingsSaveButton"
            };
            foreach (var page in expectedControls)
            {
                _viewModel.Navigate(page.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (!HasVisibleControl(page.Value)
                    || !string.Equals(_viewModel.Status.Message, $"{_viewModel.PageTitle} ready", StringComparison.Ordinal))
                    return 3;
            }

            if (_viewModel.History.Items.Count == 0
                || _viewModel.Vocabulary.Items.Count == 0
                || _viewModel.Modes.Items.Count == 0) return 6;
            _viewModel.Settings.Language = "en";
            _viewModel.Settings.Save();
            if (_viewModel.Settings.Status.HasError) return 7;
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private bool HasVisibleControl(string name)
        => this.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name && control.IsVisible);

    private async Task SeedSmokeDataAsync()
    {
        await using (var context = _database.CreateContext())
        {
            if (!context.Transcripts.Any())
                context.Transcripts.Add(new Transcript { Text = "Smoke transcript", Status = TranscriptStatus.Completed, Date = DateTime.UtcNow });
            if (!context.VocabularyItems.Any())
                context.VocabularyItems.Add(new VocabularyItem { Word = "SmokeTerm" });
            if (!context.Modes.Any())
                context.Modes.Add(new Mode { Name = "Smoke Mode", Language = "en" });
            await context.SaveChangesAsync();
        }
        await Task.WhenAll(
            _viewModel.Home.RefreshAsync(),
            _viewModel.History.RefreshAsync(),
            _viewModel.Vocabulary.RefreshAsync(),
            _viewModel.Modes.RefreshAsync());
    }
}
