using System;

namespace HyperWhisper.Services;

public partial class SettingsService
{
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
    /// Whether to contribute anonymous speed measurements to the public
    /// latency page at hyperwhisper.com/en/latency.
    ///
    /// When enabled, every HyperWhisper Cloud transcription adds one anonymous
    /// row per provider attempt: which provider ran, from which server region,
    /// how long the clip was, how long the provider took, and whether it
    /// worked. No account, no key, no request id, no IP, no audio, and no text
    /// — nothing links two rows to the same person.
    ///
    /// When disabled, the app sends the X-Latency-Opt-Out header and the server
    /// drops the measurement instead of storing it. Nothing else about the
    /// request changes. Local models never report anything either way.
    ///
    /// Default: true (opt-out model)
    /// </summary>
    public bool ShareAnonymousSpeedData
    {
        get => _settings.ShareAnonymousSpeedData ?? true;
        set
        {
            if ((_settings.ShareAnonymousSpeedData ?? true) != value)
            {
                _settings.ShareAnonymousSpeedData = value;
                Save();
                LoggingService.Debug($"SettingsService: ShareAnonymousSpeedData set to: {value}");
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
    // FIRST-RUN ONBOARDING
    // =========================================================================

    /// <summary>
    /// Whether this install still owes the user the first-run onboarding flow.
    ///
    /// A genuine fresh install is born owing it (see ApplyDefaults); every
    /// pre-existing settings.json defaults to false. It stays true until the flow
    /// is completed, so an interrupted first run is re-offered on the next launch.
    ///
    /// Deliberately machine-local and therefore NOT in BuildBackupSettingsSnapshot,
    /// for the same reason as LastSelectedMicrophone and LocalApiServerPersistedPort:
    /// restoring a backup onto a new PC must not re-run, or skip, that PC's setup.
    /// </summary>
    public bool OnboardingPending
    {
        get => _settings.OnboardingPending ?? false;
        set
        {
            if ((_settings.OnboardingPending ?? false) != value)
            {
                _settings.OnboardingPending = value;
                Save();
                LoggingService.Debug($"SettingsService: OnboardingPending set to: {value}");
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
}
