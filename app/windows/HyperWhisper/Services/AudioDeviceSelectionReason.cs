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
        _ => Unknown
    };
}
