using Avalonia.Controls;
using Avalonia.Styling;
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
using HyperWhisper.ModelReadiness;
using HyperWhisper.PortableApplication.ModelLibrary;
using HyperWhisper.Diagnostics;
using System.Reflection;
using HyperWhisper.Linux.Localization;
using HyperWhisper.Linux.Platform.Files;

namespace HyperWhisper.Linux;

public partial class MainWindow : Window
{
    private readonly AvaloniaLocalizationBridge _localization;
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
    private readonly PrivacySafeRotatingLogger _diagnosticLogger;
    private readonly LinuxLifecycleDiagnostics _lifecycleDiagnostics;
    private readonly TranscriptionWorkflow _workflow;
    private readonly LinuxInteractionRecordingSession _recordingSession;
    private readonly LinuxInteractionCoordinator _interaction;
    private readonly LinuxTrayActionHandler _trayActions;
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
    private bool _trayAvailable;
    private bool _localApiTokenRevealed;

    public MainWindow() : this(new LinuxDesktopServices())
    {
        Closed += (_, _) => _platformServices.Dispose();
    }

    internal MainWindow(LinuxDesktopServices platformServices)
    {
        _localization = (Avalonia.Application.Current as App)?.Localization
            ?? throw new InvalidOperationException("The application localization service is unavailable.");
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        _database = new ApplicationDb(_platformServices.Paths);
        _settings = new PortableSettingsService(_platformServices.PrivateFiles, _platformServices.Paths);
        _modelManager = new PortableModelManager(_platformServices.Paths, _modelHttp);
        _cloudAccount = new PortableCloudAccountService(_platformServices.CredentialStore);
        _storageLifecycle = new PortableStorageLifecycleService(
            _platformServices.Paths, _platformServices.PrivateFiles);
        _storageCoordinator = new TranscriptStorageCoordinator(_database, _storageLifecycle);
        var diagnosticDirectory = Path.Combine(_platformServices.Paths.LogsDirectory, "diagnostics");
        _diagnosticLogger = new PrivacySafeRotatingLogger(diagnosticDirectory);
        _lifecycleDiagnostics = new LinuxLifecycleDiagnostics(
            _diagnosticLogger, () => _settings.Get("general.enableErrorLogging", true));
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
                () => _settings.Get("storage.keepAudioFiles", true), _storageLifecycle,
                new FfmpegM4aAudioTransformer(
                    () => _settings.Get("storage.storeAsM4A", false))));
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
            _platformServices.TextInjection,
            ModelReadinessComposition.Create(
                _modelManager,
                _platformServices.CredentialStore,
            new LinuxMetadataOnlyHealthProbe()),
            CreateAboutViewModel(diagnosticDirectory),
            L);
        var history = new HistoryRepository(_database, _platformServices.Paths);
        var contextCapture = new LinuxContextCaptureCoordinator(
            _platformServices.ApplicationContext, _platformServices.ScreenOcr);
        _overlay = new LazyLinuxRecordingOverlayFeedback(
            new AvaloniaLinuxOverlayDispatcher(),
            () => _viewModel.Settings.ShowRecordingWindow,
            L,
            () => _ = StopFromOverlayAsync(),
            () => _ = ConfirmCancelFromOverlayAsync(),
            DismissCancelFromOverlay);
        _recordingSession = new LinuxInteractionRecordingSession(
            _viewModel, _workflow, _platformServices, contextCapture, _postProcessingRouter, history, _overlay,
            _lifecycleDiagnostics);
        _interaction = new LinuxInteractionCoordinator(
            _platformServices.GlobalShortcuts, _platformServices.PushToTalk,
            _platformServices.TextInjection, _recordingSession, new AvaloniaUiDispatcher());
        _trayActions = new LinuxTrayActionHandler(
            () => _recordingSession.IsActive,
            () => _viewModel.Recording?.IsImporting == true,
            _interaction.StartRecordingAsync,
            _interaction.StopRecordingAsync,
            TranscribeFileFromTrayAsync,
            SelectDefaultMicrophone,
            () => SelectAdjacentMicrophone(-1),
            () => SelectAdjacentMicrophone(1),
            CycleMode,
            () => NavigateFromTray("history"),
            () => NavigateFromTray("settings"),
            () => OpenTrayUri(TrayHelpUri),
            () => OpenTrayUri(TraySupportUri),
            () => OpenTrayUri(TrayFeedbackUri),
            ShowFromTray,
            HideFromTray,
            QuitFromTray,
            L);
        InitializeComponent();
        DataContext = _viewModel;
        PlatformStatusText.Text = LF("linux.platform.connected", _platformServices.Paths.DataDirectory);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        PropertyChanged += OnWindowPropertyChanged;
        _viewModel.Settings.LocalApiSettingsChanged += OnLocalApiSettingsChanged;
        _viewModel.Settings.DesktopSettingsChanged += OnDesktopSettingsChanged;
        _viewModel.Settings.TelemetrySettingsChanged += OnTelemetrySettingsChanged;
        _viewModel.Settings.StorageSettingsChanged += OnStorageSettingsChanged;
        _interaction.OperationFailed += OnInteractionFailed;
        _interaction.ChangeModeRequested += OnChangeModeRequested;
        _platformServices.Tray.ActionRequested += OnTrayActionRequested;
        _platformServices.Tray.Unavailable += OnTrayUnavailable;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await EnsureInitializedAsync();
            await WriteDiagnosticAsync(DiagnosticSeverity.Information, DiagnosticComponent.Application, DiagnosticOutcome.Started);
            await ApplyLocalApiSettingsAsync(_lifetime.Token);
            ApplyTelemetrySettings();
            ApplyDesktopSettings();
            await RunStorageMaintenanceAsync(_lifetime.Token);
            _storageMaintenance = RunStorageMaintenanceLoopAsync(_lifetime.Token);
            var tray = await _platformServices.Tray.StartAsync(_lifetime.Token);
            if (tray.IsFailure)
                PlatformStatusText.Text += LF("linux.platform.tray_unavailable", tray.Error!.Message);
            else
            {
                _trayAvailable = true;
                if (_viewModel.Settings.LaunchMinimized) Hide();
            }
            await WriteDiagnosticAsync(DiagnosticSeverity.Information, DiagnosticComponent.Application, DiagnosticOutcome.Succeeded);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            await WriteDiagnosticAsync(DiagnosticSeverity.Error, DiagnosticComponent.Application, DiagnosticOutcome.Failed);
            _viewModel.Status.Failure("app.desktop_start_failed", L("linux.error.desktop_start_failed"));
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_viewModel.Settings.MinimizeToTray && _trayAvailable)
        {
            e.Cancel = true;
            Hide();
            return;
        }
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
        catch { _viewModel.Status.Failure("interaction.close_cancel_failed", L("linux.error.close_cancel_failed")); }
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
        PropertyChanged -= OnWindowPropertyChanged;
        _lifetime.Cancel();
        _viewModel.Settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        _viewModel.Settings.DesktopSettingsChanged -= OnDesktopSettingsChanged;
        _viewModel.Settings.TelemetrySettingsChanged -= OnTelemetrySettingsChanged;
        _viewModel.Settings.StorageSettingsChanged -= OnStorageSettingsChanged;
        _interaction.OperationFailed -= OnInteractionFailed;
        _interaction.ChangeModeRequested -= OnChangeModeRequested;
        _platformServices.Tray.ActionRequested -= OnTrayActionRequested;
        _platformServices.Tray.Unavailable -= OnTrayUnavailable;
        _trayActions.Dispose();
        _interaction.Dispose();
        _overlay.Dispose();
        _viewModel.Dispose();
        _postProcessor.Dispose();
        _postProcessingRouter.Dispose();
        _cloudAccount.Dispose();
        _diagnosticLogger.Dispose();
        _modelHttp.Dispose();
        _storageTimer.Dispose();
    }

    private Task EnsureInitializedAsync() => _initialization ??= _viewModel.InitializeAsync();

    private AboutViewModel CreateAboutViewModel(string diagnosticDirectory)
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var packageVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? version;
        var capabilities = LinuxDiagnosticCapabilityProbe.Detect(_platformServices);
        return new AboutViewModel(
            version,
            packageVersion,
            new DiagnosticArchiveExporter(diagnosticDirectory),
            DiagnosticSystemInfo.Detect(version),
            capabilities);
    }

    private Task<DiagnosticWriteResult> WriteDiagnosticAsync(
        DiagnosticSeverity severity,
        DiagnosticComponent component,
        DiagnosticOutcome outcome)
        => !_viewModel.Settings.EnableErrorLogging
            ? Task.FromResult(DiagnosticWriteResult.Ok)
            : _diagnosticLogger.WriteAsync(new(DateTimeOffset.UtcNow, severity, component, outcome), _lifetime.Token);

    private async void OnChooseBackup(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("linux.picker.backup.open"),
            AllowMultiple = false,
            FileTypeFilter = [CreateUniversalBackupFileType()],
        });
        if (files.Count == 1) _viewModel.Backup.Path = files[0].Path.LocalPath;
    }

    private async void OnExportBackup(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("settings.backup.export.dialogTitle"),
            SuggestedFileName = $"hyperwhisper-{DateTime.UtcNow:yyyy-MM-dd}.hwbackup",
            DefaultExtension = "hwbackup",
            FileTypeChoices = [CreateUniversalBackupFileType()],
            ShowOverwritePrompt = true,
        });
        if (file is null) return;
        _viewModel.Backup.Path = file.Path.LocalPath;
        await _viewModel.Backup.ExportAsync(_lifetime.Token);
    }

    private void OnModelCredentialAction(object? sender, RoutedEventArgs e)
    {
        var action = _viewModel.Models?.Selected?.CredentialNavigationActionId;
        if (action == "navigate.account")
        {
            NavigateFromModel("account");
            return;
        }
        const string prefix = "navigate.credentials:";
        if (action?.StartsWith(prefix, StringComparison.Ordinal) != true) return;
        _viewModel.Credentials?.SelectAccount(action[prefix.Length..]);
        NavigateFromModel("credentials");
    }

    private void OnModelAccountAction(object? sender, RoutedEventArgs e) => NavigateFromModel("account");

    private void NavigateFromModel(string page)
    {
        try
        {
            _viewModel.Navigate(page);
            foreach (var item in Navigation.Items.OfType<ListBoxItem>())
                if (string.Equals(item.Tag?.ToString(), page, StringComparison.Ordinal))
                { Navigation.SelectedItem = item; break; }
        }
        catch (ArgumentException) { _viewModel.Status.Failure("models.navigation_unavailable", L("linux.error.navigation_unavailable")); }
    }

    private async void OnExportDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.About is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("settings.about.exportDiagnostics.dialogTitle"),
            SuggestedFileName = $"hyperwhisper-diagnostics-{DateTime.UtcNow:yyyy-MM-dd}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType(L("linux.picker.zip_archive")) { Patterns = ["*.zip"], MimeTypes = ["application/zip"] }],
            ShowOverwritePrompt = true,
        });
        if (file is not null) await _viewModel.About.ExportDiagnosticsAsync(file.Path.LocalPath, _lifetime.Token);
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e) => OpenFixedLocation(_platformServices.Paths.LogsDirectory);
    private void OnOpenDocumentation(object? sender, RoutedEventArgs e) => OpenSafeUri(new Uri("https://hyperwhisper.com/docs"));
    private void OnOpenSupport(object? sender, RoutedEventArgs e) => OpenSafeUri(new Uri("https://hyperwhisper.com/support"));

    private void OpenFixedLocation(string path)
    {
        try
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            _ = Process.Start(start);
        }
        catch { _viewModel.About?.Status.Failure("about.open_logs_failed", L("linux.error.open_logs_failed")); }
    }

    private void OpenSafeUri(Uri uri)
    {
        var result = OpenAccountUri(uri);
        if (result.IsFailure) _viewModel.About?.Status.Failure(result.Error!.Code, result.Error.Message);
    }

    private async void OnLocalApiSettingsChanged(object? sender, EventArgs e)
    {
        try { await ApplyLocalApiSettingsAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { _viewModel.Settings.Status.Failure("local_api.apply_failed", L("linux.error.local_api_apply_failed")); }
    }

    private void OnDesktopSettingsChanged(object? sender, EventArgs e)
    {
        try { ApplyDesktopSettings(); }
        catch { _viewModel.Settings.Status.Failure("desktop_settings.apply_failed", L("linux.error.desktop_settings_apply_failed")); }
    }

    private void OnTelemetrySettingsChanged(object? sender, EventArgs e) => ApplyTelemetrySettings();

    private async void OnStorageSettingsChanged(object? sender, EventArgs e)
    {
        try { await RunStorageMaintenanceAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { SetStorageText("StorageStatusText", L("linux.storage.maintenance_failed")); }
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
            ? L("linux.storage.no_cleanup")
            : LF("linux.storage.last_cleanup", cleanup.CompletedAtUtc.LocalDateTime,
                cleanup.TranscriptsDeleted, cleanup.AudioFilesDeleted);
        SetStorageText("StoragePathText", _platformServices.Paths.RecordingsDirectory);
        SetStorageText("StorageStatusText", LF("linux.storage.inventory", inventory.FileCount, size, cleanupText));
    }

    private async void OnDeleteStoredAudioNow(object? sender, RoutedEventArgs e)
    {
        try { await RunStorageMaintenanceAsync(_lifetime.Token); }
        catch { SetStorageText("StorageStatusText", L("linux.storage.cleanup_failed")); }
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
        catch { SetStorageText("StorageStatusText", L("linux.storage.inventory_failed")); }
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
        catch { SetStorageText("StorageStatusText", L("linux.storage.open_failed")); }
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
        if (Avalonia.Application.Current is { } application)
            application.RequestedThemeVariant = settings.ThemeMode switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        _overlay.ApplyPreference();
        _platformServices.TextInjection.SetClipboardHistoryPrivacyPolicy(
            settings.HideFromClipboardHistory
                ? ClipboardHistoryPrivacyPolicy.BestEffort
                : ClipboardHistoryPrivacyPolicy.Disabled);
        settings.ClipboardHistoryPrivacyStatus = settings.HideFromClipboardHistory
            ? _platformServices.TextInjection.ClipboardHistoryPrivacyCapability == ClipboardHistoryPrivacyCapability.BestEffortAvailable
                ? L("linux.clipboard.privacy_enabled")
                : L("linux.clipboard.privacy_unavailable")
            : L("linux.clipboard.privacy_disabled");
        if (_platformServices.ApplicationContext is LinuxApplicationContextProvider contextProvider)
        {
            var capability = contextProvider.GetCapabilities();
            settings.DesktopContextStatus = capability.Backend.StartsWith("gnome-", StringComparison.Ordinal)
                ? LF("linux.desktop.context_gnome", capability.Detail)
                : LF("linux.desktop.context", capability.Detail);
        }
        var toggle = ParseShortcut(settings.ToggleShortcutModifiers, settings.ToggleShortcutKey);
        var cancel = ParseShortcut(settings.CancelShortcutModifiers, settings.CancelShortcutKey);
        var changeMode = ParseShortcut(settings.ChangeModeShortcutModifiers, settings.ChangeModeShortcutKey);
        var streaming = ParseShortcut(settings.StreamingShortcutModifiers, settings.StreamingShortcutKey);
        var shortcutFailure = new[] { toggle, cancel, changeMode, streaming }.FirstOrDefault(item => item.IsFailure);
        if (shortcutFailure?.Error is { } shortcutError)
        {
            PlatformStatusText.Text = LF("linux.platform.warning", shortcutError.Message);
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
        if (result.IsFailure) PlatformStatusText.Text = LF("linux.platform.warning", result.Error!.Message);
        else PlatformStatusText.Text = LF("linux.platform.connected", _platformServices.Paths.DataDirectory);

        var autostart = settings.AutostartEnabled ? _platformServices.Autostart.Enable() : _platformServices.Autostart.Disable();
        if (autostart.IsFailure && settings.AutostartEnabled)
            _viewModel.Settings.Status.Failure(autostart.Error!.Code, autostart.Error.Message);
        _platformServices.MicrophoneKeepWarm.Configure(
            settings.KeepMicrophoneWarm, _viewModel.Recording?.SelectedAudioDevice?.Id);
    }

    private PlatformResult<GlobalShortcut?> ParseShortcut(string? modifiersText, string? keyText)
    {
        var normalizedModifiers = string.IsNullOrWhiteSpace(modifiersText) ? "None" : modifiersText.Trim();
        if (!Enum.TryParse<ShortcutModifiers>(normalizedModifiers, true, out var modifiers)
            || (modifiers & ~(ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Meta)) != 0)
            return PlatformResult<GlobalShortcut?>.Failure(
                "interaction.shortcut_modifiers_invalid",
                L("linux.error.shortcut_modifiers_invalid"));
        var key = keyText?.Trim() ?? string.Empty;
        if (modifiers == ShortcutModifiers.None && key.Length == 0)
            return PlatformResult<GlobalShortcut?>.Success(null);
        if (key.Length == 0 && modifiers is ShortcutModifiers.Control or ShortcutModifiers.Alt
            or ShortcutModifiers.Shift or ShortcutModifiers.Meta)
            return PlatformResult<GlobalShortcut?>.Failure(
                "interaction.shortcut_bare_modifier",
                L("linux.error.shortcut_bare_modifier"));
        return PlatformResult<GlobalShortcut?>.Success(key.Length == 0
            ? new GlobalShortcut(modifiers)
            : new GlobalShortcut(modifiers, new ShortcutKeyCode(key)));
    }

    private Task StopFromOverlayAsync() => _interaction.StopRecordingAsync();

    private Task ConfirmCancelFromOverlayAsync() => _interaction.ConfirmCancelRecordingAsync();

    private void DismissCancelFromOverlay() => _interaction.DismissCancelConfirmation();

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

    private void OnTrayActionRequested(object? sender, StatusNotifierActionEventArgs e)
        => Dispatcher.UIThread.Post(async () =>
        {
            var result = await _trayActions.HandleAsync(e.Action, _lifetime.Token);
            if (result.IsFailure && result.Error?.Code is not "tray.busy" and not "tray.unsafe_state"
                and not "tray.cancelled" and not "tray.disposed")
                _viewModel.Status.Failure(result.Error!.Code, result.Error.Message);
        });

    private void ShowFromTray()
    {
        Show(); WindowState = WindowState.Normal; Activate();
    }

    private void HideFromTray()
    {
        if (_trayAvailable) Hide();
    }

    private void QuitFromTray()
    {
        _trayAvailable = false;
        Close();
    }

    private void NavigateFromTray(string page)
    {
        ShowFromTray();
        _viewModel.Navigate(page);
        foreach (var item in Navigation.Items.OfType<ListBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), page, StringComparison.Ordinal)) continue;
            Navigation.SelectedItem = item;
            break;
        }
    }

    private void SelectDefaultMicrophone()
    {
        var recording = _viewModel.Recording;
        if (recording is null) return;
        recording.RefreshDevices();
        recording.SelectedAudioDevice = LinuxTrayMicrophoneSelector.SelectDefault(recording.AudioDevices);
    }

    private void SelectAdjacentMicrophone(int direction)
    {
        var recording = _viewModel.Recording;
        if (recording is null) return;
        recording.RefreshDevices();
        recording.SelectedAudioDevice = LinuxTrayMicrophoneSelector.SelectAdjacent(
            recording.AudioDevices, recording.SelectedAudioDevice?.Id, direction);
    }

    private static readonly Uri TrayHelpUri = new("https://hyperwhisper.com/docs");
    private static readonly Uri TraySupportUri = new("https://hyperwhisper.com/support");
    private static readonly Uri TrayFeedbackUri = new("https://hyperwhisper.userjot.com");

    private void OpenTrayUri(Uri uri)
    {
        if (uri != TrayHelpUri && uri != TraySupportUri && uri != TrayFeedbackUri) return;
        try { _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { _viewModel.Status.Failure("tray.link_failed", L("linux.error.tray_link_failed")); }
    }
    private void OnTrayUnavailable(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _trayAvailable = false;
        if (!IsVisible)
        {
            Show();
            WindowState = WindowState.Normal;
        }
        PlatformStatusText.Text += L("linux.platform.tray_disconnected");
    });

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized
            && _trayAvailable && _viewModel.Settings.MinimizeToTray)
            Dispatcher.UIThread.Post(Hide);
    }

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
            if (!_viewModel.Settings.LocalApiEnabled)
            {
                await Dispatcher.UIThread.InvokeAsync(UpdateLocalApiConnectionUi);
                return;
            }
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
                _viewModel.Settings.Status.Failure(state.Failure?.Code ?? "local_api.start_failed",
                    state.Failure?.Message ?? L("linux.error.local_api_start_failed"));
            await Dispatcher.UIThread.InvokeAsync(UpdateLocalApiConnectionUi);
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

    private async void OnChooseRecordingsDirectory(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _localization.GetRequired("settings.storage.recordings.title"),
                AllowMultiple = false,
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            var validated = LinuxRecordingDirectoryValidator.ValidateAndPrepare(path);
            if (validated.IsFailure)
            {
                _viewModel.Settings.Status.Failure(validated.Error!.Code, validated.Error.Message);
                return;
            }
            _viewModel.Settings.RecordingsDirectory = validated.Value!;
            _viewModel.Settings.Status.Success(L("linux.storage.restart_required"));
        }
        catch { _viewModel.Settings.Status.Failure("storage.folder_picker_failed", L("linux.error.file_picker_failed")); }
    }

    private void OnRevealLocalApiToken(object? sender, RoutedEventArgs e)
    {
        _localApiTokenRevealed = !_localApiTokenRevealed;
        UpdateLocalApiConnectionUi();
    }

    private async void OnCopyLocalApiToken(object? sender, RoutedEventArgs e) =>
        await CopyLocalApiTextAsync(TryReadLocalApiToken());

    private async void OnCopyLocalApiPort(object? sender, RoutedEventArgs e) =>
        await CopyLocalApiTextAsync(_localApiHost?.State.Port > 0
            ? _localApiHost.State.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null);

    private async void OnCopyLocalApiMcpSnippet(object? sender, RoutedEventArgs e) =>
        await CopyLocalApiTextAsync(LocalApiMcpSnippet);

    private async void OnCopyLocalApiCurlSnippet(object? sender, RoutedEventArgs e) =>
        await CopyLocalApiTextAsync(LocalApiCurlSnippet);

    private async void OnRegenerateLocalApiToken(object? sender, RoutedEventArgs e)
    {
        if (_localApiHost is null) return;
        var state = await _localApiHost.RegenerateBearerTokenAsync(_lifetime.Token);
        _localApiTokenRevealed = false;
        if (state.Failure is not null)
            _viewModel.Settings.Status.Failure(state.Failure.Code, state.Failure.Message);
        UpdateLocalApiConnectionUi();
    }

    private async Task CopyLocalApiTextAsync(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var copied = await _platformServices.TextInjection.CopyToClipboardAsync(value, _lifetime.Token);
        if (copied.IsFailure) _viewModel.Settings.Status.Failure(copied.Error!.Code, copied.Error.Message);
    }

    private string? TryReadLocalApiToken()
    {
        try { return _localApiHost?.RevealBearerToken(); }
        catch { return null; }
    }

    private void UpdateLocalApiConnectionUi()
    {
        var state = _localApiHost?.State ?? LocalApiHostState.Stopped;
        SetNamedText("SettingsLocalApiStatus", state.IsRunning
            ? $"127.0.0.1:{state.Port}"
            : state.Failure?.Message ?? _localization.GetRequired("settings.localApi.status.idle"));
        SetNamedText("SettingsLocalApiBoundPort", state.Port > 0
            ? state.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—");
        SetNamedText("SettingsLocalApiDiscoveryPath", _localApiHost?.DiscoveryPath ??
            Path.Combine(_platformServices.Paths.DataDirectory, "local-api.json"));
        var token = TryReadLocalApiToken();
        SetNamedText("SettingsLocalApiToken", token is null ? "—" : _localApiTokenRevealed
            ? token : new string('•', Math.Max(0, token.Length - 4)) + token[^Math.Min(4, token.Length)..]);
        SetNamedText("SettingsLocalApiMcpSnippet", LocalApiMcpSnippet);
        SetNamedText("SettingsLocalApiCurlSnippet", LocalApiCurlSnippet);
    }

    private void SetNamedText(string name, string text)
    {
        var descendant = this.GetLogicalDescendants().OfType<Control>()
            .FirstOrDefault(candidate => candidate.Name == name);
        if (descendant is TextBlock block) block.Text = text;
        else if (descendant is TextBox box) box.Text = text;
    }

    private const string LocalApiMcpSnippet = "{\n  \"mcpServers\": {\n    \"hyperwhisper\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@hyperwhisper/mcp\"]\n    }\n  }\n}";
    private const string LocalApiCurlSnippet = "DISCOVERY=\"${XDG_DATA_HOME:-$HOME/.local/share}/hyperwhisper/local-api.json\"\nPORT=\"$(jq -r .port \"$DISCOVERY\")\"\nTOKEN=\"$(jq -r .token \"$DISCOVERY\")\"\ncurl \"http://127.0.0.1:$PORT/health\"\ncurl -H \"Authorization: Bearer $TOKEN\" \"http://127.0.0.1:$PORT/models\"";

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is ListBoxItem { Tag: string pageId })
        {
            _viewModel.Navigate(pageId);
            if (pageId == "settings") Dispatcher.UIThread.Post(UpdateLocalApiConnectionUi);
        }
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
        await ChooseAudioFileAsync();
    }

    private async Task TranscribeFileFromTrayAsync(CancellationToken cancellationToken)
    {
        ShowFromTray();
        if (!await ChooseAudioFileAsync()) return;
        if (_viewModel.Recording is { CanTranscribeFile: true } recording)
            await recording.TranscribeFileAsync(cancellationToken);
    }

    private async Task<bool> ChooseAudioFileAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("file.transcribe.dialogTitle"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(L("linux.picker.supported_audio"))
                    {
                        Patterns = ["*.wav", "*.mp3", "*.m4a", "*.flac", "*.ogg", "*.webm"],
                        MimeTypes = ["audio/wav", "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/flac", "audio/ogg", "audio/webm"]
                    },
                    FilePickerFileTypes.All,
                ],
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null && _viewModel.Recording is not null)
            {
                _viewModel.Recording.FilePath = path;
                return true;
            }
        }
        catch (Exception)
        {
            _viewModel.Recording?.ReportInputFailure(
                "workflow.file_picker_failed",
                L("linux.error.file_picker_failed"));
        }
        return false;
    }

    internal async Task<int> RunSmokeTestAsync()
    {
        try
        {
            await EnsureInitializedAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (Bounds.Width <= 0 || Bounds.Height <= 0 || !IsVisible) return 2;
            var expectedCulture = AvaloniaLocalizationBridge.ResolveStartupCulture(
                Environment.GetEnvironmentVariable("HYPERWHISPER_UI_CULTURE"));
            if (!string.Equals(_localization.Culture.Name, expectedCulture.Name,
                    StringComparison.OrdinalIgnoreCase)) return 20;
            if (FlowDirection != _localization.FlowDirection) return 21;
            if (Navigation.Items.OfType<ListBoxItem>().FirstOrDefault()?.Content?.ToString()
                != _localization.GetRequired("sidebar.home")) return 22;
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
                || !HasVisibleControl("SettingsLocalApiStatus")
                || !HasVisibleControl("SettingsLocalApiToken")
                || !HasVisibleControl("SettingsLocalApiDiscoveryPath")
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
                || !HasVisibleControl("SettingsStoreAsM4A")
                || !HasVisibleControl("SettingsRecordingsDirectory")
                || !HasVisibleControl("SettingsChooseRecordingsDirectory")
                || !HasVisibleControl("SettingsAutoDeleteEnabled")
                || !HasVisibleControl("SettingsAutoDeleteDays")
                || !HasVisibleControl("SettingsStorageDeleteNow")
                || !HasVisibleControl("SettingsStorageOpenFolder")
                || !HasVisibleControl("SettingsAutostart")
                || !HasVisibleControl("SettingsDesktopContextStatus")
                || !HasVisibleControl("SettingsEnableErrorLogging")
                || !HasVisibleControl("SettingsShareSpeedData")) return 10;

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
            _overlay.Completed(LinuxRecordingOverlayCompletion.Pasted);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Pasted, IsVisible: true }) return 15;
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

    private PlatformResult OpenAccountUri(Uri uri)
    {
        if (uri != CloudAccountLinks.Purchase && uri != CloudAccountLinks.ManageAccount)
            return PlatformResult.Failure("account.link_rejected", L("linux.error.account_link_rejected"));

        try
        {
            _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return PlatformResult.Success();
        }
        catch
        {
            return PlatformResult.Failure("account.link_failed", L("linux.error.account_link_failed"));
        }
    }

    private FilePickerFileType CreateUniversalBackupFileType() => new(L("linux.picker.universal_backup"))
    {
        Patterns = ["*.hwbackup", "*.json"],
        MimeTypes = ["application/json"],
    };

    private string L(string key) => _localization.GetRequired(key);
    private string LF(string key, params object?[] arguments) => _localization.Format(key, arguments);

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

