// MAIN VIEW MODEL
// Central orchestrator for the HyperWhisper Windows application.
// Manages recording, transcription (local + cloud), and UI state.
//
// PROVIDER ROUTING:
// - Local modes: Use TranscriptionService (WhisperNet/GPU)
// - Cloud modes: Use OpenAIWhisperService (or other cloud providers)
//
// The mode.ProviderType field determines which provider is used:
// - "local": GPU-accelerated WhisperNet transcription
// - "cloud": Cloud API transcription (OpenAI, etc.)

using System.Diagnostics;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Services.Streaming;
using HyperWhisper.Services.Transcription;
using HyperWhisper.Utilities;
using HyperWhisper.ViewModels.Base;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace HyperWhisper.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AudioDeviceService _deviceService;
    private readonly AudioRecorderService _recorderService;
    private readonly WhisperModelService _modelService;
    private readonly TranscriptionService _transcriptionService;
    private readonly ParakeetModelService _parakeetModelService;
    private readonly ParakeetTranscriptionService _parakeetTranscriptionService;
    private readonly LocalLlmModelService _localLlmModelService;
    private readonly TranscriptionOrchestrator _transcriptionOrchestrator;
    private readonly VocabularyService _vocabularyService;
    private readonly SettingsService _settingsService;
    private readonly StorageService _storageService;
    private readonly KeyboardShortcutService _shortcutService;
    private readonly PushToTalkMonitor _pushToTalkMonitor;
    private SmartPasteService? _pasteService;
    private StreamingAudioCapture? _streamingAudioCapture;
    private StreamingTranscriptionClient? _streamingClient;
    private System.Timers.Timer? _durationTimer;
    private CancellationTokenSource? _activeTranscriptionCts;
    private bool _toggleShortcutHeld;
    private bool _isStreamingSession;
    private bool _isStreamingStarting;
    private bool _isStoppingStreaming;
    private bool _isStoppingRecording;
    private bool _streamingStartCancelledByUser;
    private bool _streamingPastedFinalSegment;
    private bool _streamingTargetLost;
    private bool _recordingDurationLimitReached;
    private bool _streamingDurationLimitReached;
    private bool _isCancellingRecording;
    private SmartPasteResult _streamingLastPasteResult = SmartPasteResult.Failed;
    private string _streamingPendingFinalFallbackText = string.Empty;
    private string? _streamingFailureMessage;
    private CancellationTokenSource? _streamingStartCts;
    private int _streamingSessionGeneration;
    private Services.ApplicationContext? _capturedApplicationContext;
    private Mode? _activeRecordingMode;
    private AudioEnvironmentService.AudioEnvironmentState? _audioEnvironmentState;

    // DEBOUNCE MECHANISM: Track last toggle time to prevent rapid shortcut presses
    // Problem: Users pressing shortcuts rapidly could trigger multiple recording
    // sessions before the first one initializes, causing immediate stop
    // Solution: Ignore toggle requests that come within 1 second of the previous one
    // (1 second is used because recordings shorter than that are unlikely to be useful)
    private DateTime? _lastToggleTime;
    private const int DebounceIntervalMs = 1000;
    private static readonly TimeSpan MaxRecordingDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StreamingConnectionTimeout = TimeSpan.FromSeconds(15);

    // Event handler delegates stored for proper unsubscription in Cleanup()
    private readonly Action<float> _audioLevelHandler;
    private readonly EventHandler<Mode> _modeChangedHandler;
    private readonly EventHandler<Mode> _modeSelectedHandler;
    private readonly EventHandler<ErrorToastEventArgs> _orchestratorWarningHandler;

    public enum NavigationPage { Home, Modes, Vocabulary, Streaming, ModelLibrary, History, Settings }

    [ObservableProperty] private NavigationPage _currentPage = NavigationPage.Home;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private bool _isModelLoading;
    [ObservableProperty] private bool _isModelLoaded;
    [ObservableProperty] private TimeSpan _recordingDuration;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Mode? _currentMode;
    [ObservableProperty] private string _modelStatus = "";
    [ObservableProperty] private bool _hasLocalPostProcessingStatus;
    [ObservableProperty] private string _localPostProcessingStatus = "";
    [ObservableProperty] private string _hotkeyText = "Ctrl+Alt";
    [ObservableProperty] private List<AudioDeviceService.AudioDevice> _audioDevices = new();
    [ObservableProperty] private AudioDeviceService.AudioDevice? _selectedAudioDevice;
    [ObservableProperty] private List<Mode> _modes = new();
    [ObservableProperty] private Mode? _selectedMode;

    // Getting Started
    [ObservableProperty] private bool _showGettingStarted = true;
    [ObservableProperty] private ObservableCollection<GettingStartedItem> _gettingStartedItems = new();

    // Shortcut Conflicts (inline banner instead of transient toast)
    [ObservableProperty] private bool _hasShortcutConflicts;
    [ObservableProperty] private string _shortcutConflictMessage = "";
    [ObservableProperty] private bool _hasStreamingShortcutConflict;
    [ObservableProperty] private string _streamingShortcutConflictMessage = "";

    // Low Mic Volume Warning (inline banner on Home page)
    [ObservableProperty] private bool _hasLowMicVolume;
    [ObservableProperty] private string _lowMicVolumeMessage = "";

    // Recent Releases
    [ObservableProperty] private List<AppcastItem>? _recentReleases;
    [ObservableProperty] private bool _isLoadingReleases;
    [ObservableProperty] private string? _releasesError;

    // One-shot request flag observed by ModelLibrary page (mirrors macOS shouldOpenModelLibraryAPIKeys).
    // Set by MainWindow when a credential-error toast's action is clicked; cleared by ModelsSettingsPage.
    [ObservableProperty] private bool _shouldOpenModelLibraryApiKeys;

    private bool _hotkeyBlocked;

    partial void OnSelectedAudioDeviceChanged(AudioDeviceService.AudioDevice? value)
    {
        UpdateMicrophoneKeepWarm();
        CheckMicVolume();
    }

    /// <summary>
    /// Prevents concurrent model loads which crash the native whisper.cpp library.
    /// Both AutoLoadModelAsync (startup) and OnSelectedModeChanged (mode switch)
    /// can trigger LoadModelAsync; without this guard they race into
    /// WhisperFactory.FromPath simultaneously causing a native access violation.
    /// </summary>
    private readonly SemaphoreSlim _modelLoadLock = new(1, 1);

    public event EventHandler? ShowOverlayRequested;
    public event EventHandler<string>? ShowStreamingOverlayRequested;
    public event EventHandler<StreamingConnectionState>? StreamingConnectionStateChanged;
    public event EventHandler? HideOverlayRequested;
    public event EventHandler? ShowTranscribingRequested;
    public event EventHandler? ShowSuccessRequested;
    public event EventHandler<string>? ShowStatusRequested;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<ErrorToastEventArgs>? ShowErrorToastRequested;
    public event EventHandler<string>? ShowModeToastRequested;
    public event EventHandler? ShowCopiedRequested;

    // =========================================================================
    // CANCEL RECORDING EVENTS
    // Supports the macOS-style cancel flow:
    // - Escape key pressed during recording triggers cancel
    // - If recording < 15 seconds: cancel immediately
    // - If recording >= 15 seconds: show confirmation dialog
    // =========================================================================
    public event EventHandler? ShowCancelConfirmationRequested;
    public event EventHandler? HideCancelConfirmationRequested;

    // =========================================================================
    // FILE TRANSCRIPTION PROGRESS EVENTS
    // Supports progress UI for file transcription:
    // - ShowFileProgressRequested: Show progress window with file name and cancel handler
    // - HideFileProgressRequested: Dismiss progress window
    // - UpdateFileProgressRequested: Update progress (0.0 to 1.0)
    // =========================================================================
    public event EventHandler<FileTranscriptionProgressEventArgs>? ShowFileProgressRequested;
    public event EventHandler? HideFileProgressRequested;
    public event EventHandler<float>? UpdateFileProgressRequested;

    public MainViewModel()
    {
        _deviceService = new AudioDeviceService();
        _recorderService = new AudioRecorderService();
        _modelService = new WhisperModelService();
        // Shared with LocalApiServer via TranscriptionRuntime so the API
        // server sees the same loaded model instance the GUI loads.
        _transcriptionService = TranscriptionRuntime.LocalProvider;
        _parakeetModelService = new ParakeetModelService();
        // Shared with LocalApiServer via TranscriptionRuntime so the API
        // server sees the same loaded model instance the GUI loads.
        _parakeetTranscriptionService = TranscriptionRuntime.ParakeetProvider;
        _localLlmModelService = new LocalLlmModelService();
        _transcriptionOrchestrator = TranscriptionRuntime.Orchestrator;
        _vocabularyService = VocabularyService.Instance;
        _settingsService = SettingsService.Instance;
        _storageService = StorageService.Instance;
        _shortcutService = new KeyboardShortcutService();
        _pushToTalkMonitor = new PushToTalkMonitor();

        // Initialize handler delegates for proper unsubscription
        _audioLevelHandler = level =>
        {
            AudioLevel = level;
            AudioLevelChanged?.Invoke(this, level);
        };
        _modeChangedHandler = (s, e) => RefreshModes();
        _modeSelectedHandler = (s, mode) =>
        {
            SelectedMode = Modes.FirstOrDefault(m => m.Id == mode.Id) ?? mode;
            CurrentMode = SelectedMode;
        };
        _orchestratorWarningHandler = (s, args) =>
        {
            // The orchestrator is shared with the Local API server via
            // TranscriptionRuntime. Warnings from API-driven calls were
            // returned in the HTTP response — don't pop a toast on top of the
            // user's GUI for something they didn't trigger.
            if (args is OrchestratorPostProcessingWarningEventArgs tagged
                && tagged.CallSite == TranscriptionCallSite.Api)
            {
                return;
            }
            ShowErrorToastRequested?.Invoke(this, args);
        };

        _recorderService.AudioLevelChanged += _audioLevelHandler;
        _transcriptionOrchestrator.PostProcessingWarning += _orchestratorWarningHandler;

        _pushToTalkMonitor.Pressed += OnPushToTalkPressed;
        _pushToTalkMonitor.Released += OnPushToTalkReleased;
        _pushToTalkMonitor.Interfered += OnPushToTalkInterfered;

        ModeService.Instance.ModeChanged += _modeChangedHandler;
        ModeService.Instance.ModeSelected += _modeSelectedHandler;

        HotkeyText = _settingsService.ToggleShortcut.ToDisplayString();
    }

    /// <summary>
    /// Starts the foreground HyperWhisper Cloud keepalive ticker. Wired from
    /// <c>MainWindow.Activated</c>; pair with <see cref="StopCloudKeepalive"/>
    /// on <c>MainWindow.Deactivated</c>.
    /// </summary>
    public void StartCloudKeepalive()
        => _transcriptionOrchestrator.StartKeepalive(() => SelectedMode);

    /// <summary>
    /// Stops the foreground HyperWhisper Cloud keepalive ticker.
    /// </summary>
    public void StopCloudKeepalive()
        => _transcriptionOrchestrator.StopKeepalive();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RegisterShortcutsFromSettings();
        UpdateMicrophoneKeepWarm();
    }

    private void RegisterShortcutsFromSettings()
    {
        var shortcuts = new Dictionary<string, Models.KeyboardShortcut>
        {
            { "toggle", _settingsService.ToggleShortcut },
            { "cancel", _settingsService.CancelShortcut },
            { "changeMode", _settingsService.ChangeModeShortcut }
        };

        if (_settingsService.StreamingEnabled)
        {
            shortcuts["streaming"] = _settingsService.StreamingShortcut;
        }

        _shortcutService.AttachWindowIfNeeded();
        var results = _shortcutService.RegisterShortcuts(shortcuts);

        // Check for failures and set inline conflict banner (persistent, not a transient toast)
        var conflictMessages = new List<string>();
        var streamingConflictMessages = new List<string>();
        foreach (var kvp in results)
        {
            if (kvp.Value.IsFailure)
            {
                int win32Error = ExtractWin32ErrorCode(kvp.Value.Error);
                string userMessage = ShortcutValidationService.GetRegistrationErrorMessage(
                    win32Error,
                    shortcuts[kvp.Key]
                );
                conflictMessages.Add(userMessage);
                if (kvp.Key == "streaming")
                {
                    streamingConflictMessages.Add(userMessage);
                }
                LoggingService.Warn($"Shortcut registration failed for '{kvp.Key}': {kvp.Value.Error}");
            }
        }
        HasShortcutConflicts = conflictMessages.Count > 0;
        ShortcutConflictMessage = string.Join("\n", conflictMessages);
        HasStreamingShortcutConflict = streamingConflictMessages.Count > 0;
        StreamingShortcutConflictMessage = string.Join("\n", streamingConflictMessages);

        HotkeyText = shortcuts["toggle"].ToDisplayString();
        StatusText = Loc.S("status.ready.withHotkey", HotkeyText);

        _pushToTalkMonitor.Configure(_settingsService.PushToTalk);
        _pushToTalkMonitor.Start();
    }

    private static int ExtractWin32ErrorCode(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return 0;

        // Extract from format: "...Win32 error=1409"
        var match = System.Text.RegularExpressions.Regex.Match(errorMessage, @"Win32 error=(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int code))
        {
            return code;
        }

        return 0;
    }

    public override async Task OnNavigatedToAsync()
    {
        if (IsInitialized) return;
        IsLoading = true;
        try
        {
            RefreshAudioDevices();
            InitializeDeviceChangeMonitoring();
            CheckMicVolume();
            RefreshModes();
            UpdateModelStatus();
            InitializeShortcuts();
            _pasteService = new SmartPasteService();
            await AutoLoadModelAsync();
            InitializeGettingStarted();
            _ = LoadRecentReleasesAsync();
            IsInitialized = true;
        }
        finally { IsLoading = false; }
    }

    // =========================================================================
    // AUDIO DEVICE HOT-PLUG MONITORING
    // Automatically refreshes the device list when microphones are
    // plugged in or unplugged. Uses Windows Core Audio API callbacks.
    // =========================================================================

    private void InitializeDeviceChangeMonitoring()
    {
        _deviceService.DevicesChanged += OnAudioDevicesChanged;
        LoggingService.Debug("MainViewModel: Audio device change monitoring initialized");
    }

    /// <summary>
    /// Called when audio devices are added, removed, or changed.
    /// Refreshes the device list and preserves selection if possible.
    ///
    /// THREAD SAFETY:
    /// Device change callbacks come from a COM thread, so we must
    /// dispatch to the UI thread to update observable properties.
    ///
    /// ERROR HANDLING:
    /// - Check if dispatcher is available and not shutting down
    /// - Wrap refresh in try-catch to prevent silent failures
    /// </summary>
    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        // Get dispatcher and verify it's available
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            LoggingService.Debug("MainViewModel: Dispatcher unavailable, skipping device refresh");
            return;
        }

        // Dispatch to UI thread since callback comes from COM thread
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                RefreshAudioDevicesPreservingSelection();
                CheckMicVolume();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"MainViewModel: Failed to refresh audio devices: {ex.Message}", ex);
            }
        });
    }

    /// <summary>
    /// Checks the current default capture device volume and shows a warning
    /// if it's below 25% (matching macOS lowInputVolumeWarningThreshold).
    /// </summary>
    public void CheckMicVolume()
    {
        try
        {
            var micInfo = _recorderService.ReadMicVolume(SelectedAudioDevice?.DeviceNumber ?? -1);
            if (micInfo == null)
            {
                HasLowMicVolume = false;
                return;
            }

            var (volume, deviceName) = micInfo.Value;
            int volumePercent = (int)(volume * 100);

            if (volume < 0.25f)
            {
                HasLowMicVolume = true;
                LowMicVolumeMessage = string.Format(
                    Loc.S("home.microphone.low.message"),
                    deviceName, volumePercent);
                LoggingService.Info($"MainViewModel: Low mic volume detected - {deviceName} at {volumePercent}%");
            }
            else
            {
                HasLowMicVolume = false;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"MainViewModel: CheckMicVolume failed - {ex.Message}");
            HasLowMicVolume = false;
        }
    }

    [RelayCommand]
    private void DismissLowMicVolume()
    {
        HasLowMicVolume = false;
        LoggingService.Debug("MainViewModel: Low mic volume warning dismissed");
    }

    [RelayCommand]
    private void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MainViewModel: Failed to open Sound Settings - {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes the audio device list while preserving the current selection
    /// if the previously selected device is still available.
    ///
    /// BEHAVIOR:
    /// - If selected device still exists: keep it selected
    /// - If selected device was removed: select first available device
    /// - If no devices available: clear selection
    /// - If enumeration fails: clear selection and log error
    ///
    /// RESULT PATTERN:
    /// Handles Result&lt;List&lt;AudioDevice&gt;&gt; to distinguish between:
    /// - Success with empty list: No devices found (normal state)
    /// - Success with devices: Update UI with new list
    /// - Failure: System error, clear devices and warn user
    /// </summary>
    private void RefreshAudioDevicesPreservingSelection()
    {
        var previousSelection = SelectedAudioDevice;
        var result = _deviceService.GetAvailableDevices();

        // Handle enumeration failure
        if (result.IsFailure)
        {
            LoggingService.Error($"MainViewModel: Failed to refresh audio devices: {result.Error}");
            AudioDevices = new List<AudioDeviceService.AudioDevice>();
            SelectedAudioDevice = null;
            StatusText = Loc.S("status.audio.enumerationFailed");
            return;
        }

        // Success - update device list (may be empty)
        AudioDevices = result.Value!;

        LoggingService.Info($"MainViewModel: Device list refreshed, found {AudioDevices.Count} device(s)");
        foreach (var device in AudioDevices)
        {
            LoggingService.Debug($"  Audio device: {device.Name} (#{device.DeviceNumber})");
        }

        if (AudioDevices.Count == 0)
        {
            SelectedAudioDevice = null;
            LoggingService.Warn("MainViewModel: No audio devices available after refresh");
            return;
        }

        // Try to preserve the previous selection by matching device name
        // (Device numbers can change when devices are added/removed)
        if (previousSelection != null)
        {
            var matchingDevice = AudioDevices.FirstOrDefault(d => d.Name == previousSelection.Name);
            if (matchingDevice != null)
            {
                SelectedAudioDevice = matchingDevice;
                LoggingService.Info($"MainViewModel: Preserved device selection: {matchingDevice.Name}");
                return;
            }

            LoggingService.Info($"MainViewModel: Previous device '{previousSelection.Name}' no longer available");
        }

        // Select first device if previous selection not found
        SelectedAudioDevice = AudioDevices[0];
        LoggingService.Info($"MainViewModel: Selected new device: {SelectedAudioDevice.Name}");
    }

    /// <summary>
    /// Refreshes the audio device list and selects the first device.
    /// Called during initialization (OnNavigatedToAsync).
    ///
    /// RESULT PATTERN:
    /// Handles Result&lt;List&lt;AudioDevice&gt;&gt; to distinguish between:
    /// - Success with empty list: No devices found (normal state)
    /// - Success with devices: Update UI with new list
    /// - Failure: System error, clear devices and warn user
    /// </summary>
    private void RefreshAudioDevices()
    {
        var result = _deviceService.GetAvailableDevices();

        // Handle enumeration failure
        if (result.IsFailure)
        {
            LoggingService.Error($"MainViewModel: Failed to enumerate audio devices: {result.Error}");
            AudioDevices = new List<AudioDeviceService.AudioDevice>();
            SelectedAudioDevice = null;
            StatusText = Loc.S("status.audio.enumerationFailed");
            return;
        }

        // Success - update device list (may be empty)
        AudioDevices = result.Value!;

        LoggingService.Info($"MainViewModel: Found {AudioDevices.Count} audio device(s)");
        foreach (var device in AudioDevices)
        {
            LoggingService.Debug($"  Audio device: {device.Name} (#{device.DeviceNumber})");
        }
        if (AudioDevices.Count > 0)
        {
            SelectedAudioDevice = AudioDevices[0];
            LoggingService.Info($"MainViewModel: Selected audio device: {SelectedAudioDevice.Name}");
        }
        else
        {
            LoggingService.Warn("MainViewModel: No audio devices found!");
        }
    }

    private void RefreshModes()
    {
        Modes = ModeService.Instance.GetAllModes();
        LoggingService.Info($"MainViewModel: Found {Modes.Count} mode(s)");
        var selected = ModeService.Instance.GetSelectedMode();
        SelectedMode = selected != null ? Modes.FirstOrDefault(m => m.Id == selected.Id) : Modes.FirstOrDefault();
        CurrentMode = SelectedMode;
        LoggingService.Info($"MainViewModel: Selected mode: {SelectedMode?.Name ?? "NULL"}");
    }

    private void InitializeShortcuts()
    {
        _shortcutService.ShortcutPressed += OnShortcutPressed;
        _shortcutService.ShortcutReleased += OnShortcutReleased;
        _settingsService.SettingsChanged += OnSettingsChanged;
        RegisterShortcutsFromSettings();
    }

    private async Task AutoLoadModelAsync()
    {
        if (!PlatformHelper.SupportsLocalTranscription)
        {
            LoggingService.Debug("Skipping local model auto-load - not supported on this platform");
            return;
        }

        var mode = ModeService.Instance.GetSelectedMode();
        if (mode == null || mode.ProviderType == "cloud") return;

        try { await LoadModelAsync(); }
        catch { }
    }

    [RelayCommand] private void NavigateToHome() => CurrentPage = NavigationPage.Home;
    [RelayCommand] private void NavigateToModes() => CurrentPage = NavigationPage.Modes;
    [RelayCommand] private void NavigateToVocabulary() => CurrentPage = NavigationPage.Vocabulary;
    [RelayCommand] private void NavigateToStreaming() => CurrentPage = NavigationPage.Streaming;
    [RelayCommand] private void NavigateToModelLibrary() => CurrentPage = NavigationPage.ModelLibrary;
    [RelayCommand] private void NavigateToHistory() => CurrentPage = NavigationPage.History;
    [RelayCommand] private void NavigateToSettings() => CurrentPage = NavigationPage.Settings;

    // =========================================================================
    // GETTING STARTED
    // =========================================================================

    private void InitializeGettingStarted()
    {
        var completedSteps = _settingsService.GettingStartedCompletedSteps
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        GettingStartedItems = new ObservableCollection<GettingStartedItem>
        {
            new() { Id = "recording", Icon = "\U0001F3A4", IconColor = System.Windows.Media.Color.FromRgb(0, 122, 255), Title = Localization.Loc.S("home.gettingStarted.recording.title"), Description = Localization.Loc.S("home.gettingStarted.recording.description"), ShortcutText = HotkeyText, IsCompleted = completedSteps.Contains("recording") },
            new() { Id = "shortcuts", Icon = "\u2328\uFE0F", IconColor = System.Windows.Media.Color.FromRgb(175, 82, 222), Title = Localization.Loc.S("home.gettingStarted.shortcuts.title"), Description = Localization.Loc.S("home.gettingStarted.shortcuts.description"), IsCompleted = completedSteps.Contains("shortcuts") },
            new() { Id = "mode", Icon = "\U0001F3AF", IconColor = System.Windows.Media.Color.FromRgb(52, 199, 89), Title = Localization.Loc.S("home.gettingStarted.mode.title"), Description = Localization.Loc.S("home.gettingStarted.mode.description"), ShortcutText = _settingsService.ChangeModeShortcut.ToDisplayString(), IsCompleted = completedSteps.Contains("mode") },
            new() { Id = "vocabulary", Icon = "\U0001F4DA", IconColor = System.Windows.Media.Color.FromRgb(255, 149, 0), Title = Localization.Loc.S("home.gettingStarted.vocabulary.title"), Description = Localization.Loc.S("home.gettingStarted.vocabulary.description"), IsCompleted = completedSteps.Contains("vocabulary") },
        };

        ShowGettingStarted = completedSteps.Count < 4;
    }

    [RelayCommand]
    private void ToggleGettingStartedStep(string stepId)
    {
        var completedSteps = _settingsService.GettingStartedCompletedSteps
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        if (completedSteps.Contains(stepId))
            completedSteps.Remove(stepId);
        else
            completedSteps.Add(stepId);

        _settingsService.GettingStartedCompletedSteps = string.Join(",", completedSteps.OrderBy(s => s));

        var item = GettingStartedItems.FirstOrDefault(i => i.Id == stepId);
        if (item != null)
            item.IsCompleted = completedSteps.Contains(stepId);

        ShowGettingStarted = completedSteps.Count < 4;

        // Navigate to relevant page on check (not uncheck)
        if (completedSteps.Contains(stepId))
        {
            switch (stepId)
            {
                case "shortcuts": CurrentPage = NavigationPage.Settings; break;
                case "mode": CurrentPage = NavigationPage.Modes; break;
                case "vocabulary": CurrentPage = NavigationPage.Vocabulary; break;
            }
        }
    }

    // =========================================================================
    // RECENT RELEASES
    // =========================================================================

    private async Task LoadRecentReleasesAsync()
    {
        IsLoadingReleases = true;
        ReleasesError = null;

        var result = await AppcastService.Instance.GetRecentReleasesAsync();
        result.Match(
            onSuccess: releases =>
            {
                RecentReleases = releases;
                IsLoadingReleases = false;
            },
            onFailure: error =>
            {
                ReleasesError = error;
                IsLoadingReleases = false;
            });
    }

    [RelayCommand]
    private async Task RetryLoadReleases()
    {
        AppcastService.Instance.ClearCache();
        await LoadRecentReleasesAsync();
    }

    partial void OnSelectedModeChanged(Mode? value)
    {
        if (value == null)
        {
            CurrentMode = null;
            UpdateModelStatus();
            return;
        }

        if (value != null)
        {
            ModeService.Instance.SetSelectedMode(value.Id);
            CurrentMode = value;
            UpdateModelStatus();

            // If the newly selected mode does not use Parakeet, tear down the
            // background daemon as soon as the app is idle so it doesn't keep
            // running unnecessarily while the user stays on cloud/Whisper modes.
            if (!IsRecording && !IsTranscribing)
            {
                DisposeParakeetIfNotNeededForSelectedMode();
            }

            // Eagerly load local model in background so it's ready when the user starts recording
            if (value.ProviderType != "cloud" && PlatformHelper.SupportsLocalTranscription)
            {
                var model = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == value.ModelType);
                if (model != null && _modelService.IsModelDownloaded(model))
                {
                    _ = LoadModelAsync();
                }
            }
        }
    }

    partial void OnIsRecordingChanged(bool value)
    {
        if (!value && !IsTranscribing)
        {
            DisposeParakeetIfNotNeededForSelectedMode();
        }
    }

    partial void OnIsTranscribingChanged(bool value)
    {
        if (!value && !IsRecording)
        {
            DisposeParakeetIfNotNeededForSelectedMode();
        }
    }

    private void DisposeParakeetIfNotNeededForSelectedMode()
    {
        if (!_parakeetTranscriptionService.IsInitialized)
        {
            return;
        }

        var modeUsesParakeet = SelectedMode != null
            && SelectedMode.ProviderType != "cloud"
            && SelectedMode.LocalEngine == "parakeet";

        if (modeUsesParakeet)
        {
            return;
        }

        LoggingService.Info("MainViewModel: Disposing Parakeet daemon because selected mode does not use it");
        _parakeetTranscriptionService.DisposeModel();
        UpdateModelStatus();
    }

    private void CycleMode()
    {
        if (Modes.Count == 0) return;

        int currentIndex = SelectedMode != null ? Modes.FindIndex(m => m.Id == SelectedMode.Id) : -1;
        int nextIndex = currentIndex >= 0 ? (currentIndex + 1) % Modes.Count : 0;
        SelectedMode = Modes[nextIndex];
    }

    [RelayCommand]
    private async Task LoadModelAsync()
    {
        if (!PlatformHelper.SupportsLocalTranscription)
        {
            LoggingService.Warn("LoadModelAsync: local transcription not supported on this platform");
            return;
        }

        if (SelectedMode == null) return;

        // Route to engine-specific loader
        if (SelectedMode.LocalEngine == "parakeet")
        {
            await LoadParakeetModelAsync();
        }
        else
        {
            await LoadWhisperModelAsync();
        }
    }

    private async Task LoadWhisperModelAsync()
    {
        if (!PlatformHelper.SupportsWhisperTranscription)
        {
            LoggingService.Warn("LoadWhisperModelAsync: Whisper not supported on this platform");
            return;
        }

        if (SelectedMode == null) return;
        var model = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == SelectedMode.ModelType);
        if (model == null) return;
        var modelPath = _modelService.GetModelPath(model);
        if (_transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath) return;

        if (_parakeetTranscriptionService.IsInitialized)
        {
            LoggingService.Info("LoadWhisperModelAsync: Disposing Parakeet daemon before loading Whisper");
            _parakeetTranscriptionService.DisposeModel();
        }

        // Serialize model loads — concurrent WhisperFactory.FromPath calls cause
        // native access violations (0xC0000005) in the whisper.cpp library.
        await _modelLoadLock.WaitAsync();
        try
        {
            // Re-check after acquiring lock (another caller may have loaded it)
            if (_transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath) return;

            IsModelLoading = true;
            StatusText = Loc.S("status.model.loading", model.DisplayName);

            await _transcriptionService.InitializeAsync(modelPath, p => { }, CancellationToken.None);
            IsModelLoaded = true;
            ModelStatus = Loc.S("status.model.ready", model.DisplayName);
            StatusText = Loc.S("status.ready.withHotkey", HotkeyText);
        }
        catch (Exception ex)
        {
            ModelStatus = Loc.S("status.model.loadFailed");
            StatusText = Loc.S("status.failed", ex.Message);
            throw;
        }
        finally
        {
            IsModelLoading = false;
            _modelLoadLock.Release();
        }
    }

    private async Task LoadParakeetModelAsync()
    {
        if (SelectedMode == null) return;
        var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == SelectedMode.LocalParakeetModel);
        if (model == null) return;

        string? language = SelectedMode.Language == "auto" ? null : SelectedMode.Language;
        var effectiveLanguage = language ?? "auto";

        // Check if already loaded. The daemon's startup language/join hint affects
        // some Parakeet-family engines, so same-model mode switches with a
        // different language must reinitialize.
        if (_parakeetTranscriptionService.IsInitialized &&
            _parakeetTranscriptionService.LoadedModelId == model.Id &&
            string.Equals(_parakeetTranscriptionService.LoadedLanguage, effectiveLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var modelDir = _parakeetModelService.GetModelDirectory(model);
        if (!_parakeetModelService.IsModelDownloaded(model)) return;

        IsModelLoading = true;
        StatusText = Loc.S("status.model.parakeet.loading", model.DisplayName);
        try
        {
            // On systems with less than 32 GB RAM, unload the Whisper model
            // first. Use UnloadModel (not Dispose) — the service is shared
            // with the Local API server via TranscriptionRuntime, so
            // disposing would leave the API with a dead factory pointer.
            if (GetTotalSystemMemoryGB() < 32 && _transcriptionService.IsInitialized)
            {
                LoggingService.Info("LoadParakeetModelAsync: Unloading Whisper model to free memory (<32GB RAM)");
                _transcriptionService.UnloadModel();
            }

            await _parakeetTranscriptionService.InitializeAsync(modelDir, language);
            IsModelLoaded = true;
            ModelStatus = Loc.S("status.model.parakeet.ready", model.DisplayName, _parakeetTranscriptionService.ActiveProvider ?? "CPU");
            StatusText = Loc.S("status.ready.withHotkey", HotkeyText);
        }
        catch (Exception ex)
        {
            ModelStatus = Loc.S("status.model.loadFailed");
            StatusText = Loc.S("status.failed", ex.Message);
            throw;
        }
        finally { IsModelLoading = false; }
    }

    private static double GetTotalSystemMemoryGB()
    {
        var memInfo = GC.GetGCMemoryInfo();
        return memInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    }

    private void UpdateModelStatus()
    {
        UpdateLocalPostProcessingStatus();

        if (SelectedMode == null) { ModelStatus = Loc.S("status.mode.none"); return; }

        var computeMode = PlatformHelper.IsArm64 ? "CPU" :
                         PlatformHelper.SupportsGpuTranscription ? "GPU" : "CPU";

        if (SelectedMode.ProviderType == "cloud")
        {
            // For cloud modes, show which local model is loaded (if any)
            if (_transcriptionService.IsInitialized && !string.IsNullOrEmpty(_transcriptionService.LoadedModelPath))
            {
                var loadedModel = WhisperModelInfo.AllModels.FirstOrDefault(m =>
                    _modelService.GetModelPath(m) == _transcriptionService.LoadedModelPath);
                if (loadedModel != null)
                {
                    ModelStatus = Loc.S("status.model.localReady", loadedModel.DisplayName, computeMode);
                    return;
                }
            }
            if (_parakeetTranscriptionService.IsInitialized)
            {
                ModelStatus = Loc.S("status.model.parakeet.ready",
                    _parakeetTranscriptionService.LoadedModelId ?? "Parakeet",
                    _parakeetTranscriptionService.ActiveProvider ?? "CPU");
                return;
            }
            var provider = CloudTranscriptionProviderExtensions.FromIdentifier(SelectedMode.CloudProvider);
            var providerName = provider.GetDisplayName();
            var model = CloudTranscriptionModels.GetById(SelectedMode.CloudTranscriptionModel, provider)
                        ?? CloudTranscriptionModels.GetDefault(provider);
            var cloudStatus = provider == CloudTranscriptionProvider.HyperWhisperCloud || string.IsNullOrWhiteSpace(model?.DisplayName)
                ? providerName
                : $"{providerName} - {model.DisplayName}";
            ModelStatus = Loc.S("status.model.ready", cloudStatus);
            return;
        }

        // Local mode: show engine-specific status
        if (SelectedMode.LocalEngine == "parakeet")
        {
            var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == SelectedMode.LocalParakeetModel);
            if (model == null) { ModelStatus = Loc.S("status.model.unknown"); return; }
            bool isLoaded = _parakeetTranscriptionService.IsInitialized &&
                            _parakeetTranscriptionService.LoadedModelId == model.Id;
            ModelStatus = isLoaded
                ? Loc.S("status.model.parakeet.ready", model.DisplayName, _parakeetTranscriptionService.ActiveProvider ?? "CPU")
                : _parakeetModelService.IsModelDownloaded(model)
                    ? Loc.S("status.model.downloaded", model.DisplayName)
                    : Loc.S("status.model.parakeet.notDownloaded", model.DisplayName);
        }
        else
        {
            var model = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == SelectedMode.ModelType);
            if (model == null) { ModelStatus = Loc.S("status.model.unknown"); return; }
            var modelPath = _modelService.GetModelPath(model);
            bool isLoaded = _transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath;
            ModelStatus = isLoaded ? Loc.S("status.model.computeReady", model.DisplayName, computeMode) :
                          _modelService.IsModelDownloaded(model) ? Loc.S("status.model.downloaded", model.DisplayName) :
                          Loc.S("status.model.notDownloaded", model.DisplayName);
        }
    }

    private void UpdateLocalPostProcessingStatus()
    {
        HasLocalPostProcessingStatus = false;
        LocalPostProcessingStatus = "";

        var mode = SelectedMode;
        if (mode == null || mode.PostProcessingMode == 0)
        {
            return;
        }

        var provider = PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider);
        if (provider != PostProcessingProvider.LocalLlm)
        {
            return;
        }

        var modelId = mode.LocalPostProcessingModel ?? mode.LanguageModel;
        var model = LocalLlmModelInfo.GetById(modelId) ?? LocalLlmModelInfo.GetDefault();
        var displayName = model.DisplayName;

        if (!_localLlmModelService.IsModelDownloaded(model))
        {
            displayName += Loc.S("settings.models.localLlm.notDownloadedSuffix");
        }

        LocalPostProcessingStatus = displayName;
        HasLocalPostProcessingStatus = true;
    }

    private async void OnShortcutPressed(object? sender, KeyboardShortcutService.ShortcutEventArgs e)
    {
        switch (e.Name)
        {
            case "toggle":
                _transcriptionOrchestrator.PrewarmCloudConnectionIfActive(SelectedMode);
                if (IsStreamingActive())
                {
                    ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                        "Stop streaming before starting a standard recording.",
                        showSettingsButton: false));
                    return;
                }

                if (_hotkeyBlocked || IsTranscribing)
                {
                    _toggleShortcutHeld = true;
                    return;
                }

                // DEBOUNCE CHECK: Prevent race conditions from rapid key presses
                // When shortcuts are pressed faster than 1 second apart, subsequent presses
                // are ignored to allow the first operation to complete properly
                if (_lastToggleTime.HasValue)
                {
                    var timeSinceLastToggle = DateTime.Now - _lastToggleTime.Value;
                    if (timeSinceLastToggle.TotalMilliseconds < DebounceIntervalMs)
                    {
                        LoggingService.Debug($"Ignoring rapid toggle (pressed {timeSinceLastToggle.TotalMilliseconds:F0}ms after previous)");
                        return;
                    }
                }

                _toggleShortcutHeld = true;
                _lastToggleTime = DateTime.Now;

                if (IsRecording) await StopRecordingAndTranscribeAsync();
                else await StartRecordingAsync();
                break;
            case "cancel":
                if (_isStreamingStarting)
                {
                    CancelStreamingStart();
                    return;
                }

                if (IsRecording || IsTranscribing) await HandleCancelRequest();
                break;
            case "changeMode":
                CycleMode();
                ShowModeToastRequested?.Invoke(this, SelectedMode?.Name ?? "Default");
                break;
            case "streaming":
                if (!_settingsService.StreamingEnabled || _hotkeyBlocked || IsTranscribing)
                    return;

                if (_isStreamingStarting)
                {
                    CancelStreamingStart();
                    return;
                }

                if (_isStreamingSession)
                {
                    await StopStreamingRecordingAsync();
                }
                else if (IsRecording)
                {
                    ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                        "Stop the current recording before starting streaming.",
                        showSettingsButton: false));
                }
                else
                {
                    await StartStreamingRecordingAsync();
                }
                break;
        }
    }

    private void OnShortcutReleased(object? sender, KeyboardShortcutService.ShortcutEventArgs e)
    {
        if (e.Name == "toggle")
        {
            _toggleShortcutHeld = false;
        }
    }

    private async void OnPushToTalkPressed(object? sender, EventArgs e)
    {
        // async void event handler: any exception escaping the awaits below would be
        // rethrown on the SynchronizationContext and crash the process. Catch here so a
        // device/OCR/audio failure becomes a logged error and the monitor returns to idle.
        try
        {
            _transcriptionOrchestrator.PrewarmCloudConnectionIfActive(SelectedMode);
            if (IsStreamingActive())
            {
                _pushToTalkMonitor.ResetToIdle();
                return;
            }

            if (_hotkeyBlocked || _toggleShortcutHeld) return;
            if (!IsRecording)
            {
                await StartRecordingAsync();

                // If recording failed to start (early return in StartRecordingAsync),
                // reset the monitor so it doesn't carry stale PttActive/LatchActive state.
                if (!IsRecording) _pushToTalkMonitor.ResetToIdle();
            }
            else
            {
                // Already recording — reset monitor to prevent stale state
                // (matches macOS AudioRecordingManager behavior)
                _pushToTalkMonitor.ResetToIdle();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error($"OnPushToTalkPressed: unhandled error - {ex.Message}", ex);
            _pushToTalkMonitor.ResetToIdle();
        }
    }

    private async void OnPushToTalkReleased(object? sender, EventArgs e)
    {
        // async void event handler: catch so a failure in the stop/transcribe path is
        // logged instead of crashing the process via the SynchronizationContext.
        try
        {
            if (_hotkeyBlocked || _toggleShortcutHeld) return;
            if (_isStreamingStarting) return;
            if (IsRecording) await StopRecordingAndTranscribeAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Error($"OnPushToTalkReleased: unhandled error - {ex.Message}", ex);
            _pushToTalkMonitor.ResetToIdle();
        }
    }

    private async void OnPushToTalkInterfered(object? sender, EventArgs e)
    {
        // async void event handler: catch so a failure in the cancel path is logged
        // instead of crashing the process via the SynchronizationContext.
        try
        {
            if (_hotkeyBlocked || _toggleShortcutHeld) return;
            if (_isStreamingStarting) return;
            if (IsRecording) await CancelRecordingAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Error($"OnPushToTalkInterfered: unhandled error - {ex.Message}", ex);
            _pushToTalkMonitor.ResetToIdle();
        }
    }

    [RelayCommand]
    public async Task StartRecordingAsync()
    {
        if (IsStreamingActive())
        {
            LoggingService.Warn("StartRecordingAsync: Ignoring standard recording start while streaming is active");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                "Stop streaming before starting a standard recording.",
                showSettingsButton: false));
            return;
        }

        // Show user-friendly error if no audio device is available
        if (SelectedAudioDevice == null)
        {
            LoggingService.Warn($"StartRecordingAsync: No audio device - DeviceCount={AudioDevices.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noMicrophone"),
                showSettingsButton: false));
            return;
        }

        if (SelectedMode == null)
        {
            LoggingService.Warn($"StartRecordingAsync: No mode selected - ModeCount={Modes.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noModeSelected"),
                showSettingsButton: false));
            return;
        }

        // No local trial gate — local transcription is unlimited (open source).

        var recordingMode = SelectedMode;
        _activeRecordingMode = recordingMode;

        LoggingService.LogPerformanceMarker("Recording", "StartRecordingAsync invoked");

        // Capture the foreground window before any UI is shown.
        // The clipboard session starts later, after model readiness checks, so failed starts
        // do not cancel pending clipboard restoration from the previous recording.
        _pasteService?.CaptureForegroundWindow();

        // Capture application context BEFORE showing overlay (overlay steals focus)
        _capturedApplicationContext = ApplicationContextService.Instance.GatherContext();

        // Capture screen OCR text if enabled on this mode.
        if (recordingMode.EnableScreenOCR && recordingMode.PostProcessingMode != 0)
        {
            _capturedApplicationContext ??= new HyperWhisper.Services.ApplicationContext();
            _capturedApplicationContext.ScreenOCRText = await ScreenOCRCaptureService.Instance.CaptureAndOcrAsync();
            if (_capturedApplicationContext.ScreenOCRText != null)
            {
                LoggingService.Info($"Screen OCR captured: {_capturedApplicationContext.ScreenOCRText.Length} characters");
            }
        }

        // CLOUD VS LOCAL MODEL LOADING
        // Cloud modes don't need a local model loaded - they use the API
        // Local modes need the Whisper model loaded into GPU memory
        if (recordingMode.ProviderType != "cloud" && !IsLocalProviderReady(recordingMode))
        {
            // Check model is downloaded
            bool modelDownloaded = IsLocalModelDownloaded(recordingMode);
            if (!modelDownloaded)
            {
                var modelName = recordingMode.LocalEngine == "parakeet"
                    ? recordingMode.LocalParakeetModel ?? "Unknown"
                    : recordingMode.ModelType ?? "Unknown";
                LoggingService.Warn($"StartRecordingAsync: Model not downloaded - {modelName}");
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.modelNotDownloaded", modelName),
                    showSettingsButton: false));
                CleanupFailedRecordingStart();
                return;
            }
            try
            {
                await LoadModelAsync();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"StartRecordingAsync: Model load failed - {ex.Message}");
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.modelLoadFailed"),
                    showSettingsButton: false));
                CleanupFailedRecordingStart();
                return;
            }
        }

        AudioEnvironmentService.AudioEnvironmentRestoreClaim? audioRestoreClaim = null;

        try
        {
            // CLIPBOARD PRESERVATION - STEP 1: Capture clipboard content only once
            // recording is ready to start.
            _pasteService?.StartRecordingSession();
            SuspendMicrophoneKeepWarm();

            if (SettingsService.Instance.AutoIncreaseMicVolume)
                _recorderService.BoostMicVolume(SelectedAudioDevice.DeviceNumber);

            audioRestoreClaim = AudioEnvironmentService.Instance.ClaimRestoreOwnershipForRecording();
            _audioEnvironmentState = audioRestoreClaim.InheritedRestoreState;
            _recorderService.StartRecording(SelectedAudioDevice.DeviceNumber);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"StartRecordingAsync: Recording start failed - {ex.Message}");
            CleanupFailedRecordingStart();

            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.recordingStartFailed"),
                showSettingsButton: false));
            return;
        }

        IsRecording = true;
        SoundEffectsService.Instance.PlayStartSound();
        _audioEnvironmentState = AudioEnvironmentService.Instance.PrepareForRecording(audioRestoreClaim!);
        ShowOverlayRequested?.Invoke(this, EventArgs.Empty);
        RecordingDuration = TimeSpan.Zero;
        _recordingDurationLimitReached = false;
        _durationTimer?.Dispose();
        _durationTimer = new System.Timers.Timer(100);
        _durationTimer.Elapsed += (s, e) =>
        {
            RecordingDuration = _recorderService.Duration;
            CheckRecordingDurationLimit();
        };
        _durationTimer.Start();
    }

    private void CleanupFailedRecordingStart()
    {
        _recorderService.RestoreMicVolume();
        RestoreAudioEnvironment();
        ResumeMicrophoneKeepWarm();
        _pasteService?.EndRecordingSession();
        _capturedApplicationContext = null;
        _activeRecordingMode = null;
    }

    private bool IsStreamingActive() => _isStreamingSession || _isStreamingStarting || _isStoppingStreaming;

    private bool IsActiveStreamingGeneration(int generation) =>
        _isStreamingSession && generation == _streamingSessionGeneration;

    private string GetStreamingStartupFailureMessage() =>
        !string.IsNullOrWhiteSpace(_streamingFailureMessage)
            ? _streamingFailureMessage
            : "Streaming transcription connection could not be started.";

    private void RestoreAudioEnvironment()
    {
        var state = _audioEnvironmentState;
        if (state == null)
            return;

        _audioEnvironmentState = null;
        AudioEnvironmentService.Instance.ScheduleRestoreAfterRecording(state);
    }

    private Task RestoreAudioEnvironmentImmediatelyAsync()
    {
        var state = _audioEnvironmentState;
        _audioEnvironmentState = null;
        return AudioEnvironmentService.Instance.RestoreAfterRecordingImmediatelyAsync(state);
    }

    private void UpdateMicrophoneKeepWarm()
    {
        MicrophoneKeepWarmService.Instance.Configure(
            _settingsService.KeepMicrophoneWarm,
            SelectedAudioDevice?.DeviceNumber);
    }

    private void SuspendMicrophoneKeepWarm()
    {
        MicrophoneKeepWarmService.Instance.SuspendForRecording();
    }

    private void ResumeMicrophoneKeepWarm()
    {
        MicrophoneKeepWarmService.Instance.ResumeAfterRecording(SelectedAudioDevice?.DeviceNumber);
    }

    private async Task StartStreamingRecordingAsync()
    {
        if (_isStreamingStarting || _isStreamingSession)
            return;

        if (SelectedAudioDevice == null)
        {
            LoggingService.Warn($"StartStreamingRecordingAsync: No audio device - DeviceCount={AudioDevices.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noMicrophone"),
                showSettingsButton: false));
            return;
        }

        // No local trial gate — local transcription is unlimited (open source).

        var vocabulary = _vocabularyService.GetVocabularyWords(100);
        var clientResult = StreamingTranscriptionSessionFactory.Create(vocabulary);
        if (clientResult.IsFailure)
        {
            LoggingService.Warn($"StartStreamingRecordingAsync: {clientResult.Error}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                clientResult.Error ?? Loc.S("errors.recordingStartFailed"),
                showSettingsButton: true,
                openApiKeysManager: true));
            return;
        }

        _pasteService?.CaptureForegroundWindow();
        _capturedApplicationContext = ApplicationContextService.Instance.GatherContext();
        _streamingFailureMessage = null;
        _streamingPastedFinalSegment = false;
        _streamingTargetLost = false;
        _streamingDurationLimitReached = false;
        _streamingLastPasteResult = SmartPasteResult.Failed;
        _streamingPendingFinalFallbackText = string.Empty;

        _streamingClient = clientResult.Value!;
        _streamingClient.ErrorReceived += OnStreamingErrorReceived;
        _streamingClient.FinalTranscriptSegmentReceived += OnStreamingFinalTranscriptSegmentReceived;
        _streamingClient.WarningReceived += OnStreamingWarningReceived;
        _streamingClient.SessionCompleted += OnStreamingSessionCompleted;
        _streamingClient.StateChanged += OnStreamingConnectionStateChanged;
        _isStreamingStarting = true;
        _streamingStartCancelledByUser = false;
        _streamingStartCts = new CancellationTokenSource(StreamingConnectionTimeout);
        _streamingSessionGeneration++;

        var providerName = GetStreamingProviderDisplayName();
        SentryService.AddBreadcrumb(
            "streaming_start_requested",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = providerName });

        _streamingAudioCapture = new StreamingAudioCapture();
        _streamingAudioCapture.AudioChunkAvailable += OnStreamingAudioChunkAvailable;
        _streamingAudioCapture.AudioLevelChanged += _audioLevelHandler;

        AudioEnvironmentService.AudioEnvironmentRestoreClaim? audioRestoreClaim = null;

        try
        {
            ShowStreamingOverlayRequested?.Invoke(this, providerName);
            _pasteService?.StartRecordingSession();
            SuspendMicrophoneKeepWarm();

            if (SettingsService.Instance.AutoIncreaseMicVolume)
                _recorderService.BoostMicVolume(SelectedAudioDevice.DeviceNumber);

            audioRestoreClaim = AudioEnvironmentService.Instance.ClaimRestoreOwnershipForRecording();
            _audioEnvironmentState = audioRestoreClaim.InheritedRestoreState;
            var started = await _streamingClient.StartAsync(_streamingStartCts.Token);
            if (!started)
                throw new InvalidOperationException(GetStreamingStartupFailureMessage());

            _streamingAudioCapture.Start(SelectedAudioDevice.DeviceNumber, _streamingClient.AudioSampleRate);
        }
        catch (OperationCanceledException)
        {
            var failureMessage = _streamingFailureMessage;
            LoggingService.Warn(_streamingStartCancelledByUser
                ? "StartStreamingRecordingAsync: Streaming start cancelled by user"
                : "StartStreamingRecordingAsync: Streaming connection timed out");
            SentryService.AddBreadcrumb(
                _streamingStartCancelledByUser ? "streaming_start_cancelled" : "streaming_start_timeout",
                "audio.streaming",
                data: new Dictionary<string, string> { ["provider"] = providerName });
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            await CleanupStreamingSessionAsync();
            CleanupFailedRecordingStart();

            if (!_streamingStartCancelledByUser)
            {
                var message = !string.IsNullOrWhiteSpace(failureMessage)
                    ? failureMessage
                    : "Streaming connection timed out. Check your internet connection and try again.";
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    message,
                    showSettingsButton: false));
                StatusText = Loc.S("status.failed", message);
            }
            return;
        }
        catch (Exception ex)
        {
            var failureMessage = _streamingFailureMessage;
            LoggingService.Error($"StartStreamingRecordingAsync: Recording start failed - {ex.Message}", ex);
            SentryService.AddBreadcrumb(
                "streaming_start_failed",
                "audio.streaming",
                data: new Dictionary<string, string> { ["provider"] = providerName, ["errorType"] = ex.GetType().Name });
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            await CleanupStreamingSessionAsync();
            CleanupFailedRecordingStart();

            var message = !string.IsNullOrWhiteSpace(failureMessage)
                ? failureMessage
                : !string.IsNullOrWhiteSpace(ex.Message)
                    ? ex.Message
                    : Loc.S("errors.recordingStartFailed");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                message,
                showSettingsButton: false));
            StatusText = Loc.S("status.failed", message);
            return;
        }
        finally
        {
            _isStreamingStarting = false;
            _streamingStartCancelledByUser = false;
            _streamingStartCts?.Dispose();
            _streamingStartCts = null;
        }

        _isStreamingSession = true;
        IsRecording = true;
        SoundEffectsService.Instance.PlayStartSound();
        _audioEnvironmentState = AudioEnvironmentService.Instance.PrepareForRecording(audioRestoreClaim!);
        SentryService.AddBreadcrumb(
            "streaming_started",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = providerName });
        RecordingDuration = TimeSpan.Zero;
        _durationTimer?.Dispose();
        _durationTimer = new System.Timers.Timer(100);
        _durationTimer.Elapsed += (s, e) =>
        {
            RecordingDuration = _streamingAudioCapture?.Duration ?? TimeSpan.Zero;
            CheckStreamingTargetAvailability();
            CheckStreamingDurationLimit();
        };
        _durationTimer.Start();
    }

    private async Task StopStreamingRecordingAsync()
    {
        if (_isStoppingStreaming)
        {
            LoggingService.Debug("StopStreamingRecordingAsync: stop already in progress; ignoring duplicate request");
            return;
        }

        _isStoppingStreaming = true;
        LoggingService.LogPerformanceMarker("StreamingTranscriptionFlow", "StopStreamingRecordingAsync invoked");
        SentryService.AddBreadcrumb(
            "streaming_stop_requested",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = GetStreamingProviderDisplayName() });
        _durationTimer?.Stop();
        _hotkeyBlocked = true;
        IsTranscribing = true;
        ShowTranscribingRequested?.Invoke(this, EventArgs.Empty);

        Transcript? transcript = null;

        try
        {
            var durationSeconds = RecordingDuration.TotalSeconds;

            _streamingAudioCapture?.Stop();
            _recorderService.RestoreMicVolume();
            RestoreAudioEnvironment();
            ResumeMicrophoneKeepWarm();
            IsRecording = false;

            var finalText = _streamingClient != null
                ? await _streamingClient.StopAsync()
                : string.Empty;

            finalText = TranscriptionTextProcessing.FinalizeStreamingText(finalText);
            if (string.IsNullOrWhiteSpace(finalText))
            {
                if (!string.IsNullOrWhiteSpace(_streamingFailureMessage))
                {
                    throw new InvalidOperationException(_streamingFailureMessage);
                }

                throw new TranscriptionException(
                    TranscriptionErrorCode.NoSpeechDetected,
                    Loc.S("errors.noSpeechDetected"),
                    GetStreamingProviderDisplayName());
            }

            transcript = HistoryService.Instance.CreateProcessingTranscript(
                durationSeconds,
                SelectedMode?.Name,
                audioFilePath: null);

            transcript.Text = finalText;
            transcript.TranscribedText = finalText;
            transcript.Status = TranscriptStatus.Completed;
            transcript.TranscriptionProvider = GetStreamingProviderDisplayName();

            if (!string.IsNullOrWhiteSpace(_streamingFailureMessage) && !_streamingDurationLimitReached)
            {
                transcript.Status = TranscriptStatus.Failed;
                transcript.FailedReason = _streamingFailureMessage;
                HistoryService.Instance.UpdateTranscript(transcript);

                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    _streamingFailureMessage,
                    showSettingsButton: false));
                StatusText = Loc.S("status.failed", _streamingFailureMessage);
                return;
            }

            // No local usage recording — local transcription is unlimited (open source).

            var textToProcess = finalText;

            var pasteResult = _streamingLastPasteResult;
            var pendingFallbackText = TranscriptionTextProcessing.FinalizeStreamingText(_streamingPendingFinalFallbackText);
            if (!SettingsService.Instance.AutoPasteEnabled)
            {
                var spacedText = SmartSpacing.AppendTrailingSpace(textToProcess, _settingsService.StreamingLanguage);
                _pasteService?.CopyToClipboard(spacedText);
                pasteResult = SmartPasteResult.CopiedToClipboard;
                LoggingService.Debug("MainViewModel: Auto-paste disabled, streaming text copied to clipboard only");
            }
            else if (!string.IsNullOrWhiteSpace(pendingFallbackText))
            {
                var targetAvailable = !_streamingTargetLost && _pasteService?.IsCapturedTargetAvailable() != false;
                pasteResult = targetAvailable
                    ? PasteStreamingFinalSegment(pendingFallbackText)
                    : SmartPasteResult.Failed;

                if (pasteResult == SmartPasteResult.Pasted)
                {
                    _streamingPendingFinalFallbackText = string.Empty;
                }
                else
                {
                    var spacedText = SmartSpacing.AppendTrailingSpace(textToProcess, _settingsService.StreamingLanguage);
                    _pasteService?.CopyToClipboard(spacedText);
                    if (pasteResult != SmartPasteResult.SecureFieldSkipped)
                    {
                        pasteResult = SmartPasteResult.CopiedToClipboard;
                        LoggingService.Warn("MainViewModel: Streaming pending final segment paste failed; copied full transcript to clipboard");
                    }
                    else
                    {
                        LoggingService.Info("MainViewModel: Streaming pending final segment hit secure field; full transcript left on clipboard for manual paste");
                    }
                }
            }
            else if (!_streamingPastedFinalSegment && !_streamingTargetLost)
            {
                pasteResult = PasteStreamingFinalSegment(textToProcess);
            }

            // SecureFieldSkipped intentionally left the transcription on the clipboard
            // for manual paste — restoring the old clipboard would wipe it. Skip restore.
            if (pasteResult != SmartPasteResult.SecureFieldSkipped)
            {
                _pasteService?.ScheduleClipboardRestore();
            }
            HistoryService.Instance.UpdateTranscript(transcript);
            SentryService.AddBreadcrumb(
                "streaming_saved",
                "audio.streaming",
                data: new Dictionary<string, string>
                {
                    ["provider"] = GetStreamingProviderDisplayName(),
                    ["durationSeconds"] = ((int)durationSeconds).ToString()
                });

            switch (pasteResult)
            {
                case SmartPasteResult.Pasted:
                    ShowSuccessRequested?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(400);
                    break;
                case SmartPasteResult.SecureFieldSkipped:
                case SmartPasteResult.CopiedToClipboard:
                    ShowCopiedRequested?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(500);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (transcript != null)
            {
                MarkTranscriptAsGenericFailure(transcript, ex);
            }

            var isCredentialError = ex is TranscriptionException { Code: TranscriptionErrorCode.ApiKeyMissing or TranscriptionErrorCode.Unauthorized };
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                ex is TranscriptionException txEx ? txEx.GetUserMessage() : Loc.S("errors.transcriptionFailed", ex.Message),
                showSettingsButton: isCredentialError,
                openApiKeysManager: isCredentialError));

            StatusText = Loc.S("status.failed", ex.Message);
        }
        finally
        {
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            try
            {
                await CleanupStreamingSessionAsync();
            }
            finally
            {
                _hotkeyBlocked = false;
                IsTranscribing = false;
                _toggleShortcutHeld = false;
                _pushToTalkMonitor.Reset();
                _shortcutService.ResetKeyboardState();
                _pasteService?.EndRecordingSession();
                _isStoppingStreaming = false;
            }
        }
    }

    private void OnStreamingFinalTranscriptSegmentReceived(string segment)
    {
        var generation = _streamingSessionGeneration;
        OnStreamingFinalTranscriptSegmentReceived(segment, generation);
    }

    private void OnStreamingFinalTranscriptSegmentReceived(string segment, int generation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStreamingFinalTranscriptSegmentReceived(segment, generation));
            return;
        }

        if (!IsActiveStreamingGeneration(generation))
            return;

        if (!SettingsService.Instance.AutoPasteEnabled)
            return;

        if (!CheckStreamingTargetAvailability())
            return;

        _streamingLastPasteResult = PasteStreamingFinalSegment(segment);
        if (_streamingLastPasteResult == SmartPasteResult.Pasted)
        {
            _streamingPastedFinalSegment = true;
        }
        else if (_streamingLastPasteResult == SmartPasteResult.Failed)
        {
            AppendStreamingPendingFallback(segment);
        }
    }

    private void AppendStreamingPendingFallback(string segment)
    {
        var cleaned = TranscriptionTextProcessing.FinalizeStreamingText(segment);
        if (string.IsNullOrWhiteSpace(cleaned))
            return;

        _streamingPendingFinalFallbackText = string.IsNullOrWhiteSpace(_streamingPendingFinalFallbackText)
            ? cleaned
            : TranscriptionTextProcessing.FinalizeStreamingText($"{_streamingPendingFinalFallbackText} {cleaned}");
    }

    private bool CheckStreamingTargetAvailability()
    {
        if (!_isStreamingSession ||
            _streamingTargetLost ||
            !SettingsService.Instance.AutoPasteEnabled ||
            _pasteService?.IsCapturedTargetAvailable() != false)
        {
            return true;
        }

        _streamingTargetLost = true;
        LoggingService.Warn("Streaming target window lost; stopping streaming session");
        SentryService.AddBreadcrumb("streaming_target_lost", "audio.streaming");

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(async () => await StopStreamingRecordingAsync());
        }
        else
        {
            _ = StopStreamingRecordingAsync();
        }

        return false;
    }

    private void CheckRecordingDurationLimit()
    {
        if (!IsRecording ||
            _isStreamingSession ||
            _recordingDurationLimitReached ||
            RecordingDuration < MaxRecordingDuration)
        {
            return;
        }

        _recordingDurationLimitReached = true;
        LoggingService.Warn("Recording duration limit reached; auto-stopping session");
        SentryService.AddBreadcrumb("recording_duration_limit_reached", "audio.recording");

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(async () =>
            {
                await AutoStopRecordingAfterDurationLimitAsync();
            });
        }
        else
        {
            _ = AutoStopRecordingAfterDurationLimitAsync();
        }
    }

    private async Task AutoStopRecordingAfterDurationLimitAsync()
    {
        if (!IsRecording || _isStreamingSession || IsTranscribing)
        {
            return;
        }

        ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
            "Recording stopped — 20-minute safety limit reached.",
            showSettingsButton: false));
        await StopRecordingAndTranscribeAsync();
    }

    private void CheckStreamingDurationLimit()
    {
        if (!_isStreamingSession ||
            _streamingDurationLimitReached ||
            RecordingDuration < MaxRecordingDuration)
        {
            return;
        }

        _streamingDurationLimitReached = true;
        _streamingFailureMessage = "Streaming reached the 20-minute safety limit.";
        LoggingService.Warn("Streaming duration limit reached; stopping session");
        SentryService.AddBreadcrumb("streaming_duration_limit_reached", "audio.streaming");

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() =>
            {
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    _streamingFailureMessage,
                    showSettingsButton: false));
                _ = StopStreamingRecordingAsync();
            });
        }
        else
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                _streamingFailureMessage,
                showSettingsButton: false));
            _ = StopStreamingRecordingAsync();
        }
    }

    private SmartPasteResult PasteStreamingFinalSegment(string segment)
    {
        var spacedText = SmartSpacing.AppendTrailingSpace(segment, _settingsService.StreamingLanguage);
        return _pasteService?.SmartPaste(spacedText) ?? SmartPasteResult.Failed;
    }

    private async void OnStreamingAudioChunkAvailable(byte[] chunk)
    {
        try
        {
            if (_streamingClient != null)
            {
                await _streamingClient.SendAudioAsync(chunk);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Streaming audio send failed: {ex.Message}");
        }
    }

    private void OnStreamingErrorReceived(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStreamingErrorReceived(message));
            return;
        }

        _streamingFailureMessage = message;
        StatusText = Loc.S("status.failed", message);
        LoggingService.Error($"Streaming provider error: {message}");
        SentryService.AddBreadcrumb("streaming_provider_error", "audio.streaming");

        if (_isStreamingSession && IsRecording)
        {
            _ = StopStreamingRecordingAsync();
        }
    }

    private void OnStreamingWarningReceived(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStreamingWarningReceived(message));
            return;
        }

        LoggingService.Warn($"Streaming warning: {message}");
        SentryService.AddBreadcrumb("streaming_provider_warning", "audio.streaming");
        ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
            message,
            showSettingsButton: false));
    }

    private void OnStreamingConnectionStateChanged(StreamingConnectionState state)
    {
        StreamingConnectionStateChanged?.Invoke(this, state);
    }

    private void CancelStreamingStart()
    {
        if (!_isStreamingStarting)
            return;

        _streamingStartCancelledByUser = true;
        LoggingService.Info("Cancelling streaming connection attempt");
        SentryService.AddBreadcrumb("streaming_start_cancel_requested", "audio.streaming");
        _streamingStartCts?.Cancel();
    }

    private void OnStreamingSessionCompleted(double durationSeconds, double creditsUsed)
    {
        LoggingService.Info($"Streaming session complete: {durationSeconds:F2}s, {creditsUsed:F2} credits");
        SentryService.AddBreadcrumb(
            "streaming_session_complete",
            "audio.streaming",
            data: new Dictionary<string, string>
            {
                ["provider"] = GetStreamingProviderDisplayName(),
                ["durationSeconds"] = durationSeconds.ToString("F2"),
                ["creditsUsed"] = creditsUsed.ToString("F2")
            });

        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(_settingsService.StreamingProvider);
        if (provider != StreamingTranscriptionProvider.HyperWhisperCloud)
            return;

        var cloudManager = HyperWhisperCloudManager.Instance;
        cloudManager.InvalidateCache();
        _ = RefreshHyperWhisperCloudCreditsAfterStreamingAsync();
    }

    private static async Task RefreshHyperWhisperCloudCreditsAfterStreamingAsync()
    {
        try
        {
            await HyperWhisperCloudManager.Instance.RefreshCreditsAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Streaming credit refresh failed: {ex.Message}");
        }
    }

    private async Task CleanupStreamingSessionAsync()
    {
        _isStreamingSession = false;
        _streamingSessionGeneration++;
        _streamingFailureMessage = null;
        _streamingTargetLost = false;

        if (_streamingAudioCapture != null)
        {
            _streamingAudioCapture.AudioChunkAvailable -= OnStreamingAudioChunkAvailable;
            _streamingAudioCapture.AudioLevelChanged -= _audioLevelHandler;
            _streamingAudioCapture.Dispose();
            _streamingAudioCapture = null;
        }

        if (_streamingClient != null)
        {
            _streamingClient.ErrorReceived -= OnStreamingErrorReceived;
            _streamingClient.FinalTranscriptSegmentReceived -= OnStreamingFinalTranscriptSegmentReceived;
            _streamingClient.WarningReceived -= OnStreamingWarningReceived;
            _streamingClient.SessionCompleted -= OnStreamingSessionCompleted;
            _streamingClient.StateChanged -= OnStreamingConnectionStateChanged;
            await _streamingClient.DisposeAsync();
            _streamingClient = null;
        }
    }

    private string GetStreamingProviderDisplayName()
    {
        var provider = StreamingTranscriptionProviderExtensions.FromStorageValue(_settingsService.StreamingProvider);
        return $"{provider.DisplayName()} (Streaming)";
    }

    [RelayCommand]
    public async Task StopRecordingAndTranscribeAsync()
    {
        if (_isStreamingStarting)
        {
            return;
        }

        if (_isStreamingSession)
        {
            await StopStreamingRecordingAsync();
            return;
        }

        LoggingService.LogPerformanceMarker("TranscriptionFlow", "StopRecordingAndTranscribeAsync invoked");
        var recordingMode = _activeRecordingMode ?? SelectedMode;
        if (recordingMode == null)
        {
            LoggingService.Warn("StopRecordingAndTranscribeAsync: No active recording mode");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noModeSelected"),
                showSettingsButton: false));
            return;
        }

        if (_isStoppingRecording)
        {
            LoggingService.Debug("StopRecordingAndTranscribeAsync: stop already in progress; ignoring duplicate request");
            return;
        }

        _isStoppingRecording = true;

        _durationTimer?.Stop();
        var hangWatch = Stopwatch.StartNew();
        var hangCts = new CancellationTokenSource();
        var transcriptionCts = new CancellationTokenSource();
        _activeTranscriptionCts = transcriptionCts;
        Transcript? transcript = null;
        string? permanentAudioPath = null;
        var transcriptDeleted = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), hangCts.Token);
                if (!hangCts.IsCancellationRequested && IsTranscribing)
                {
                    LoggingService.Warn($"[PERF] Transcribing still pending after 20s (mode={recordingMode.ProviderType}, lang={recordingMode.Language})");
                }
                await Task.Delay(TimeSpan.FromSeconds(20), hangCts.Token);
                if (!hangCts.IsCancellationRequested && IsTranscribing)
                {
                    LoggingService.Warn($"[PERF] Transcribing still pending after 40s (mode={recordingMode.ProviderType}, lang={recordingMode.Language})");
                }
            }
            catch (TaskCanceledException) { }
        }, hangCts.Token);

        _hotkeyBlocked = true;
        LoggingService.LogPerformanceMarker("TranscriptionFlow", "Hotkey blocked");

        try
        {
            IsTranscribing = true;
            LoggingService.LogPerformanceMarker("TranscriptionFlow", "Entered transcribing state");
            ShowTranscribingRequested?.Invoke(this, EventArgs.Empty);

            // STEP 1: Stop recording and get the audio file path
            // Uses Result<T> pattern to handle both success and failure cases explicitly
            var stopResult = _recorderService.StopRecording();
            _recorderService.RestoreMicVolume();
            RestoreAudioEnvironment();
            ResumeMicrophoneKeepWarm();
            IsRecording = false;
            transcriptionCts.Token.ThrowIfCancellationRequested();

            // Play stop sound on successful recording stop (not on cancel)
            if (stopResult.IsSuccess) SoundEffectsService.Instance.PlayStopSound();

            // RESULT HANDLING: Check if recording stopped successfully
            if (stopResult.IsFailure)
            {
                LoggingService.Warn($"TranscriptionFlow: StopRecording failed - {stopResult.Error}");
                HideOverlayRequested?.Invoke(this, EventArgs.Empty);
                _hotkeyBlocked = false;
                IsTranscribing = false;
                _toggleShortcutHeld = false;
                _shortcutService.ResetKeyboardState();
                hangCts.Cancel();
                return;
            }

            var tempAudioPath = stopResult.Value!;

            // HISTORY INTEGRATION - STEP 1: Save audio to permanent location
            // Move audio from temp to %LOCALAPPDATA%\HyperWhisper\Audio\ for history and retry
            permanentAudioPath = HistoryService.Instance.SaveAudioFile(tempAudioPath);
            transcriptionCts.Token.ThrowIfCancellationRequested();
            try
            {
                var fileInfo = new FileInfo(permanentAudioPath);
                LoggingService.Info($"TranscriptionFlow: Audio saved to history ({permanentAudioPath}, {fileInfo.Length:N0} bytes, {RecordingDuration.TotalSeconds:F2}s)");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"TranscriptionFlow: Unable to log audio file info: {ex.Message}");
            }

            // HISTORY INTEGRATION - STEP 2: Create processing transcript immediately
            // This appears in History view with "Processing..." status right away
            transcript = HistoryService.Instance.CreateProcessingTranscript(
                RecordingDuration.TotalSeconds,
                recordingMode.Name,
                permanentAudioPath);
            transcriptionCts.Token.ThrowIfCancellationRequested();

            LoggingService.LogPerformanceMarker("TranscriptionFlow", "Transcription start");

            // STEP 3: Perform transcription via orchestrator
            // The orchestrator handles cloud/local routing, post-processing, and vocabulary replacements
            var vocabulary = _vocabularyService.GetVocabularyWords(100);
            var providerStopwatch = Stopwatch.StartNew();

            // Show enhancing status if post-processing is enabled
            if (recordingMode.PostProcessingMode != 0)
            {
                ShowStatusRequested?.Invoke(this, Loc.S("recording.state.enhancing"));
            }

            var result = await _transcriptionOrchestrator.TranscribeAsync(
                permanentAudioPath,
                recordingMode,
                vocabulary,
                localTranscriptionProvider: GetLocalProvider(recordingMode),
                applicationContext: _capturedApplicationContext,
                cancellationToken: transcriptionCts.Token);
            transcriptionCts.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_activeTranscriptionCts, transcriptionCts))
            {
                _activeTranscriptionCts = null;
            }

            providerStopwatch.Stop();
            LoggingService.Info($"TranscriptionFlow: Transcription completed in {providerStopwatch.ElapsedMilliseconds}ms via {result.TranscriptionProvider}");

            // HISTORY INTEGRATION - STEP 5: Update transcript with success
            // This changes status from "processing" to "completed" in History view
            transcript.Text = result.FinalText;
            transcript.TranscribedText = result.RawText;
            transcript.PostProcessedText = result.PostProcessedText;
            transcript.Status = TranscriptStatus.Completed;
            transcript.TranscriptionProvider = result.TranscriptionProvider;
            transcript.PostProcessingProvider = result.PostProcessingProvider;

            // No local usage recording — local transcription is unlimited (open source).

            // STEP 6: Smart paste FIRST, then persist to DB (paste is latency-critical)
            string modeLanguage = recordingMode.Language ?? "auto";
            string textToProcess = result.FinalText;
            if (recordingMode.RemoveTrailingPeriod)
            {
                textToProcess = SmartSpacing.RemoveTrailingPeriod(textToProcess);
            }

            // AUTOCAPITALIZE INSERT — mirrors macOS RecordingTranscriptionFlow+StopRecording.swift.
            // Streaming insert paths are intentionally NOT modified — same MVP boundary as macOS.
            if (SettingsService.Instance.AutocapitalizeInsert)
            {
                var ctx = TextFieldContextHelper.GetFocusedElementContext();
                textToProcess = AutocapitalizeInsert.Apply(textToProcess, ctx);
            }

            string spacedText = SmartSpacing.AppendTrailingSpace(textToProcess, modeLanguage);

            SmartPasteResult pasteResult = SmartPasteResult.Failed;
            if (SettingsService.Instance.AutoPasteEnabled)
            {
                pasteResult = _pasteService?.SmartPaste(spacedText) ?? SmartPasteResult.Failed;
            }
            else
            {
                _pasteService?.CopyToClipboard(spacedText);
                pasteResult = SmartPasteResult.CopiedToClipboard;
                LoggingService.Debug("MainViewModel: Auto-paste disabled, text copied to clipboard only");
            }

            // CLIPBOARD PRESERVATION - STEP 2: Schedule clipboard restoration.
            // SecureFieldSkipped intentionally left the transcription on the clipboard
            // for manual paste — restoring the old clipboard would wipe it. Skip restore.
            if (pasteResult != SmartPasteResult.SecureFieldSkipped)
            {
                _pasteService?.ScheduleClipboardRestore();
            }

            // Persist the terminal transcript status after paste. If this write
            // fails, the finally safety net below will retry before the flow exits.
            try
            {
                HistoryService.Instance.UpdateTranscript(transcript);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"History terminal update failed after paste; retrying from safety net: {ex.Message}");
            }

            // Compress audio in background after the terminal status is durable.
            _ = Task.Run(() =>
            {
                try
                {
                    if (_storageService.StoreAsM4A)
                    {
                        var compressedPath = _storageService.TryConvertWavToM4A(permanentAudioPath);
                        if (!string.IsNullOrEmpty(compressedPath))
                        {
                            transcript.AudioFilePath = compressedPath;
                            HistoryService.Instance.UpdateTranscript(transcript);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"Background audio compression failed: {ex.Message}");
                }
            });
            LoggingService.LogPerformanceMarker("TranscriptionFlow", "Paste done, history save attempted");

            switch (pasteResult)
            {
                case SmartPasteResult.Pasted:
                    ShowSuccessRequested?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(400);
                    break;
                case SmartPasteResult.SecureFieldSkipped:
                case SmartPasteResult.CopiedToClipboard:
                    ShowCopiedRequested?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(500);
                    break;
                case SmartPasteResult.Failed:
                    break;
            }
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (transcriptionCts.IsCancellationRequested)
        {
            LoggingService.Info("TranscriptionFlow: Transcription cancelled by user");
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            StatusText = Loc.S("status.recordingCancelled");
            if (transcript != null)
            {
                transcriptDeleted = HistoryService.Instance.DeleteTranscript(transcript.Id);
            }
            else if (!string.IsNullOrEmpty(permanentAudioPath))
            {
                HistoryService.Instance.DeleteAudioFile(permanentAudioPath);
            }
        }
        catch (Exception ex)
        {
            HideOverlayRequested?.Invoke(this, EventArgs.Empty);

            // Show user-friendly error toast based on exception type
            if (ex is TranscriptionException txEx)
            {
                if (txEx.Code == TranscriptionErrorCode.NoSpeechDetected)
                {
                    if (transcript != null && !string.IsNullOrEmpty(permanentAudioPath))
                    {
                        MarkTranscriptAsNoSpeechFailure(transcript, txEx.ProviderName);
                        TranscriptionDiagnosticsService.CaptureNoSpeechDiagnostic(
                            transcriptId: transcript.Id,
                            audioPath: permanentAudioPath,
                            fallbackDurationSeconds: RecordingDuration.TotalSeconds,
                            mode: recordingMode,
                            diagnosticStage: "live_recording",
                            diagnosticSource: "provider_no_speech",
                            inputDeviceName: SelectedAudioDevice?.Name,
                            transcriptionProviderDisplayName: txEx.ProviderName,
                            providerDiagnostics: txEx.ProviderDiagnostics,
                            exception: txEx);
                    }

                    ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                        txEx.GetUserMessage(),
                        showSettingsButton: false));
                    StatusText = Loc.S("status.failed", txEx.GetUserMessage());
                    return;
                }

                if (transcript != null)
                {
                    MarkTranscriptAsGenericFailure(transcript, txEx);
                }
                var showSettings = txEx.Code is TranscriptionErrorCode.ApiKeyMissing or TranscriptionErrorCode.Unauthorized;
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    txEx.GetUserMessage(),
                    showSettingsButton: showSettings,
                    openApiKeysManager: showSettings));
            }
            else
            {
                if (transcript != null)
                {
                    MarkTranscriptAsGenericFailure(transcript, ex);
                }
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.transcriptionFailed", ex.Message),
                    showSettingsButton: false));
            }

            StatusText = Loc.S("status.failed", ex.Message);
        }
        finally
        {
            // SAFETY NET: Ensure the transcript is never left stuck in Processing.
            // If any code path above returned or threw without writing a terminal
            // status, flip it to Failed here so the History row doesn't spin forever.
            // See tasks/windows/phils-feedback/05-processing-audio-stuck-state.md
            if (!transcriptDeleted)
            {
                EnsureTranscriptTerminalStatus(transcript);
            }

            _hotkeyBlocked = false;
            LoggingService.LogPerformanceMarker("TranscriptionFlow", "Hotkey unblocked");
            IsTranscribing = false;
            LoggingService.LogPerformanceMarker("TranscriptionFlow", $"Exit transcribing state after {hangWatch.ElapsedMilliseconds}ms");
            hangCts.Cancel();
            hangCts.Dispose();
            _toggleShortcutHeld = false;
            _pushToTalkMonitor.Reset();
            _shortcutService.ResetKeyboardState();

            // CLIPBOARD PRESERVATION - STEP 3: End recording session
            // Mark the recording session as complete. This allows the clipboard
            // restoration to clear its saved data after restoration is complete.
            _pasteService?.EndRecordingSession();
            _activeRecordingMode = null;
            if (ReferenceEquals(_activeTranscriptionCts, transcriptionCts))
            {
                _activeTranscriptionCts = null;
            }
            transcriptionCts.Dispose();
            _isStoppingRecording = false;

            // NOTE: Audio file is no longer deleted - kept for history and retry
        }
    }

    // =========================================================================
    // CANCEL RECORDING
    // Implements macOS-style cancel flow:
    // - Escape pressed during recording triggers HandleCancelRequest()
    // - Recording < 15 seconds: Cancel immediately without confirmation
    // - Recording >= 15 seconds: Show confirmation dialog
    // - Confirmation dialog: "Cancel?" with No (Escape) / Yes (Enter) buttons
    // =========================================================================

    /// <summary>
    /// Threshold in seconds before showing cancel confirmation.
    /// Matches macOS app behavior: short recordings cancel immediately,
    /// longer recordings require confirmation to prevent accidental data loss.
    /// </summary>
    private const double CANCEL_CONFIRMATION_THRESHOLD_SECONDS = 15.0;

    /// <summary>
    /// Tracks whether the cancel confirmation dialog is currently shown.
    /// Used to handle Escape/Enter key responses in the confirmation state.
    /// </summary>
    [ObservableProperty]
    private bool _showingCancelConfirmation;

    /// <summary>
    /// Called when user presses Escape during recording.
    /// Implements the same logic as macOS TranscriptionCoordinator.handleCancelShortcut():
    /// - If confirmation is visible: dismiss it (resume recording)
    /// - If transcribing: cancel immediately
    /// - If recording < 15s: cancel immediately
    /// - If recording >= 15s: show confirmation
    /// </summary>
    [RelayCommand]
    public async Task HandleCancelRequest()
    {
        if (_isStreamingStarting)
        {
            CancelStreamingStart();
            return;
        }

        // STATE 1: Cancel confirmation is visible
        // Escape dismisses confirmation and resumes recording
        if (ShowingCancelConfirmation)
        {
            ShowingCancelConfirmation = false;
            HideCancelConfirmationRequested?.Invoke(this, EventArgs.Empty);
            LoggingService.Debug("Cancel confirmation dismissed, continuing recording");
            return;
        }

        // STATE 2: Currently transcribing or post-processing
        // Cancel immediately (no confirmation needed, user can retry)
        if (IsTranscribing)
        {
            if (CancelActiveTranscription())
            {
                LoggingService.Debug("Cancelled transcription via Escape");
                HideOverlayRequested?.Invoke(this, EventArgs.Empty);
                HideFileProgressRequested?.Invoke(this, EventArgs.Empty);
                StatusText = Loc.S("status.recordingCancelled");
            }
            return;
        }

        // STATE 3: Not recording - nothing to cancel
        if (!IsRecording)
        {
            return;
        }

        if (_isStreamingSession)
        {
            await CancelRecordingAsync();
            return;
        }

        // STATE 4: Recording in progress
        // Check duration threshold for confirmation
        double durationSeconds = RecordingDuration.TotalSeconds;

        if (durationSeconds >= CANCEL_CONFIRMATION_THRESHOLD_SECONDS)
        {
            // Long recording: show confirmation to prevent accidental data loss
            ShowingCancelConfirmation = true;
            ShowCancelConfirmationRequested?.Invoke(this, EventArgs.Empty);
            LoggingService.Debug($"Showing cancel confirmation (recording duration: {durationSeconds:F1}s)");
        }
        else
        {
            // Short recording: cancel immediately
            await CancelRecordingAsync();
        }
    }

    private bool CancelActiveTranscription()
    {
        var cts = _activeTranscriptionCts;
        if (cts == null || cts.IsCancellationRequested)
        {
            return false;
        }

        LoggingService.Info("MainViewModel: Cancelling active transcription");
        cts.Cancel();
        return true;
    }

    /// <summary>
    /// Called when user confirms cancellation (presses Enter on confirmation dialog).
    /// Stops recording and hides the overlay without transcribing.
    /// </summary>
    [RelayCommand]
    public async Task ConfirmCancelRecordingAsync()
    {
        ShowingCancelConfirmation = false;
        HideCancelConfirmationRequested?.Invoke(this, EventArgs.Empty);
        await CancelRecordingAsync();
    }

    /// <summary>
    /// Actually cancels the recording: stops recorder, cleans up, hides overlay.
    /// Does NOT transcribe the audio - it is discarded.
    /// </summary>
    private async Task CancelRecordingAsync()
    {
        if (_isCancellingRecording)
        {
            return;
        }

        if (_isStreamingStarting)
        {
            CancelStreamingStart();
            return;
        }

        _isCancellingRecording = true;

        try
        {
            if (_isStreamingSession)
            {
                await CancelStreamingRecordingAsync();
                return;
            }

            LoggingService.Debug($"Cancelling recording (duration: {RecordingDuration.TotalSeconds:F1}s)");

            _durationTimer?.Stop();

            // Stop recording and discard the audio file
            // Uses Result<T> pattern to handle both success and failure cases
            var stopResult = _recorderService.StopRecording();
            _recorderService.RestoreMicVolume();
            RestoreAudioEnvironment();
            ResumeMicrophoneKeepWarm();
            IsRecording = false;

            // RESULT HANDLING: Process the result whether success or failure
            stopResult.Match(
                onSuccess: tempPath =>
                {
                    // SUCCESS: Clean up the temp audio file since we're not transcribing
                    if (File.Exists(tempPath))
                    {
                        try
                        {
                            File.Delete(tempPath);
                            LoggingService.Debug($"Deleted cancelled recording: {tempPath}");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Warn($"Failed to delete cancelled recording: {ex.Message}");
                        }
                    }
                },
                onFailure: error =>
                {
                    // FAILURE: Log the error but continue cleanup
                    // The recording may not have been active, which is fine for cancellation
                    LoggingService.Debug($"CancelRecording: StopRecording failed (expected if not recording) - {error}");
                });

            HideOverlayRequested?.Invoke(this, EventArgs.Empty);
            StatusText = Loc.S("status.recordingCancelled");
            _toggleShortcutHeld = false;
            _pushToTalkMonitor.Reset();
            _shortcutService.ResetKeyboardState();
            _pasteService?.EndRecordingSession();
        }
        finally
        {
            _isCancellingRecording = false;
        }
    }

    private async Task CancelStreamingRecordingAsync()
    {
        LoggingService.Debug($"Cancelling streaming recording (duration: {RecordingDuration.TotalSeconds:F1}s)");

        _durationTimer?.Stop();
        _streamingAudioCapture?.Stop();
        _recorderService.RestoreMicVolume();
        RestoreAudioEnvironment();
        ResumeMicrophoneKeepWarm();
        IsRecording = false;

        try
        {
            if (_streamingClient != null)
            {
                await _streamingClient.StopAsync();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"CancelStreamingRecordingAsync: Stop failed - {ex.Message}");
        }

        await CleanupStreamingSessionAsync();
        HideOverlayRequested?.Invoke(this, EventArgs.Empty);
        StatusText = Loc.S("status.recordingCancelled");
        _toggleShortcutHeld = false;
        _pushToTalkMonitor.Reset();
        _shortcutService.ResetKeyboardState();
        _pasteService?.EndRecordingSession();
    }

    // =========================================================================
    // FILE TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Opens a file dialog and transcribes the selected audio file.
    /// Implements file transcription with the same provider routing as live recording.
    /// </summary>
    [RelayCommand]
    private async Task TranscribeFile()
    {
        if (SelectedMode == null)
        {
            LoggingService.Warn($"TranscribeFile: No mode selected - ModeCount={Modes.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noModeSelected"),
                showSettingsButton: false));
            return;
        }

        await TranscribeFileWithModeAsync(SelectedMode);
    }

    public async Task TranscribeFileWithModeAsync(Mode mode)
    {
        if (!CanStartFileTranscription())
        {
            return;
        }

        // Open file dialog
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.S("file.transcribe.dialogTitle"),
            Filter = FileTranscriptionService.FileFilter,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            await TranscribeFileAsync(dialog.FileName, mode);
        }
    }

    /// <summary>
    /// Transcribes an audio file using the selected mode and provider.
    ///
    /// FLOW:
    /// 1. Validate file and mode selection
    /// 2. Check file size limits per provider
    /// 3. Show transcribing overlay
    /// 4. Convert file to 16kHz mono WAV (Whisper format)
    /// 5. Get audio duration
    /// 6. Save to permanent storage
    /// 7. Create processing transcript in History
    /// 8. Transcribe via orchestrator (same as live recording)
    /// 9. Update transcript with results
    /// 10. Smart paste/copy to clipboard
    /// 11. Show success and cleanup
    ///
    /// ERROR HANDLING:
    /// - File not found: Error toast
    /// - File too large: Error toast with max size
    /// - Conversion failed: Error toast with reason
    /// - Transcription failed: Create failed transcript in History for retry
    /// </summary>
    public async Task TranscribeFileAsync(string filePath)
    {
        if (!CanStartFileTranscription())
        {
            return;
        }

        if (SelectedMode == null)
        {
            LoggingService.Warn($"TranscribeFileAsync: No mode selected - ModeCount={Modes.Count}");
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.noModeSelected"),
                showSettingsButton: false));
            return;
        }

        await TranscribeFileAsync(filePath, SelectedMode);
    }

    private async Task TranscribeFileAsync(string filePath, Mode mode)
    {
        if (!CanStartFileTranscription())
        {
            return;
        }

        // STEP 1: Validate file
        if (!File.Exists(filePath))
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.fileNotFound"), showSettingsButton: false));
            return;
        }

        // STEP 2: Check file size per provider
        var fileInfo = new FileInfo(filePath);
        var maxSize = GetMaxFileSizeForProvider(mode);
        if (fileInfo.Length > maxSize)
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.fileTooLarge", FileTranscriptionService.FormatFileSize(maxSize)),
                showSettingsButton: false));
            return;
        }

        if (!await EnsureLocalProviderReadyForFileAsync(mode))
        {
            return;
        }

        // FILE TRANSCRIPTION PROGRESS TRACKING
        var fileName = Path.GetFileName(filePath);
        bool isCancelled = false;
        var transcriptionCts = new CancellationTokenSource();
        _activeTranscriptionCts = transcriptionCts;
        var transcriptDeleted = false;

        // STEP 3: Show progress window
        IsTranscribing = true;
        ShowFileProgressRequested?.Invoke(this, new FileTranscriptionProgressEventArgs(
            fileName,
            onCancel: () =>
            {
                isCancelled = true;
                CancelActiveTranscription();
            }
        ));

        Transcript? transcript = null;
        string? permanentPath = null;
        string? convertedTempPath = null;
        double duration = 0;

        try
        {
            LoggingService.Info($"TranscribeFileAsync: Starting file transcription - {filePath}");

            // STEP 4: Preparing stage (0-15%) - Convert format if needed
            UpdateFileProgressRequested?.Invoke(this, 0.05f);
            string pathForTranscription;

            if (mode.ProviderType == "cloud")
            {
                // Cloud providers accept mp3/m4a/wav natively — send original file as-is
                pathForTranscription = filePath;
                LoggingService.Info($"TranscribeFileAsync: Cloud mode - skipping WAV conversion, using original file");
            }
            else
            {
                // Local WhisperNet requires 16kHz mono WAV
                var convertResult = await FileTranscriptionService.ConvertToWhisperFormatAsync(
                    filePath,
                    transcriptionCts.Token);
                if (convertResult.IsFailure)
                {
                    throw new Exception(convertResult.Error);
                }
                pathForTranscription = convertResult.Value!;
                convertedTempPath = convertResult.Value!;
                LoggingService.Info($"TranscribeFileAsync: Local mode - converted to WAV: {pathForTranscription}");
            }
            transcriptionCts.Token.ThrowIfCancellationRequested();

            // STEP 5: Get duration
            UpdateFileProgressRequested?.Invoke(this, 0.10f);
            var durationResult = FileTranscriptionService.GetAudioDuration(pathForTranscription);
            if (durationResult.IsFailure)
            {
                throw new Exception(durationResult.Error);
            }
            transcriptionCts.Token.ThrowIfCancellationRequested();
            duration = durationResult.Value;

            // STEP 6: Save file to permanent location
            UpdateFileProgressRequested?.Invoke(this, 0.15f);
            permanentPath = HistoryService.Instance.SaveAudioFile(pathForTranscription);
            LoggingService.Info($"TranscribeFileAsync: Audio saved ({permanentPath}, {fileInfo.Length:N0} bytes, {duration:F2}s)");

            // STEP 7: Create processing transcript
            transcript = HistoryService.Instance.CreateProcessingTranscript(
                duration, mode.Name, permanentPath);

            // STEP 8: Transcribing stage (15-85%) - Start slow animation to 80%
            // (will be cut short when transcription completes)
            UpdateFileProgressRequested?.Invoke(this, 0.80f);

            var vocabulary = _vocabularyService.GetVocabularyWords(100);
            var result = await _transcriptionOrchestrator.TranscribeAsync(
                permanentPath, mode, vocabulary,
                localTranscriptionProvider: GetLocalProvider(mode),
                cancellationToken: transcriptionCts.Token);
            transcriptionCts.Token.ThrowIfCancellationRequested();

            // STEP 9: Finishing stage (85-100%) - Update transcript with results
            UpdateFileProgressRequested?.Invoke(this, 0.85f);
            transcript.Text = result.FinalText;
            transcript.TranscribedText = result.RawText;
            transcript.PostProcessedText = result.PostProcessedText;
            transcript.Status = TranscriptStatus.Completed;
            transcript.TranscriptionProvider = result.TranscriptionProvider;
            transcript.PostProcessingProvider = result.PostProcessingProvider;

            // STORAGE: Optionally compress to M4A for space savings (local mode saves WAV)
            if (_storageService.StoreAsM4A && convertedTempPath != null)
            {
                var compressedPath = _storageService.TryConvertWavToM4A(permanentPath);
                if (!string.IsNullOrEmpty(compressedPath))
                {
                    transcript.AudioFilePath = compressedPath;
                }
            }

            HistoryService.Instance.UpdateTranscript(transcript);
            LoggingService.Info($"TranscribeFileAsync: Transcription complete - {result.FinalText.Length} chars");

            // STEP 10: Navigate to History to show result
            UpdateFileProgressRequested?.Invoke(this, 0.95f);
            CurrentPage = NavigationPage.History;

            // STEP 11: Complete and cleanup
            UpdateFileProgressRequested?.Invoke(this, 1.0f);
            await Task.Delay(500); // Brief pause to show 100%
            HideFileProgressRequested?.Invoke(this, EventArgs.Empty);

            // Clean up temp file if conversion created one (local mode only)
            if (convertedTempPath != null && convertedTempPath != permanentPath && File.Exists(convertedTempPath))
            {
                try
                {
                    File.Delete(convertedTempPath);
                    LoggingService.Debug($"TranscribeFileAsync: Deleted temp file - {convertedTempPath}");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"TranscribeFileAsync: Failed to delete temp file: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (transcriptionCts.IsCancellationRequested || isCancelled)
        {
            LoggingService.Info("TranscribeFileAsync: File transcription cancelled by user");
            HideFileProgressRequested?.Invoke(this, EventArgs.Empty);
            StatusText = Loc.S("status.recordingCancelled");

            if (transcript != null)
            {
                transcriptDeleted = HistoryService.Instance.DeleteTranscript(transcript.Id);
            }
            else if (!string.IsNullOrEmpty(permanentPath))
            {
                HistoryService.Instance.DeleteAudioFile(permanentPath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error($"TranscribeFileAsync failed: {ex.Message}", ex);
            HideFileProgressRequested?.Invoke(this, EventArgs.Empty);

            if (transcript != null)
            {
                if (ex is TranscriptionException txEx && txEx.Code == TranscriptionErrorCode.NoSpeechDetected && permanentPath != null)
                {
                    MarkTranscriptAsNoSpeechFailure(transcript, txEx.ProviderName);
                    TranscriptionDiagnosticsService.CaptureNoSpeechDiagnostic(
                        transcriptId: transcript.Id,
                        audioPath: permanentPath,
                        fallbackDurationSeconds: duration,
                        mode: mode,
                        diagnosticStage: "file_transcription",
                        diagnosticSource: "provider_no_speech",
                        transcriptionProviderDisplayName: txEx.ProviderName,
                        providerDiagnostics: txEx.ProviderDiagnostics,
                        exception: txEx);

                    ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                        txEx.GetUserMessage(),
                        showSettingsButton: false));
                }
                else
                {
                    MarkTranscriptAsGenericFailure(transcript, ex);
                    ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                        Loc.S("errors.transcriptionFailed", ex.Message), showSettingsButton: false));
                }
            }
            else
            {
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    ex is TranscriptionException txEx
                        ? txEx.GetUserMessage()
                        : Loc.S("errors.transcriptionFailed", ex.Message),
                    showSettingsButton: false));
            }
        }
        finally
        {
            // SAFETY NET: Ensure the transcript is never left stuck in Processing.
            // The isCancelled early-returns above bail out without writing a terminal
            // status, which would leave the History row spinning forever.
            if (!transcriptDeleted)
            {
                EnsureTranscriptTerminalStatus(transcript);
            }

            if (convertedTempPath != null && convertedTempPath != permanentPath && File.Exists(convertedTempPath))
            {
                try
                {
                    File.Delete(convertedTempPath);
                    LoggingService.Debug($"TranscribeFileAsync: Deleted temp file - {convertedTempPath}");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"TranscribeFileAsync: Failed to delete temp file: {ex.Message}");
                }
            }

            IsTranscribing = false;
            if (ReferenceEquals(_activeTranscriptionCts, transcriptionCts))
            {
                _activeTranscriptionCts = null;
            }
            transcriptionCts.Dispose();
        }
    }

    private bool CanStartFileTranscription()
    {
        if (IsRecording || IsTranscribing || _activeTranscriptionCts != null)
        {
            LoggingService.Warn("File transcription requested while recording or transcribing; ignoring request");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Provider file size limits (matches macOS FileTranscriptionFlow.swift).
    ///
    /// LIMITS:
    /// - Local: No limit (only constrained by available memory/disk)
    /// - Cloud: Uses the selected CloudProvider's declared max size
    /// - Missing cloud provider: falls back to a conservative 25 MB
    /// </summary>
    private long GetMaxFileSizeForProvider(Mode mode)
    {
        if (mode.ProviderType?.Equals("local", StringComparison.OrdinalIgnoreCase) == true)
        {
            return long.MaxValue;
        }

        if (mode.ProviderType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true)
        {
            var provider = CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider);
            return provider != CloudTranscriptionProvider.None
                ? provider.GetMaxFileSizeBytes()
                : 25L * 1024 * 1024;
        }

        return long.MaxValue;
    }

    private async Task<bool> EnsureLocalProviderReadyForFileAsync(Mode mode)
    {
        if (mode.ProviderType?.Equals("cloud", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (mode.LocalEngine == "parakeet")
        {
            var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == mode.LocalParakeetModel);
            if (model == null || !_parakeetModelService.IsModelDownloaded(model))
            {
                var modelName = mode.LocalParakeetModel ?? "Unknown";
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.modelNotDownloaded", modelName),
                    showSettingsButton: false));
                return false;
            }

            string? language = mode.Language == "auto" ? null : mode.Language;
            var effectiveLanguage = language ?? "auto";

            // The daemon's startup language/join hint affects some Parakeet-family
            // engines, so a warm daemon matching the model but a different language
            // must reinitialize before file transcription.
            if (_parakeetTranscriptionService.IsInitialized &&
                _parakeetTranscriptionService.LoadedModelId == model.Id &&
                string.Equals(_parakeetTranscriptionService.LoadedLanguage, effectiveLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                if (GetTotalSystemMemoryGB() < 32 && _transcriptionService.IsInitialized)
                {
                    LoggingService.Info("EnsureLocalProviderReadyForFileAsync: Unloading Whisper model to free memory before Parakeet file transcription");
                    _transcriptionService.UnloadModel();
                }

                IsModelLoading = true;
                StatusText = Loc.S("status.model.parakeet.loading", model.DisplayName);
                await _parakeetTranscriptionService.InitializeAsync(
                    _parakeetModelService.GetModelDirectory(model),
                    language);
                IsModelLoaded = true;
                ModelStatus = Loc.S("status.model.parakeet.ready", model.DisplayName, _parakeetTranscriptionService.ActiveProvider ?? "CPU");
                return true;
            }
            catch (Exception ex)
            {
                ModelStatus = Loc.S("status.model.loadFailed");
                StatusText = Loc.S("status.failed", ex.Message);
                LoggingService.Error($"EnsureLocalProviderReadyForFileAsync: Parakeet model load failed - {ex.Message}", ex);
                ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                    Loc.S("errors.modelLoadFailed"),
                    showSettingsButton: false));
                return false;
            }
            finally
            {
                IsModelLoading = false;
            }
        }

        if (!PlatformHelper.SupportsWhisperTranscription)
        {
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.modelLoadFailed"),
                showSettingsButton: false));
            return false;
        }

        var whisperModel = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == mode.ModelType);
        if (whisperModel == null || !_modelService.IsModelDownloaded(whisperModel))
        {
            var modelName = mode.ModelType ?? "Unknown";
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.modelNotDownloaded", modelName),
                showSettingsButton: false));
            return false;
        }

        var modelPath = _modelService.GetModelPath(whisperModel);
        if (_transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath)
        {
            return true;
        }

        if (_parakeetTranscriptionService.IsInitialized)
        {
            LoggingService.Info("EnsureLocalProviderReadyForFileAsync: Disposing Parakeet daemon before Whisper file transcription");
            _parakeetTranscriptionService.DisposeModel();
        }

        await _modelLoadLock.WaitAsync();
        try
        {
            if (_transcriptionService.IsInitialized && _transcriptionService.LoadedModelPath == modelPath)
            {
                return true;
            }

            IsModelLoading = true;
            StatusText = Loc.S("status.model.loading", whisperModel.DisplayName);
            await _transcriptionService.InitializeAsync(modelPath, p => { }, CancellationToken.None);
            IsModelLoaded = true;
            ModelStatus = Loc.S("status.model.ready", whisperModel.DisplayName);
            return true;
        }
        catch (Exception ex)
        {
            ModelStatus = Loc.S("status.model.loadFailed");
            StatusText = Loc.S("status.failed", ex.Message);
            LoggingService.Error($"EnsureLocalProviderReadyForFileAsync: Whisper model load failed - {ex.Message}", ex);
            ShowErrorToastRequested?.Invoke(this, new ErrorToastEventArgs(
                Loc.S("errors.modelLoadFailed"),
                showSettingsButton: false));
            return false;
        }
        finally
        {
            IsModelLoading = false;
            _modelLoadLock.Release();
        }
    }

    // =========================================================================
    // CLEANUP
    // =========================================================================

    /// <summary>
    /// Returns the appropriate local transcription provider based on the selected mode's engine,
    /// or null for cloud modes.
    /// </summary>
    private ITranscriptionProvider? GetLocalProvider()
    {
        return SelectedMode == null ? null : GetLocalProvider(SelectedMode);
    }

    private ITranscriptionProvider? GetLocalProvider(Mode mode)
    {
        if (mode.ProviderType == "cloud") return null;
        return mode.LocalEngine == "parakeet"
            ? _parakeetTranscriptionService
            : _transcriptionService;
    }

    /// <summary>
    /// Checks if the current local provider is ready to transcribe.
    /// </summary>
    private bool IsLocalProviderReady()
    {
        return SelectedMode != null && IsLocalProviderReady(SelectedMode);
    }

    private bool IsLocalProviderReady(Mode mode)
    {
        if (mode.LocalEngine == "parakeet")
            return _parakeetTranscriptionService.IsAvailable;
        return _transcriptionService.IsInitialized;
    }

    /// <summary>
    /// Checks if the local model for the selected mode is downloaded.
    /// </summary>
    private bool IsLocalModelDownloaded()
    {
        return SelectedMode != null && IsLocalModelDownloaded(SelectedMode);
    }

    private bool IsLocalModelDownloaded(Mode mode)
    {
        if (mode.LocalEngine == "parakeet")
        {
            var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == mode.LocalParakeetModel);
            return model != null && _parakeetModelService.IsModelDownloaded(model);
        }
        else
        {
            var model = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == mode.ModelType);
            return model != null && _modelService.IsModelDownloaded(model);
        }
    }

    private void MarkTranscriptAsNoSpeechFailure(Transcript transcript, string? transcriptionProvider = null)
    {
        if (!string.IsNullOrWhiteSpace(transcriptionProvider))
        {
            transcript.TranscriptionProvider = transcriptionProvider;
        }

        transcript.Status = TranscriptStatus.Failed;
        transcript.FailedReason = Loc.S("errors.noSpeechDetected");
        transcript.Text = Loc.S("errors.noSpeechDetected");
        HistoryService.Instance.UpdateTranscript(transcript);
    }

    /// <summary>
    /// Safety net invoked from the <c>finally</c> block of every transcription flow.
    /// If a transcript is somehow still in <see cref="TranscriptStatus.Processing"/>
    /// when we exit the flow (early return, swallowed exception, task abort),
    /// flip it to Failed so the History row stops spinning forever.
    /// See tasks/windows/phils-feedback/05-processing-audio-stuck-state.md
    /// </summary>
    private static void EnsureTranscriptTerminalStatus(Transcript? transcript)
    {
        if (transcript == null) return;

        try
        {
            var persisted = HistoryService.Instance.GetTranscript(transcript.Id);
            if (persisted?.Status == TranscriptStatus.Processing &&
                transcript.Status != TranscriptStatus.Processing)
            {
                HistoryService.Instance.UpdateTranscript(transcript);
                LoggingService.Warn($"EnsureTranscriptTerminalStatus: Repaired persisted Processing transcript {transcript.Id} with in-memory {transcript.Status} status");
                return;
            }

            if (transcript.Status != TranscriptStatus.Processing) return;

            const string reason = "Transcription did not finish";
            transcript.Status = TranscriptStatus.Failed;
            transcript.FailedReason = string.IsNullOrWhiteSpace(transcript.FailedReason)
                ? reason
                : transcript.FailedReason;
            transcript.Text = reason;
            HistoryService.Instance.UpdateTranscript(transcript);
            LoggingService.Warn($"EnsureTranscriptTerminalStatus: Flipped orphaned transcript {transcript.Id} from Processing to Failed");
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"EnsureTranscriptTerminalStatus: Failed to enforce terminal status for transcript {transcript.Id}: {ex.Message}");
        }
    }

    private void MarkTranscriptAsGenericFailure(Transcript transcript, Exception ex)
    {
        if (ex is TranscriptionException txEx && !string.IsNullOrWhiteSpace(txEx.ProviderName))
        {
            transcript.TranscriptionProvider = txEx.ProviderName;
        }

        transcript.Status = TranscriptStatus.Failed;
        transcript.FailedReason = ex.Message;
        transcript.Text = $"Transcription failed: {ex.Message}";
        HistoryService.Instance.UpdateTranscript(transcript);
    }

    public async Task CleanupAsync()
    {
        // Unsubscribe from all events to prevent memory leaks
        CancelActiveTranscription();
        _activeTranscriptionCts?.Dispose();
        _activeTranscriptionCts = null;

        _recorderService.AudioLevelChanged -= _audioLevelHandler;
        _pushToTalkMonitor.Pressed -= OnPushToTalkPressed;
        _pushToTalkMonitor.Released -= OnPushToTalkReleased;
        _pushToTalkMonitor.Interfered -= OnPushToTalkInterfered;
        ModeService.Instance.ModeChanged -= _modeChangedHandler;
        ModeService.Instance.ModeSelected -= _modeSelectedHandler;
        _shortcutService.ShortcutPressed -= OnShortcutPressed;
        _shortcutService.ShortcutReleased -= OnShortcutReleased;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _transcriptionOrchestrator.PostProcessingWarning -= _orchestratorWarningHandler;

        // Use try-finally to ensure device service cleanup happens
        // even if other Dispose calls throw exceptions
        try
        {
            _durationTimer?.Dispose();
            await CleanupStreamingSessionAsync();
            _recorderService.RestoreMicVolume();
            await RestoreAudioEnvironmentImmediatelyAsync();
            ResumeMicrophoneKeepWarm();
            _activeRecordingMode = null;
            var shouldFlushPendingClipboardRestore = _pasteService?.HasPendingClipboardRestore == true;
            _pasteService?.EndRecordingSession();
            if (shouldFlushPendingClipboardRestore)
            {
                _pasteService?.RestoreClipboardImmediately();
            }
            _shortcutService?.Dispose();
            _pushToTalkMonitor?.Dispose();
            _recorderService?.Dispose();
            // _transcriptionService, _parakeetTranscriptionService, and
            // _transcriptionOrchestrator are owned by TranscriptionRuntime
            // (shared with LocalApiServer). They outlive the window — the OS
            // reclaims native handles at process exit. Disposing here would
            // leave the API with dead instances.
            MicrophoneKeepWarmService.Instance.Dispose();

            // Dispose smart paste service (CancellationTokenSource cleanup)
            _pasteService?.Dispose();
        }
        finally
        {
            // Always clean up device monitoring to prevent COM callback leaks
            try
            {
                _deviceService.DevicesChanged -= OnAudioDevicesChanged;
            }
            finally
            {
                _deviceService.Dispose();
            }
        }
    }
}
