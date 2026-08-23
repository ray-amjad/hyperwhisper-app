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
using HyperWhisper.CloudPostProcessing;
using HyperWhisper.Linux.Overlay;
using HyperWhisper.FileTranscription;
using HyperWhisper.TranscriptionRouting;
using HyperWhisper.CloudAccount;
using System.Diagnostics;
using HyperWhisper.Storage;

namespace HyperWhisper.Linux;

public partial class MainWindow : Window
{
    private readonly LinuxDesktopServices _platformServices;
    private readonly ApplicationDb _database;
    private readonly ApplicationShellViewModel _viewModel;
    private readonly LinuxLocalPostProcessor _postProcessor;
    private readonly LinuxPostProcessingRouter _postProcessingRouter;
    private readonly PortableSettingsService _settings;
    private readonly PortableModelManager _modelManager;
    private readonly HttpClient _modelHttp = new();
    private readonly PortableCloudAccountService _cloudAccount;
    private readonly PortableStorageLifecycleService _storageLifecycle;
    private readonly TranscriptStorageCoordinator _storageCoordinator;
    private readonly TranscriptionWorkflow _workflow;
    private readonly LinuxInteractionRecordingSession _recordingSession;
    private readonly LinuxInteractionCoordinator _interaction;
    private readonly LazyLinuxRecordingOverlayFeedback _overlay;
    private PortableLocalApiHost? _localApiHost;
    private Task? _initialization;
    private readonly SemaphoreSlim _localApiSettingsGate = new(1, 1);
    private readonly SemaphoreSlim _storageMaintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _storageTimer = new(TimeSpan.FromHours(1));
    private Task? _storageMaintenance;
    private TranscriptStorageCleanupResult? _lastStorageCleanup;
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
        _cloudAccount = new PortableCloudAccountService(_platformServices.CredentialStore);
        _storageLifecycle = new PortableStorageLifecycleService(
            _platformServices.Paths, _platformServices.PrivateFiles);
        _storageCoordinator = new TranscriptStorageCoordinator(_database, _storageLifecycle);
        _postProcessor = new LinuxLocalPostProcessor(
            _platformServices.Paths.ModelsDirectory, _settings, _database);
        _postProcessingRouter = new LinuxPostProcessingRouter(
            _postProcessor,
            new CloudPostProcessingService(new CredentialStorePostProcessingCredentialSource(
                _platformServices.CredentialStore, _platformServices.DeviceIdentity)),
            _settings,
            _database);
        _workflow = new TranscriptionWorkflow(
            _platformServices.AudioRecorder,
            _platformServices.AudioDevices,
            _platformServices.AudioTranscriber,
            new HistoryRepository(_database, _platformServices.Paths),
            _postProcessingRouter,
            _platformServices.TextInjection,
            audioRetention: new CompletedAudioRetention(
                () => _settings.Get("storage.keepAudioFiles", true), _storageLifecycle));
        _viewModel = new ApplicationShellViewModel(
            _database, _settings, _workflow, LinuxLocalPostProcessor.RuntimeStatus,
            _modelManager, _platformServices.AudioPlayback,
            new DurableAudioImportService(_platformServices.PrivateFiles, _platformServices.Paths),
            _platformServices.Paths, _platformServices.CredentialStore,
            _platformServices.AudioTranscriber.Capability.DisplayName,
            new PortableFileTranscriptionPreflight(
                new StreamingFileAudioMetadataSource(),
                new LinuxFileTranscriptionReadiness(_modelManager, engine => engine switch
                {
                    LocalTranscriptionEngine.Whisper => _platformServices.LocalWhisperCapability.IsAvailable,
                    LocalTranscriptionEngine.Parakeet => _platformServices.LocalParakeetCapability.IsAvailable,
                    _ => false,
                }),
                new CredentialStoreCloudCredentialSource(
                    _platformServices.CredentialStore, _platformServices.DeviceIdentity)),
            _cloudAccount,
            _platformServices.DeviceIdentity,
            Environment.MachineName,
            OpenAccountUri,
            _platformServices.TextInjection);
        var history = new HistoryRepository(_database, _platformServices.Paths);
        var contextCapture = new LinuxContextCaptureCoordinator(
            _platformServices.ApplicationContext, _platformServices.ScreenOcr);
        _overlay = new LazyLinuxRecordingOverlayFeedback(new AvaloniaLinuxOverlayDispatcher());
        _recordingSession = new LinuxInteractionRecordingSession(
            _viewModel, _workflow, _platformServices, contextCapture, _postProcessingRouter, history, _overlay);
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
        _viewModel.Settings.TelemetrySettingsChanged += OnTelemetrySettingsChanged;
        _viewModel.Settings.StorageSettingsChanged += OnStorageSettingsChanged;
        _interaction.OperationFailed += OnInteractionFailed;
        _interaction.ChangeModeRequested += OnChangeModeRequested;
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
            ApplyTelemetrySettings();
            ApplyDesktopSettings();
            await RunStorageMaintenanceAsync(_lifetime.Token);
            _storageMaintenance = RunStorageMaintenanceLoopAsync(_lifetime.Token);
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
        if (!_recordingSession.IsActive && _localApiHost is null
            && _storageMaintenance is not { IsCompleted: false }) return;
        e.Cancel = true;
        try
        {
            if (_recordingSession.IsActive) await _interaction.CancelRecordingAsync();
            await ShutdownLocalApiAsync();
            if (_storageMaintenance is not null) await _storageMaintenance;
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
        _viewModel.Settings.TelemetrySettingsChanged -= OnTelemetrySettingsChanged;
        _viewModel.Settings.StorageSettingsChanged -= OnStorageSettingsChanged;
        _interaction.OperationFailed -= OnInteractionFailed;
        _interaction.ChangeModeRequested -= OnChangeModeRequested;
        _platformServices.Tray.ShowRequested -= OnTrayShowRequested;
        _platformServices.Tray.HideRequested -= OnTrayHideRequested;
        _platformServices.Tray.QuitRequested -= OnTrayQuitRequested;
        _interaction.Dispose();
        _overlay.Dispose();
        _viewModel.Dispose();
        _postProcessor.Dispose();
        _postProcessingRouter.Dispose();
        _cloudAccount.Dispose();
        _modelHttp.Dispose();
        _storageTimer.Dispose();
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

    private void OnTelemetrySettingsChanged(object? sender, EventArgs e) => ApplyTelemetrySettings();

    private async void OnStorageSettingsChanged(object? sender, EventArgs e)
    {
        try { await RunStorageMaintenanceAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { SetStorageText("StorageStatusText", "Storage maintenance could not be completed."); }
    }

    private async Task RunStorageMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _storageTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RunStorageMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunStorageMaintenanceAsync(CancellationToken cancellationToken)
    {
        await _storageMaintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _viewModel.Settings;
            _lastStorageCleanup = await _storageCoordinator.CleanupAsync(
                new StorageRetentionPolicy(settings.KeepAudioFiles, settings.AutoDeleteEnabled, settings.AutoDeleteDaysOld),
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            var inventory = await _storageCoordinator.InventoryAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => UpdateStorageStatus(inventory, _lastStorageCleanup));
        }
        finally
        {
            _storageMaintenanceGate.Release();
        }
    }

    private void UpdateStorageStatus(RecordingInventoryResult inventory, TranscriptStorageCleanupResult? cleanup)
    {
        var size = inventory.TotalBytes < 1024 * 1024
            ? $"{inventory.TotalBytes / 1024d:0.0} KiB"
            : $"{inventory.TotalBytes / (1024d * 1024d):0.0} MiB";
        var cleanupText = cleanup is null || cleanup.Status == StorageLifecycleStatus.Disabled
            ? "No enabled cleanup has run."
            : $"Last cleanup: {cleanup.CompletedAtUtc.LocalDateTime:g}; {cleanup.TranscriptsDeleted} transcripts and {cleanup.AudioFilesDeleted} audio files removed.";
        SetStorageText("StoragePathText", _platformServices.Paths.RecordingsDirectory);
        SetStorageText("StorageStatusText", $"{inventory.FileCount} app-owned audio files · {size}. {cleanupText}");
    }

    private async void OnDeleteStoredAudioNow(object? sender, RoutedEventArgs e)
    {
        try { await RunStorageMaintenanceAsync(_lifetime.Token); }
        catch { SetStorageText("StorageStatusText", "Storage cleanup failed; external audio was not touched."); }
    }

    private async void OnRefreshStorage(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _storageMaintenanceGate.WaitAsync(_lifetime.Token);
            try
            {
                var inventory = await _storageCoordinator.InventoryAsync(_lifetime.Token);
                UpdateStorageStatus(inventory, _lastStorageCleanup);
            }
            finally
            {
                _storageMaintenanceGate.Release();
            }
        }
        catch { SetStorageText("StorageStatusText", "Storage inventory could not be read."); }
    }