internal sealed class LinuxTrayActionHandler : IDisposable
{
    private readonly Func<bool> _isRecording;
    private readonly Func<bool> _isImporting;
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<CancellationToken, Task> _stop;
    private readonly Func<CancellationToken, Task> _transcribeFile;
    private readonly IReadOnlyDictionary<StatusNotifierAction, Action> _immediateActions;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Func<string, string> _text;
    private bool _disposed;

    internal LinuxTrayActionHandler(
        Func<bool> isRecording,
        Func<bool> isImporting,
        Func<CancellationToken, Task> start,
        Func<CancellationToken, Task> stop,
        Func<CancellationToken, Task> transcribeFile,
        Action selectDefaultMicrophone,
        Action selectPreviousMicrophone,
        Action selectNextMicrophone,
        Action cycleMode,
        Action openHistory,
        Action openSettings,
        Action openHelp,
        Action openSupport,
        Action sendFeedback,
        Action show,
        Action hide,
        Action quit,
        Func<string, string> text)
    {
        _isRecording = isRecording;
        _isImporting = isImporting;
        _start = start;
        _stop = stop;
        _transcribeFile = transcribeFile;
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _immediateActions = new Dictionary<StatusNotifierAction, Action>
        {
            [StatusNotifierAction.SelectDefaultMicrophone] = selectDefaultMicrophone,
            [StatusNotifierAction.SelectPreviousMicrophone] = selectPreviousMicrophone,
            [StatusNotifierAction.SelectNextMicrophone] = selectNextMicrophone,
            [StatusNotifierAction.CycleMode] = cycleMode,
            [StatusNotifierAction.OpenHistory] = openHistory,
            [StatusNotifierAction.OpenSettings] = openSettings,
            [StatusNotifierAction.OpenHelp] = openHelp,
            [StatusNotifierAction.OpenSupport] = openSupport,
            [StatusNotifierAction.SendFeedback] = sendFeedback,
            [StatusNotifierAction.Show] = show,
            [StatusNotifierAction.Hide] = hide,
            [StatusNotifierAction.Quit] = quit,
        };
    }

