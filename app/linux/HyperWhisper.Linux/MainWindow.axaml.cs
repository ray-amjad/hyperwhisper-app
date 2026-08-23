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
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;

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
    private readonly LinuxInteractionRecordingSession _recordingSession;
    private readonly LinuxInteractionCoordinator _interaction;
    private PortableLocalApiHost? _localApiHost;
    private Task? _initialization;
    private readonly SemaphoreSlim _localApiSettingsGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _allowClose;

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
        var history = new HistoryRepository(_database, _platformServices.Paths);
        var contextCapture = new LinuxContextCaptureCoordinator(
            _platformServices.ApplicationContext, _platformServices.ScreenOcr);
        _recordingSession = new LinuxInteractionRecordingSession(
            _viewModel, _workflow, _platformServices, contextCapture, _postProcessor, history);
        _interaction = new LinuxInteractionCoordinator(
            _platformServices.GlobalShortcuts, _platformServices.PushToTalk,
            _platformServices.TextInjection, _recordingSession, new AvaloniaUiDispatcher());
        InitializeComponent();
        DataContext = _viewModel;
        PlatformStatusText.Text = $"Linux platform connected · {_platformServices.Paths.DataDirectory}";
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        _viewModel.Settings.LocalApiSettingsChanged += OnLocalApiSettingsChanged;
        _viewModel.Settings.DesktopSettingsChanged += OnDesktopSettingsChanged;
        _interaction.OperationFailed += OnInteractionFailed;
        _platformServices.Tray.ShowRequested += OnTrayShowRequested;
        _platformServices.Tray.HideRequested += OnTrayHideRequested;
        _platformServices.Tray.QuitRequested += OnTrayQuitRequested;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await EnsureInitializedAsync();
            await ApplyLocalApiSettingsAsync(_lifetime.Token);
            ApplyDesktopSettings();
            var tray = await _platformServices.Tray.StartAsync(_lifetime.Token);
            if (tray.IsFailure)
                PlatformStatusText.Text += $" · Tray unavailable; window fallback active ({tray.Error!.Message})";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            _viewModel.Status.Failure("app.desktop_start_failed", "Linux desktop integration could not be started.");
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        _lifetime.Cancel();
        if (!_recordingSession.IsActive && _localApiHost is null) return;
        e.Cancel = true;
        try
        {
            if (_recordingSession.IsActive) await _interaction.CancelRecordingAsync();
            await ShutdownLocalApiAsync();
        }
        catch { _viewModel.Status.Failure("interaction.close_cancel_failed", "The active recording could not be cancelled cleanly."); }
        finally
        {
            _allowClose = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closing -= OnClosing;
        Closed -= OnClosed;
        _lifetime.Cancel();
        _viewModel.Settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        _viewModel.Settings.DesktopSettingsChanged -= OnDesktopSettingsChanged;
        _interaction.OperationFailed -= OnInteractionFailed;
        _platformServices.Tray.ShowRequested -= OnTrayShowRequested;
        _platformServices.Tray.HideRequested -= OnTrayHideRequested;
        _platformServices.Tray.QuitRequested -= OnTrayQuitRequested;
        _interaction.Dispose();
        _viewModel.Dispose();
        _postProcessor.Dispose();
        _modelHttp.Dispose();
    }

    private Task EnsureInitializedAsync() => _initialization ??= _viewModel.InitializeAsync();

    private async void OnLocalApiSettingsChanged(object? sender, EventArgs e)
    {
        try { await ApplyLocalApiSettingsAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { _viewModel.Settings.Status.Failure("local_api.apply_failed", "Local API settings could not be applied."); }
    }

    private void OnDesktopSettingsChanged(object? sender, EventArgs e)
    {
        try { ApplyDesktopSettings(); }
        catch { _viewModel.Settings.Status.Failure("desktop_settings.apply_failed", "Desktop settings could not be applied."); }
    }

    private void ApplyDesktopSettings()
    {
        var settings = _viewModel.Settings;
        if (_platformServices.ApplicationContext is LinuxApplicationContextProvider contextProvider)
        {
            var capability = contextProvider.GetCapabilities();
            settings.DesktopContextStatus = capability.Backend.StartsWith("gnome-", StringComparison.Ordinal)
                ? $"Application context: {capability.Detail} The optional packaged GNOME companion improves title discovery; AT-SPI remains the local fallback."
                : $"Application context: {capability.Detail}";
        }
        var toggleModifiers = Enum.TryParse<ShortcutModifiers>(settings.ToggleShortcutModifiers, true, out var parsedModifiers)
            ? parsedModifiers : ShortcutModifiers.Control | ShortcutModifiers.Shift;
        var toggle = new GlobalShortcut(toggleModifiers,
            string.IsNullOrWhiteSpace(settings.ToggleShortcutKey) ? new ShortcutKeyCode("Space") : new ShortcutKeyCode(settings.ToggleShortcutKey.Trim()));
        var pttMode = Enum.TryParse<PushToTalkMode>(settings.PushToTalkMode, true, out var parsedMode)
            ? parsedMode : PushToTalkMode.Disabled;
        var modifier = Enum.TryParse<ModifierSide>(settings.PushToTalkModifier, true, out var parsedModifier)
            ? parsedModifier : ModifierSide.LeftAlt;
        var customModifiers = Enum.TryParse<ShortcutModifiers>(settings.PushToTalkShortcutModifiers, true, out var parsedCustomModifiers)
            ? parsedCustomModifiers : ShortcutModifiers.None;
        GlobalShortcut? custom = string.IsNullOrWhiteSpace(settings.PushToTalkShortcutKey) ? null
            : new GlobalShortcut(customModifiers, new ShortcutKeyCode(settings.PushToTalkShortcutKey.Trim()));
        var result = _interaction.ConfigureAndStart(new LinuxInteractionConfiguration(
            toggle,
            new PushToTalkConfiguration(pttMode, modifier, custom, settings.PushToTalkDoublePressLock),
            TimeSpan.FromSeconds(settings.ClipboardRestoreDelaySeconds)));
        if (result.IsFailure) PlatformStatusText.Text = $"Linux integration warning · {result.Error!.Message}";
        else PlatformStatusText.Text = $"Linux platform connected · {_platformServices.Paths.DataDirectory}";

        var autostart = settings.AutostartEnabled ? _platformServices.Autostart.Enable() : _platformServices.Autostart.Disable();
        if (autostart.IsFailure && settings.AutostartEnabled)
            _viewModel.Settings.Status.Failure(autostart.Error!.Code, autostart.Error.Message);
        _platformServices.MicrophoneKeepWarm.Configure(
            settings.KeepMicrophoneWarm, _viewModel.Recording?.SelectedAudioDevice?.Id);
    }

    private void OnInteractionFailed(object? sender, PlatformError error) =>
        _viewModel.Status.Failure(error.Code, error.Message);

    private void OnTrayShowRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        Show(); WindowState = WindowState.Normal; Activate();
    });
    private void OnTrayHideRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Hide);
    private void OnTrayQuitRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Close);

    private async Task ApplyLocalApiSettingsAsync(CancellationToken cancellationToken)
    {
        await _localApiSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_localApiHost is not null)
            {
                await _localApiHost.DisposeAsync().ConfigureAwait(false);
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
                new LinuxLocalApiPostProcessor(_postProcessor, modes),
                vocabulary: new VocabularyRepository(_database));
            _localApiHost = new PortableLocalApiHost(
                _platformServices.PrivateFiles, _platformServices.Paths, backend, "1.0.0",
                _viewModel.Settings.LocalApiPort);
            var state = await _localApiHost.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!state.IsRunning)
                _viewModel.Settings.Status.Failure(state.Failure?.Code ?? "local_api.start_failed", state.Failure?.Message ?? "Local API could not start.");
        }
        finally { _localApiSettingsGate.Release(); }
    }

    private async Task ShutdownLocalApiAsync()
    {
        await _localApiSettingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_localApiHost is not null)
            {
                await _localApiHost.DisposeAsync().ConfigureAwait(false);
                _localApiHost = null;
            }
        }
        finally { _localApiSettingsGate.Release(); }
    }

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is ListBoxItem { Tag: string pageId })
            _viewModel.Navigate(pageId);
    }

    private async void OnStartRecording(object? sender, RoutedEventArgs e) => await _interaction.StartRecordingAsync();
    private async void OnStopRecording(object? sender, RoutedEventArgs e) => await _interaction.StopRecordingAsync();
    private async void OnCancelRecording(object? sender, RoutedEventArgs e) => await _interaction.CancelRecordingAsync();

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
                || !HasVisibleControl("SettingsLocalApiPort")
                || !HasVisibleControl("SettingsToggleKey")
                || !HasVisibleControl("SettingsPushToTalkMode")
                || !HasVisibleControl("SettingsRestoreClipboard")
                || !HasVisibleControl("SettingsStreamingEnabled")
                || !HasVisibleControl("SettingsStreamingProvider")
                || !HasVisibleControl("SettingsAudioEnvironmentPolicy")
                || !HasVisibleControl("SettingsAutostart")
                || !HasVisibleControl("SettingsDesktopContextStatus")) return 10;

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
