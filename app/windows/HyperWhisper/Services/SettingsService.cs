using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// SETTINGS SERVICE
///
/// Persists user preferences to a JSON file in %LOCALAPPDATA%\HyperWhisper\settings.json.
/// This keeps settings alongside the logs folder for easy backup and portability.
///
/// STORED SETTINGS:
/// - LastSelectedModel: The model file name that was last selected
/// - LastSelectedMicrophone: The microphone device ID that was last selected (future)
///
/// FILE FORMAT:
/// {
///   "lastSelectedModel": "ggml-base.bin",
///   "lastSelectedMicrophone": "device-guid"
/// }
///
/// THREAD SAFETY:
/// - Property setters are intended to be used from the UI thread (the normal usage
///   pattern), but file I/O in Load/Save is guarded by an instance lock (_ioLock) so
///   that background writers (e.g. BackupService.Import, which runs on a Task and
///   triggers Save() via property setters) cannot tear settings.json with the UI thread.
/// - Setters mutate the in-memory <see cref="_settings"/> object graph (which Save()
///   then serializes). That field mutation is NOT guarded by _ioLock, so callers that
///   are not on the UI thread must funnel their setter batch through
///   <see cref="ApplyImport"/>, which marshals the whole batch onto the UI thread. This
///   keeps every _settings mutation single-threaded relative to normal UI usage (so
///   Save()'s serialization never reads a half-mutated object graph) and keeps the
///   UI-affine SettingsChanged handlers (e.g. global-shortcut re-registration) on the
///   UI thread. NotifySettingsChanged() additionally marshals to the UI thread
///   defensively so a stray off-thread setter cannot fire UI-affine handlers off-thread.
///
/// DURABILITY:
/// - Save() writes atomically (temp file + rename) so a crash/force-kill mid-write
///   never leaves a truncated settings.json. Load() falls back to the .bak left by the
///   previous atomic save before resetting to defaults.
/// </summary>
public partial class SettingsService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int LatestSettingsVersion = 3;

    private static readonly string SettingsFolder = AppPaths.AppDataRoot;

    private static readonly string SettingsFilePath = Path.Combine(SettingsFolder, "settings.json");

    // Previous-good copy written by File.Replace during an atomic Save(); Load() falls
    // back to this if the live settings.json is corrupt (e.g. torn by a crash mid-write).
    private static readonly string BackupFilePath = SettingsFilePath + ".bak";

    // =========================================================================
    // SINGLETON INSTANCE
    // =========================================================================

    private static SettingsService? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of SettingsService.
    /// Thread-safe lazy initialization.
    /// </summary>
    public static SettingsService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new SettingsService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // SETTINGS DATA
    // =========================================================================

    /// <summary>
    /// The internal settings data structure.
    /// Properties here are serialized to/from JSON.
    /// </summary>
    private class SettingsData
    {
        public int Version { get; set; } = 1;
        public string? LastSelectedModel { get; set; }
        public string? ModelLibraryLanguageFilter { get; set; }
        public string? LastSelectedMicrophone { get; set; }
        public Guid? SelectedModeId { get; set; }
        public string? ToggleShortcut { get; set; }
        public string? CancelShortcut { get; set; }
        public string? ChangeModeShortcut { get; set; }
        public string? StreamingShortcut { get; set; }
        public PushToTalkSettings? PushToTalk { get; set; }
        public string? RecordingsFolder { get; set; }
        public bool? StoreAsM4A { get; set; }
        public bool? UserChoseAlternateStorage { get; set; }

        // General settings
        public bool? AutoPasteEnabled { get; set; }
        public bool? LaunchMinimized { get; set; }
        public bool? ShowRecordingWindow { get; set; }
        public bool? MinimizeToTray { get; set; }

        // Output settings
        public bool? RemoveFillerWords { get; set; }
        public bool? AutocapitalizeInsert { get; set; }

        // Clipboard restoration settings
        public bool? RestoreClipboardAfterPaste { get; set; }
        public double? ClipboardRestoreDelaySeconds { get; set; }
        public bool? HideFromClipboardHistory { get; set; }

        // Appearance settings
        public int? ThemeMode { get; set; }

        // Logging & Updates settings
        public bool? EnableErrorLogging { get; set; }
        public bool? CheckForUpdatesAutomatically { get; set; }

        // Auto-delete settings
        public bool? AutoDeleteEnabled { get; set; }
        public int? AutoDeleteDaysOld { get; set; }

        // Sound settings
        public bool? EnableSoundEffects { get; set; }
        public bool? AutoIncreaseMicVolume { get; set; }
        public bool? KeepMicrophoneWarm { get; set; }
        public string? MediaControlMode { get; set; }

        // Streaming transcription settings
        public bool? StreamingEnabled { get; set; }
        public string? StreamingProvider { get; set; }
        public string? StreamingLanguage { get; set; }
        public string? StreamingDeepgramModel { get; set; }
        public bool? StreamingFastFormatting { get; set; }

        // Recording overlay position (screen ratios)
        public double? RecordingOverlayXRatio { get; set; }
        public double? RecordingOverlayYRatio { get; set; }

        // Getting Started checklist
        public string? GettingStartedCompletedSteps { get; set; }

        // Parakeet engine feature flag
        public bool? ParakeetEnabled { get; set; }

        // Custom OpenAI-compatible endpoints for post-processing
        public List<CustomPostProcessingEndpoint>? CustomEndpoints { get; set; }

        // Home stats bar — assumed typing speed used to compute "minutes saved"
        public int? TypingSpeedWPM { get; set; }

        // Local HTTP API (Settings → Local API)
        public bool? LocalApiServerEnabled { get; set; }
        public int? LocalApiServerPersistedPort { get; set; }
    }

    private SettingsData _settings;
    private bool _settingsFileExists;

    // Serializes file I/O in Load()/Save(). Distinct from the static singleton-init
    // _lock — BackupService.Import runs on a background Task and mutates settings
    // (each setter calls Save()) concurrently with the UI thread, so writes must be
    // guarded to avoid interleaved/torn writes to settings.json.
    private readonly object _ioLock = new();

    /// <summary>
    /// True if this is a fresh install (no settings file existed when the app started).
    /// Used to apply first-launch defaults like enabling startup registration.
    /// </summary>
    public bool IsFirstLaunch => !_settingsFileExists;

    public event EventHandler? SettingsChanged;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private SettingsService()
    {
        _settings = new SettingsData();
        Load();
        ApplyDefaults();
    }

    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    /// <summary>
    /// Gets or sets the folder where recordings are stored.
    /// Defaults to Documents\\HyperWhisper\\recordings for new installs,
    /// but retains legacy %LOCALAPPDATA%\\HyperWhisper\\Audio for existing users.
    /// </summary>
    public string RecordingsFolder
    {
        get => string.IsNullOrWhiteSpace(_settings.RecordingsFolder)
            ? GetDefaultRecordingsFolder()
            : _settings.RecordingsFolder!;
        set
        {
            if (_settings.RecordingsFolder != value)
            {
                _settings.RecordingsFolder = value;
                Save();
                LoggingService.Debug($"SettingsService: RecordingsFolder set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to compress WAV recordings to M4A after transcription.
    /// When enabled, completed WAV recordings are converted to AAC M4A files
    /// using Windows Media Foundation to reduce disk usage.
    /// Default: true for new installs; existing settings files missing this key keep legacy WAV storage.
    /// </summary>
    public bool StoreAsM4A
    {
        get => _settings.StoreAsM4A ?? (!_settingsFileExists);
        set
        {
            if (StoreAsM4A != value)
            {
                _settings.StoreAsM4A = value;
                Save();
                LoggingService.Debug($"SettingsService: StoreAsM4A set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Tracks if the user explicitly selected an alternate storage location.
    /// Used to avoid repeatedly prompting when the default location is unavailable.
    /// </summary>
    public bool UserChoseAlternateStorage
    {
        get => _settings.UserChoseAlternateStorage ?? false;
        set
        {
            if ((_settings.UserChoseAlternateStorage ?? false) != value)
            {
                _settings.UserChoseAlternateStorage = value;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the selected mode ID.
    /// Setting this property automatically saves to disk.
    /// </summary>
    public Guid? SelectedModeId
    {
        get => _settings.SelectedModeId;
        set
        {
            if (_settings.SelectedModeId != value)
            {
                _settings.SelectedModeId = value;
                Save();
                LoggingService.Debug($"SettingsService: SelectedModeId set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the last selected model file name (e.g., "ggml-base.bin").
    /// Setting this property automatically saves to disk.
    /// </summary>
    public string? LastSelectedModel
    {
        get => _settings.LastSelectedModel;
        set
        {
            if (_settings.LastSelectedModel != value)
            {
                _settings.LastSelectedModel = value;
                Save();
                LoggingService.Debug($"SettingsService: LastSelectedModel set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Persisted base language code for the Model Library language filter.
    /// Empty string = "Any language" (no filtering). Restored on next open.
    /// Setting this property automatically saves to disk.
    /// </summary>
    public string ModelLibraryLanguageFilter
    {
        get => _settings.ModelLibraryLanguageFilter ?? "";
        set
        {
            var normalized = value ?? "";
            if (_settings.ModelLibraryLanguageFilter != normalized)
            {
                _settings.ModelLibraryLanguageFilter = normalized;
                Save();
            }
        }
    }

    /// <summary>
    /// Gets or sets the last selected microphone device ID.
    /// Setting this property automatically saves to disk.
    /// </summary>
    public string? LastSelectedMicrophone
    {
        get => _settings.LastSelectedMicrophone;
        set
        {
            if (_settings.LastSelectedMicrophone != value)
            {
                _settings.LastSelectedMicrophone = value;
                Save();
                LoggingService.Debug($"SettingsService: LastSelectedMicrophone set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // STREAMING TRANSCRIPTION SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether the streaming transcription hotkey is active.
    /// Default: false; users explicitly opt in before the streaming shortcut does anything.
    /// </summary>
    public bool StreamingEnabled
    {
        get => _settings.StreamingEnabled ?? false;
        set
        {
            if ((_settings.StreamingEnabled ?? false) != value)
            {
                _settings.StreamingEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: StreamingEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Selected streaming provider. Valid values map to StreamingTranscriptionProvider storage values.
    /// </summary>
    public string StreamingProvider
    {
        get => string.IsNullOrWhiteSpace(_settings.StreamingProvider)
            ? Models.StreamingTranscriptionProvider.HyperWhisperCloud.StorageValue()
            : _settings.StreamingProvider!;
        set
        {
            var normalized = Models.StreamingTranscriptionProviderExtensions.IsValidStorageValue(value)
                ? value
                : Models.StreamingTranscriptionProvider.HyperWhisperCloud.StorageValue();

            if (_settings.StreamingProvider != normalized)
            {
                _settings.StreamingProvider = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingProvider set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Language code used for streaming transcription. "en" by default.
    /// </summary>
    public string StreamingLanguage
    {
        get => string.IsNullOrWhiteSpace(_settings.StreamingLanguage) ? "en" : _settings.StreamingLanguage!;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "en" : value;
            if (_settings.StreamingLanguage != normalized)
            {
                _settings.StreamingLanguage = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingLanguage set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Deepgram streaming model. Nova-3 general is the default.
    /// Legacy IDs from the 2026-05 catalog cleanup are migrated to
    /// nova-3-general at construction in <see cref="ApplyDefaults"/>.
    /// </summary>
    public string StreamingDeepgramModel
    {
        get => string.IsNullOrWhiteSpace(_settings.StreamingDeepgramModel) ? "nova-3-general" : _settings.StreamingDeepgramModel!;
        set
        {
            var normalized = value is "nova-3-medical" ? "nova-3-medical" : "nova-3-general";
            if (_settings.StreamingDeepgramModel != normalized)
            {
                _settings.StreamingDeepgramModel = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingDeepgramModel set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Deepgram no-delay smart formatting. Enabled by default for lower latency.
    /// </summary>
    public bool StreamingFastFormatting
    {
        get => _settings.StreamingFastFormatting ?? true;
        set
        {
            if ((_settings.StreamingFastFormatting ?? true) != value)
            {
                _settings.StreamingFastFormatting = value;
                Save();
                LoggingService.Debug($"SettingsService: StreamingFastFormatting set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // GENERAL SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to automatically paste transcribed text into the focused application.
    /// When enabled, after transcription completes:
    /// 1. Text is copied to clipboard
    /// 2. Previously focused window is reactivated
    /// 3. Ctrl+V is simulated to paste
    ///
    /// When disabled, text is only copied to clipboard.
    /// Default: true
    /// </summary>
    public bool AutoPasteEnabled
    {
        get => _settings.AutoPasteEnabled ?? true;
        set
        {
            if ((_settings.AutoPasteEnabled ?? true) != value)
            {
                _settings.AutoPasteEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: AutoPasteEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to start the app minimized to the system tray.
    /// When enabled, the main window is hidden on startup.
    /// Users can show the window via the system tray icon.
    /// Default: false
    /// </summary>
    public bool LaunchMinimized
    {
        get => _settings.LaunchMinimized ?? false;
        set
        {
            if ((_settings.LaunchMinimized ?? false) != value)
            {
                _settings.LaunchMinimized = value;
                Save();
                LoggingService.Debug($"SettingsService: LaunchMinimized set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to show the recording overlay window during recording.
    /// When enabled, a floating window with audio level visualization is shown.
    /// When disabled, recording happens silently in the background.
    /// Default: true
    /// </summary>
    public bool ShowRecordingWindow
    {
        get => _settings.ShowRecordingWindow ?? true;
        set
        {
            if ((_settings.ShowRecordingWindow ?? true) != value)
            {
                _settings.ShowRecordingWindow = value;
                Save();
                LoggingService.Debug($"SettingsService: ShowRecordingWindow set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether clicking the window close button minimizes to system tray instead of exiting.
    /// When enabled (default), closing the window hides it to the tray.
    /// When disabled, closing the window exits the application.
    /// Default: true (matches typical utility app behavior)
    /// </summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? true;
        set
        {
            if ((_settings.MinimizeToTray ?? true) != value)
            {
                _settings.MinimizeToTray = value;
                Save();
                LoggingService.Debug($"SettingsService: MinimizeToTray set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // OUTPUT SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to remove common filler words from raw transcripts when AI post-processing is disabled.
    /// Default: true (preserves historical behavior)
    /// </summary>
    public bool RemoveFillerWords
    {
        get => _settings.RemoveFillerWords ?? true;
        set
        {
            if ((_settings.RemoveFillerWords ?? true) != value)
            {
                _settings.RemoveFillerWords = value;
                Save();
                LoggingService.Debug($"SettingsService: RemoveFillerWords set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// When enabled, lowercases the first letter of inserted transcript text
    /// if the caret is mid-sentence in the focused field. Leaves the text
    /// untouched at sentence start. Falls back to pass-through when the UIA
    /// probe can't read the focused element (e.g. some Electron/web apps).
    /// Default: true
    /// </summary>
    public bool AutocapitalizeInsert
    {
        get => _settings.AutocapitalizeInsert ?? true;
        set
        {
            if ((_settings.AutocapitalizeInsert ?? true) != value)
            {
                _settings.AutocapitalizeInsert = value;
                Save();
                LoggingService.Debug($"SettingsService: AutocapitalizeInsert set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // CLIPBOARD RESTORATION SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to restore the original clipboard content after pasting transcription.
    /// When enabled, the clipboard content that existed before recording starts
    /// is automatically restored after a configurable delay following the paste.
    ///
    /// FLOW:
    /// 1. User starts recording → original clipboard is captured
    /// 2. Transcription completes → text is pasted (overwrites clipboard)
    /// 3. After delay → original clipboard content is restored
    ///
    /// This matches macOS HyperWhisper behavior where users can paste again
    /// with their original clipboard content after the transcription is inserted.
    /// Default: true
    /// </summary>
    public bool RestoreClipboardAfterPaste
    {
        get => _settings.RestoreClipboardAfterPaste ?? true;
        set
        {
            if ((_settings.RestoreClipboardAfterPaste ?? true) != value)
            {
                _settings.RestoreClipboardAfterPaste = value;
                Save();
                LoggingService.Debug($"SettingsService: RestoreClipboardAfterPaste set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Delay in seconds before restoring the original clipboard content.
    /// This delay allows users to:
    /// - Paste the transcription multiple times if needed
    /// - Complete any clipboard operations before restoration
    ///
    /// Range: 1-60 seconds
    /// Default: 10 seconds (matches macOS default)
    /// </summary>
    public double ClipboardRestoreDelaySeconds
    {
        get => _settings.ClipboardRestoreDelaySeconds ?? 10.0;
        set
        {
            // Clamp value to valid range (1-60 seconds)
            var clampedValue = Math.Max(1.0, Math.Min(60.0, value));
            if ((_settings.ClipboardRestoreDelaySeconds ?? 10.0) != clampedValue)
            {
                _settings.ClipboardRestoreDelaySeconds = clampedValue;
                Save();
                LoggingService.Debug($"SettingsService: ClipboardRestoreDelaySeconds set to: {clampedValue}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to hide transcription text from Windows clipboard history (Win+V).
    /// Uses the ExcludeClipboardContentFromMonitorProcessing clipboard format
    /// to prevent transcriptions from appearing in clipboard history and
    /// third-party clipboard managers.
    ///
    /// Matches macOS behavior where org.nspasteboard.ConcealedType is used.
    /// Default: true
    /// </summary>
    public bool HideFromClipboardHistory
    {
        get => _settings.HideFromClipboardHistory ?? true;
        set
        {
            if ((_settings.HideFromClipboardHistory ?? true) != value)
            {
                _settings.HideFromClipboardHistory = value;
                Save();
                LoggingService.Debug($"SettingsService: HideFromClipboardHistory set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // APPEARANCE SETTINGS
    // =========================================================================

    /// <summary>
    /// The application theme mode.
    /// - System: Follows Windows system theme (light/dark)
    /// - Light: Always use light theme
    /// - Dark: Always use dark theme
    ///
    /// Default: System (follows Windows appearance)
    /// </summary>
    public Models.ThemeMode ThemeMode
    {
        get => (Models.ThemeMode)(_settings.ThemeMode ?? (int)Models.ThemeMode.System);
        set
        {
            var intValue = (int)value;
            if ((_settings.ThemeMode ?? (int)Models.ThemeMode.System) != intValue)
            {
                _settings.ThemeMode = intValue;
                Save();
                LoggingService.Debug($"SettingsService: ThemeMode set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // LOGGING & UPDATES SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether to send error reports to Sentry for crash tracking.
    /// When enabled, unhandled exceptions and errors are automatically
    /// reported to help improve the application.
    ///
    /// PRIVACY:
    /// - Transcription text is NEVER sent
    /// - Breadcrumbs are stripped before sending
    /// - Only crash data, stack traces, and system info are reported
    ///
    /// Default: true (opt-out model)
    /// </summary>
    public bool EnableErrorLogging
    {
        get => _settings.EnableErrorLogging ?? true;
        set
        {
            if ((_settings.EnableErrorLogging ?? true) != value)
            {
                _settings.EnableErrorLogging = value;
                Save();
                LoggingService.Debug($"SettingsService: EnableErrorLogging set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to automatically check for updates on app startup.
    /// When enabled, the app silently checks the appcast URL and shows
    /// a dialog if a new version is available.
    ///
    /// Uses NetSparkle framework with Ed25519 signature verification.
    /// Default: true (opt-out model, matching other settings)
    /// </summary>
    public bool CheckForUpdatesAutomatically
    {
        get => _settings.CheckForUpdatesAutomatically ?? true;
        set
        {
            if ((_settings.CheckForUpdatesAutomatically ?? true) != value)
            {
                _settings.CheckForUpdatesAutomatically = value;
                Save();
                LoggingService.Debug($"SettingsService: CheckForUpdatesAutomatically set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // RECORDING OVERLAY POSITION
    // =========================================================================

    /// <summary>
    /// X position of the recording overlay as a ratio of the work area width (0.0–1.0).
    /// Returns -1.0 when no position has been saved (use default placement).
    /// </summary>
    public double RecordingOverlayXRatio
    {
        get => _settings.RecordingOverlayXRatio ?? -1.0;
        set
        {
            var clamped = Math.Max(0.0, Math.Min(1.0, value));
            if ((_settings.RecordingOverlayXRatio ?? -1.0) != clamped)
            {
                _settings.RecordingOverlayXRatio = clamped;
                Save();
                LoggingService.Debug($"SettingsService: RecordingOverlayXRatio set to: {clamped}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Y position of the recording overlay as a ratio of the work area height (0.0–1.0).
    /// Returns -1.0 when no position has been saved (use default placement).
    /// </summary>
    public double RecordingOverlayYRatio
    {
        get => _settings.RecordingOverlayYRatio ?? -1.0;
        set
        {
            var clamped = Math.Max(0.0, Math.Min(1.0, value));
            if ((_settings.RecordingOverlayYRatio ?? -1.0) != clamped)
            {
                _settings.RecordingOverlayYRatio = clamped;
                Save();
                LoggingService.Debug($"SettingsService: RecordingOverlayYRatio set to: {clamped}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // GETTING STARTED
    // =========================================================================

    public string GettingStartedCompletedSteps
    {
        get => _settings.GettingStartedCompletedSteps ?? "";
        set
        {
            if ((_settings.GettingStartedCompletedSteps ?? "") != value)
            {
                _settings.GettingStartedCompletedSteps = value;
                Save();
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // PARAKEET ENGINE
    // =========================================================================

    /// <summary>
    /// Whether the Parakeet local transcription engine is enabled.
    /// When enabled, users can select Parakeet as a local engine option
    /// for speech-to-text transcription using sherpa-onnx with DirectML.
    /// Default: true
    /// </summary>
    public bool ParakeetEnabled
    {
        get => _settings.ParakeetEnabled ?? true;
        set
        {
            if ((_settings.ParakeetEnabled ?? true) != value)
            {
                _settings.ParakeetEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: ParakeetEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // AUTO-INCREASE MIC VOLUME
    // =========================================================================

    /// <summary>
    /// Whether to automatically increase low mic volume to 90% when recording starts.
    /// Restores the original level when recording stops if HyperWhisper changed it.
    /// Default: true
    /// </summary>
    public bool AutoIncreaseMicVolume
    {
        get => _settings.AutoIncreaseMicVolume ?? true;
        set
        {
            if ((_settings.AutoIncreaseMicVolume ?? true) != value)
            {
                _settings.AutoIncreaseMicVolume = value;
                Save();
                LoggingService.Debug($"SettingsService: AutoIncreaseMicVolume set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Whether to keep a low-overhead idle capture session open between recordings.
    /// This can reduce startup latency for Bluetooth and driver-heavy microphones.
    /// Default: false.
    /// </summary>
    public bool KeepMicrophoneWarm
    {
        get => _settings.KeepMicrophoneWarm ?? false;
        set
        {
            if ((_settings.KeepMicrophoneWarm ?? false) != value)
            {
                _settings.KeepMicrophoneWarm = value;
                Save();
                LoggingService.Debug($"SettingsService: KeepMicrophoneWarm set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Controls how Windows output audio is handled during recording.
    /// Values: "off" or "muteAudio". Default: off.
    /// </summary>
    public string MediaControlMode
    {
        get => NormalizeMediaControlMode(_settings.MediaControlMode);
        set
        {
            var normalized = NormalizeMediaControlMode(value);
            if (!NormalizeMediaControlMode(_settings.MediaControlMode).Equals(normalized, StringComparison.Ordinal))
            {
                _settings.MediaControlMode = normalized;
                Save();
                LoggingService.Debug($"SettingsService: MediaControlMode set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // HOME STATS BAR
    // =========================================================================

    /// <summary>
    /// Assumed typing speed (words per minute) used by the Home stats bar
    /// to compute "minutes saved this week". Default: 40 WPM.
    /// </summary>
    public int TypingSpeedWPM
    {
        get => _settings.TypingSpeedWPM ?? 40;
        set
        {
            if ((_settings.TypingSpeedWPM ?? 40) != value)
            {
                _settings.TypingSpeedWPM = value;
                Save();
                LoggingService.Debug($"SettingsService: TypingSpeedWPM set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    // =========================================================================
    // CUSTOM ENDPOINTS
    // =========================================================================

    /// <summary>
    /// Gets or sets the list of custom OpenAI-compatible endpoints for post-processing.
    /// </summary>
    public List<CustomPostProcessingEndpoint> CustomEndpoints
    {
        get => _settings.CustomEndpoints ?? [];
        set
        {
            _settings.CustomEndpoints = value;
            Save();
            NotifySettingsChanged();
        }
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    /// <summary>
    /// Loads settings from disk. If file doesn't exist, uses defaults.
    /// </summary>
    private void Load()
    {
        lock (_ioLock)
        {
            try
            {
                _settingsFileExists = File.Exists(SettingsFilePath);
                if (!_settingsFileExists)
                {
                    // The live file can also go missing (not just be truncated) if a
                    // crash interrupts File.Replace after the original is moved aside —
                    // ReplaceFileW documents a partial-failure state where the previous
                    // good copy is left only under the .bak name. Recover from .bak
                    // before falling back to defaults so we don't silently wipe an
                    // existing user's preferences. TryLoadBackup() marks the file as
                    // existing (skipping first-launch defaults) and restores the live
                    // settings.json from the recovered backup.
                    if (TryLoadBackup())
                    {
                        return;
                    }

                    LoggingService.Debug("SettingsService: No settings file found, using defaults");
                    return;
                }

                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<SettingsData>(json);

                if (loaded != null)
                {
                    _settings = loaded;
                    LoggingService.Info($"SettingsService: Loaded settings from {SettingsFilePath}");
                    LoggingService.Debug($"SettingsService: LastSelectedModel = {_settings.LastSelectedModel}");
                }

                ApplyDefaults();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"SettingsService: Failed to load settings: {ex.Message}");

                // settings.json is corrupt (e.g. truncated by a crash/force-kill
                // mid-write). Try the previous-good .bak left by File.Replace before
                // falling back to defaults — otherwise the next property setter would
                // call Save() and silently overwrite a recoverable file with all
                // defaults, wiping every user preference.
                if (TryLoadBackup())
                {
                    return;
                }

                // No usable backup: continue with defaults.
            }
        }
    }

    /// <summary>
    /// Attempts to recover settings from the .bak written during atomic saves.
    /// Returns true if a valid backup was loaded into <see cref="_settings"/>.
    /// </summary>
    private bool TryLoadBackup()
    {
        try
        {
            if (!File.Exists(BackupFilePath))
            {
                return false;
            }

            string json = File.ReadAllText(BackupFilePath);
            var recovered = JsonSerializer.Deserialize<SettingsData>(json);
            if (recovered == null)
            {
                return false;
            }

            _settings = recovered;

            // A .bak only exists after a prior successful save, so recovering from it
            // means this is not a fresh install. Mark the file as existing BEFORE
            // ApplyDefaults() runs — ApplyDefaults() reads _settingsFileExists to pick
            // existing-user defaults (legacy recordings folder, StoreAsM4A=false). If
            // it were still false here, a recovered settings file missing those newer
            // nullable fields would be treated as a fresh install and get first-launch
            // defaults baked in (Documents folder, M4A on), which can't be undone later.
            _settingsFileExists = true;
            ApplyDefaults();

            // Restore the live settings.json from the recovered backup. The caller
            // reached here because the live file was corrupt or missing; if we leave it
            // in that state, the next Save() does File.Replace(tmp, settings.json,
            // settings.json.bak), which moves the bad live file over our only good .bak
            // and destroys it. Rewriting the live file now keeps a valid .bak available
            // for the next interrupted save. A failure here is non-fatal: the recovery
            // into memory already succeeded.
            try
            {
                string restoredJson = JsonSerializer.Serialize(
                    _settings,
                    new JsonSerializerOptions { WriteIndented = true });

                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                string tmpPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tmpPath, restoredJson);
                File.Move(tmpPath, SettingsFilePath, overwrite: true);
            }
            catch (Exception restoreEx)
            {
                LoggingService.Warn(
                    $"SettingsService: Recovered settings into memory but failed to restore {SettingsFilePath}: {restoreEx.Message}");
            }

            LoggingService.Warn($"SettingsService: Recovered settings from {BackupFilePath} after corrupt settings.json");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"SettingsService: Failed to recover settings from backup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves settings to disk.
    /// Creates the settings folder if it doesn't exist.
    /// </summary>
    private void Save()
    {
        lock (_ioLock)
        {
            try
            {
                _settings.Version = LatestSettingsVersion;

                // Ensure directory exists
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true  // Human-readable JSON
                };

                string json = JsonSerializer.Serialize(_settings, options);

                // Write atomically: serialize to a sibling .tmp, then rename it over the
                // real file. A rename is atomic on NTFS, so a crash or force-kill (e.g.
                // the installer's /FORCECLOSEAPPLICATIONS) mid-write can never leave a
                // truncated settings.json — the live file is always either the old
                // complete copy or the new complete copy. File.Replace also keeps a .bak
                // of the previous good file, which Load() falls back to on parse failure.
                string tmpPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tmpPath, json);

                if (File.Exists(SettingsFilePath))
                {
                    File.Replace(tmpPath, SettingsFilePath, BackupFilePath);
                }
                else
                {
                    File.Move(tmpPath, SettingsFilePath);
                }

                LoggingService.Debug($"SettingsService: Saved settings to {SettingsFilePath}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"SettingsService: Failed to save settings: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reloads settings from disk. Used after importing a backup to pick up the new values.
    /// </summary>
    public void Reload()
    {
        Load();
        NotifySettingsChanged();
        LoggingService.Info("SettingsService: Settings reloaded from disk");
    }

    /// <summary>
    /// Gets the path to the settings JSON file.
    /// Used by BackupService to read/write the settings file directly.
    /// </summary>
    public static string GetSettingsFilePath() => SettingsFilePath;

    /// <summary>
    /// Fires <see cref="SettingsChanged"/>, marshaling to the UI thread when called from
    /// a background thread. SettingsChanged handlers (e.g. MainViewModel re-registering
    /// global shortcuts via WPF window interop) are UI-affine, so firing them on a worker
    /// thread -- as a backup import on a Task would -- can crash or corrupt window state.
    /// Invoke() (synchronous) preserves the caller's ordering: the import's setter batch
    /// already runs on the UI thread via <see cref="ApplyImport"/>, so this is normally a
    /// no-op marshal; it stays as a defensive guard for any stray off-thread setter.
    /// Event handlers are invoked one at a time so one bad subscriber cannot prevent the
    /// rest from observing the settings change or make a completed setter look failed.
    /// </summary>
    private void NotifySettingsChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted)
            {
                LoggingService.Info("SettingsService: Skipping SettingsChanged notification because the UI dispatcher is shutting down");
                return;
            }

            dispatcher.Invoke(RaiseSettingsChanged);
            return;
        }

        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        var handler = SettingsChanged;
        if (handler == null)
            return;

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LoggingService.Error("SettingsService: SettingsChanged subscriber failed", ex);
            }
        }
    }

    /// <summary>
    /// Runs a batch of setter calls (e.g. a backup import applying many settings) on the
    /// UI thread. Property setters mutate the in-memory <see cref="_settings"/> object
    /// graph that Save() serializes; that mutation is not guarded by _ioLock, so running
    /// the batch off-thread (BackupService.ImportSelective runs under Task.Run) races the
    /// UI thread's setters and Save()'s serialization, which can throw or emit
    /// inconsistent JSON. Marshaling onto the UI thread restores the single-threaded
    /// setter invariant and keeps the resulting SettingsChanged handlers UI-affine-safe.
    /// This method blocks the caller while the dispatcher runs <paramref name="apply"/>;
    /// callers must not synchronously wait for the import task from the UI thread.
    /// When already on the UI thread (or no dispatcher exists, e.g. tests), the action
    /// runs inline. If a background import races app shutdown, the action is rejected
    /// instead of falling back to unsafe worker-thread mutation.
    /// </summary>
    internal void ApplyImport(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted)
                throw new OperationCanceledException("Cannot apply imported settings because the UI dispatcher is shutting down");

            dispatcher.Invoke(apply);
            return;
        }

        apply();
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static string GetDefaultRecordingsFolder()
    {
        if (AppPaths.IsAppDataRootOverridden)
        {
            return AppPaths.ProfileRecordingsDirectory;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "HyperWhisper", "recordings");
    }

    internal static string GetLegacyAudioFolder()
    {
        return AppPaths.LegacyAudioDirectory;
    }

    private static string NormalizeMediaControlMode(string? value)
    {
        return string.Equals(value, "muteAudio", StringComparison.OrdinalIgnoreCase) ? "muteAudio" : "off";
    }

}