    internal async Task<PlatformResult> HandleAsync(
        StatusNotifierAction action,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return PlatformResult.Failure("tray.disposed", _text("linux.error.tray_disposed"));
        if (action is StatusNotifierAction.CycleMode or StatusNotifierAction.OpenHistory
            or StatusNotifierAction.OpenSettings or StatusNotifierAction.OpenHelp
            or StatusNotifierAction.OpenSupport or StatusNotifierAction.SendFeedback
            or StatusNotifierAction.Show or StatusNotifierAction.Hide or StatusNotifierAction.Quit)
        {
            try
            {
                _immediateActions[action]();
                return PlatformResult.Success();
            }
            catch
            {
                return PlatformResult.Failure("tray.action_failed", _text("linux.error.tray_action_failed"));
            }
        }
        try
        {
            if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(true))
                return PlatformResult.Failure("tray.busy", _text("linux.error.tray_busy"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult.Failure("tray.cancelled", _text("linux.error.tray_cancelled"));
        }
        try
        {
            switch (action)
            {
                case StatusNotifierAction.StartRecording:
                    if (_isRecording() || _isImporting()) return UnsafeState();
                    await _start(cancellationToken).ConfigureAwait(true);
                    break;
                case StatusNotifierAction.StopRecording:
                    if (!_isRecording() || _isImporting()) return UnsafeState();
                    await _stop(cancellationToken).ConfigureAwait(true);
                    break;
                case StatusNotifierAction.TranscribeFile:
                    if (_isRecording() || _isImporting()) return UnsafeState();
                    await _transcribeFile(cancellationToken).ConfigureAwait(true);
                    break;
                case StatusNotifierAction.SelectDefaultMicrophone:
                case StatusNotifierAction.SelectPreviousMicrophone:
                case StatusNotifierAction.SelectNextMicrophone:
                    if (_isRecording() || _isImporting()) return UnsafeState();
                    _immediateActions[action]();
                    break;
                default:
                    return PlatformResult.Failure("tray.action_unknown", _text("linux.error.tray_action_unknown"));
            }
            return PlatformResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult.Failure("tray.cancelled", _text("linux.error.tray_cancelled"));
        }
        catch
        {
            return PlatformResult.Failure("tray.action_failed", _text("linux.error.tray_action_failed"));
        }
        finally
        {
            _operation.Release();
        }
    }

    private PlatformResult UnsafeState() => PlatformResult.Failure(
        "tray.unsafe_state", _text("linux.error.tray_unsafe_state"));

    public void Dispose()
    {
        _disposed = true;
    }
}

internal static class LinuxTrayMicrophoneSelector
{
    internal static AudioInputDevice? SelectDefault(IEnumerable<AudioInputDevice> source)
    {
        var devices = source.OrderBy(device => device.Id, StringComparer.Ordinal).ToArray();
        return devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
    }

    internal static AudioInputDevice? SelectAdjacent(
        IEnumerable<AudioInputDevice> source,
        string? selectedId,
        int direction)
    {
        var devices = source.OrderBy(device => device.Id, StringComparer.Ordinal).ToArray();
        if (devices.Length == 0) return null;
        var selected = Array.FindIndex(devices,
            device => string.Equals(device.Id, selectedId, StringComparison.Ordinal));
        if (selected < 0) selected = Array.FindIndex(devices, device => device.IsDefault);
        if (selected < 0) selected = 0;
        var step = direction < 0 ? -1 : 1;
        return devices[(selected + step + devices.Length) % devices.Length];
    }
}