    private void OnOpenRecordingsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_platformServices.Paths.RecordingsDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add(_platformServices.Paths.RecordingsDirectory);
            _ = Process.Start(start);
        }
        catch { SetStorageText("StorageStatusText", "The fixed XDG recordings folder could not be opened."); }
    }

    private void SetStorageText(string name, string text)
    {
        var control = this.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(candidate => candidate.Name == name);
        if (control is not null) control.Text = text;
    }

    private void ApplyTelemetrySettings()
    {
        if (_viewModel.Settings.EnableErrorLogging) _ = _platformServices.Telemetry.Initialize();
        else _platformServices.Telemetry.Shutdown();
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
        var toggle = ParseShortcut(settings.ToggleShortcutModifiers, settings.ToggleShortcutKey);
        var cancel = ParseShortcut(settings.CancelShortcutModifiers, settings.CancelShortcutKey);
        var changeMode = ParseShortcut(settings.ChangeModeShortcutModifiers, settings.ChangeModeShortcutKey);
        var streaming = ParseShortcut(settings.StreamingShortcutModifiers, settings.StreamingShortcutKey);
        var shortcutFailure = new[] { toggle, cancel, changeMode, streaming }.FirstOrDefault(item => item.IsFailure);
        if (shortcutFailure?.Error is { } shortcutError)
        {
            PlatformStatusText.Text = $"Linux integration warning · {shortcutError.Message}";
            settings.Status.Failure(shortcutError.Code, shortcutError.Message);
            return;
        }
        var pttMode = Enum.TryParse<PushToTalkMode>(settings.PushToTalkMode, true, out var parsedMode)
            ? parsedMode : PushToTalkMode.Disabled;
        var modifier = Enum.TryParse<ModifierSide>(settings.PushToTalkModifier, true, out var parsedModifier)
            ? parsedModifier : ModifierSide.LeftAlt;
        var customModifiers = Enum.TryParse<ShortcutModifiers>(settings.PushToTalkShortcutModifiers, true, out var parsedCustomModifiers)
            ? parsedCustomModifiers : ShortcutModifiers.None;
        GlobalShortcut? custom = string.IsNullOrWhiteSpace(settings.PushToTalkShortcutKey) ? null
            : new GlobalShortcut(customModifiers, new ShortcutKeyCode(settings.PushToTalkShortcutKey.Trim()));
        var result = _interaction.ConfigureAndStart(new LinuxInteractionConfiguration(
            toggle.Value,
            new PushToTalkConfiguration(pttMode, modifier, custom, settings.PushToTalkDoublePressLock),
            TimeSpan.FromSeconds(settings.ClipboardRestoreDelaySeconds),
            ChangeModeShortcut: changeMode.Value)
        {
            SessionCancelShortcut = cancel.Value,
            StreamingEnabled = settings.StreamingEnabled,
            StreamingShortcut = streaming.Value,
        });
        if (result.IsFailure) PlatformStatusText.Text = $"Linux integration warning · {result.Error!.Message}";
        else PlatformStatusText.Text = $"Linux platform connected · {_platformServices.Paths.DataDirectory}";

        var autostart = settings.AutostartEnabled ? _platformServices.Autostart.Enable() : _platformServices.Autostart.Disable();
        if (autostart.IsFailure && settings.AutostartEnabled)
            _viewModel.Settings.Status.Failure(autostart.Error!.Code, autostart.Error.Message);
        _platformServices.MicrophoneKeepWarm.Configure(
            settings.KeepMicrophoneWarm, _viewModel.Recording?.SelectedAudioDevice?.Id);
    }

    private static PlatformResult<GlobalShortcut?> ParseShortcut(string? modifiersText, string? keyText)
    {
        var normalizedModifiers = string.IsNullOrWhiteSpace(modifiersText) ? "None" : modifiersText.Trim();
        if (!Enum.TryParse<ShortcutModifiers>(normalizedModifiers, true, out var modifiers)
            || (modifiers & ~(ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Meta)) != 0)
            return PlatformResult<GlobalShortcut?>.Failure(
                "interaction.shortcut_modifiers_invalid",
                "Shortcut modifiers must use Control, Alt, Shift, or Meta separated by commas.");
        var key = keyText?.Trim() ?? string.Empty;
        if (modifiers == ShortcutModifiers.None && key.Length == 0)
            return PlatformResult<GlobalShortcut?>.Success(null);
        if (key.Length == 0 && modifiers is ShortcutModifiers.Control or ShortcutModifiers.Alt
            or ShortcutModifiers.Shift or ShortcutModifiers.Meta)
            return PlatformResult<GlobalShortcut?>.Failure(
                "interaction.shortcut_bare_modifier",
                "A modifier-only shortcut needs at least two modifiers.");
        return PlatformResult<GlobalShortcut?>.Success(key.Length == 0
            ? new GlobalShortcut(modifiers)
            : new GlobalShortcut(modifiers, new ShortcutKeyCode(key)));
    }

    private void OnInteractionFailed(object? sender, PlatformError error)
        => _viewModel.Status.Failure(error.Code, error.Message);

    private void OnChangeModeRequested(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) CycleMode();
        else Dispatcher.UIThread.Post(CycleMode);
    }

    private void CycleMode()
    {
        try
        {
            var next = LinuxModeCycler.Next(_viewModel.Modes.Items, _viewModel.Modes.Selected);
            if (next is null) return;
            _viewModel.Modes.Selected = next;
            _overlay.ModeChanged(LinuxOverlayModeLabel.Create(next.Name));
        }
        catch { /* Mode feedback and cycling must not interrupt recording. */ }
    }

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
                new LinuxLocalApiCapabilityCatalog(
                    _modelManager, _platformServices.AudioTranscriber, _platformServices.CredentialStore,
                    _platformServices.DeviceIdentity, _settings),
                _platformServices.PrivateFiles,
                _platformServices.Paths,
                "1.0.0",
                new LinuxLocalApiPostProcessor(_postProcessingRouter, modes),
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

    private void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.DataContext is not HistoryViewModel history) return;
        history.UpdateSelection(list.SelectedItems?.Cast<Transcript>() ?? []);
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
                Title = "Choose an audio recording",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Supported audio")
                    {
                        Patterns = ["*.wav", "*.mp3", "*.m4a", "*.flac", "*.ogg", "*.webm"],
                        MimeTypes = ["audio/wav", "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/flac", "audio/ogg", "audio/webm"]
                    },
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
            if (_viewModel.Status.HasError)
            {
                Console.Error.WriteLine($"Smoke startup failed: {_viewModel.Status.ErrorCode} · "
                    + $"settings={_viewModel.Settings.Status.ErrorCode}, home={_viewModel.Home.Status.ErrorCode}, "
                    + $"history={_viewModel.History.Status.ErrorCode}, vocabulary={_viewModel.Vocabulary.Status.ErrorCode}, "
                    + $"modes={_viewModel.Modes.Status.ErrorCode}");
                return 5;
            }
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
                ["account"] = "AccountActivateButton",
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
            if (!HasVisibleControl("ModePostProcessingMode")
                || !HasVisibleControl("ModePostProcessingProvider")
                || !HasVisibleControl("ModePostProcessingModel")
                || !HasVisibleControl("ModeHyperWhisperCloudModel")
                || !HasVisibleControl("ModeLocalPostProcessingModel")
                || !HasVisibleControl("ModeCustomEndpointUrl")
                || !HasVisibleControl("ModeCustomInstructions")
                || !HasVisibleControl("ModePunctuation")
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
                || !HasVisibleControl("HistoryPlaybackSlider") || !HasVisibleControl("HistoryCopyButton")
                || !HasVisibleControl("HistoryRetryMode") || !HasVisibleControl("HistoryRetryButton")
                || !HasVisibleControl("HistoryDeleteAudio")) return 11;
            _viewModel.Navigate("vocabulary");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("VocabularyTransferPath") || !HasVisibleControl("VocabularyImportButton")
                || !HasVisibleControl("VocabularyExportButton")) return 12;
            _viewModel.Navigate("settings");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("SettingsLocalLlmBackend")
                || !HasVisibleControl("SettingsLocalLlmCpuFallback")
                || !HasVisibleControl("SettingsLocalWhisperBackend")
                || !HasVisibleControl("SettingsLocalWhisperCpuFallback")
                || !HasVisibleControl("SettingsLocalWhisperRuntimeStatus")
                || !HasVisibleControl("SettingsLocalApiEnabled")
                || !HasVisibleControl("SettingsLocalApiPort")
                || !HasVisibleControl("SettingsToggleKey")
                || !HasVisibleControl("SettingsPushToTalkMode")
                || !HasVisibleControl("SettingsPasteResultText")
                || !HasVisibleControl("SettingsRemoveFillerWords")
                || !HasVisibleControl("SettingsAutocapitalizeInsert")
                || !HasVisibleControl("SettingsRestoreClipboard")
                || !HasVisibleControl("SettingsStreamingEnabled")
                || !HasVisibleControl("SettingsStreamingProvider")
                || !HasVisibleControl("SettingsAudioEnvironmentPolicy")
                || !HasVisibleControl("SettingsKeepAudioFiles")
                || !HasVisibleControl("SettingsAutoDeleteEnabled")
                || !HasVisibleControl("SettingsAutoDeleteDays")
                || !HasVisibleControl("SettingsStorageDeleteNow")
                || !HasVisibleControl("SettingsStorageOpenFolder")
                || !HasVisibleControl("SettingsAutostart")
                || !HasVisibleControl("SettingsDesktopContextStatus")
                || !HasVisibleControl("SettingsEnableErrorLogging")) return 10;

            _viewModel.Navigate("account");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("AccountKeyInput")
                || !HasVisibleControl("AccountStatusRefreshButton")
                || !HasVisibleControl("AccountCreditsRefreshButton")
                || !HasVisibleControl("AccountPurchaseButton")
                || !HasVisibleControl("AccountManageButton")) return 16;

            if (_viewModel.History.Items.Count == 0
                || _viewModel.Vocabulary.Items.Count == 0
                || _viewModel.Modes.Items.Count == 0) return 6;
            _overlay.RecordingStarted(LinuxOverlayModeLabel.Create("Smoke Mode"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Recording, IsVisible: true }) return 13;
            _overlay.Transcribing();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Transcribing, IsVisible: true }) return 14;
            _overlay.Completed();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Hidden, IsVisible: false }) return 15;
            _viewModel.Settings.ResetShortcuts();
            _viewModel.Settings.Language = "en";
            _viewModel.Settings.Save();
            if (_viewModel.Settings.Status.HasError)
            {
                Console.Error.WriteLine($"Smoke settings save failed: {_viewModel.Settings.Status.ErrorCode}");
                return 7;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Smoke failed: {exception}");
            return 1;
        }
    }

    private bool HasVisibleControl(string name)
        => this.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name && control.IsVisible);

    private static PlatformResult OpenAccountUri(Uri uri)
    {
        if (uri != CloudAccountLinks.Purchase && uri != CloudAccountLinks.ManageAccount)
            return PlatformResult.Failure("account.link_rejected", "The account link was not recognized.");

        try
        {
            _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return PlatformResult.Success();
        }
        catch
        {
            return PlatformResult.Failure("account.link_failed", "The account page could not be opened in the browser.");
        }
    }

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
