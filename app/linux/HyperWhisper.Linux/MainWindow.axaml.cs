using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.ViewModels;
using HyperWhisper.PortableApplication.Transcription;
using Avalonia.Platform.Storage;
using HyperWhisper.LocalApi;
using HyperWhisper.ModelManagement;

namespace HyperWhisper.Linux;

public partial class MainWindow : Window
{
    private readonly LinuxDesktopServices _platformServices;
    private readonly ApplicationDb _database;
    private readonly ApplicationShellViewModel _viewModel;
    private readonly LinuxLocalPostProcessor _postProcessor;
    private readonly PortableSettingsService _settings;
    private readonly PortableModelManager _modelManager;
    private readonly HttpClient _modelHttp = new();
    private readonly TranscriptionWorkflow _workflow;
    private PortableLocalApiHost? _localApiHost;
    private Task? _initialization;

    public MainWindow() : this(new LinuxDesktopServices())
    {
        Closed += (_, _) => _platformServices.Dispose();
    }

    internal MainWindow(LinuxDesktopServices platformServices)
    {
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        _database = new ApplicationDb(_platformServices.Paths);
        _settings = new PortableSettingsService(_platformServices.PrivateFiles, _platformServices.Paths);
        _modelManager = new PortableModelManager(_platformServices.Paths, _modelHttp);
        _postProcessor = new LinuxLocalPostProcessor(
            _platformServices.Paths.ModelsDirectory, _settings, _database);
        _workflow = new TranscriptionWorkflow(
            _platformServices.AudioRecorder,
            _platformServices.AudioDevices,
            _platformServices.AudioTranscriber,
            new HistoryRepository(_database, _platformServices.Paths),
            _postProcessor,
            _platformServices.TextInjection);
        _viewModel = new ApplicationShellViewModel(
            _database, _settings, _workflow, LinuxLocalPostProcessor.RuntimeStatus,
            _modelManager, _platformServices.AudioPlayback,
            new DurableAudioImportService(_platformServices.PrivateFiles, _platformServices.Paths),
            _platformServices.Paths, _platformServices.CredentialStore);
        InitializeComponent();
        DataContext = _viewModel;
        PlatformStatusText.Text = $"Linux platform connected · {_platformServices.Paths.DataDirectory}";
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.Settings.LocalApiSettingsChanged += OnLocalApiSettingsChanged;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await EnsureInitializedAsync();
        await ApplyLocalApiSettingsAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.Settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        if (_localApiHost is not null) _localApiHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _viewModel.Dispose();
        _postProcessor.Dispose();
        _modelHttp.Dispose();
    }

    private Task EnsureInitializedAsync() => _initialization ??= _viewModel.InitializeAsync();

    private async void OnLocalApiSettingsChanged(object? sender, EventArgs e) => await ApplyLocalApiSettingsAsync();

    private async Task ApplyLocalApiSettingsAsync()
    {
        if (_localApiHost is not null)
        {
            await _localApiHost.DisposeAsync();
            _localApiHost = null;
        }
        if (!_viewModel.Settings.LocalApiEnabled) return;
        var modes = new ModeRepository(_database);
        var backend = new ApplicationLocalApiBackend(
            modes,
            new HistoryRepository(_database, _platformServices.Paths),
            _workflow,
            new LinuxLocalApiCapabilityCatalog(_modelManager, _platformServices.AudioTranscriber),
            _platformServices.PrivateFiles,
            _platformServices.Paths,
            "1.0.0",
            new LinuxLocalApiPostProcessor(_postProcessor, modes));
        _localApiHost = new PortableLocalApiHost(
            _platformServices.PrivateFiles, _platformServices.Paths, backend, "1.0.0",
            _viewModel.Settings.LocalApiPort);
        var state = await _localApiHost.StartAsync();
        if (!state.IsRunning)
            _viewModel.Settings.Status.Failure(state.Failure?.Code ?? "local_api.start_failed", state.Failure?.Message ?? "Local API could not start.");
    }

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is ListBoxItem { Tag: string pageId })
            _viewModel.Navigate(pageId);
    }

    private async void OnBrowseAudioFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a WAV recording",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] },
                    FilePickerFileTypes.All,
                ],
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null && _viewModel.Recording is not null)
                _viewModel.Recording.FilePath = path;
        }
        catch (Exception)
        {
            _viewModel.Recording?.ReportInputFailure(
                "workflow.file_picker_failed",
                "The desktop file picker could not be opened.");
        }
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
            if (_viewModel.Recording is null || string.IsNullOrWhiteSpace(_viewModel.Recording.Message)) return 8;

            await SeedSmokeDataAsync();
            var expectedControls = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["home"] = "HomeStartRecordingButton",
                ["modes"] = "ModeSaveButton",
                ["history"] = "HistoryDeleteButton",
                ["vocabulary"] = "VocabularyAddButton",
                ["models"] = "ModelDownloadButton",
                ["backup"] = "BackupExportButton",
                ["credentials"] = "CredentialSaveButton",
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

            _viewModel.Navigate("modes");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("ModeLocalPostProcessingEnabled")
                || !HasVisibleControl("ModeLocalPostProcessingModel")
                || !HasVisibleControl("ModeUserSystemPrompt")
                || !HasVisibleControl("ModeProviderType")
                || !HasVisibleControl("ModeLocalEngine")
                || !HasVisibleControl("ModeTranscriptionModel")
                || !HasVisibleControl("ModeCloudProvider")
                || !HasVisibleControl("ModeCloudAccuracyTier")
                || !HasVisibleControl("ModeCloudDomain")
                || !HasVisibleControl("ModeGeminiPrompt")
                || !HasVisibleControl("ModeCustomVocabulary")
                || !HasVisibleControl("ModeEnableScreenOcr")) return 9;
            _viewModel.Navigate("history");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("HistorySearchInput") || !HasVisibleControl("HistoryPlayButton")
                || !HasVisibleControl("HistoryRetryButton") || !HasVisibleControl("HistoryDeleteAudio")) return 11;
            _viewModel.Navigate("vocabulary");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("VocabularyTransferPath") || !HasVisibleControl("VocabularyImportButton")
                || !HasVisibleControl("VocabularyExportButton")) return 12;
            _viewModel.Navigate("settings");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("SettingsLocalLlmBackend")
                || !HasVisibleControl("SettingsLocalLlmCpuFallback")
                || !HasVisibleControl("SettingsLocalApiEnabled")
                || !HasVisibleControl("SettingsLocalApiPort")) return 10;

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
