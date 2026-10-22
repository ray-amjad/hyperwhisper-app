namespace HyperWhisper.Services;

/// <summary>
/// Fixed diagnostic slugs for the path that selected the active audio input.
/// </summary>
internal static class AudioDeviceSelectionReason
{
    public const string NotSelected = "not_selected";
    public const string StartupFirstAvailable = "startup_first_available";
    public const string StartupNoDevices = "startup_no_devices";
    public const string StartupFailed = "startup_failed";
    public const string RefreshFirstAvailable = "refresh_first_available";
    public const string RefreshNoDevices = "refresh_no_devices";
    public const string RefreshFailed = "refresh_failed";
    public const string ExplicitSelection = "explicit_selection";
    public const string OnboardingSelection = "onboarding_selection";
    public const string Unknown = "unknown";

    // The saved-choice resolver (MicrophoneSelection.Resolve). The two
    // *_first_available slugs above are kept for old diagnostics: the resolver
    // only reaches the first row when Windows names no default it can match.
    public const string StartupSaved = "startup_saved";
    public const string StartupSystemDefault = "startup_system_default";
    public const string StartupSavedMissing = "startup_saved_missing";
    public const string RefreshSaved = "refresh_saved";
    public const string RefreshSystemDefault = "refresh_system_default";
    public const string RefreshSavedMissing = "refresh_saved_missing";

    /// <summary>When the resolver ran: at launch, or after a device change.</summary>
    public enum Phase
    {
        Startup,
        Refresh,
    }

    /// <summary>The slug for one resolver outcome.</summary>
    public static string For(Phase phase, MicrophoneSource source) => (phase, source) switch
    {
        (Phase.Startup, MicrophoneSource.Saved) => StartupSaved,
        (Phase.Startup, MicrophoneSource.SystemDefault) => StartupSystemDefault,
        (Phase.Startup, MicrophoneSource.SavedMissingUsingDefault) => StartupSavedMissing,
        (Phase.Startup, _) => StartupNoDevices,
        (_, MicrophoneSource.Saved) => RefreshSaved,
        (_, MicrophoneSource.SystemDefault) => RefreshSystemDefault,
        (_, MicrophoneSource.SavedMissingUsingDefault) => RefreshSavedMissing,
        _ => RefreshNoDevices,
    };

    public static string Normalize(string? value) => value switch
    {
        NotSelected => NotSelected,
        StartupFirstAvailable => StartupFirstAvailable,
        StartupNoDevices => StartupNoDevices,
        StartupFailed => StartupFailed,
        RefreshFirstAvailable => RefreshFirstAvailable,
        RefreshNoDevices => RefreshNoDevices,
        RefreshFailed => RefreshFailed,
        ExplicitSelection => ExplicitSelection,
        OnboardingSelection => OnboardingSelection,
        StartupSaved => StartupSaved,
        StartupSystemDefault => StartupSystemDefault,
        StartupSavedMissing => StartupSavedMissing,
        RefreshSaved => RefreshSaved,
        RefreshSystemDefault => RefreshSystemDefault,
        RefreshSavedMissing => RefreshSavedMissing,
        _ => Unknown
    };
}
