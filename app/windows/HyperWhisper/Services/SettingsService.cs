using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        public bool? KeepAudioFiles { get; set; }
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
        public bool? ShareAnonymousSpeedData { get; set; }
        public bool? CheckForUpdatesAutomatically { get; set; }

        // Auto-delete settings
        public bool? AutoDeleteEnabled { get; set; }
        public int? AutoDeleteDaysOld { get; set; }

        // Auto-delete RESULT, not a preference: when the last cleanup sweep finished
        // and how many transcripts it removed. Persisted because the Storage page's
        // "last cleanup" line is a fact about the profile, not about this process.
        // Null means no sweep has ever run here. Deliberately NOT carried in the
        // universal backup: a restored backup must not claim a sweep ran on this
        // machine. Always stored in UTC; the page converts for display.
        public DateTime? AutoDeleteLastCleanupUtc { get; set; }
        public int? AutoDeleteLastCleanupDeleted { get; set; }

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
        public string? StreamingCloudTier { get; set; }
        public bool? StreamingFastFormatting { get; set; }

        // Recording overlay position (screen ratios)
        public double? RecordingOverlayXRatio { get; set; }
        public double? RecordingOverlayYRatio { get; set; }

        // Getting Started checklist
        public string? GettingStartedCompletedSteps { get; set; }

        // First-run onboarding. NULLABLE IS LOAD-BEARING: null means "this install
        // never wrote it", which ApplyDefaults turns into true only when there was
        // no settings.json at all. Every pre-existing install defaults to false.
        public bool? OnboardingPending { get; set; }

        // Parakeet engine feature flag
        public bool? ParakeetEnabled { get; set; }

        // Custom OpenAI-compatible endpoints for post-processing
        public List<CustomPostProcessingEndpoint>? CustomEndpoints { get; set; }

        // Home stats bar — assumed typing speed used to compute "minutes saved"
        public int? TypingSpeedWPM { get; set; }

        // Recording safety cap, in SECONDS. Read through
        // MaxRecordingDurationSeconds, which clamps it to (0, 20 min]. The name
        // has no unit suffix because it is also the universal backup key
        // (advanced.maxRecordingDuration) and the WINDOWS_ADVANCED_PAIRS native
        // name; the property is where the unit is stated.
        public int? MaxRecordingDuration { get; set; }

        // Local HTTP API (Settings → Local API)
        public bool? LocalApiServerEnabled { get; set; }
        public int? LocalApiServerPersistedPort { get; set; }

        // ---------------------------------------------------------------------
        // BACKUP ROUND-TRIP BOOKKEEPING — raw JSON, never interpreted
        //
        // Two fields, two shapes, two merge points. Collapsing them into one
        // would re-emit settings keys under platformExtensions, which is a schema
        // violation. Neither is a user preference and neither is shown anywhere.
        // ---------------------------------------------------------------------

        /// Settings keys the last imported backup carried that this build has no
        /// property for, as a MIRROR of the universal `settings` tree so every key
        /// keeps its section: {"textOutput":{"storeWordTimestamps":true}}.
        /// Merged back in UniversalBackupMapper.MapSettings.
        public string? BackupUnknownSettings { get; set; }

        /// The non-"windows" TOP-LEVEL platformExtensions slices of the last
        /// imported backup: {"macos":{...},"linux":{...}}. Merged back in
        /// UniversalBackupMapper.BuildPlatformExtensions.
        public string? BackupForeignPlatformExtensions { get; set; }

        /// Top-level backup keys this build has no property for (never
        /// `platformExtensions`, which has its own field above). Re-emitted at the
        /// `new UniversalBackup` site in BackupService.
        public string? BackupUnknownRootKeys { get; set; }
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
    // AUTO-INCREASE MIC VOLUME
    // =========================================================================


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
    // RECORDING SAFETY CAP
    // =========================================================================

    /// <summary>
    /// Hard ceiling on a single recording or streaming session, in SECONDS.
    /// Default and MAXIMUM: 1200 (20 minutes). Consumed by
    /// <c>MainViewModel.EffectiveMaxRecordingDuration</c>.
    /// </summary>
    /// <remarks>
    /// The setter CLAMPS to [1, <see cref="MaxRecordingDurationCeilingSeconds"/>].
    /// A restored backup — or a hand-edited settings.json — may only TIGHTEN the
    /// runaway guard, never loosen or disable it. The shared core applies the same
    /// ceiling on the import path (<c>universal_to_windows_settings</c>), where it
    /// additionally reads macOS's <c>0</c> ("no limit") and <c>300</c> (its old
    /// never-exposed default) as "unset" so neither silently rewrites the cap.
    /// This second clamp is what makes the ceiling hold for every writer, not just
    /// the backup path.
    /// </remarks>
    public int MaxRecordingDurationSeconds
    {
        get => Clamp(_settings.MaxRecordingDuration ?? MaxRecordingDurationCeilingSeconds);
        set
        {
            var clamped = Clamp(value);
            if (MaxRecordingDurationSeconds != clamped)
            {
                _settings.MaxRecordingDuration = clamped;
                Save();
                LoggingService.Debug($"SettingsService: MaxRecordingDurationSeconds set to: {clamped}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// The 20-minute runaway-recording ceiling, in seconds. Mirrors
    /// <c>MainViewModel.MaxRecordingDuration = TimeSpan.FromMinutes(20)</c> and the
    /// shared core's <c>WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS</c>.
    /// </summary>
    public const int MaxRecordingDurationCeilingSeconds = 20 * 60;

    private static int Clamp(int seconds) =>
        seconds < 1 ? MaxRecordingDurationCeilingSeconds
        : seconds > MaxRecordingDurationCeilingSeconds ? MaxRecordingDurationCeilingSeconds
        : seconds;


    // =========================================================================
    // BACKUP SNAPSHOT
    // =========================================================================

    /// <summary>
    /// A JSON snapshot of the settings that are promoted to the universal backup
    /// <c>settings</c> block, in this class's own NATIVE shape: flat, and
    /// PascalCase because that is how <c>settings.json</c> stores them
    /// (<see cref="Save"/> uses a plain <c>JsonSerializerOptions</c> with no
    /// naming policy — <c>UniversalBackupMapper.CamelCaseOptions</c> is a
    /// different serializer and does not apply here).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE method serves BOTH directions. On export it is the input to the shared
    /// core's <c>windows_settings_to_universal</c>; on import it is the BASELINE
    /// the core's answer is deep-merged over, so a key the backup does not carry
    /// cannot clobber a live setting. <c>SettingsData</c> is private and every
    /// value here is read through its public property, so the defaults each
    /// property applies are already resolved.
    /// </para>
    /// <para>
    /// <b>This snapshot is deliberately NOT all of <c>SettingsData</c>, and must
    /// never become that.</b> <c>SettingsData</c> also holds
    /// <c>RecordingsFolder</c> (a real user filesystem path),
    /// <c>LastSelectedMicrophone</c> (a device name),
    /// <c>GettingStartedCompletedSteps</c> and <c>LocalApiServerPersistedPort</c>.
    /// A <c>.hwbackup.json</c> is a file users share. Add a key here only when it
    /// is a cross-platform setting that belongs in the universal block — the
    /// Windows-only settings that DO get exported travel through the curated
    /// <c>WindowsSettingsExtensions</c> list in
    /// <c>UniversalBackupMapper.BuildPlatformExtensions</c> instead.
    /// </para>
    /// <para>
    /// The three backup bookkeeping fields (<see cref="BackupUnknownSettings"/>,
    /// <see cref="BackupForeignPlatformExtensions"/>,
    /// <see cref="BackupUnknownRootKeys"/>) are NOT here either. They are raw
    /// preserved JSON, not settings; each has its own merge point, and putting
    /// them through the pairs tables would re-emit them at the wrong path.
    /// </para>
    /// <para>
    /// <c>StreamingShortcut</c> is a <see cref="KeyboardShortcut"/>, not a scalar,
    /// so it crosses as its persisted-string form; <c>FromPersistedString</c>
    /// stays on the import side.
    /// </para>
    /// </remarks>
    public string BuildBackupSettingsSnapshot()
    {
        var snapshot = new JsonObject
        {
            // general
            ["LaunchMinimized"] = LaunchMinimized,
            ["ShowRecordingWindow"] = ShowRecordingWindow,
            ["CheckForUpdatesAutomatically"] = CheckForUpdatesAutomatically,
            ["EnableErrorLogging"] = EnableErrorLogging,
            ["ShareAnonymousSpeedData"] = ShareAnonymousSpeedData,
            ["EnableSoundEffects"] = EnableSoundEffects,

            // textOutput
            ["AutoPasteEnabled"] = AutoPasteEnabled,
            ["RemoveFillerWords"] = RemoveFillerWords,
            ["RestoreClipboardAfterPaste"] = RestoreClipboardAfterPaste,
            ["HideFromClipboardHistory"] = HideFromClipboardHistory,
            ["ClipboardRestoreDelaySeconds"] = ClipboardRestoreDelaySeconds,
            ["AutocapitalizeInsert"] = AutocapitalizeInsert,

            // storage
            ["StoreAsM4A"] = StoreAsM4A,
            ["KeepAudioFiles"] = KeepAudioFiles,

            // streaming — seven separately-named native properties
            ["StreamingEnabled"] = StreamingEnabled,
            ["StreamingProvider"] = StreamingProvider,
            ["StreamingLanguage"] = StreamingLanguage,
            ["StreamingDeepgramModel"] = StreamingDeepgramModel,
            // The clamping GETTER, not the raw SettingsData field: unset reads as
            // deepgramNova3, so an export never carries a null tier.
            ["StreamingCloudTier"] = StreamingCloudTier,
            ["StreamingFastFormatting"] = StreamingFastFormatting,
            ["StreamingShortcut"] = StreamingShortcut.ToPersistedString(),

            // advanced
            ["TypingSpeedWPM"] = TypingSpeedWPM,
            // Seconds, already clamped to the 20-minute ceiling by the property.
            ["MaxRecordingDuration"] = MaxRecordingDurationSeconds,
        };

        return snapshot.ToJsonString();
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
                AppPaths.PrepareForOverwrite(SettingsFilePath, "SettingsService.TryLoadBackup");
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

                // A read-only attribute (backup/restore tools, sync utilities) on
                // either file would make File.Replace throw — and the catch below
                // swallows it, silently losing every subsequent save.
                AppPaths.PrepareForOverwrite(SettingsFilePath, "SettingsService.Save", BackupFilePath);

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
