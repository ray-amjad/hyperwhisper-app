using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Base;

namespace HyperWhisper.ViewModels.Onboarding;

public sealed partial class OnboardingFlowViewModel : ViewModelBase
{
    // =========================================================================
    // PERMISSIONS
    // =========================================================================

    private bool _hasMicrophoneAccess;

    /// <summary>The one permission that gates the flow.</summary>
    public bool HasMicrophoneAccess
    {
        get => _hasMicrophoneAccess;
        private set
        {
            if (SetProperty(ref _hasMicrophoneAccess, value))
                RaiseGateChanged();
        }
    }

    private OnboardingMicrophoneAuthorization _microphoneAuthorization = OnboardingMicrophoneAuthorization.Undetermined;

    public OnboardingMicrophoneAuthorization MicrophoneAuthorization
    {
        get => _microphoneAuthorization;
        private set => SetProperty(ref _microphoneAuthorization, value);
    }

    /// <summary>Non-null when the last permission request was refused. Drives the alert.</summary>
    [ObservableProperty]
    private string? _permissionErrorMessage;

    // --- The shortcut row (Windows-only; macOS shows Accessibility here) --------

    private string _shortcutDisplay = string.Empty;

    /// <summary>
    /// The configured toggle shortcut, already formatted. The UI splits it on "+"
    /// to draw keycaps.
    /// </summary>
    public string ShortcutDisplay
    {
        get => _shortcutDisplay;
        private set => SetProperty(ref _shortcutDisplay, value);
    }

    private OnboardingShortcutStatus _shortcutStatus = OnboardingShortcutStatus.Unknown;

    /// <summary>
    /// Whether the shortcut is registered. Unknown is a real state, not a failure,
    /// and none of the three ever gates Continue.
    /// </summary>
    public OnboardingShortcutStatus ShortcutStatus
    {
        get => _shortcutStatus;
        private set => SetProperty(ref _shortcutStatus, value);
    }

    private string? _shortcutFailureReason;

    /// <summary>A user-facing sentence produced by the adapter, never a Win32 code.</summary>
    public string? ShortcutFailureReason
    {
        get => _shortcutFailureReason;
        private set => SetProperty(ref _shortcutFailureReason, value);
    }

    /// <summary>
    /// Re-read both permissions. Called on entry to the step and, by the window, on
    /// activation, so a trip to Windows Settings is picked up.
    /// </summary>
    public void RefreshPermissions()
    {
        if (!_isLive)
            return;

        MicrophoneAuthorization = _permissions.MicrophoneAuthorization;
        HasMicrophoneAccess = MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Authorized;
        // Keep the audio gateway's own preview guard from holding stale state after
        // the user returns from Windows Settings.
        _audio.RefreshMicrophoneAuthorization();
        DeviceAvailability = _audio.Availability;
        ApplyShortcutState();
    }

    /// <summary>
    /// Re-run the registration check. This is the Windows replacement for macOS's
    /// polling waitForAccessibilityPermission: cheap, on demand, no timer.
    /// </summary>
    public void RefreshShortcutRegistration()
    {
        if (!_isLive)
            return;

        _permissions.RefreshShortcutRegistration();
        ApplyShortcutState();
    }

    private void ApplyShortcutState()
    {
        var state = _permissions.Shortcut;
        ShortcutDisplay = state.DisplayText;
        ShortcutStatus = state.Status;
        ShortcutFailureReason = state.Status == OnboardingShortcutStatus.Failed ? state.FailureReason : null;
    }

    /// <summary>
    /// The microphone row's action. Windows cannot re-prompt, so anything other than
    /// Undetermined deep-links Windows Settings.
    /// </summary>
    [RelayCommand]
    public void HandleMicrophoneAction()
    {
        if (!_isLive)
            return;

        if (_permissions.MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Undetermined)
        {
            RequestMicrophoneAccess();
            return;
        }

        _permissions.OpenMicrophonePrivacySettings();
    }

    /// <summary>Ask for microphone access. Kept for shape parity with macOS.</summary>
    public void RequestMicrophoneAccess()
    {
        if (!_isLive)
            return;

        RunTracked(OnboardingTaskKeys.MicrophonePermission, RequestMicrophoneAccessCoreAsync);
    }

    private async Task RequestMicrophoneAccessCoreAsync(CancellationToken cancellationToken)
    {
        bool granted;
        try
        {
            granted = await _permissions.RequestMicrophoneAccessAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // A throwing consent probe is a refusal, not a crash. Same rule as the
            // two credential checks: no seam may leave the step in a state the user
            // cannot get out of.
            granted = false;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        HasMicrophoneAccess = granted;
        MicrophoneAuthorization = granted
            ? OnboardingMicrophoneAuthorization.Authorized
            : OnboardingMicrophoneAuthorization.Denied;

        if (!granted)
            PermissionErrorMessage = Loc.S("onboarding.error.microphone.denied");

        _taskBox.Clear(OnboardingTaskKeys.MicrophonePermission);
    }

    /// <summary>
    /// Store a shortcut the user recorded inline on the Permissions step, then
    /// re-check. The row never gates Continue, so this is an offer, not a
    /// requirement - but it is now an offer that works: it used to deep-link the
    /// Shortcuts settings section, and this window is application modal, so the page
    /// it raised could be looked at and not typed into.
    ///
    /// The argument is the persisted string rather than a WPF key, so this file and
    /// its whole suite stay WPF-free. See the seam for why this one write is
    /// deliberately not rolled back by "Set Up Later".
    /// </summary>
    /// <returns>false if the seam refused it; the recorder has already validated it.</returns>
    public bool ApplyToggleShortcut(string persistedShortcut)
    {
        if (!_isLive || string.IsNullOrWhiteSpace(persistedShortcut))
            return false;

        var stored = _permissions.SetToggleShortcut(persistedShortcut);

        // Refresh either way. A refused write still has to leave the row showing
        // what is actually configured rather than what the user just typed.
        RefreshShortcutRegistration();
        return stored;
    }

}
