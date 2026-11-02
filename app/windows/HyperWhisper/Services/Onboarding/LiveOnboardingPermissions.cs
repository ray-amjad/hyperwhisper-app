// The live IOnboardingPermissions adapter for the first-run flow. Part of the onboarding
// composition described in OnboardingLiveDependencies.cs; split out of that file (#941).

using HyperWhisper.Models;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Permissions
// =============================================================================

/// <summary>
/// Microphone consent plus the "your shortcut works" row.
///
/// Row 2 is the global shortcut rather than macOS's Accessibility grant, because
/// Windows has no Accessibility analogue: RegisterHotKey, a low-level keyboard hook,
/// SendInput and UI Automation all work here without a user grant. Registering
/// the shortcut genuinely fails though - Win32 1409 "already registered by
/// another application" is a daily occurrence - so the row is a real
/// environmental precondition with three renderings and a sentence to show.
/// Like Accessibility on macOS it never gates Continue.
/// </summary>
public sealed class LiveOnboardingPermissions : IOnboardingPermissions
{
    /// <summary>
    /// The name MainViewModel.RegisterShortcutsFromSettings registers the toggle
    /// shortcut under. The two screens must agree, so this is keyed off the same
    /// string rather than a second copy of the shortcut.
    /// </summary>
    private const string ToggleShortcutName = "toggle";

    private readonly Action<KeyboardShortcut> _persistToggleShortcut;

    /// <summary>
    /// The write is injected rather than reaching <c>SettingsService.Instance</c>
    /// here, for the same reason the device seam takes two delegates: this adapter
    /// is the seam between the flow and the app, and a singleton read inside it is a
    /// dependency the composition point cannot see.
    /// </summary>
    public LiveOnboardingPermissions(Action<KeyboardShortcut> persistToggleShortcut)
    {
        _persistToggleShortcut = persistToggleShortcut;
    }

    /// <summary>
    /// Never Undetermined. An unpackaged Win32 app has no request-and-prompt API:
    /// the consent is a system-wide toggle, so the answer is only ever allowed or
    /// denied. The third case is kept on the enum for shape parity with the shared
    /// gating table.
    /// </summary>
    public OnboardingMicrophoneAuthorization MicrophoneAuthorization =>
        MicrophonePrivacyService.ReadConsent() == MicrophoneConsent.Allowed
            ? OnboardingMicrophoneAuthorization.Authorized
            : OnboardingMicrophoneAuthorization.Denied;

    public OnboardingShortcutState Shortcut { get; private set; } = OnboardingShortcutState.Unknown;

    public event EventHandler? ShortcutChanged;

    /// <summary>
    /// Windows cannot prompt, so this re-reads consent and answers. The seam keeps
    /// the async shape because macOS genuinely prompts here.
    /// </summary>
    public Task<bool> RequestMicrophoneAccessAsync()
    {
        return Task.FromResult(MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Authorized);
    }

    public void OpenMicrophonePrivacySettings() => MicrophonePrivacyService.OpenPrivacySettings();

    /// <summary>
    /// Store a shortcut the user recorded on the Permissions step. See the seam's
    /// doc comment for why this is a write and not a deep link, and why it is the
    /// one thing the flow does that "Set Up Later" does not roll back.
    ///
    /// It deliberately does not register the hotkey itself.
    /// <c>MainViewModel.OnSettingsChanged</c> re-registers every shortcut whenever a
    /// setting changes, and <c>NotifySettingsChanged</c> raises that inline when the
    /// caller is already on the UI thread - which a key handler always is. So by the
    /// time this returns, the attempt has been made and recorded, and the flow's
    /// RefreshShortcutRegistration() reads the NEW outcome rather than the old one.
    /// </summary>
    public bool SetToggleShortcut(string persistedShortcut)
    {
        try
        {
            var shortcut = KeyboardShortcut.FromPersistedString(persistedShortcut);
            if (shortcut.IsEmpty)
            {
                LoggingService.Warn(
                    $"LiveOnboardingPermissions: refused an empty toggle shortcut ('{persistedShortcut}')");
                return false;
            }

            _persistToggleShortcut(shortcut);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingPermissions: Could not store the toggle shortcut: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-read the registration outcome and publish it.
    ///
    /// It deliberately does NOT re-register. A Win32 hotkey is per-thread, so a
    /// second RegisterHotKey for the same combination fails with 1409 against
    /// ourselves; and KeyboardShortcutService.RegisterShortcut unregisters before
    /// it re-registers, so a competing attempt that lost would leave the user with
    /// no working hotkey at all. The app already re-registers everything whenever
    /// settings change (MainViewModel.OnSettingsChanged), which is exactly what
    /// happens when the user edits the shortcut from this step - so reading the
    /// recorded outcome is both current and side-effect free.
    ///
    /// This also replaces macOS's polling waitForAccessibilityPermission, which
    /// has no Windows analogue.
    /// </summary>
    public void RefreshShortcutRegistration()
    {
        var shortcut = SettingsService.Instance.ToggleShortcut;

        // The same call ShortcutsSettingsPage makes, so the two screens can never
        // disagree about what the shortcut is called.
        var display = shortcut.ToDisplayString();

        var state = BuildState(display, shortcut, KeyboardShortcutService.Current?.GetLastRegistrationResult(ToggleShortcutName));

        if (state != Shortcut)
        {
            Shortcut = state;
        }

        // Raised unconditionally: the caller asked for a refresh and the view has
        // to be told the answer arrived even when it did not change.
        ShortcutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns a registration Result into the row's three states. The ADAPTER maps
    /// the Win32 code into a sentence; the flow model never sees an error number.
    /// </summary>
    internal static OnboardingShortcutState BuildState(
        string displayText,
        KeyboardShortcut shortcut,
        Result? registration)
    {
        // Never registered through this service: no window yet, or the app has not
        // reached RegisterShortcutsFromSettings. Unknown is a real renderable
        // state and is NOT a failure.
        if (registration is not { } result)
        {
            return new OnboardingShortcutState(displayText, OnboardingShortcutStatus.Unknown, null);
        }

        if (result.IsSuccess)
        {
            return new OnboardingShortcutState(displayText, OnboardingShortcutStatus.Registered, null);
        }

        // "HwndSource is null" means the main window had no HWND when the attempt
        // ran. That is an ordering fact about the app, not a verdict about the
        // user's shortcut, so it must never render as a conflict.
        if (result.Error?.Contains("HwndSource is null", StringComparison.Ordinal) == true)
        {
            return new OnboardingShortcutState(displayText, OnboardingShortcutStatus.Unknown, null);
        }

        var code = ShortcutValidationService.ExtractWin32ErrorCode(result.Error);
        return new OnboardingShortcutState(
            displayText,
            OnboardingShortcutStatus.Failed,
            ShortcutValidationService.GetRegistrationErrorMessage(code, shortcut));
    }
}
