using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
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
        ComboWheelGuard.Attach(this);
        DataContext = _viewModel;
        // The library count under the title moves with the filters, so it follows the view.
        if (_viewModel.Models is { } models)
            models.PropertyChanged += (_, _) => UpdateModelLibrarySubtitle();
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
        _viewModel.Status.PropertyChanged += OnShellStatusChanged;
        _interaction.OperationFailed += OnInteractionFailed;
        _interaction.ChangeModeRequested += OnChangeModeRequested;
        _platformServices.Tray.ActionRequested += OnTrayActionRequested;
        _platformServices.Tray.Unavailable += OnTrayUnavailable;
        if (_viewModel.Recording is not null)
        {
            _viewModel.Recording.TranscriptionSaved += OnOnboardingTranscriptionSaved;
            _viewModel.Recording.PropertyChanged += OnRecordingStatusChanged;
        }
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
        _shuttingDown = true;
        _lifetime.Cancel();
        _viewModel.Settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        _viewModel.Settings.DesktopSettingsChanged -= OnDesktopSettingsChanged;
        _viewModel.Settings.TelemetrySettingsChanged -= OnTelemetrySettingsChanged;
        _viewModel.Settings.StorageSettingsChanged -= OnStorageSettingsChanged;
        _interaction.OperationFailed -= OnInteractionFailed;
        _interaction.ChangeModeRequested -= OnChangeModeRequested;
        _platformServices.Tray.ActionRequested -= OnTrayActionRequested;
        _platformServices.Tray.Unavailable -= OnTrayUnavailable;
        if (_viewModel.Recording is not null)
        {
            _viewModel.Recording.TranscriptionSaved -= OnOnboardingTranscriptionSaved;
            _viewModel.Recording.PropertyChanged -= OnRecordingStatusChanged;
        }
        if (_modeToast is not null)
        {
            try { _modeToast.Close(); } catch { }
            _modeToast = null;
        }
        if (_fileProgress is not null)
        {
            _fileProgress.CancelRequested -= OnFileProgressCancelRequested;
            try { _fileProgress.Close(); } catch { }
            _fileProgress = null;
        }
        if (_errorToast is not null)
        {
            _errorToast.SettingsRequested -= OnErrorToastSettingsRequested;
            try { _errorToast.Close(); } catch { }
            _errorToast = null;
        }
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

    private Task EnsureInitializedAsync() => _initialization ??= InitializeAndArmAutoSaveAsync();

    private async Task InitializeAndArmAutoSaveAsync()
    {
        await _viewModel.InitializeAsync();
        // The Storage page prints the folder recordings actually go to, the way Windows does, and
        // an unset preference is an empty string. Seeding it here, BEFORE the listener is armed,
        // shows the real path without writing a value the user never chose.
        if (_viewModel.Settings.RecordingsDirectory.Length == 0)
            _viewModel.Settings.RecordingsDirectory = _platformServices.Paths.RecordingsDirectory;
        // Windows applies each settings change the moment it is made and has no Save button
        // anywhere. Arming only AFTER the first load matters: Load() writes every property, and
        // an armed listener would answer that with a save of the values it just read.
        _viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>
    /// Debounced so a typed field (the recordings directory, the language) is written once the
    /// user stops, not once per keystroke — a half-typed path fails Save()'s own validation.
    /// </summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.ClipboardHistoryPrivacyStatus)
            or nameof(SettingsViewModel.DesktopContextStatus)) return;
        _settingsAutoSave ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnSettingsAutoSaveTick);
        _settingsAutoSave.Stop();
        _settingsAutoSave.Start();
    }

    private void OnSettingsAutoSaveTick(object? sender, EventArgs e)
    {
        _settingsAutoSave?.Stop();
        if (_viewModel.Settings.SaveCommand.CanExecute(null)) _viewModel.Settings.SaveCommand.Execute(null);
    }

    private DispatcherTimer? _settingsAutoSave;

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
        if (files.Count != 1) return;
        _viewModel.Backup.Path = files[0].Path.LocalPath;
        // Windows inspects the archive inside this handler and opens its selection panel with the
        // result (BackupExportSettingsPage.xaml.cs:128); there is no separate Inspect control on
        // that page. Linux used to leave the user to press a Linux-only Inspect button.
        await _viewModel.Backup.InspectAsync(_lifetime.Token);
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

    /// <summary>
    /// Windows opens a 760x760 modal holding its whole API-keys page over the Model Library, and
    /// rebuilds the library when it closes (ModelsSettingsPage.xaml.cs:880-890). Linux used to
    /// navigate the app to the credentials page, so the library was gone and the user had to
    /// find their own way back through the sidebar. The credentials page is still reachable from
    /// the sidebar; this is only what the library's own buttons do.
    /// </summary>
    private async void OnModelCredentialAction(object? sender, RoutedEventArgs e)
    {
        var action = _viewModel.Models?.Selected?.CredentialNavigationActionId;
        if (action == "navigate.account")
        {
            // A HyperWhisper Cloud row is an account concern, not an API key; Windows sends it
            // to the account surface rather than the key modal.
            NavigateFromModel("account");
            return;
        }
        if (_viewModel.Credentials is not { } credentials) { NavigateFromModel("credentials"); return; }

        const string prefix = "navigate.credentials:";
        if (action?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            // A specific provider needs a key: Windows shows the small 460x360
            // ProviderApiKeyWindow for exactly that provider, not the whole key page.
            var account = action[prefix.Length..];
            var row = _viewModel.Models?.Selected;
            await new ProviderApiKeyWindow(credentials, account,
                    row?.ProviderName ?? account, ApiKeyConsoleUrl(account))
                .ShowDialog(this);
        }
        else
        {
            await new CredentialsWindow(credentials).ShowDialog(this);
        }
        // Windows re-runs provider health once the modal closes.
        if (_viewModel.Models?.RefreshReadinessCommand.CanExecute(null) == true)
            _viewModel.Models.RefreshReadinessCommand.Execute(null);
    }

    /// <summary>
    /// Where a provider hands out API keys, for the modal's "Get key" button. Windows reads the
    /// same list off ApiKeyType.GetApiKeyUrl(); an unknown provider simply hides the button.
    /// The keys are the credential-store account ids from CredentialManagementViewModel.Accounts
    /// ("DeepgramApiKey", not "deepgram"), which is what the row's navigation action carries.
    /// </summary>
    private static string ApiKeyConsoleUrl(string account) => account switch
    {
        "OpenAIApiKey" => "https://platform.openai.com/api-keys",
        "AnthropicApiKey" => "https://console.anthropic.com/settings/keys",
        "GroqApiKey" => "https://console.groq.com/keys",
        "DeepgramApiKey" => "https://console.deepgram.com/",
        "AssemblyAIApiKey" => "https://www.assemblyai.com/app/account",
        "ElevenLabsApiKey" => "https://elevenlabs.io/app/settings/api-keys",
        "MistralApiKey" => "https://console.mistral.ai/api-keys/",
        "SonioxApiKey" => "https://console.soniox.com/",
        "GeminiApiKey" or "GeminiTranscribeApiKey" => "https://aistudio.google.com/apikey",
        "GrokApiKey" => "https://console.x.ai/",
        "CerebrasApiKey" => "https://cloud.cerebras.ai/platform/",
        "MetaApiKey" => "https://llama.developer.meta.com/",
        _ => string.Empty,
    };

    // A table row carries its own action button. Every command on the view model works on the
    // selected model, so a row action selects its row first and then runs the command.
    private ManagedModelViewModel? SelectModelRow(object? sender)
    {
        if (_viewModel.Models is null
            || sender is not Control { DataContext: ManagedModelViewModel row }) return null;
        _viewModel.Models.Selected = row;
        return row;
    }

    /// <summary>
    /// Windows guards a model delete twice: a mode still pointing at the model blocks it
    /// outright, and otherwise a YesNo MessageBox confirms
    /// (Views/Pages/Settings/ModelsSettingsPage.xaml.cs:802-803, 843-850). Linux deleted on the
    /// first click with neither check.
    /// </summary>
    private async void OnModelRowDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectModelRow(sender) is not { } row || _viewModel.Models is not { } models) return;
        if (ModeUsingModel(row) is { } modeName)
        {
            await ConfirmWindow.ShowNoticeAsync(this,
                L("linux.models.delete.inUse.title"),
                LF("linux.models.delete.inUse.message", row.DisplayName, modeName));
            return;
        }
        var confirmed = await ConfirmWindow.ShowAsync(this,
            L("settings.models.delete.confirm.title"),
            LF("settings.models.delete.confirm.message", row.DisplayName));
        if (!confirmed) return;
        if (models.DeleteCommand.CanExecute(null)) models.DeleteCommand.Execute(null);
    }

    /// <summary>The name of the first mode whose transcription model is this row, if any.</summary>
    private string? ModeUsingModel(ManagedModelViewModel row)
    {
        // ModelId, not Id: Id is Capability.Key, a composite library key that never equals the
        // catalog id a mode stores, so the guard silently matched nothing.
        var id = string.IsNullOrEmpty(row.ModelId) ? row.Model?.Id : row.ModelId;
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var mode in _viewModel.Modes.Items)
        {
            var used = mode.ProviderType == "cloud"
                ? mode.CloudTranscriptionModel
                : mode.LocalEngine == "parakeet" ? mode.LocalParakeetModel : mode.ModelType ?? mode.Model;
            if (string.Equals(used, id, StringComparison.OrdinalIgnoreCase)) return mode.Name;
            if (string.Equals(mode.LocalPostProcessingModel, id, StringComparison.OrdinalIgnoreCase)) return mode.Name;
        }
        return null;
    }

    private void NavigateFromModel(string page)
    {
        try
        {
            GoTo(page);
        }
        catch (ArgumentException) { _viewModel.Status.Failure("models.navigation_unavailable", L("linux.error.navigation_unavailable")); }
    }

    /// <summary>
    /// Windows disables the button, relabels it "Exporting...", and then reports the outcome in a
    /// MessageBox rather than leaving it in a status line at the foot of the page. Cancelling the
    /// picker returns before any relabel, which is why the swap happens after the picker and not
    /// before it.
    /// </summary>
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
        if (file is null) return;

        using (BeginButtonWork("AboutExportDiagnosticsButton", "settings.about.exportDiagnostics.exporting"))
            await _viewModel.About.ExportDiagnosticsAsync(file.Path.LocalPath, _lifetime.Token);

        if (Program.IsSmokeTest) return;
        var failed = _viewModel.About.Status.HasError;
        await ConfirmWindow.ShowNoticeAsync(this,
            L(failed ? "settings.about.exportDiagnostics.failed.title"
                     : "settings.about.exportDiagnostics.success.title"),
            failed
                ? _localization.Format("settings.about.exportDiagnostics.failed.message",
                    _viewModel.About.Status.Message)
                : L("settings.about.exportDiagnostics.success.message"));
    }

    /// <summary>
    /// Disable a button and swap its label for the duration of a piece of work, then put both
    /// back. Windows writes this as a captured originalContent plus a try/finally on every such
    /// button; the disposable is the same shape, so a failure cannot leave a button reading
    /// "Exporting..." forever.
    /// </summary>
    private IDisposable BeginButtonWork(string buttonName, string busyLabelKey)
    {
        var button = FindNamed<Button>(buttonName);
        if (button is null) return new ButtonWork(null, null);
        var original = button.Content;
        button.Content = L(busyLabelKey);
        button.IsEnabled = false;
        return new ButtonWork(button, original);
    }

    private sealed class ButtonWork(Button? button, object? original) : IDisposable
    {
        public void Dispose()
        {
            if (button is null) return;
            button.Content = original;
            button.IsEnabled = true;
        }
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e) => OpenFixedLocation(_platformServices.Paths.LogsDirectory);
    private void OnOpenSupport(object? sender, RoutedEventArgs e) => OpenSafeUri(new Uri("https://hyperwhisper.com/support"));
    private void OnOpenSpeedComparison(object? sender, RoutedEventArgs e) => OpenSafeUri(new Uri("https://www.hyperwhisper.com/en/latency"));

    /// <summary>
    /// Windows closes its General page with settings.version.detail. The section view model only
    /// re-presents SettingsViewModel, so the two version strings come from the About view model
    /// the same shell owns, and the line is written once the page is on screen.
    /// </summary>
    private void UpdateGeneralVersionText()
    {
        if (FindNamed<TextBlock>("SettingsVersionText") is not { } version || _viewModel.About is null) return;
        version.Text = _localization.Format(
            "settings.version.detail", _viewModel.About.AppVersion, _viewModel.About.PackageVersion);
    }

    private async void OnRefreshPackageStatus(object? sender, RoutedEventArgs e)
    {
        // Windows relabels this button "Checking..." while it works, not just disables it.
        // Windows relabels this button "Checking..." while it works, not just disables it.
        string message;
        using (BeginButtonWork("AboutUpdateRefreshButton", "settings.about.checkingForUpdates"))
        {
            try
            {
                var status = await _packageUpdateProbe.CheckAsync(_lifetime.Token);
                message = status.State switch
                {
                LinuxPackageUpdateState.Current when status.InstalledVersion is not null =>
                    LF("linux.update.current_version", status.InstalledVersion),
                LinuxPackageUpdateState.UpdateAvailable when status.InstalledVersion is not null && status.CandidateVersion is not null =>
                    LF("linux.update.available_versions", status.InstalledVersion, status.CandidateVersion),
                LinuxPackageUpdateState.UpdateAvailable => L("linux.update.available"),
                    LinuxPackageUpdateState.NotPackageManaged => L("linux.update.not_package_managed"),
                    LinuxPackageUpdateState.Unavailable => L("linux.update.unavailable"),
                    _ => L("linux.update.failed"),
                };
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
        // The result used to land in a Linux-only text block under the last card. Windows reports
        // its own update check in a dialog and draws nothing there, so this does the same.
        await ConfirmWindow.ShowNoticeAsync(this, L("settings.about.checkUpdates"), message);
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
        catch { SetStorageError(L("linux.storage.maintenance_failed")); }
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
        // Windows reports no disk usage on this page, so the inventory line has no home here any
        // more; what it fed was the Linux-only card. The last-cleanup line stays, because Windows
        // draws that one too (LastCleanupInfoPanel).
        SetStorageText("StoragePathText", cleanupText);
    }

    /// <summary>
    /// Windows confirms, swaps the button label to "Deleting...", then reports the count in a
    /// second box (Views/Pages/Settings/StorageSettingsPage.xaml.cs:164-205). Linux ran the
    /// cleanup straight off the first click with no prompt and no result.
    /// </summary>
    private async void OnDeleteStoredAudioNow(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmWindow.ShowAsync(this,
            L("settings.storage.autoDelete.confirmDelete.title"),
            L("settings.storage.autoDelete.confirmDelete.message"));
        if (!confirmed) return;
        var button = sender as Button;
        var restore = button?.Content;
        if (button is not null)
        {
            button.IsEnabled = false;
            button.Content = L("settings.storage.autoDelete.deleting");
        }
        try
        {
            await RunStorageMaintenanceAsync(_lifetime.Token);
            await ConfirmWindow.ShowNoticeAsync(this,
                L("settings.storage.autoDelete.deleteComplete.title"),
                LF("settings.storage.autoDelete.deleteComplete.message",
                   _lastStorageCleanup?.AudioFilesDeleted ?? 0));
        }
        catch (Exception exception)
        {
            SetStorageError(L("linux.storage.cleanup_failed"));
            await ConfirmWindow.ShowNoticeAsync(this,
                L("settings.storage.autoDelete.deleteFailed.title"),
                LF("settings.storage.autoDelete.deleteFailed.message", exception.Message));
        }
        finally
        {
            if (button is not null)
            {
                button.Content = restore;
                button.IsEnabled = true;
            }
        }
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
        catch { SetStorageError(L("linux.storage.open_failed")); }
    }

    /// <summary>
    /// Windows keeps a collapsed StorageErrorText under the recordings-folder card and reveals it
    /// when a folder operation fails (StorageSettingsPage.xaml:60-65). This page has the same
    /// line, so the failures that used to be written into the Linux-only status readout land
    /// there now instead of going nowhere.
    /// </summary>
    private void SetStorageError(string text)
    {
        if (this.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault(candidate => candidate.Name == "StorageErrorText") is not { } error) return;
        error.Text = text;
        error.IsVisible = true;
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
            // Windows carries the same fault as an inline banner at the top of Home, not only
            // as a settings error, so the user sees it where recording is explained.
            ReportShortcutConflict(shortcutError.Message);
            return;
        }
        _viewModel.Home.ClearShortcutConflict();
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
        if (result.IsFailure)
        {
            ShowPlatformStatus(LF("linux.platform.warning", result.Error!.Message), true);
            ReportShortcutConflict(result.Error.Message);
        }
        else
        {
            ShowPlatformStatus(LF("linux.platform.connected", _platformServices.Paths.DataDirectory), false);
            _viewModel.Home.ClearShortcutConflict();
        }

        var wantedAutostart = settings.AutostartEnabled;
        var autostart = wantedAutostart ? _platformServices.Autostart.Enable() : _platformServices.Autostart.Disable();
        // Windows reverts the checkbox and puts the failure in front of the user rather than
        // leaving a switch on that did not take: settings.general.startup.enableFailed /
        // .disableFailed in an OK box titled common.error. Only the enable path reported anything
        // here, into a status line at the foot of the page, and the checkbox stayed where the
        // click left it, so the UI claimed a setting the desktop file does not have.
        if (autostart.IsFailure)
        {
            _viewModel.Settings.AutostartEnabled = !wantedAutostart;
            if (FindNamed<CheckBox>("SettingsAutostart") is { } box) box.IsChecked = !wantedAutostart;
            _viewModel.Settings.Status.Failure(autostart.Error!.Code, autostart.Error.Message);
            var messageKey = wantedAutostart
                ? "settings.general.startup.enableFailed"
                : "settings.general.startup.disableFailed";
            _ = ConfirmWindow.ShowNoticeAsync(this, L("common.error"), L(messageKey));
        }
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

    // =====================================================================================
    // THE ERROR TOAST
    //
    // Windows raises ErrorToastWindow from ~25 sites. Every one of them is a failure the user
    // needs to see while they are typing into ANOTHER application, which is why it is a floating
    // pill above the recording overlay and not a line in the main window.
    //
    // Linux funnels the same failures through two observable surfaces -- the shell UiStatus (what
    // the 11px status bar renders) and the transcription workflow's own error state. Routing the
    // toast off those two covers the whole set at once, rather than by transcribing 25 call sites
    // that would then drift. The status line is left exactly as it was: the toast is additive.
    // =====================================================================================

    private LinuxErrorToastWindow? _errorToast;
    private bool _errorToastQueued;
    private bool _shuttingDown;

    /// <summary>
    /// UiStatus.Failure writes ErrorCode and THEN Message, so the first notification arrives with a
    /// stale message. Coalescing both notifications into one dispatcher turn means the toast always
    /// reads the settled pair, and a single failure never raises two toasts.
    /// </summary>
    private void QueueErrorToast(Func<(string? Code, string Message)> read)
    {
        if (_errorToastQueued) return;
        _errorToastQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _errorToastQueued = false;
            try
            {
                var (code, message) = read();
                if (code is null || string.IsNullOrWhiteSpace(message)) return;
                ShowErrorToast(message, ClassifyErrorToastAction(code));
            }
            catch { /* Error feedback must never become a second failure. */ }
        }, DispatcherPriority.Background);
    }

    private void ShowErrorToast(string message, LinuxErrorToastAction action)
    {
        // The smoke test is a headless self-check that tears the app down as soon as it has walked
        // the tree; opening a second top-level window on the way out raced its shutdown and threw
        // "Dispatcher shut down". Every other transient surface here is guarded the same way.
        if (Program.IsSmokeTest || _shuttingDown) return;
        // Windows dismisses the previous toast before showing the next, so two failures in a row
        // never stack into two overlapping pills.
        _errorToast ??= CreateErrorToast();
        _errorToast.DismissImmediately();
        _errorToast.ShowError(message, action);
    }

    private LinuxErrorToastWindow CreateErrorToast()
    {
        var toast = new LinuxErrorToastWindow();
        toast.SettingsRequested += OnErrorToastSettingsRequested;
        return toast;
    }

    /// <summary>
    /// Windows decides the button's destination from the exception type: a missing or rejected API
    /// key opens the API-keys surface, a cloud-account problem goes to the Cloud settings section,
    /// and a missing local post-processing model goes to the Model Library. Linux carries the same
    /// distinctions in the error CODE, so the mapping is made from that rather than from the text.
    /// </summary>
    private static LinuxErrorToastAction ClassifyErrorToastAction(string code)
    {
        var value = code.ToLowerInvariant();
        if (value.Contains("credential", StringComparison.Ordinal)
            || value.Contains("api_key", StringComparison.Ordinal)
            || value.Contains("apikey", StringComparison.Ordinal)
            || value.Contains("unauthorized", StringComparison.Ordinal))
            return LinuxErrorToastAction.ApiKeys;
        if (value.StartsWith("account.", StringComparison.Ordinal)
            || value.Contains("cloud_account", StringComparison.Ordinal))
            return LinuxErrorToastAction.CloudSettings;
        if (value.StartsWith("models.", StringComparison.Ordinal)
            || value.Contains("model_not_downloaded", StringComparison.Ordinal)
            || value.Contains("model_missing", StringComparison.Ordinal))
            return LinuxErrorToastAction.ModelLibrary;
        return LinuxErrorToastAction.None;
    }

    private void OnErrorToastSettingsRequested(object? sender, LinuxErrorToastAction action)
    {
        try
        {
            // Windows shows and activates the main window first: the toast is the only thing on
            // screen when the app is minimised to tray.
            if (!IsVisible) Show();
            Activate();
            switch (action)
            {
                case LinuxErrorToastAction.ApiKeys:
                    NavigateFromModel("models");
                    // Windows auto-opens the API-keys modal over the Model Library.
                    Dispatcher.UIThread.Post(() => OnModelCredentialAction(this, new RoutedEventArgs()),
                        DispatcherPriority.Background);
                    break;
                case LinuxErrorToastAction.ModelLibrary:
                    NavigateFromModel("models");
                    break;
                case LinuxErrorToastAction.CloudSettings:
                    NavigateFromModel("account");
                    break;
            }
        }
        catch { }
    }

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
            ShowModeChangeToast(next.Name);
        }
        catch { /* Mode feedback and cycling must not interrupt recording. */ }
    }

    private LinuxModeChangeToastWindow? _modeToast;

    /// <summary>
    /// Windows raises ModeChangeToastWindow on every mode change, on top of whatever the recording
    /// overlay is showing (MainWindow.xaml.cs ShowModeChangeToast). Linux only annotated the
    /// overlay, so a mode cycled with the window hidden and no recording in progress — the case
    /// the hotkey exists for — confirmed nothing at all.
    /// </summary>
    private void ShowModeChangeToast(string modeName)
    {
        // Same guard the error toast uses: a headless smoke run has no compositor to place a
        // window against, and a shutting-down dispatcher cannot service one.
        if (Program.IsSmokeTest || _shuttingDown) return;
        try
        {
            _modeToast ??= new LinuxModeChangeToastWindow();
            _modeToast.DismissImmediately();
            _modeToast.ShowMode(LF("mode.change.toast", modeName));
        }
        catch { }
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

    // =====================================================================================
    // SHORTCUT RECORDERS
    // Windows uses a ShortcutRecorderBox: a read-only field that captures the next chord
    // pressed into it. Two raw text fields could not honour the row's own description
    // ("Click and press the keys you want to use"), so the Linux boxes capture too. The
    // page template is rebuilt on navigation, so the boxes are filled from the view model
    // each time the page loads rather than bound.
    // =====================================================================================
    private static readonly (string Tag, string Modifiers, string Key, string Box)[] ShortcutRoles =
    [
        ("toggle", nameof(SettingsViewModel.ToggleShortcutModifiers), nameof(SettingsViewModel.ToggleShortcutKey), "SettingsToggleKey"),
        ("cancel", nameof(SettingsViewModel.CancelShortcutModifiers), nameof(SettingsViewModel.CancelShortcutKey), "SettingsCancelKey"),
        ("changeMode", nameof(SettingsViewModel.ChangeModeShortcutModifiers), nameof(SettingsViewModel.ChangeModeShortcutKey), "SettingsChangeModeKey"),
        ("streaming", nameof(SettingsViewModel.StreamingShortcutModifiers), nameof(SettingsViewModel.StreamingShortcutKey), "SettingsStreamingShortcutKey"),
        ("pushToTalk", nameof(SettingsViewModel.PushToTalkShortcutModifiers), nameof(SettingsViewModel.PushToTalkShortcutKey), "SettingsPushToTalkCustomKey"),
    ];

    private void UpdateShortcutsUi()
    {
        var settings = _viewModel.Settings;
        foreach (var role in ShortcutRoles)
        {
            if (FindNamed<TextBox>(role.Box) is not { } box) continue;
            box.Text = FormatChord(
                ReadSetting(settings, role.Modifiers), ReadSetting(settings, role.Key));
            // A verdict about the last chord cannot outlive the value it was about. Windows
            // clears through the same hook a reset or a re-seed goes through.
            ClearShortcutError(box);
        }
        SelectByTag("SettingsPushToTalkMode", settings.PushToTalkMode);
        SelectByTag("SettingsPushToTalkModifier", settings.PushToTalkModifier);
    }

    private static string ReadSetting(SettingsViewModel settings, string property)
        => settings.GetType().GetProperty(property)?.GetValue(settings) as string ?? string.Empty;

    private static void WriteSetting(SettingsViewModel settings, string property, string value)
        => settings.GetType().GetProperty(property)?.SetValue(settings, value);

    /// <summary>
    /// The recorder boxes spell a chord exactly as the status bar and the Home chips do, which is
    /// the Windows spelling: "Ctrl+Alt", "Esc", "Ctrl+Shift+.". This used to be a second local
    /// copy of the formatter and the two drifted.
    /// </summary>
    private static string FormatChord(string? modifiers, string? key)
        => ShortcutDisplay.Format(modifiers, key);

    private void SelectByTag(string comboName, string? value)
    {
        if (FindNamed<ComboBox>(comboName) is not { } combo) return;
        var match = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal));
        combo.SelectedItem = match ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void OnShortcutBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Every key press belongs to the recorder while it has focus, including Tab and Escape.
        e.Handled = true;
        if (sender is not TextBox box) return;
        var role = ShortcutRoles.FirstOrDefault(item => item.Tag == (box.Tag?.ToString() ?? string.Empty));
        if (role.Box is null) return;

        // Windows reads the LIVE modifier state and folds in the pressed key when that key is
        // itself a modifier (ShortcutRecorderBox.BuildShortcutFromKeyEvent). That is the whole
        // reason "Ctrl+Alt" -- the product default toggle chord -- can be typed at all. Reading
        // only e.KeyModifiers dropped the very press that completes a modifier-only chord, so a
        // user who reset or re-recorded could never get the default back through the UI.
        var modifiers = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is Key.LeftCtrl or Key.RightCtrl) modifiers.Add("Control");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.Key is Key.LeftAlt or Key.RightAlt) modifiers.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Key is Key.LeftShift or Key.RightShift) modifiers.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.Key is Key.LWin or Key.RWin) modifiers.Add("Meta");

        string key;
        if (IsModifierKey(e.Key))
        {
            // A modifier-only chord is a complete chord, not an unfinished one: Windows leaves
            // KeyboardShortcut.Key null and commits the modifier set as it stands.
            key = string.Empty;
        }
        else if (MapShortcutKey(e.Key) is { } mapped)
        {
            key = mapped;
        }
        else
        {
            // Neither X11GlobalShortcutService nor EvdevShortcutFilter can grab an unmapped key,
            // so it is discarded rather than stored as something that would never fire.
            return;
        }

        var modifierText = modifiers.Count == 0 ? string.Empty : string.Join(", ", modifiers);

        if (key.Length == 0)
        {
            // Nothing held at all is not a capture yet.
            if (modifiers.Count == 0) return;
            // Windows rejects a SINGLE bare modifier -- it would steal ordinary typing -- but
            // allows a deliberate multi-modifier chord such as Ctrl+Alt or Ctrl+Win.
            if (modifiers.Count == 1)
            {
                ShowShortcutError(box, L("linux.shortcuts.error.singleModifier"));
                return;
            }
        }

        if (FindShortcutDuplicate(role.Tag, modifierText, key) is { } duplicate)
        {
            ShowShortcutError(box, duplicate);
            return;
        }

        ClearShortcutError(box);
        WriteSetting(_viewModel.Settings, role.Modifiers, modifierText);
        WriteSetting(_viewModel.Settings, role.Key, key);
        // Never stamp a local value over a binding: in Avalonia, as in WPF, a local assignment
        // outranks the binding and the field would stop following the view model afterwards. The
        // Streaming page's recorder is bound to StreamingShortcutDisplay, which the write above
        // has already refreshed.
        if (!box.Classes.Contains("boundText")) box.Text = FormatChord(modifierText, key);
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>
    /// The four global roles may not claim the same chord. Windows checks Toggle, Cancel,
    /// ChangeMode and Streaming in <c>ShortcutValidationService.ValidateDuplicate</c>, excluding
    /// the role being edited, and names the clashing role in the message. Push-to-talk is not in
    /// that set on Windows either, so it is not checked here.
    /// </summary>
    private string? FindShortcutDuplicate(string editingTag, string modifiers, string key)
    {
        if (modifiers.Length == 0 && key.Length == 0) return null;

        (string Tag, string Label)[] guarded =
        [
            ("toggle", "settings.shortcuts.toggle.label"),
            ("cancel", "settings.shortcuts.cancel.label"),
            ("changeMode", "settings.shortcuts.changeMode.label"),
            ("streaming", "settings.shortcuts.streaming.label"),
        ];

        foreach (var (tag, labelKey) in guarded)
        {
            if (tag == editingTag) continue;
            var other = ShortcutRoles.FirstOrDefault(item => item.Tag == tag);
            if (other.Box is null) continue;

            var otherModifiers = ReadSetting(_viewModel.Settings, other.Modifiers);
            var otherKey = ReadSetting(_viewModel.Settings, other.Key);
            // Compare the display spelling: it normalises modifier order and naming, so
            // "Alt, Control" and "Control, Alt" are correctly seen as the same chord.
            if (!string.Equals(FormatChord(otherModifiers, otherKey), FormatChord(modifiers, key),
                    StringComparison.Ordinal))
                continue;

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("linux.shortcuts.error.duplicate"),
                L(labelKey),
                FormatChord(otherModifiers, otherKey));
        }

        return null;
    }

    /// <summary>
    /// Windows draws the reason and the red 2px border as ONE thing; splitting them once left a
    /// field red with nothing saying why. Both are set and cleared together here for that reason.
    /// </summary>
    private void ShowShortcutError(TextBox box, string message)
    {
        if (FindNamed<TextBlock>(box.Name + "Error") is { } line)
        {
            line.Text = message;
            line.IsVisible = true;
        }
        if (!box.Classes.Contains("error")) box.Classes.Add("error");
    }

    private void ClearShortcutError(TextBox box)
    {
        if (FindNamed<TextBlock>(box.Name + "Error") is { } line)
        {
            line.Text = string.Empty;
            line.IsVisible = false;
        }
        box.Classes.Remove("error");
    }

    // Focusing a recorder starts a fresh attempt, so the last attempt's verdict goes with it.
    // Windows clears on GotKeyboardFocus for exactly this reason.
    private void OnShortcutBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) ClearShortcutError(box);
    }

    // A Meta release must never reach text input while the global hook is live, so the box
    // swallows KeyUp as well, exactly as the Windows recorder does.
    private void OnShortcutBoxKeyUp(object? sender, KeyEventArgs e) => e.Handled = true;

    /// <summary>
    /// Avalonia key names are not the names the shortcut services grab by: those follow the web
    /// naming X11GlobalShortcutService and EvdevShortcutFilter both map. An unmapped key is
    /// rejected rather than stored, which is what the Windows validator does.
    /// </summary>
    private static string? MapShortcutKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => "Digit" + (char)('0' + (key - Key.D0)),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Escape => "Escape",
        Key.Space => "Space",
        Key.Return or Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.OemPeriod => "Period",
        Key.OemComma => "Comma",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Equal",
        Key.OemQuestion => "Slash",
        Key.OemPipe or Key.OemBackslash => "Backslash",
        Key.OemSemicolon => "Semicolon",
        Key.OemQuotes => "Quote",
        Key.OemOpenBrackets => "LeftBracket",
        Key.OemCloseBrackets => "RightBracket",
        Key.OemTilde => "Grave",
        _ => null,
    };

    private void OnPushToTalkModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item }) return;
        var value = item.Tag?.ToString();
        if (value is not null) _viewModel.Settings.PushToTalkMode = value;
    }

    private void OnPushToTalkModifierChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item }) return;
        var value = item.Tag?.ToString();
        if (value is not null) _viewModel.Settings.PushToTalkModifier = value;
    }

    // The reset command runs on the view model; the recorder boxes are not bound to it, so they
    // are redrawn once it has written the defaults back.
    private void OnShortcutsReset(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(UpdateShortcutsUi);

    /// <summary>
    /// Windows makes the whole theme tile the hit target, not just the radio inside it, so a
    /// press anywhere on the Border checks the one radio it contains.
    /// </summary>
    private void OnThemeTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border tile) return;
        var radio = tile.GetLogicalDescendants().OfType<RadioButton>().FirstOrDefault();
        if (radio is not null) radio.IsChecked = true;
    }

    // The Windows restore-delay control is a minus/plus pair around its own read-out and steps
    // in whole seconds. The view model clamps to 0..60, so the buttons only have to move by one.
    private void OnClipboardRestoreDelayDecrease(object? sender, RoutedEventArgs e)
        => _viewModel.Settings.ClipboardRestoreDelaySeconds
            = Math.Floor(_viewModel.Settings.ClipboardRestoreDelaySeconds) - 1;

    private void OnClipboardRestoreDelayIncrease(object? sender, RoutedEventArgs e)
        => _viewModel.Settings.ClipboardRestoreDelaySeconds
            = Math.Floor(_viewModel.Settings.ClipboardRestoreDelaySeconds) + 1;

    /// <summary>
    /// The Linux half of Windows' UIA self-test. Windows reads AutomationElement.FocusedElement
    /// once on enable and, if that throws, shows an OK + Information box titled
    /// settings.output.autocapitalizeInsert.title carrying
    /// settings.output.autocapitalizeInsert.permissionMessage; it does NOT revert the toggle,
    /// because the failure is per-application rather than a system permission. AT-SPI is the same
    /// capability here: if the caret context cannot be read at all, the insert will pass text
    /// through unchanged in exactly the apps the message names.
    /// </summary>
    private async void OnAutocapitalizeInsertChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: true }) return;
        if (_autocapitalizeProbeRan) return;
        _autocapitalizeProbeRan = true;
        var reachable = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _platformServices.InsertionContext.GetCursorContextAsync(timeout.Token);
        }
        catch
        {
            // The diagnostic log is structured and privacy-safe, so the exception text stays out
            // of it; the notice below is the whole of what Windows does with this failure anyway.
            reachable = false;
        }
        if (reachable) return;
        await ConfirmWindow.ShowNoticeAsync(
            this,
            L("settings.output.autocapitalizeInsert.title"),
            L("settings.output.autocapitalizeInsert.permissionMessage"));
    }

    // Windows probes on every check; once per session is enough to say the same thing, and it
    // keeps a broken AT-SPI from putting a box in front of the user on every toggle.
    private bool _autocapitalizeProbeRan;

    // Windows' NumericOnly_PreviewTextInput swallows anything that is not an integer, so a letter
    // never reaches the box (StorageSettingsPage.xaml.cs:159-162).
    private void OnAutoDeleteDaysTextInput(object? sender, TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsAsciiDigit)) e.Handled = true;
    }

    // ...and DaysOld_LostFocus clamps to 1-365 and writes the clamped figure back, or restores the
    // stored one when the text will not parse at all.
    private void OnAutoDeleteDaysLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (int.TryParse(box.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var value))
            _viewModel.Settings.AutoDeleteDaysOld = Math.Clamp(value, 1, 365);
        box.Text = _viewModel.Settings.AutoDeleteDaysOld.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    private async void OnChooseRecordingsDirectory(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Windows titles this dialog settings.storage.folderBrowser.description, "Choose a
            // folder for new recordings", and seeds it with the folder in use
            // (StorageSettingsPage.xaml.cs:95-100). This used the section heading, "Recordings
            // Folder", which names the setting rather than asking for anything, and it opened
            // wherever the picker last was instead of at the current folder.
            var current = _viewModel.Settings.RecordingsDirectory;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _localization.GetRequired("settings.storage.folderBrowser.description"),
                AllowMultiple = false,
                SuggestedStartLocation = string.IsNullOrWhiteSpace(current) || !Directory.Exists(current)
                    ? null
                    : await StorageProvider.TryGetFolderFromPathAsync(current),
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            var validated = LinuxRecordingDirectoryValidator.ValidateAndPrepare(path);
            if (validated.IsFailure)
            {
                _viewModel.Settings.Status.Failure(validated.Error!.Code, validated.Error.Message);
                ShowStorageError(validated.Error.Message);
                return;
            }
            _viewModel.Settings.RecordingsDirectory = validated.Value!;
            ShowStorageError(null);
            _viewModel.Settings.Status.Success(L("linux.storage.restart_required"));
        }
        catch
        {
            _viewModel.Settings.Status.Failure("storage.folder_picker_failed", L("linux.error.file_picker_failed"));
            ShowStorageError(L("linux.error.file_picker_failed"));
        }
    }

    /// <summary>
    /// Windows keeps a collapsed error line inside the recordings card and reveals it when a
    /// chosen folder is rejected, rather than only reporting through the page footer.
    /// </summary>
    private void ShowStorageError(string? message)
    {
        if (FindNamed<TextBlock>("StorageErrorText") is not { } line) return;
        line.Text = message ?? string.Empty;
        line.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void OnRevealLocalApiToken(object? sender, RoutedEventArgs e)
    {
        _localApiTokenRevealed = !_localApiTokenRevealed;
        UpdateLocalApiConnectionUi();
    }

    private async void OnCopyLocalApiToken(object? sender, RoutedEventArgs e) =>
        await CopyLocalApiTextAsync(TryReadLocalApiToken(), "settings.localApi.action.tokenUnavailable");

    private async void OnCopyLocalApiPort(object? sender, RoutedEventArgs e) =>
        await CopyLocalApiTextAsync(_localApiHost?.State.Port > 0
            ? _localApiHost.State.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null, "settings.localApi.action.portUnavailable");

    /// <summary>
    /// Windows' ShowActionStatus: the message stays until the next action replaces it, and an
    /// error is orange-red where a normal report is secondary text.
    /// </summary>
    private void ShowLocalApiActionStatus(string message, bool isError)
    {
        if (FindNamed<TextBlock>("LocalApiActionStatus") is not { } line) return;
        line.Text = message;
        line.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x00))
            : this.FindResource("HwTextSecondaryBrush") as IBrush
              ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        line.IsVisible = true;
    }

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
        else
            // Windows confirms this in the same action line: the token is masked, so without it
            // there is nothing at all to show that the press did anything.
            ShowLocalApiActionStatus(L("settings.localApi.action.tokenRegenerated"), false);
        UpdateLocalApiConnectionUi();
    }

    private async Task CopyLocalApiTextAsync(string? value, string? unavailableKey = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Windows explains WHY nothing was copied -- "Port is not available until the server
            // is running." -- rather than doing nothing at all, which is what Linux did.
            if (unavailableKey is not null) ShowLocalApiActionStatus(L(unavailableKey), true);
            return;
        }
        var copied = await _platformServices.TextInjection.CopyToClipboardAsync(value, _lifetime.Token);
        if (copied.IsFailure)
        {
            _viewModel.Settings.Status.Failure(copied.Error!.Code, copied.Error.Message);
            ShowLocalApiActionStatus(
                _localization.Format("settings.localApi.action.copyFailed", copied.Error.Message), true);
            return;
        }
        ShowLocalApiActionStatus(L("settings.localApi.action.copied"), false);
    }

    private string? TryReadLocalApiToken()
    {
        try { return _localApiHost?.RevealBearerToken(); }
        catch { return null; }
    }

    /// <summary>
    /// Windows shows one of three tab bodies at a time under a strip whose active label is
    /// primary over a 2px accent rule. The page template is rebuilt on every navigation, so the
    /// choice lives here rather than on the control, and is re-applied whenever the page loads.
    /// </summary>
    private string _localApiTab = "connection";

    private void OnLocalApiTab(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _localApiTab = button.Tag?.ToString() ?? "connection";
        ApplyLocalApiTabSelection();
    }

    private void ApplyLocalApiTabSelection()
    {
        foreach (var (tab, body, label, rule) in new[]
                 {
                     ("connection", "LocalApiConnectionTab", "LocalApiTabConnectionLabel", "LocalApiTabConnectionRule"),
                     ("mcp", "LocalApiMcpTab", "LocalApiTabMcpLabel", "LocalApiTabMcpRule"),
                     ("curl", "LocalApiCurlTab", "LocalApiTabCurlLabel", "LocalApiTabCurlRule"),
                 })
        {
            var active = string.Equals(tab, _localApiTab, StringComparison.Ordinal);
            if (FindNamed<Border>(body) is { } panel) panel.IsVisible = active;
            if (FindNamed<TextBlock>(label) is { } text)
                text.Foreground = active
                    ? this.FindResource("HwTextPrimaryBrush") as IBrush
                    : this.FindResource("HwTextSecondaryBrush") as IBrush;
            if (FindNamed<Avalonia.Controls.Shapes.Rectangle>(rule) is { } line)
                line.Fill = active ? this.FindResource("HwAccentBrush") as IBrush : Brushes.Transparent;
        }
    }

    private void OnOpenLocalApiDocs(object? sender, RoutedEventArgs e)
        => OpenSafeUri(new Uri("https://hyperwhisper.com/docs/api-reference/local-api/overview"));

    private void OnOpenLocalApiMcpGuide(object? sender, RoutedEventArgs e)
        => OpenSafeUri(new Uri("https://hyperwhisper.com/docs/api-reference/local-api/mcp-setup"));

    /// <summary>Opens the folder holding the discovery file, as the Windows Show button does.</summary>
    private void OnShowLocalApiPortFile(object? sender, RoutedEventArgs e)
    {
        var file = _localApiHost?.DiscoveryPath
            ?? Path.Combine(_platformServices.Paths.DataDirectory, "local-api.json");
        var folder = Path.GetDirectoryName(file);
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            _ = Process.Start(start);
        }
        catch { /* No file manager is installed; the path itself is already on screen. */ }
    }

    private void UpdateLocalApiConnectionUi()
    {
        var state = _localApiHost?.State ?? LocalApiHostState.Stopped;
        SetNamedText("SettingsLocalApiEnabledLabel", _localization.GetRequired(
            _viewModel.Settings.LocalApiEnabled
                ? "settings.localApi.toggle.enabled"
                : "settings.localApi.toggle.disabled"));
        SetNamedText("SettingsLocalApiStatus", state.IsRunning
            ? $"127.0.0.1:{state.Port}"
            : state.Failure?.Message ?? _localization.GetRequired("settings.localApi.status.idle"));
        // The reveal button says what the next press will do, not what the last one did.
        if (FindNamed<Button>("SettingsLocalApiReveal") is { } reveal)
            reveal.Content = _localization.GetRequired(
                _localApiTokenRevealed ? "settings.localApi.hide" : "settings.localApi.reveal");
        ApplyLocalApiTabSelection();
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
            if (PageSubtitle is not null) PageSubtitle.Text = PageSubtitleKey(pageId) is { } subtitleKey
                ? _localization[subtitleKey]
                : string.Empty;
            // The header band and the body share one centred column, so the title always sits
            // over the content it belongs to. Streaming is a form page and uses the narrow width.
            if (PageHeaderContent is not null) PageHeaderContent.MaxWidth = pageId == "streaming" ? 560 : 720;
            // Create Mode belongs on the header band beside the title, as on Windows.
            if (ModeNewButton is not null) ModeNewButton.IsVisible = pageId == "modes";
            if (pageId == "models") Dispatcher.UIThread.Post(UpdateModelLibrarySubtitle);
            if (pageId == "streaming") Dispatcher.UIThread.Post(UpdateStreamingUi);
            if (pageId == "sound") Dispatcher.UIThread.Post(UpdateSoundUi);
            if (pageId == "settings") Dispatcher.UIThread.Post(UpdateGeneralVersionText);
            // Windows draws no page heading on Home, on History or on any settings section: each
            // of those carries its own title, and History runs edge to edge.
            var ownsItsHeader = pageId is "home" or "history" || inSettings;
            if (PageHeader is not null) PageHeader.IsVisible = !ownsItsHeader;
            // Windows uses PagePadding = 24,24 on every page, and it applies that padding INSIDE
            // the scroll area: its 17px scroll bar sits hard against the window edge and the 24px
            // gap is between the bar and the card. The padding therefore belongs to
            // ScrollViewer.page, not to a margin out here. A margin would push the whole scroll
            // area, and its bar, 24px off the window edge and shorten it top and bottom.
            if (PageContent is not null) PageContent.Margin = new Thickness(0);
            if (pageId == "localapi") Dispatcher.UIThread.Post(UpdateLocalApiConnectionUi);
            if (pageId == "shortcuts") Dispatcher.UIThread.Post(UpdateShortcutsUi);
        }
        finally { _navigationSyncing = false; }
    }

    /// <summary>
    /// The base-catalog key for a page's subtitle, or null for a page Windows gives no subtitle.
    /// Windows draws one on exactly three pages — Modes (ModesPage.xaml:27), Vocabulary
    /// (VocabularyPage.xaml:59) and Streaming (StreamingSettingsPage.xaml:23). Model Library
    /// replaces the line with its live summary, and every other page carries its own heading with
    /// no subtitle at all. This used to read linux.page.subtitle.{pageId} for every page, which
    /// invented subtitles Windows does not have and kept three Linux-only copies of strings the
    /// base catalog already owns and translates.
    /// </summary>
    private static string? PageSubtitleKey(string pageId) => pageId switch
    {
        "modes" => "modes.header.subtitle",
        "vocabulary" => "vocabulary.description",
        "streaming" => "settings.streaming.enable.subtitle",
        _ => null,
    };

    /// <summary>
    /// The platform integration message used to sit in the sidebar, where a long warning
    /// pushed the rest of the column off screen. It now shows as a notice over the page, and
    /// only when something is actually wrong.
    /// </summary>
    private bool _platformStatusIsWarning;

    /// <summary>
    /// Set by the pixel-parity capture harness only. Under Xvfb there is no readable device in
    /// /dev/input, so the evdev probe always fails and the shortcut-conflict banner covers the top
    /// of Home, Streaming and Shortcuts, pushing their content down by up to 172px against the
    /// Windows reference. Windows carries the same banner from the same view model; it simply is
    /// not triggered on that box. This flag silences the two warning surfaces for a screenshot run
    /// so the pages underneath can be compared. It changes nothing else, and it is off unless
    /// HYPERWHISPER_UI_CAPTURE is set to 1.
    /// </summary>
    private static readonly bool SuppressPlatformWarningsForCapture = string.Equals(
        Environment.GetEnvironmentVariable("HYPERWHISPER_UI_CAPTURE"), "1", StringComparison.Ordinal);

    private void ShowPlatformStatus(string text, bool isWarning)
    {
        PlatformStatusText.Text = text;
        _platformStatusIsWarning = isWarning && !SuppressPlatformWarningsForCapture;
        UpdatePlatformNotice();
    }

    /// <summary>
    /// The one place that raises the shortcut-conflict banner, so the capture flag has a single
    /// gate rather than three call sites that can drift apart.
    /// </summary>
    private void ReportShortcutConflict(string message)
    {
        if (SuppressPlatformWarningsForCapture) return;
        _viewModel.Home.ReportShortcutConflict(message);
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

    /// <summary>Reveal or re-mask the stored Account Key, the way the Windows RedEye button does.</summary>
    private void OnToggleAccountKeyReveal(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Account is not { } account) return;
        account.IsAccountKeyRevealed = !account.IsAccountKeyRevealed;
        if (FindNamed<Button>("AccountKeyRevealButton") is { } button)
            ToolTip.SetTip(button, L(account.IsAccountKeyRevealed
                ? "settings.cloud.key.hide" : "settings.cloud.key.reveal"));
    }

    /// <summary>
    /// Windows swaps the copy glyph to a checkmark for 1500 ms and back. It is the only sign the
    /// key reached the clipboard: the field is masked, so nothing else on the row changes.
    /// A failed copy does NOT show the checkmark, exactly as on Windows.
    /// </summary>
    private async void OnCopyAccountKey(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Account?.StoredAccountKey is not { Length: > 0 } key) return;
        var copied = await _platformServices.TextInjection.CopyToClipboardAsync(key, _lifetime.Token);
        if (copied.IsFailure)
        {
            _viewModel.Account.Status.Failure(copied.Error!.Code, copied.Error.Message);
            return;
        }
        ShowCopiedGlyph("AccountKeyCopyGlyph");
    }

    /// <summary>
    /// Swap an icon button's glyph to a checkmark, then put the original back after 1500 ms.
    /// The token guards against a second click landing while the first restore is pending: the
    /// Windows version is a bare await Task.Delay and does stack, which is a bug worth not
    /// copying -- a rapid double click there restores the copy glyph while the second tick is
    /// still counting.
    /// </summary>
    private readonly Dictionary<string, int> _copiedGlyphTokens = new(StringComparer.Ordinal);

    private void ShowCopiedGlyph(string glyphName)
    {
        if (FindNamed<Avalonia.Controls.Shapes.Path>(glyphName) is not { } glyph) return;
        if (Application.Current?.FindResource("HwIconCheck") is not Geometry check) return;
        var original = glyph.Data;
        var token = _copiedGlyphTokens.TryGetValue(glyphName, out var current) ? current + 1 : 1;
        _copiedGlyphTokens[glyphName] = token;
        glyph.Data = check;
        DispatcherTimer.RunOnce(() =>
        {
            if (_copiedGlyphTokens.TryGetValue(glyphName, out var latest) && latest != token) return;
            if (FindNamed<Avalonia.Controls.Shapes.Path>(glyphName) is { } restore) restore.Data = original;
        }, TimeSpan.FromMilliseconds(1500));
    }
    private void OnGoToModes(object? sender, RoutedEventArgs e) => GoTo("modes");
    private void OnGoToVocabulary(object? sender, RoutedEventArgs e) => GoTo("vocabulary");
    private void OnGoToSound(object? sender, RoutedEventArgs e) => GoTo("sound");

    /// <summary>The sidebar Cloud call to action opens the Cloud account section, as on Windows.</summary>
    private void OnGoToCloud(object? sender, RoutedEventArgs e) => GoTo("account");

    private void OnDismissLowMicVolume(object? sender, RoutedEventArgs e) => _viewModel.Home.DismissLowMicVolume();

    /// <summary>
    /// The assumed typing speed, behind the gear on the "Saved this week" cell. Windows offers
    /// the same six values from a context menu on the same 14px button.
    /// </summary>
    private void OnTypingSpeedMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu();
        // Windows makes every entry IsCheckable and ticks the active one (HomePage.xaml.cs
        // TypingSpeedGear_Click), so the menu says which speed the saved figure is using. These
        // were plain MenuItems, so nothing marked the current value. Checkable is the same helper
        // the model filter menus use. Picking the checked one keeps it: the WPM is a choice, not
        // a toggle, and Windows leaves the value alone when you reselect it.
        foreach (var choice in _viewModel.Home.TypingSpeedChoices)
        {
            var speed = choice;
            menu.Items.Add(Checkable(
                $"{speed} WPM",
                speed == _viewModel.Home.TypingSpeedWordsPerMinute,
                _ => _viewModel.Home.TypingSpeedWordsPerMinute = speed));
        }
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.Open(button);
    }

    // The window owns its caption, so minimise, close and the drag are all handled here.
    private void OnMinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private static void Select(ListBox list, string tag)
    {
        foreach (var item in list.Items.OfType<ListBoxItem>())
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            { list.SelectedItem = item; return; }
    }

    /// <summary>
    /// Windows keeps the status bar's left column to a single TextBlock and folds everything
    /// into one string: "Ready - Press Ctrl+Alt to record". The message, the error code and the
    /// shortcut hint are composed here rather than laid out as three separate blocks.
    /// </summary>
    private void UpdateShortcutHint()
    {
        if (StatusText is null) return;
        var parts = new List<string>(3);
        var message = _viewModel.Status.Message;
        if (!string.IsNullOrWhiteSpace(message)) parts.Add(message);
        if (_viewModel.Status.HasError && !string.IsNullOrWhiteSpace(_viewModel.Status.ErrorCode))
            parts.Add(_viewModel.Status.ErrorCode!);
        var shortcut = _viewModel.Settings.ToggleShortcutDisplay;
        if (!string.IsNullOrEmpty(shortcut))
            parts.Add(string.Format(_localization["linux.status.record_hint"], shortcut));
        StatusText.Text = string.Join(" - ", parts);
        // Windows prints the same composed line a second time, under "No recordings yet" in the
        // History empty state. Nothing was assigning it, so the Linux empty state showed the
        // title alone where Windows shows two lines.
        _viewModel.History.HotkeyInstruction = StatusText.Text;
    }

    private void OnShellStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateShortcutHint();
        else Dispatcher.UIThread.Post(UpdateShortcutHint);

        if (e.PropertyName is nameof(UiStatus.ErrorCode) or nameof(UiStatus.Message)
            && _viewModel.Status.HasError)
            QueueErrorToast(() => (_viewModel.Status.ErrorCode, _viewModel.Status.Message));
    }

    /// <summary>
    /// The transcription workflow carries its own error pair rather than writing to the shell
    /// status, and it is the source of the failures Windows toasts most often: no microphone, no
    /// mode, a model that is not downloaded, a provider that rejected the key.
    /// </summary>
    private void OnRecordingStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel.Recording is not { } recording) return;
        if (e.PropertyName is nameof(recording.ErrorCode) or nameof(recording.Message)
            && recording.HasError)
            QueueErrorToast(() => (recording.ErrorCode, recording.Message));
        if (e.PropertyName is nameof(recording.IsImporting) or nameof(recording.ImportProgress))
            UpdateFileTranscriptionProgressWindow();
    }

    private LinuxFileTranscriptionProgressWindow? _fileProgress;

    /// <summary>
    /// Windows shows FileTranscriptionProgressWindow for the whole life of an import, modeless and
    /// always on top, so progress and Cancel stay reachable from any application. Linux drew them
    /// inline on Home, where a user who navigated away or hid the window could neither see the
    /// progress nor stop the job.
    /// </summary>
    private void UpdateFileTranscriptionProgressWindow()
    {
        if (Program.IsSmokeTest || _shuttingDown) return;
        if (_viewModel.Recording is not { } recording) return;
        try
        {
            if (!recording.IsImporting)
            {
                _fileProgress?.HideProgress();
                return;
            }
            if (_fileProgress is null)
            {
                _fileProgress = new LinuxFileTranscriptionProgressWindow();
                _fileProgress.CancelRequested += OnFileProgressCancelRequested;
            }
            if (!_fileProgress.IsVisible)
                _fileProgress.ShowForFile(Path.GetFileName(recording.FilePath) is { Length: > 0 } name
                    ? name
                    : recording.FilePath);
            _fileProgress.SetProgress(recording.ImportProgress);
        }
        catch { }
    }

    private void OnFileProgressCancelRequested(object? sender, EventArgs e)
    {
        if (_viewModel.Recording is not { } recording) return;
        if (recording.CancelCommand.CanExecute(null)) recording.CancelCommand.Execute(null);
    }

    private void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.DataContext is not HistoryViewModel history) return;
        // The list is grouped, so it also holds day headers. They cannot be clicked, but a Cast
        // would still throw the moment one arrived, and OfType costs nothing.
        history.UpdateSelection(list.SelectedItems?.OfType<Transcript>() ?? []);
    }

    /// <summary>
    /// Marks a day header's container so the style can take it out of hover and selection.
    /// Avalonia has no grouped ItemsSource, so the headers travel in the same flat list as the
    /// rows and the container is what tells them apart.
    /// </summary>
    private void OnHistoryContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem item) return;
        var isHeader = e.Container.DataContext is HistoryDateGroup;
        item.Classes.Set("groupHeader", isHeader);
    }

    /// <summary>
    /// Fills "Retry With…" from the modes the view model offers. A MenuItem builds its children
    /// in its own popup name scope, so a per-item Command cannot be bound from the page markup.
    /// </summary>
    private void OnHistoryContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // A name inside a DataTemplate generates no field, so the submenu is found on the menu
        // that raised the event rather than through a compiler-generated member.
        if (sender is not ContextMenu menu) return;
        var retryWith = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Name == "HistoryRetryMode");
        if (retryWith is null) return;
        var history = _viewModel.History;
        var items = new List<MenuItem>();
        foreach (var mode in history.AvailableRetryModes)
        {
            var captured = mode;
            var item = new MenuItem { Header = captured.Name };
            item.Click += (_, _) =>
            {
                history.SelectedRetryMode = captured;
                if (history.RetryCommand.CanExecute(null)) history.RetryCommand.Execute(null);
            };
            items.Add(item);
        }
        retryWith.ItemsSource = items;
        retryWith.IsEnabled = items.Count > 0 && history.CanRetry;
    }

    /// <summary>Windows swaps one outlined button's label rather than showing a check box.</summary>
    private void OnHistoryToggleRawTranscript(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.History is not { } history) return;
        history.ShowRawTranscript = !history.ShowRawTranscript;
    }

    /// <summary>
    /// The delete confirmation. Windows deletes straight from the footer button; Linux keeps a
    /// confirmation because the same button also decides whether the audio file goes with it.
    /// </summary>
    /// <summary>
    /// Windows confirms both shapes of a history delete with a YesNo MessageBox and always takes
    /// the audio file with it (ViewModels/HistoryViewModel.cs:651-655, :685-686). Linux confirmed
    /// only the single-row case, in a flyout with its own wording, and made the audio deletion an
    /// opt-in checkbox that defaulted to off.
    /// </summary>
    /// <summary>
    /// The page level Delete key. Windows binds DeleteCommand from all four routes (the two
    /// buttons, the context menu and Page.InputBindings) and still prompts every time, because the
    /// confirmation is inside <c>HistoryViewModel</c>. Linux owns the prompt in the view, so this
    /// key had to stop invoking the command directly or it would delete a transcript, and its
    /// audio file, with no prompt at all.
    /// </summary>
    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        // An already handled Delete belongs to whatever handled it - in practice the detail pane's
        // transcript TextBox, where Delete has to keep editing text.
        if (e.Handled || e.Key != Key.Delete) return;
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        if (_viewModel.History.SelectionCount <= 0) return;
        e.Handled = true;
        OnHistoryConfirmDelete(sender, e);
    }

    private async void OnHistoryConfirmDelete(object? sender, RoutedEventArgs e)
    {
        var history = _viewModel.History;
        var count = history.SelectionCount;
        var confirmed = count > 1
            ? await ConfirmWindow.ShowAsync(this,
                LF("transcripts.delete.multiple.title", count),
                LF("transcripts.delete.multiple.message", count))
            : await ConfirmWindow.ShowAsync(this,
                L("transcripts.delete.single.title"),
                L("transcripts.delete.single.message"));
        if (!confirmed) return;
        // Both Windows messages promise the audio file goes too, so the flag is not optional.
        history.DeleteAudio = true;
        if (history.DeleteCommand.CanExecute(null)) history.DeleteCommand.Execute(null);
    }

    /// <summary>
    /// Windows confirms a device deactivation
    /// (Views/Pages/Settings/CloudAccountSettingsPage.xaml.cs:435-436); Linux fired the command
    /// straight off the button binding.
    /// </summary>
    private async void OnDeactivateLicense(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Account is not { } account) return;
        var confirmed = await ConfirmWindow.ShowAsync(this,
            L("settings.license.deactivate.confirm.title"),
            L("settings.license.deactivate.confirm.message"));
        if (!confirmed) return;
        if (account.DeactivateCommand.CanExecute(null)) account.DeactivateCommand.Execute(null);
    }

    // =====================================================================================
    // GETTING STARTED CARDS
    //
    // Windows makes the whole card a toggle: the click marks the step complete (or un-marks it)
    // and ONLY navigates on the press that completes it. These are separate handlers from the
    // plain OnGoTo* ones because those are also wired to the shortcut-conflict banner and the
    // sidebar, where a click must navigate without ticking a checklist item.
    // =====================================================================================

    private void OnGettingStartedShortcuts(object? sender, RoutedEventArgs e)
        => ToggleGettingStartedStep("shortcuts", "shortcuts");

    private void OnGettingStartedModes(object? sender, RoutedEventArgs e)
        => ToggleGettingStartedStep("mode", "modes");

    private void OnGettingStartedVocabulary(object? sender, RoutedEventArgs e)
        => ToggleGettingStartedStep("vocabulary", "vocabulary");

    private void ToggleGettingStartedStep(string stepId, string? navigateTo)
    {
        // Windows navigates only when the click COMPLETED the step; un-ticking leaves you here.
        if (_viewModel.Home.ToggleGettingStartedStep(stepId) && navigateTo is not null)
            GoTo(navigateTo);
    }

    /// <summary>
    /// The "Start Recording" card. Windows leaves this one as a checklist item only -- it never
    /// navigates and never records, because on Windows you start a recording with the global
    /// hotkey. On Linux the global hotkey frequently cannot register at all (which is what the
    /// shortcut-conflict banner above this card is about), and this button is the ONLY way to
    /// start a recording from the UI. So it keeps its action and gains the completion toggle:
    /// the step is "Try your first transcription", and pressing it does exactly that.
    /// </summary>
    private async void OnStartRecording(object? sender, RoutedEventArgs e)
    {
        _viewModel.Home.ToggleGettingStartedStep("recording");
        await _interaction.StartRecordingAsync();
    }
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


    // ---- MODES ------------------------------------------------------------------------------
    // Windows opens the mode editor as a modal dialog from the header's Create Mode and from the
    // gear on a card. The page itself is only the card grid.

    private void OnCreateMode(object? sender, RoutedEventArgs e) => ShowModeEditor(isCreate: true);

    private void OnEditMode(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: Mode mode }) _viewModel.Modes.Selected = mode;
        ShowModeEditor(isCreate: false);
    }

    /// <summary>
    /// Windows opens a fresh window over a copy of the entity, so the active mode is untouched
    /// while the dialog is open and Cancel changes nothing. The snapshot is taken BEFORE
    /// NewCommand resets the fields, which is what lets Cancel put the previous mode's values —
    /// and, on the create path, the previously selected mode — back.
    /// </summary>
    private void ShowModeEditor(bool isCreate)
    {
        if (Program.IsSmokeTest) return;
        var modes = _viewModel.Modes;
        var snapshot = modes.CaptureEditorState();
        if (isCreate && modes.NewCommand.CanExecute(null)) modes.NewCommand.Execute(null);
        var editor = new ModeEditorWindow(modes, isCreate, snapshot);
        _ = editor.ShowDialog(this);
    }

    // ---- VOCABULARY -------------------------------------------------------------------------
    // One field, two inline chip actions, and a replacement panel that only appears once the
    // user asks for it — the shape the Windows page has.

    private void OnVocabularyAdd(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Vocabulary.AddCommand.CanExecute(null)) _viewModel.Vocabulary.AddCommand.Execute(null);
        SetVocabularyEditingItem(null);
        HideVocabularyReplacement();
    }

    private void OnVocabularyRevealReplacement(object? sender, RoutedEventArgs e) => ShowVocabularyReplacement();

    private void OnVocabularyCancelEdit(object? sender, RoutedEventArgs e)
    {
        _viewModel.Vocabulary.Selected = null;
        _viewModel.Vocabulary.Word = string.Empty;
        _viewModel.Vocabulary.Replacement = string.Empty;
        SetVocabularyEditingItem(null);
        HideVocabularyReplacement();
    }

    private void OnVocabularyWordKeyDown(object? sender, KeyEventArgs e)
    {
        // Windows closes the replacement field on Esc from either box
        // (VocabularyPage.xaml.cs WordBox_PreviewKeyDown / ReplacementBox_PreviewKeyDown); the
        // Cancel button was the only way out of it here.
        if (e.Key == Key.Escape) { OnVocabularyCancelEdit(sender, e); e.Handled = true; return; }
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) ShowVocabularyReplacement();
        else OnVocabularyAdd(sender, e);
        e.Handled = true;
    }

    private void OnVocabularyReplacementKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnVocabularyCancelEdit(sender, e); e.Handled = true; return; }
        if ((e.Key != Key.Enter && e.Key != Key.Return) || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        OnVocabularyAdd(sender, e);
        e.Handled = true;
    }

    private void OnVocabularyEditRow(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: VocabularyItem item }) return;
        _viewModel.Vocabulary.Selected = item;
        if (!string.IsNullOrEmpty(item.Replacement)) ShowVocabularyReplacement();
        SetVocabularyCancelVisible(true);
        // Windows loads the row into the pill, takes it out of the table so the entry is not
        // shown twice, relabels the add chip to "Update", and scrolls the pill into view with
        // focus in it (VocabularyPage.xaml.cs EditWord_Click). Only the Cancel button appeared
        // here, so the pill could be off screen and the chip still read "Add word".
        SetVocabularyEditingItem(item);
        FindNamed<StackPanel>("VocabularyInputSection")?.BringIntoView();
        FindNamed<TextBox>("VocabularyWordInput")?.Focus();
    }

    /// <summary>
    /// The row currently loaded into the input pill, which the table hides while it is edited.
    /// </summary>
    private VocabularyItem? _vocabularyEditingItem;

    private void SetVocabularyEditingItem(VocabularyItem? item)
    {
        _vocabularyEditingItem = item;
        if (FindNamed<TextBlock>("VocabularyAddChipLabel") is { } label)
            label.Text = _localization[item is null ? "vocabulary.action.addWord" : "common.update"];
        ApplyVocabularyEditingRowVisibility();
    }

    private void ApplyVocabularyEditingRowVisibility()
    {
        if (FindNamed<ListBox>("VocabularyList") is not { } list) return;
        var items = _viewModel.Vocabulary.Items;
        for (var i = 0; i < items.Count; i++)
            if (list.ContainerFromIndex(i) is { } container)
                container.IsVisible = !ReferenceEquals(items[i], _vocabularyEditingItem);
    }

    // Containers are recycled as the list scrolls and rebuilt after every refresh, so the hidden
    // row has to be reapplied per container rather than set once.
    private void OnVocabularyContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var items = _viewModel.Vocabulary.Items;
        if (e.Index < 0 || e.Index >= items.Count) return;
        e.Container.IsVisible = !ReferenceEquals(items[e.Index], _vocabularyEditingItem);
    }

    private void OnVocabularyDeleteRow(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: VocabularyItem item }) return;
        if (_viewModel.Vocabulary.DeleteCommand.CanExecute(item)) _viewModel.Vocabulary.DeleteCommand.Execute(item);
    }

    private void ShowVocabularyReplacement()
    {
        if (FindNamed<Border>("VocabularyReplacementPanel") is { } panel) panel.IsVisible = true;
        SetVocabularyCancelVisible(true);
        FindNamed<TextBox>("VocabularyReplacementInput")?.Focus();
    }

    private void HideVocabularyReplacement()
    {
        if (FindNamed<Border>("VocabularyReplacementPanel") is { } panel) panel.IsVisible = false;
        SetVocabularyCancelVisible(false);
    }

    private void SetVocabularyCancelVisible(bool visible)
    {
        if (FindNamed<Button>("VocabularyCancelButton") is { } cancel) cancel.IsVisible = visible;
    }

    // ---- MODEL LIBRARY ----------------------------------------------------------------------
    // Sorting lives on the column headers and filtering lives in two menus, as on Windows, so
    // there is no sort combo box and no filter combo row.

    private void OnModelSortByName(object? sender, RoutedEventArgs e) => _viewModel.Models?.ToggleSort(ModelLibrarySort.Name);
    private void OnModelSortByType(object? sender, RoutedEventArgs e) => _viewModel.Models?.ToggleSort(ModelLibrarySort.Type);
    private void OnModelSortByRating(object? sender, RoutedEventArgs e) => _viewModel.Models?.ToggleSort(ModelLibrarySort.Readiness);
    private void OnModelSortByLocation(object? sender, RoutedEventArgs e) => _viewModel.Models?.ToggleSort(ModelLibrarySort.Location);

    private void OnModelClearWorkloadFilter(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Models is { } models) models.WorkloadFilter = null;
    }

    private void OnModelClearDeploymentFilter(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Models is { } models) models.DeploymentFilter = null;
    }

    private void OnModelClearVocabularyFilter(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Models is { } models) models.VocabularyFilter = false;
    }

    private void OnModelClearCloudFilter(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Models is { } models) models.CloudAvailableFilter = false;
    }

    private void OnModelClearLanguageFilter(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Models is { } models) models.LanguageFilter = null;
    }

    private void OnModelClearFilters(object? sender, RoutedEventArgs e) => _viewModel.Models?.ClearFilters();

    /// <summary>
    /// Windows puts the live count under the page title rather than in the table, and it tracks
    /// the filters, so it is refreshed here instead of bound to the static navigation subtitle.
    /// </summary>
    private void UpdateModelLibrarySubtitle()
    {
        if (_currentPageId != "models" || _viewModel.Models is not { } models) return;
        if (PageSubtitle is not null) PageSubtitle.Text = models.LibrarySummary;
    }

    /// <summary>The funnel menu: checkable Type, Location and Features groups, as on Windows.</summary>
    private void OnModelFilterMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _viewModel.Models is not { } models) return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = L("linux.models.filter.type"), IsEnabled = false });
        menu.Items.Add(Checkable(L("linux.models.filter.type.voice"),
            models.WorkloadFilter == ModelWorkload.Voice,
            @checked => models.WorkloadFilter = @checked ? ModelWorkload.Voice : null));
        menu.Items.Add(Checkable(L("linux.models.filter.type.language"),
            models.WorkloadFilter == ModelWorkload.Text,
            @checked => models.WorkloadFilter = @checked ? ModelWorkload.Text : null));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = L("linux.models.filter.location"), IsEnabled = false });
        menu.Items.Add(Checkable(L("linux.models.filter.location.cloud"),
            models.DeploymentFilter == ModelDeployment.Cloud,
            @checked => models.DeploymentFilter = @checked ? ModelDeployment.Cloud : null));
        menu.Items.Add(Checkable(L("linux.models.filter.location.offline"),
            models.DeploymentFilter == ModelDeployment.Local,
            @checked => models.DeploymentFilter = @checked ? ModelDeployment.Local : null));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = L("linux.models.filter.features"), IsEnabled = false });
        menu.Items.Add(Checkable(L("linux.models.filter.features.vocabulary"),
            models.VocabularyFilter, @checked => models.VocabularyFilter = @checked));
        menu.Items.Add(Checkable(L("linux.models.filter.features.cloud"),
            models.CloudAvailableFilter, @checked => models.CloudAvailableFilter = @checked));
        Open(menu, button);
    }

    private void OnModelProviderMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _viewModel.Models is not { } models) return;
        var menu = new ContextMenu();
        foreach (var option in models.ProviderOptions)
        {
            var value = option;
            menu.Items.Add(Checkable(value, string.Equals(models.ProviderFilter, value, StringComparison.Ordinal),
                _ => models.ProviderFilter = value));
        }
        Open(menu, button);
    }

    /// <summary>
    /// The language menu is built from the codes the visible rows actually declare, so it never
    /// offers a language no model in the library supports.
    /// </summary>
    private void OnModelLanguageMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _viewModel.Models is not { } models) return;
        var menu = new ContextMenu();
        menu.Items.Add(Checkable(L("linux.models.language.any"), !models.HasLanguageFilter,
            _ => models.LanguageFilter = null));
        var codes = models.Items
            .SelectMany(row => row.Capability.SupportedLanguages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
        if (codes.Count > 0) menu.Items.Add(new Separator());
        foreach (var code in codes)
        {
            var value = code;
            menu.Items.Add(Checkable(value,
                string.Equals(models.LanguageFilter, value, StringComparison.OrdinalIgnoreCase),
                _ => models.LanguageFilter = value));
        }
        Open(menu, button);
    }

    /// <summary>
    /// One row action, as on Windows: the button downloads, deletes or opens the credential page
    /// depending on what the row actually needs. Right-clicking it offers the two extras Windows
    /// keeps in a context menu.
    /// </summary>
    private void OnModelRowPrimaryAction(object? sender, RoutedEventArgs e)
    {
        if (SelectModelRow(sender) is not { } row || _viewModel.Models is not { } models) return;
        if (row.CanDownload) { if (models.DownloadCommand.CanExecute(null)) models.DownloadCommand.Execute(null); return; }
        if (row.NeedsCredential) { OnModelCredentialAction(sender, e); return; }
        if (models.RefreshReadinessCommand.CanExecute(null)) models.RefreshReadinessCommand.Execute(null);
    }

    private void OnModelRowCancel(object? sender, RoutedEventArgs e)
    {
        if (SelectModelRow(sender) is null || _viewModel.Models is not { } models) return;
        if (models.CancelCommand.CanExecute(null)) models.CancelCommand.Execute(null);
    }

    /// <summary>
    /// Windows opens its 500x560 CustomEndpointWindow here. Linux used to navigate to the Modes
    /// page and open the MODE EDITOR, which is a different screen answering a different question.
    /// </summary>
    private async void OnModelAddEndpoint(object? sender, RoutedEventArgs e)
    {
        if (Program.IsSmokeTest) return;
        await new CustomEndpointWindow(SaveCustomEndpointAsync).ShowDialog(this);
    }

    /// <summary>
    /// Stores an endpoint from the Add Endpoint modal, in the same shape the mode editor writes:
    /// a PortableCustomPostProcessingEndpoint under "customEndpoints", with any API key going to
    /// the credential store under CustomEndpoint_{id}.
    /// </summary>
    private Task<bool> SaveCustomEndpointAsync(string name, string url, string model, string apiKey)
    {
        var id = Guid.NewGuid();
        var endpoints = (_settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? []).ToList();
        endpoints.Add(new PortableCustomPostProcessingEndpoint(id, name, url, model));
        _settings.Set("customEndpoints", endpoints);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
            try
            {
                var written = _platformServices.CredentialStore.Write("HyperWhisper", $"CustomEndpoint_{id:D}", bytes);
                if (written.IsFailure) return Task.FromResult(false);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        // The settings write is what makes the endpoint real; a failure here must not leave the
        // credential behind pointing at an endpoint that was never stored.
        if (_settings.Save().IsFailure)
        {
            _ = _platformServices.CredentialStore.Delete("HyperWhisper", $"CustomEndpoint_{id:D}");
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    private static MenuItem Checkable(string header, bool isChecked, Action<bool> apply)
    {
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
        item.Click += (_, _) => apply(!isChecked);
        return item;
    }

    private static void Open(ContextMenu menu, Control target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.Open(target);
    }

    // ---- STREAMING --------------------------------------------------------------------------

    private void OnStreamingProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_streamingSyncing || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string id } }) return;
        _viewModel.Settings.StreamingProvider = id;
        UpdateStreamingUi();
    }

    private void OnStreamingDeepgramModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_streamingSyncing || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string id } }) return;
        _viewModel.Settings.StreamingModel = id;
    }

    private void OnFocusStreamingShortcut(object? sender, RoutedEventArgs e) => GoTo("shortcuts");

    /// <summary>
    /// The Windows media-control picker writes its Tag straight onto the setting. "duck" has no
    /// Windows item, so a profile already carrying it lands on the first row without being
    /// rewritten until the user actually picks something.
    /// </summary>
    private void OnAudioEnvironmentPolicyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_soundSyncing || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string policy } }) return;
        _viewModel.Settings.AudioEnvironmentPolicy = policy;
    }

    private bool _soundSyncing;

    private void UpdateSoundUi()
    {
        _soundSyncing = true;
        try
        {
            var box = FindNamed<ComboBox>("SettingsAudioEnvironmentPolicy");
            if (box is null) return;
            SelectByTag(box, _viewModel.Settings.AudioEnvironmentPolicy);
            if (box.SelectedItem is null) box.SelectedIndex = 0;
        }
        finally { _soundSyncing = false; }
    }

    private bool _streamingSyncing;

    /// <summary>
    /// Sync the two tag-driven pickers to the stored values, then write the provider status
    /// sentence and the vocabulary warning the Windows page shows under the Engine card.
    /// </summary>
    private void UpdateStreamingUi()
    {
        _streamingSyncing = true;
        try
        {
            var settings = _viewModel.Settings;
            SelectByTag(FindNamed<ComboBox>("StreamingProviderChoice"), settings.StreamingProvider);
            SelectByTag(FindNamed<ComboBox>("StreamingModelInput"), settings.StreamingModel);

            if (FindNamed<TextBlock>("StreamingProviderStatusText") is { } status)
                status.Text = ProviderStatusSentence(settings.StreamingProvider);

            // Vocabulary boosting is dropped while the language is auto-detected, which is the
            // one warning the Linux head can answer without a provider session.
            var autoLanguage = string.Equals(settings.StreamingLanguage, "auto", StringComparison.OrdinalIgnoreCase);
            var hasVocabulary = _viewModel.Vocabulary.Items.Count > 0;
            if (FindNamed<Border>("StreamingVocabularyWarningPanel") is { } warning)
                warning.IsVisible = hasVocabulary && autoLanguage;
            if (FindNamed<TextBlock>("StreamingVocabularyWarningText") is { } warningText)
                warningText.Text = L("settings.streaming.warning.vocabularyAutoDetect");

            var conflict = _viewModel.Home.HasShortcutConflicts;
            if (FindNamed<Border>("StreamingConflictBanner") is { } banner) banner.IsVisible = conflict;
            if (FindNamed<TextBlock>("StreamingConflictMessage") is { } message)
                message.Text = _viewModel.Home.ShortcutConflictMessage;
        }
        finally { _streamingSyncing = false; }
    }

    private string ProviderStatusSentence(string provider)
    {
        var configured = HasStreamingCredential(provider);
        return provider.ToLowerInvariant() switch
        {
            "hyperwhisper" => L("settings.streaming.providerStatus.hyperwhisperCloud"),
            "deepgram" => L(configured ? "settings.streaming.providerStatus.deepgram.configured" : "settings.streaming.providerStatus.deepgram.missingKey"),
            "elevenlabs" => L(configured ? "settings.streaming.providerStatus.elevenLabs.configured" : "settings.streaming.providerStatus.elevenLabs.missingKey"),
            "openai" => L(configured ? "settings.streaming.providerStatus.openAI.configured" : "settings.streaming.providerStatus.openAI.missingKey"),
            "grok" => L(configured ? "settings.streaming.providerStatus.xai.configured" : "settings.streaming.providerStatus.xai.missingKey"),
            "geminitranscribe" => L(configured ? "settings.streaming.providerStatus.geminiTranscribe.configured" : "settings.streaming.providerStatus.geminiTranscribe.missingKey"),
            _ => L("settings.streaming.providerStatus.hyperwhisperCloud"),
        };
    }

    /// <summary>
    /// Whether a key for this streaming provider is already stored. The credential list is the
    /// one the Model Library and the credentials page both read, so no second store is consulted.
    /// </summary>
    private bool HasStreamingCredential(string provider)
        => _viewModel.Credentials?.Items.Any(account =>
               account.IsPresent
               && account.Account.Contains(provider, StringComparison.OrdinalIgnoreCase)) == true;

    private static void SelectByTag(ComboBox? box, string? tag)
    {
        if (box is null || tag is null) return;
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = item; return; }
    }

    private T? FindNamed<T>(string name) where T : Control
        => this.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

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
                // The mode editor is a dialog now, so the page itself is only the card grid.
                ["modes"] = "ModeGrid",
                ["history"] = "HistoryDeleteButton",
                ["vocabulary"] = "VocabularyAddButton",
                // The Model Library lost its bottom detail strip: the row actions moved onto the
                // rows, so the search pill is the control that is always on this page.
                ["models"] = "ModelSearch",
                ["backup"] = "BackupExportButton",
                ["account"] = "AccountActivateButton",
                ["credentials"] = "CredentialSaveButton",
                ["settings"] = "SettingsLaunchMinimized"
            };
            foreach (var page in expectedControls)
            {
                _viewModel.Navigate(page.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                // Windows shows a page-independent "Ready" here, so the message no longer names
                // the page; the page identity is asserted by PageTitle and the visible control.
                if (!HasVisibleControl(page.Value)
                    || string.IsNullOrWhiteSpace(_viewModel.PageTitle)
                    || !string.Equals(_viewModel.Status.Message, "Ready", StringComparison.Ordinal))
                    return 3;
            }

            // The typing speed moved from a combo row under the page into the gear on the stats
            // bar, which is the only control that still reaches that setting from Home.
            _viewModel.Navigate("home");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("HomeTypingSpeed")) return 17;

            // The recording and file-import controls have no Windows equivalent anywhere, so they
            // live at the END of Home, below Recent Updates. They were on the Modes page, which
            // Windows ends at the mode grid, so keeping them there changed the shape of a page
            // both apps have. They bind past Home's own context to the shell.
            // The audio-input combo is deliberately NOT checked: it hides itself when the machine
            // reports no capture device, which is the normal state on a headless test box.
            if (!HasVisibleControl("HomeStopRecordingButton")
                || !HasVisibleControl("HomeCancelRecordingButton")
                || !HasVisibleControl("HomeAudioFileInput")
                || !HasVisibleControl("HomeTranscribeFileButton")) return 9;

            _viewModel.Navigate("modes");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // The ~25 mode fields moved into the editor dialog, so the coverage moves with them:
            // the dialog is built (never shown) and its own tree is checked instead.
            var modes = _viewModel.Modes;
            var editorSnapshot = modes.CaptureEditorState();
            var editor = new ModeEditorWindow(modes, false, editorSnapshot);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // Every field must exist in the dialog's tree. Presence, not visibility: the editor
            // now follows the Windows reveal rules, so most of these are hidden for any one
            // source. The reveal rules themselves are exercised below.
            string[] modeEditorControls =
            [
                "ModeNameInput", "ModeLanguageInput", "ModeLocalEngine",
                "ModeTranscriptionModel", "ModeCloudTranscriptionModel", "ModeCloudProvider",
                "ModeCloudAccuracyTier", "ModeCloudDomain", "ModeGeminiPrompt", "ModeCustomVocabulary",
                "ModePreset", "ModePostProcessingEnabled", "ModePostProcessingProvider",
                "ModePostProcessingModel", "ModeHyperWhisperCloudModel", "ModeLocalPostProcessingModel",
                "ModeCustomEndpointName", "ModeCustomEndpointUrl", "ModeCustomEndpointApiKey",
                "ModeCustomEndpointModel", "ModeCustomInstructions", "ModeUserSystemPrompt",
                "ModeUserPromptEnabled", "ModeClearUserPrompt",
                "ModePunctuation", "ModeCapitalization", "ModeProfanityFilter",
                "ModeRemoveTrailingPeriod", "ModeEnglishSpelling", "ModeEnableScreenOcr",
                "ModeSourceOnDevice", "ModeSourceHwCloud", "ModeSourceYourProvider",
                "ModePpSourceOnDevice", "ModePpSourceHwCloud", "ModePpSourceYourProvider",
                "ModeSaveButton", "ModeCancelButton", "ModeDeleteButton",
            ];
            foreach (var control in modeEditorControls)
                if (!HasControl(control, editor))
                {
                    Console.Error.WriteLine($"Smoke: {control} is missing from the mode editor.");
                    editor.Close();
                    return 9;
                }

            // The three transcription sources reveal three different sets of controls, which is
            // the single biggest behaviour the Linux editor was missing. Assert each one.
            if (await ModeEditorRevealFailureAsync(editor, () => modes.IsOnDeviceSource = true,
                    ["ModeLocalEngine", "ModeTranscriptionModel"],
                    ["ModeCloudProvider", "ModeCloudAccuracyTier", "ModeCloudTranscriptionModel"])
                || await ModeEditorRevealFailureAsync(editor, () => modes.IsHwCloudSource = true,
                    ["ModeCloudAccuracyTier"],
                    ["ModeLocalEngine", "ModeCloudProvider", "ModeCloudTranscriptionModel"])
                || await ModeEditorRevealFailureAsync(editor, () => modes.IsYourProviderSource = true,
                    ["ModeCloudProvider", "ModeCloudTranscriptionModel"],
                    ["ModeLocalEngine", "ModeCloudAccuracyTier"]))
            {
                editor.Close();
                return 9;
            }

            // Post-processing off hides the whole settings block and both punctuation options,
            // exactly as Windows does (ModeEditorWindow.xaml.cs:1354-1355, :1836).
            if (await ModeEditorRevealFailureAsync(editor, () => modes.PostProcessingEnabled = false,
                    ["ModePostProcessingEnabled"],
                    ["ModePreset", "ModePunctuation", "ModeCapitalization", "ModeUserPromptEnabled"])
                || await ModeEditorRevealFailureAsync(editor, () => { modes.PostProcessingEnabled = true; modes.Preset = "custom"; },
                    ["ModePreset", "ModeCustomInstructions", "ModePunctuation"],
                    ["ModeUserSystemPrompt"]))
            {
                editor.Close();
                return 9;
            }

            // Cancel must put every field back, which is what the editor's snapshot buys.
            modes.RestoreEditorState(editorSnapshot);
            editor.Close();
            _viewModel.Navigate("history");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            // Two classes of control on this page now, because it follows the Windows page.
            // These are always on screen once a row is picked.
            foreach (var control in new[]
                     { "HistorySearchInput", "HistoryPeriod", "HistoryList", "HistoryDetailText",
                       "HistoryCopyButton", "HistoryDeleteButton" })
                if (!HasVisibleControl(control))
                {
                    Console.Error.WriteLine($"Smoke: {control} is not visible on the history page.");
                    return 11;
                }
            // These are deliberately conditional, exactly as Windows draws them: the transport
            // is hidden when the audio cannot play, Retry only appears on a retryable row, the
            // raw toggle only on a post-processed one, and the two menus are popups. Presence in
            // the tree is what the check can assert; visibility is the view models job.
            foreach (var control in new[]
                     { "HistoryPlayButton", "HistoryPlaybackSlider", "HistoryRetryButton",
                       "HistoryShowRawTranscript" })
                if (!HasControl(control))
                {
                    Console.Error.WriteLine($"Smoke: {control} is missing from the history page.");
                    return 11;
                }
            // The Retry With… submenu hangs off the list ContextMenu, which is its own popup tree
            // and never a logical descendant of the window, so it is reached through its owner.
            if (FindNamed<ListBox>("HistoryList")?.ContextMenu is not { } historyMenu
                || !historyMenu.GetLogicalDescendants().OfType<Control>()
                    .Any(control => control.Name == "HistoryRetryMode"))
            {
                Console.Error.WriteLine("Smoke: HistoryRetryMode is missing from the history context menu.");
                return 11;
            }
            // The Delete flyout and its opt-in audio checkbox are gone: Windows raises a YesNo
            // MessageBox whose text already promises the audio file goes too, so both the single
            // and the multi-row Delete now run OnHistoryConfirmDelete. What has to exist is the
            // multi-selection Delete, which used to fire its command with no confirmation at all.
            if (!HasControl("HistoryDeleteSelectedButton"))
            {
                Console.Error.WriteLine("Smoke: HistoryDeleteSelectedButton is missing.");
                return 11;
            }
            _viewModel.Navigate("vocabulary");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("VocabularyWordInput") || !HasVisibleControl("VocabularyReplacementInput")
                || !HasVisibleControl("VocabularyList")) return 12;
            // Windows has no import/export row on the Vocabulary page and no vocabulary TSV
            // transfer anywhere: vocabulary travels inside the backup archive, selected by the
            // Vocabulary checkbox on each of the two Backup cards. Those are what is asserted.
            _viewModel.Navigate("backup");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("BackupExportVocabulary") || !HasVisibleControl("BackupExportButton")
                || !HasVisibleControl("BackupChooseImportButton")) return 12;
            // Settings is ten sections now, as on Windows, so each one is checked on its own page.
            var settingsSections = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // Windows closes General with the auto-update row, a Need help? card and the app
                // version, and has no language field on the page at all: that moved to Text Output.
                ["settings"] = ["SettingsAutostart", "SettingsLaunchMinimized", "SettingsMinimizeToTray",
                    "SettingsShowRecordingWindow", "SettingsEnableErrorLogging", "SettingsShareSpeedData",
                    "SettingsSpeedComparisonLink", "SettingsAutoUpdate", "SettingsSupportButton",
                    "SettingsVersionText"],
                // Three Windows cards and nothing after them: the sound-effect volume slider and
                // the silence-trim switch are gone with the Linux-only card that held them.
                ["sound"] = ["SettingsSoundEffects", "SettingsAudioEnvironmentPolicy", "SettingsBoostMicrophone",
                    "SettingsKeepMicrophoneWarm"],
                // Likewise the keep-app-owned-audio switch and the usage readout.
                ["storage"] = ["SettingsRecordingsDirectory", "SettingsChooseRecordingsDirectory",
                    "SettingsStoreAsM4A", "SettingsAutoDeleteEnabled", "SettingsStorageOpenFolder"],
                // Likewise the word-timestamp switch and the default-language box.
                ["output"] = ["SettingsPasteResultText", "SettingsRemoveFillerWords", "SettingsAutocapitalizeInsert",
                    "SettingsRestoreClipboard", "SettingsHideClipboardHistory"],
                ["localapi"] = ["SettingsLocalApiEnabled", "SettingsLocalApiStatus"],
                ["shortcuts"] = ["SettingsToggleKey", "SettingsCancelKey", "SettingsChangeModeKey",
                    "SettingsStreamingShortcutKey", "SettingsPushToTalkMode", "SettingsResetShortcuts"],
                ["appearance"] = ["SettingsThemeMode"],
                // The local Whisper and LLM runtime pickers moved off Appearance, which Windows
                // draws as one Theme card, onto the end of the Model Library page.
                ["models"] = ["SettingsLocalWhisperBackend", "SettingsLocalWhisperCpuFallback",
                    "SettingsLocalWhisperRuntimeStatus", "SettingsLocalLlmBackend", "SettingsLocalLlmCpuFallback"],
                ["streaming"] = ["StreamingEnabledToggle", "StreamingProviderChoice", "StreamingLanguageInput"]
            };
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
            // These are deliberately conditional, exactly as Windows draws them. Auto-delete hides
            // its retention field and Delete Now until the toggle is on; the clipboard restore
            // delay follows the restore switch; and the whole Local API body — the tab strip, the
            // connection rows and both snippets — stays collapsed until the server is enabled.
            // The smoke profile leaves all three off, so presence in the tree is what can be
            // asserted here. Visibility is the page's own job, checked by the bindings above.
            var conditionalSections = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["storage"] = ["SettingsAutoDeleteDays", "SettingsStorageDeleteNow"],
                ["output"] = ["SettingsClipboardRestoreDelay"],
                ["localapi"] = ["SettingsLocalApiPort", "SettingsLocalApiToken", "SettingsLocalApiDiscoveryPath",
                    "SettingsLocalApiMcpSnippet", "SettingsLocalApiCurlSnippet"],
            };
            foreach (var section in conditionalSections)
            {
                _viewModel.Navigate(section.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var control in section.Value)
                    if (!HasControl(control))
                    {
                        Console.Error.WriteLine($"Smoke: {control} is missing from the {section.Key} page.");
                        return 10;
                    }
            }


            _viewModel.Navigate("account");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            // Windows shows ONE of two sibling views here. The smoke profile has no key, so the
            // unlicensed view is the one on screen: activation field, Activate, Get Credits. The
            // licensed controls (refresh, Manage Billing, Deactivate) are correctly not visible.
            if (!HasVisibleControl("AccountKeyInput")
                || !HasVisibleControl("AccountActivateButton")
                || !HasVisibleControl("AccountPurchaseButton")) return 16;

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

    private bool HasVisibleControl(string name) => HasVisibleControl(name, this);

    /// <summary>Present in the tree, whether or not its own binding is showing it right now.</summary>
    private bool HasControl(string name) => HasControl(name, this);

    private static bool HasControl(string name, Visual root)
        => root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name);

    /// <summary>
    /// Applies one mode-editor change and checks the reveal rules it is supposed to drive.
    /// Returns true when the check FAILED, so the caller can bail with a smoke exit code.
    /// </summary>
    private static async Task<bool> ModeEditorRevealFailureAsync(
        Visual editor, Action change, string[] expectVisible, string[] expectHidden)
    {
        change();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        foreach (var control in expectVisible)
            if (!IsRevealed(control, editor))
            {
                Console.Error.WriteLine($"Smoke: {control} should be revealed in the mode editor.");
                return true;
            }
        foreach (var control in expectHidden)
            if (IsRevealed(control, editor))
            {
                Console.Error.WriteLine($"Smoke: {control} should be hidden in the mode editor.");
                return true;
            }
        return false;
    }

    /// <summary>
    /// Effective visibility inside a dialog that has not been shown. Avalonia's own
    /// IsEffectivelyVisible is false for every control while the window is closed, and a plain
    /// IsVisible is true even when the panel wrapping the control is hidden — which is exactly
    /// the case the reveal rules create. So this walks the ancestors itself and stops at the
    /// window, whose own IsVisible says nothing about the layout under test.
    /// </summary>
    private static bool IsRevealed(string name, Visual root)
    {
        var control = root.GetLogicalDescendants().OfType<Control>().FirstOrDefault(item => item.Name == name);
        for (var node = control; node is not null && !ReferenceEquals(node, root); node = node.Parent as Control)
            if (!node.IsVisible) return false;
        return control is not null;
    }

    private static bool HasVisibleControl(string name, Visual root)
        => root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name && control.IsVisible);

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
