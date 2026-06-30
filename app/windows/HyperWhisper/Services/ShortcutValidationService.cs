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
            return "Single modifier shortcuts such as Ctrl, Alt, Shift, or Win are not supported. Use a key with modifiers or a multi-modifier shortcut such as Ctrl+Win.";
        }

        return null; // Valid
    }

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
            return $"This shortcut is already used for Toggle Recording ({toggleShortcut.ToDisplayString()})";
        }

        // Check against Cancel (unless we're setting Cancel)
        if (currentRole != "Cancel" && shortcut.Equals(cancelShortcut))
        {
            return $"This shortcut is already used for Cancel Recording ({cancelShortcut.ToDisplayString()})";
        }

        // Check against ChangeMode (unless we're setting ChangeMode)
        if (currentRole != "ChangeMode" && shortcut.Equals(changeModeShortcut))
        {
            return $"This shortcut is already used for Change Mode ({changeModeShortcut.ToDisplayString()})";
        }

        if (currentRole != "Streaming" && shortcut.Equals(streamingShortcut))
        {
            return $"This shortcut is already used for Streaming ({streamingShortcut.ToDisplayString()})";
        }

        return null; // No duplicates
    }

    /// <summary>
    /// Maps Win32 RegisterHotKey error codes to user-friendly messages.
    /// </summary>
    public static string GetRegistrationErrorMessage(int win32ErrorCode, KeyboardShortcut shortcut)
    {
        if (shortcut.IsSingleBareModifier)
        {
            return $"The shortcut {shortcut.ToDisplayString()} uses a single bare modifier. Use a key with modifiers or a multi-modifier shortcut such as Ctrl+Win.";
        }

        return win32ErrorCode switch
        {
            1409 => $"The shortcut {shortcut.ToDisplayString()} is already in use by another application. Please choose a different combination.",
            1413 => $"The shortcut {shortcut.ToDisplayString()} is reserved by Windows and cannot be used.",
            _ => $"Failed to register shortcut {shortcut.ToDisplayString()} (Windows error {win32ErrorCode}). Please try a different combination."
        };
    }
}
