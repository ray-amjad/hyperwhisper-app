using Avalonia;
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
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.PortableApplication.Audio;

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
    private readonly LinuxOnboardingModeReadiness _onboardingReadiness;
    private readonly HttpClient _modelHttp = new();
    private readonly PortableCloudAccountService _cloudAccount;
    private readonly PortableStorageLifecycleService _storageLifecycle;
    private readonly TranscriptStorageCoordinator _storageCoordinator;
    private readonly PrivacySafeRotatingLogger _diagnosticLogger;
    private readonly LinuxLifecycleDiagnostics _lifecycleDiagnostics;
    private readonly LinuxPackageUpdateProbe _packageUpdateProbe = new();
    private readonly bool _isFreshProfile;
    private LinuxOnboardingViewModel? _onboarding;
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
        var priorSettings = _platformServices.PrivateFiles.ReadAllText(
            Path.Combine(_platformServices.Paths.ConfigDirectory, "settings.json"));
        _isFreshProfile = priorSettings.IsSuccess && priorSettings.Value is null;
        _database = new ApplicationDb(_platformServices.Paths);
        _settings = new PortableSettingsService(_platformServices.PrivateFiles, _platformServices.Paths);
        _modelManager = new PortableModelManager(_platformServices.Paths, _modelHttp);
        _onboardingReadiness = new LinuxOnboardingModeReadiness(
            new SecureStoreProviderCredentialSource(_platformServices.CredentialStore),
            new PortableLocalModelReadinessSource(_modelManager));
        // The account key stays in the keyring; the cached verdict, its
        // timestamp and the remote trial-limit override go beside the other
        // state, where the hw-license core reads and writes them (issue #290).
        _cloudAccount = new PortableCloudAccountService(
            _platformServices.CredentialStore,
            PortableLicenseStateStore.For(
                _platformServices.CredentialStore,
                _platformServices.PrivateFiles,
                _platformServices.Paths));
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
                    () => _settings.Get("storage.storeAsM4A", false))),
            audioPreprocessor: _platformServices.AudioPreprocessor);
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
        GoTo("home");
        UpdateShortcutHint();
        ShowPlatformStatus(LF("linux.platform.connected", _platformServices.Paths.DataDirectory), false);
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
        if (_viewModel.Recording is not null) _viewModel.Recording.TranscriptionSaved += OnOnboardingTranscriptionSaved;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await EnsureInitializedAsync();
            await new CrashAudioRecoveryService(
                _platformServices.Paths,
                new HistoryRepository(_database, _platformServices.Paths),
                () => _platformServices.AudioRecorder.IsRecording ? "active" : null)
                .RecoverAsync(_lifetime.Token);
            await _viewModel.History.RefreshAsync(_lifetime.Token);
            await InitializeOnboardingAsync();
            await WriteDiagnosticAsync(DiagnosticSeverity.Information, DiagnosticComponent.Application, DiagnosticOutcome.Started);
            await ApplyLocalApiSettingsAsync(_lifetime.Token);
            ApplyTelemetrySettings();
            ApplyDesktopSettings();
            await RunStorageMaintenanceAsync(_lifetime.Token);
            _storageMaintenance = RunStorageMaintenanceLoopAsync(_lifetime.Token);
            var tray = await _platformServices.Tray.StartAsync(_lifetime.Token);
            if (tray.IsFailure)
                ShowPlatformStatus(PlatformStatusText.Text + LF("linux.platform.tray_unavailable", tray.Error!.Message), true);
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
        if (_viewModel.Recording is not null) _viewModel.Recording.TranscriptionSaved -= OnOnboardingTranscriptionSaved;
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
        // The informational version carries the source revision after a '+'. The About card shows
        // the number a person would quote in a support mail, so keep only what precedes it.
        var informational = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var plus = informational.IndexOf('+');
        var version = plus > 0 ? informational[..plus] : informational;
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
        if (action?.StartsWith(prefix, StringComparison.Ordinal) == true)
            _viewModel.Credentials?.SelectAccount(action[prefix.Length..]);
        NavigateFromModel("credentials");
    }

    private void OnModelAccountAction(object? sender, RoutedEventArgs e) => NavigateFromModel("account");

    // A table row carries its own action button. Every command on the view model works on the
    // selected model, so a row action selects its row first and then runs the command.
    private ManagedModelViewModel? SelectModelRow(object? sender)
    {
        if (_viewModel.Models is null
            || sender is not Control { DataContext: ManagedModelViewModel row }) return null;
        _viewModel.Models.Selected = row;
        return row;
    }

    private void OnModelRowDownload(object? sender, RoutedEventArgs e)
    {
        if (SelectModelRow(sender) is null || _viewModel.Models is null) return;
        if (_viewModel.Models.DownloadCommand.CanExecute(null)) _viewModel.Models.DownloadCommand.Execute(null);
    }

    private void OnModelRowDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectModelRow(sender) is null || _viewModel.Models is null) return;
        if (_viewModel.Models.DeleteCommand.CanExecute(null)) _viewModel.Models.DeleteCommand.Execute(null);
    }

    private void OnModelRowCredential(object? sender, RoutedEventArgs e)
    {
        if (SelectModelRow(sender) is null) return;
        OnModelCredentialAction(sender, e);
    }

    private void NavigateFromModel(string page)
    {
        try
        {
            GoTo(page);
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

    private async void OnRefreshPackageStatus(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Button>("AboutUpdateRefreshButton") is { } refreshButton) refreshButton.IsEnabled = false;
        SetAboutUpdateStatus(L("linux.update.checking"));
        try
        {
            var status = await _packageUpdateProbe.CheckAsync(_lifetime.Token);
            SetAboutUpdateStatus(status.State switch
            {
                LinuxPackageUpdateState.Current when status.InstalledVersion is not null =>
                    LF("linux.update.current_version", status.InstalledVersion),
                LinuxPackageUpdateState.UpdateAvailable when status.InstalledVersion is not null && status.CandidateVersion is not null =>
                    LF("linux.update.available_versions", status.InstalledVersion, status.CandidateVersion),
                LinuxPackageUpdateState.UpdateAvailable => L("linux.update.available"),
                LinuxPackageUpdateState.NotPackageManaged => L("linux.update.not_package_managed"),
                LinuxPackageUpdateState.Unavailable => L("linux.update.unavailable"),
                _ => L("linux.update.failed"),
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            if (this.FindControl<Button>("AboutUpdateRefreshButton") is { } completedButton) completedButton.IsEnabled = true;
        }
    }

    private void SetAboutUpdateStatus(string text)
    {
        if (this.FindControl<TextBlock>("AboutUpdateStatus") is { } status) status.Text = text;
    }

    private async Task InitializeOnboardingAsync()
    {
        if (Program.IsSmokeTest || !_isFreshProfile || _settings.Get("onboarding.completed", false)
                            || _settings.Get("onboarding.skipped", false))
        {
            // No view model is assigned on this path, so the overlay keeps whatever IsVisible
            // the binding could not supply. Close it here as well as in XAML, because a
            // visible overlay covers every page and blocks all input.
            OnboardingOverlay.IsVisible = false;
            return;
        }
        var audio = (_platformServices.AudioRecorder as PulseAudioRecorder)?.GetCapabilities();
        var injection = (_platformServices.TextInjection as LinuxTextInjectionService)?.GetCapabilities();
        var shortcuts = (_platformServices.GlobalShortcuts as LinuxGlobalShortcutService)?.GetCapabilities();
        var capture = (_platformServices.ScreenOcr as LinuxScreenOcrService)?.GetCapabilities();
        var selectedMode = _viewModel.Modes.Selected;
        var selectedModeAvailable = selectedMode is not null
            && IsOnboardingEngineAvailable(selectedMode)
            && await _onboardingReadiness.IsReadyAsync(selectedMode, _lifetime.Token);
        _onboarding = new LinuxOnboardingViewModel(
            new(
                audio?.Available == true,
                injection?.ClipboardAvailable == true,
                injection?.UInputAvailable == true,
                shortcuts?.Available == true,
                capture?.UsesDesktopPortal == true,
                _platformServices.LocalWhisperCapability.IsAvailable,
                _platformServices.LocalParakeetCapability.IsAvailable),
            _viewModel.Modes.Items,
            selectedMode,
            _viewModel.Recording?.AudioDevices ?? [],
            _viewModel.Recording?.SelectedAudioDevice,
            selectedModeAvailable,
            PersistOnboardingDecision,
            mode => _viewModel.Modes.Selected = mode,
            device => { if (_viewModel.Recording is not null) _viewModel.Recording.SelectedAudioDevice = device; },
            L);
        OnboardingOverlay.DataContext = _onboarding;
        _onboarding.Show();
    }

    private bool IsOnboardingEngineAvailable(Mode mode) =>
        !string.Equals(mode.ProviderType, "local", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
            ? _platformServices.LocalParakeetCapability.IsAvailable
            : _platformServices.LocalWhisperCapability.IsAvailable);

    private async void OnOnboardingModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var onboarding = _onboarding;
        var mode = onboarding?.SelectedMode;
        if (onboarding is null || mode is null) return;
        try
        {
            var available = IsOnboardingEngineAvailable(mode)
                && await _onboardingReadiness.IsReadyAsync(mode, _lifetime.Token);
            if (ReferenceEquals(onboarding, _onboarding) && ReferenceEquals(mode, onboarding.SelectedMode))
                onboarding.SetSelectedModeAvailable(available);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            if (ReferenceEquals(onboarding, _onboarding) && ReferenceEquals(mode, onboarding.SelectedMode))
                onboarding.SetSelectedModeAvailable(false);
        }
    }

    private bool PersistOnboardingDecision(bool skipped)
    {
        _settings.Set("onboarding.completed", !skipped);
        _settings.Set("onboarding.skipped", skipped);
        var saved = _settings.Save();
        if (saved.IsSuccess) return true;
        _viewModel.Status.Failure(saved.Error!.Code, L("linux.onboarding.save_failed"));
        return false;
    }

    private void OnOnboardingBack(object? sender, RoutedEventArgs e) => _onboarding?.Back();
    private void OnOnboardingNext(object? sender, RoutedEventArgs e) => _onboarding?.Next();
    private void OnOnboardingSkip(object? sender, RoutedEventArgs e) => _onboarding?.Skip();

    private async void OnOnboardingTestDictation(object? sender, RoutedEventArgs e)
    {
        if (_onboarding is null || !_onboarding.IsTestReady)
        {
            _onboarding?.SetTestStatus(L("linux.onboarding.test.not_ready"));
            return;
        }
        try
        {
            if (_recordingSession.IsActive)
            {
                _onboarding.SetTestStatus(L("linux.onboarding.test.transcribing"));
                await _interaction.StopRecordingAsync(_lifetime.Token);
            }
            else
            {
                await _interaction.StartRecordingAsync(_lifetime.Token);
                _onboarding.SetTestStatus(L("linux.onboarding.test.recording"));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { _onboarding.SetTestStatus(L("linux.onboarding.test.failed")); }
    }

    private void OnOnboardingTranscriptionSaved(object? sender, EventArgs e) =>
        _onboarding?.SetTestStatus(L("linux.onboarding.test.succeeded"), succeeded: true);

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
        UpdateShortcutHint();
        var settings = _viewModel.Settings;
        if (Avalonia.Application.Current is { } application)
            application.RequestedThemeVariant = settings.ThemeMode switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        _overlay.ApplyPreference();
        var soundVolume = _platformServices.SoundEffects.ConfigureVolume(settings.SoundEffectsVolume);
        if (soundVolume.IsFailure)
            settings.Status.Failure(soundVolume.Error!.Code, soundVolume.Error.Message);
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
            ShowPlatformStatus(LF("linux.platform.warning", shortcutError.Message), true);
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
        if (result.IsFailure) ShowPlatformStatus(LF("linux.platform.warning", result.Error!.Message), true);
        else ShowPlatformStatus(LF("linux.platform.connected", _platformServices.Paths.DataDirectory), false);

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
        GoTo(page);
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
        ShowPlatformStatus(PlatformStatusText.Text + L("linux.platform.tray_disconnected"), true);
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

    // Backup, the cloud account, provider credentials and About are their own pages here, but
    // the Windows app reaches all four through Settings. The sidebar shows one Settings row and
    // a second column lists the family, so both apps navigate the same way.
    private static readonly string[] SettingsFamily =
        ["settings", "sound", "account", "storage", "output", "localapi", "shortcuts", "backup",
         "appearance", "about", "credentials"];
    private bool _navigationSyncing;
    private string _currentPageId = "home";

    /// <summary>
    /// Windows filters history by one period list, not by two date pickers. The dates still drive
    /// the query; this only turns the chosen period into a start and an end date.
    /// </summary>
    private void OnHistoryPeriodChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string period } }) return;
        var today = DateTimeOffset.Now.Date;
        _viewModel.History.StartDate = period switch
        {
            "Today" => new DateTimeOffset(today),
            "ThisWeek" => new DateTimeOffset(today.AddDays(-(int)today.DayOfWeek)),
            "ThisMonth" => new DateTimeOffset(new DateTime(today.Year, today.Month, 1)),
            _ => null
        };
        _viewModel.History.EndDate = null;
        if (_viewModel.History.SearchCommand.CanExecute(null)) _viewModel.History.SearchCommand.Execute(null);
    }

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Settings.SaveCommand.CanExecute(null)) _viewModel.Settings.SaveCommand.Execute(null);
    }

    private void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is ListBoxItem { Tag: string pageId }) GoTo(pageId);
    }

    private void OnSettingsNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav.SelectedItem is ListBoxItem { Tag: string pageId }) GoTo(pageId);
    }

    /// <summary>
    /// Single entry point for navigation. Selecting a list item raises the same event the user
    /// click raises, so every path guards against re-entering itself.
    /// </summary>
    private void GoTo(string pageId)
    {
        if (_navigationSyncing) return;
        _navigationSyncing = true;
        try
        {
            _viewModel.Navigate(pageId);
            _currentPageId = pageId;
            UpdatePlatformNotice();
            var inSettings = Array.IndexOf(SettingsFamily, pageId) >= 0;
            Select(Navigation, inSettings ? "settings" : pageId);
            // The sidebar list raises its first selection while the rest of the tree is still
            // being built, so every control below it in the markup can still be null here.
            if (SettingsNavPane is not null) SettingsNavPane.IsVisible = inSettings;
            if (inSettings && SettingsNav is not null) Select(SettingsNav, pageId);
            if (SettingsFooter is not null) SettingsFooter.IsVisible = inSettings;
            if (PageSubtitle is not null) PageSubtitle.Text = _localization[$"linux.page.subtitle.{pageId}"];
            // Windows draws no page heading on Home, on History or on any settings section: each
            // of those carries its own title, and History runs edge to edge.
            var ownsItsHeader = pageId is "home" or "history" || inSettings;
            if (PageHeader is not null) PageHeader.IsVisible = !ownsItsHeader;
            // Windows puts the one primary action of a page on the header row, beside the title.
            if (PageActionButton is not null)
            {
                PageActionButton.IsVisible = pageId == "modes";
                if (pageId == "modes") PageActionButton.Content = _localization["modes.header.create"];
            }
            // Every page scroller carries its own 24px padding, the way the Windows PagePadding
            // resource does, so the host adds nothing.
            if (PageContent is not null) PageContent.Margin = new Thickness(0);
            // Windows gives the header band the same content width as the page under it, so the
            // title lines up with the first card. Streaming is the one 560px page up here.
            if (PageHeaderContent is not null) PageHeaderContent.MaxWidth = pageId == "streaming" ? 560 : 720;
            if (pageId == "localapi") Dispatcher.UIThread.Post(UpdateLocalApiConnectionUi);
        }
        finally { _navigationSyncing = false; }
    }

    /// <summary>
    /// The platform integration message used to sit in the sidebar, where a long warning
    /// pushed the rest of the column off screen. It now shows as a notice over the page, and
    /// only when something is actually wrong.
    /// </summary>
    private bool _platformStatusIsWarning;

    private void ShowPlatformStatus(string text, bool isWarning)
    {
        PlatformStatusText.Text = text;
        _platformStatusIsWarning = isWarning;
        UpdatePlatformNotice();
    }

    /// <summary>
    /// Windows shows no banner over its pages. The Linux integration warning is real, so it stays,
    /// but it shows on Home only. Every other page then lays out at the same height Windows does.
    /// </summary>
    private void UpdatePlatformNotice()
    {
        if (PlatformNotice is null) return;
        PlatformNotice.IsVisible = _platformStatusIsWarning && _currentPageId == "home";
    }

    private void OnGoToShortcuts(object? sender, RoutedEventArgs e) => GoTo("shortcuts");
    private void OnGoToCredentials(object? sender, RoutedEventArgs e) => GoTo("credentials");
    private void OnGoToModes(object? sender, RoutedEventArgs e) => GoTo("modes");
    private void OnGoToVocabulary(object? sender, RoutedEventArgs e) => GoTo("vocabulary");

    /// <summary>Runs the one primary action of the current page, from the header row.</summary>
    private void OnPageAction(object? sender, RoutedEventArgs e)
    {
        if (_currentPageId != "modes") return;
        var command = _viewModel.Modes.NewCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private static void Select(ListBox list, string tag)
    {
        foreach (var item in list.Items.OfType<ListBoxItem>())
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            { list.SelectedItem = item; return; }
    }

    private void UpdateShortcutHint()
    {
        var shortcut = _viewModel.Settings.ToggleShortcutDisplay;
        ShortcutHintText.Text = string.IsNullOrEmpty(shortcut)
            ? string.Empty
            : string.Format(_localization["linux.status.record_hint"], shortcut);
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
            // History and Model Library hide their detail pane until a row is picked, the way the
            // Windows app does. Pick the first row on each so the detail controls are on screen.
            _viewModel.History.Selected ??= _viewModel.History.Items.FirstOrDefault();
            if (_viewModel.Models is not null)
                _viewModel.Models.Selected ??= _viewModel.Models.Items.FirstOrDefault();
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
                ["settings"] = "SettingsLaunchMinimized"
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
            // Settings is ten sections now, as on Windows, so each one is checked on its own page.
            var settingsSections = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["settings"] = ["SettingsAutostart", "SettingsLaunchMinimized", "SettingsMinimizeToTray",
                    "SettingsShowRecordingWindow", "SettingsEnableErrorLogging", "SettingsShareSpeedData",
                    "SettingsLanguageInput", "SettingsDesktopContextStatus"],
                ["sound"] = ["SettingsSoundEffects", "SettingsAudioEnvironmentPolicy", "SettingsBoostMicrophone",
                    "SettingsKeepMicrophoneWarm", "SettingsVoiceActivityTrimming"],
                ["storage"] = ["SettingsRecordingsDirectory", "SettingsChooseRecordingsDirectory",
                    "SettingsStoreAsM4A", "SettingsKeepAudioFiles", "SettingsAutoDeleteEnabled",
                    "SettingsAutoDeleteDays", "SettingsStorageDeleteNow", "SettingsStorageOpenFolder"],
                ["output"] = ["SettingsPasteResultText", "SettingsRemoveFillerWords", "SettingsAutocapitalizeInsert",
                    "SettingsStoreWordTimestamps", "SettingsRestoreClipboard", "SettingsClipboardRestoreDelay",
                    "SettingsHideClipboardHistory"],
                ["localapi"] = ["SettingsLocalApiEnabled", "SettingsLocalApiPort", "SettingsLocalApiStatus",
                    "SettingsLocalApiToken", "SettingsLocalApiDiscoveryPath"],
                ["shortcuts"] = ["SettingsToggleKey", "SettingsCancelKey", "SettingsChangeModeKey",
                    "SettingsStreamingShortcutKey", "SettingsPushToTalkMode", "SettingsResetShortcuts"],
                ["appearance"] = ["SettingsThemeMode", "SettingsLocalWhisperBackend", "SettingsLocalWhisperCpuFallback",
                    "SettingsLocalWhisperRuntimeStatus", "SettingsLocalLlmBackend", "SettingsLocalLlmCpuFallback"],
                ["streaming"] = ["StreamingEnabledToggle", "StreamingProviderChoice", "StreamingLanguageInput"]
            };
            // The Streaming page hides its provider rows until streaming is on, as Windows does.
            _viewModel.Settings.StreamingEnabled = true;
            foreach (var section in settingsSections)
            {
                _viewModel.Navigate(section.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var control in section.Value)
                    if (!HasVisibleControl(control))
                    {
                        Console.Error.WriteLine($"Smoke: {control} is not visible on the {section.Key} page.");
                        return 10;
                    }
            }


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
