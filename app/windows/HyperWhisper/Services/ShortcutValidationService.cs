// SHORTCUT VALIDATION SERVICE
// Validates keyboard shortcuts for duplicates and maps Win32 error codes
// to user-friendly messages.
//
// USAGE:
// - Call ValidateDuplicate() before saving a shortcut to check for conflicts
// - Call GetRegistrationErrorMessage() to map Win32 errors to friendly messages
//
// WIN32 ERROR CODES:
// - 1409: ERROR_HOTKEY_ALREADY_REGISTERED (in use by another app)
// - 1413: ERROR_HOTKEY_NOT_REGISTERED (reserved by Windows)

using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public static class ShortcutValidationService
{
    /// <summary>
    /// Validates that a shortcut is suitable for action shortcuts (Toggle, Cancel, ChangeMode).
    /// Multi-modifier chords like Ctrl+Win are intentional global shortcuts; single
    /// bare modifiers are unsafe because they steal normal typing/system behavior.
    /// </summary>
    public static string? ValidateActionShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.IsEmpty) return null; // Empty is okay (unassigned)

        if (shortcut.IsSingleBareModifier)
        {
            return Loc.S("settings.shortcuts.error.singleModifier");
        }

        return null; // Valid
    }

    /// <summary>
    /// The localized label for one of the four shortcut roles, so the duplicate
    /// message names the row the user can actually see on the page.
    ///
    /// The role strings themselves ("Toggle", "Cancel", "ChangeMode",
    /// "Streaming") are call-site identifiers and settings keys, not display
    /// text: they must NOT be translated. Only the label is.
    /// </summary>
    private static string RoleLabel(string role) => role switch
    {
        "Toggle" => Loc.S("settings.shortcuts.toggle.label"),
        "Cancel" => Loc.S("settings.shortcuts.cancel.label"),
        "ChangeMode" => Loc.S("settings.shortcuts.changeMode.label"),
        "Streaming" => Loc.S("settings.shortcuts.streaming.label"),
        _ => role
    };

    /// <summary>
    /// Validates shortcut against HyperWhisper action shortcuts.
    /// Returns error message if duplicate found, null if valid.
    /// </summary>
    public static string? ValidateDuplicate(
        KeyboardShortcut shortcut,
        string currentRole,  // "Toggle", "Cancel", "ChangeMode", or "Streaming"
        KeyboardShortcut toggleShortcut,
        KeyboardShortcut cancelShortcut,
        KeyboardShortcut changeModeShortcut,
        KeyboardShortcut streamingShortcut)
    {
        if (shortcut.IsEmpty) return null;

        // Check if valid for action shortcuts
        var actionError = ValidateActionShortcut(shortcut);
        if (actionError != null) return actionError;

        // Check against Toggle (unless we're setting Toggle)
        if (currentRole != "Toggle" && shortcut.Equals(toggleShortcut))
        {
            return Loc.S("settings.shortcuts.error.duplicate",
                RoleLabel("Toggle"), toggleShortcut.ToDisplayString());
        }

        // Check against Cancel (unless we're setting Cancel)
        if (currentRole != "Cancel" && shortcut.Equals(cancelShortcut))
        {
            return Loc.S("settings.shortcuts.error.duplicate",
                RoleLabel("Cancel"), cancelShortcut.ToDisplayString());
        }

        // Check against ChangeMode (unless we're setting ChangeMode)
        if (currentRole != "ChangeMode" && shortcut.Equals(changeModeShortcut))
        {
            return Loc.S("settings.shortcuts.error.duplicate",
                RoleLabel("ChangeMode"), changeModeShortcut.ToDisplayString());
        }

        if (currentRole != "Streaming" && shortcut.Equals(streamingShortcut))
        {
            return Loc.S("settings.shortcuts.error.duplicate",
                RoleLabel("Streaming"), streamingShortcut.ToDisplayString());
        }

        return null; // No duplicates
    }

    /// <summary>
    /// Pulls the Win32 code back out of a KeyboardShortcutService registration
    /// failure ("...Win32 error=1409"), which is the only place it survives.
    /// Returns 0 when the message carries none, which
    /// <see cref="GetRegistrationErrorMessage"/> renders as the generic arm.
    ///
    /// Lives here rather than at a call site because two screens now need it: the
    /// main window's conflict banner and the onboarding permissions row. The
    /// message and the code it is built from belong to the same class.
    /// </summary>
    public static int ExtractWin32ErrorCode(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return 0;

        var match = System.Text.RegularExpressions.Regex.Match(errorMessage, @"Win32 error=(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int code) ? code : 0;
    }

    /// <summary>
    /// Maps Win32 RegisterHotKey error codes to user-friendly messages.
    /// </summary>
    public static string GetRegistrationErrorMessage(int win32ErrorCode, KeyboardShortcut shortcut)
    {
        var display = shortcut.ToDisplayString();

        if (shortcut.IsSingleBareModifier)
        {
            return Loc.S("settings.shortcuts.error.bareModifier", display);
        }

        return win32ErrorCode switch
        {
            1409 => Loc.S("settings.shortcuts.error.inUse", display),
            1413 => Loc.S("settings.shortcuts.error.reserved", display),
            _ => Loc.S("settings.shortcuts.error.registerFailed", display, win32ErrorCode)
        };
    }
}
