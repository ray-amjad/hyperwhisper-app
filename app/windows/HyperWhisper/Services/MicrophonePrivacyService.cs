// MICROPHONE PRIVACY SERVICE
//
// Reads the Windows microphone consent that an unpackaged Win32 app is subject
// to, and opens the Settings page that changes it.
//
// WHY THIS EXISTS
// The app already degrades when consent is denied, but silently and in the wrong
// place: enumeration still succeeds (WaveIn.DeviceCount still reports devices),
// and the block only appears at OPEN time as WASAPI E_ACCESSDENIED (0x80070005)
// -> a NAudio COMException -> the bare catch in AudioRecorderService -> a generic
// "recording failed" toast. The user is never told it is a privacy setting.
//
// WHERE THE ANSWER LIVES
// Two REG_SZ values under HKCU, both below
//   Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone
//   - the key's own "Value"            -> the global "Microphone access" toggle
//   - NonPackaged\"Value"              -> "Let desktop apps access your microphone"
// Blocked when EITHER reads "Deny".
//
// A MISSING VALUE MEANS ALLOW. Windows only writes these once the user touches
// the toggle, so a machine that has never been near the privacy page has neither
// value. Defaulting a missing read to Denied would tell a working machine its
// microphone is blocked.
//
// The per-exe subkeys under NonPackaged\ carry only LastUsedTimeStart,
// LastUsedTimeStop and LastUserAnnotatedLabel - never a "Value". Windows 11 does
// not let a user deny one desktop app individually, so consent must NEVER be read
// from them.
//
// WinRT AppCapability.Create("microphone").CheckAccess() is deliberately not used:
// it is documented for packaged apps, this app ships unpackaged through Inno Setup
// with no .appxmanifest anywhere, and there is no CsWinRT projection dependency to
// hang it off.

using System.Diagnostics;
using Microsoft.Win32;

namespace HyperWhisper.Services;

/// <summary>
/// What Windows currently says about microphone access for desktop apps.
/// Two states only: an unpackaged Win32 app has no request-and-prompt API, so
/// there is no "undetermined" to report.
/// </summary>
public enum MicrophoneConsent
{
    Allowed,
    Denied
}

public static class MicrophonePrivacyService
{
    private const string ConsentStoreKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private const string NonPackagedKeyPath = ConsentStoreKeyPath + @"\NonPackaged";

    private const string DenyValue = "Deny";

    /// <summary>The Settings deep link that changes the two toggles above.</summary>
    public const string PrivacySettingsUri = "ms-settings:privacy-microphone";

    /// <summary>
    /// Reads both consent toggles. Any read failure resolves to
    /// <see cref="MicrophoneConsent.Allowed"/> for the same reason a missing value
    /// does: the app must not invent a block the user did not set.
    /// </summary>
    public static MicrophoneConsent ReadConsent()
    {
        return Evaluate(ReadValue(ConsentStoreKeyPath), ReadValue(NonPackagedKeyPath));
    }

    /// <summary>
    /// The consent policy on its own, with the two raw REG_SZ reads passed in.
    /// Split out so the smoke suite can pin "missing means Allow" and "either one
    /// denies" without writing to the real HKCU consent store, which belongs to
    /// the person whose machine the suite runs on.
    /// </summary>
    internal static MicrophoneConsent Evaluate(string? globalValue, string? nonPackagedValue)
    {
        return IsDeny(globalValue) || IsDeny(nonPackagedValue)
            ? MicrophoneConsent.Denied
            : MicrophoneConsent.Allowed;
    }

    /// <summary>Convenience for the common question.</summary>
    public static bool IsBlocked() => ReadConsent() == MicrophoneConsent.Denied;

    /// <summary>
    /// Opens Settings > Privacy &amp; security > Microphone. Modelled on the one
    /// existing ms-settings: call in this app, MainViewModel.OpenSoundSettings.
    /// </summary>
    public static bool OpenPrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PrivacySettingsUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"MicrophonePrivacyService: Failed to open {PrivacySettingsUri}: {ex.Message}", ex);
            return false;
        }
    }

    private static bool IsDeny(string? value) =>
        string.Equals(value, DenyValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The key itself is absent on a machine that has never opened the privacy
    /// page, and a failed read tells us nothing. Both come back null, which
    /// <see cref="Evaluate"/> reads as Allow.
    /// </summary>
    private static string? ReadValue(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue("Value") as string;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MicrophonePrivacyService: Could not read {keyPath}: {ex.Message}");
            return null;
        }
    }
}
