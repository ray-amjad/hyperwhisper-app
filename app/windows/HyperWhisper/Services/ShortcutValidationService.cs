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
    ///
    /// A key with NO modifier at all is refused too, unless typing never produces it
    /// (issue #1631, the Linux rule from #628 / PR #661,
    /// <c>LinuxShortcutRecorderRules.Evaluate</c>). Bare Tab was saved as the global
    /// Toggle shortcut by a keyboard user trying to leave the field, and a bare letter,
    /// digit, Space or Enter would fire the action on every press of that key while
    /// the user types. F1-F24 and Esc stay legal: the shipped Cancel default is a bare
    /// Esc, and a bare F-key is a working shortcut on main.
    ///
    /// Every recorder box reaches this - Commit calls it, and so does
    /// <see cref="ValidateDuplicate"/> - so the rule holds for all seven boxes in all
    /// three hosts, push-to-talk's custom key included.
    /// </summary>
    public static string? ValidateActionShortcut(KeyboardShortcut shortcut)
    {
        if (shortcut.IsEmpty) return null; // Empty is okay (unassigned)

        if (shortcut.IsSingleBareModifier)
        {
            return Loc.S("settings.shortcuts.error.singleModifier");
        }

        if (shortcut.Key is { } key && shortcut.ModifierCount == 0 && !IsDedicatedKey(key))
        {
            return Loc.S("settings.shortcuts.error.typingKey");
        }

        return null; // Valid
    }

    /// <summary>
    /// A key that ordinary typing never produces, so it may be a shortcut with no
    /// modifier held: F1-F24 and Esc. The same two classes Linux allows
    /// (<c>LinuxShortcutRecorderRules.IsDedicatedKey</c>).
    /// </summary>
    internal static bool IsDedicatedKey(System.Windows.Input.Key key) =>
        key == System.Windows.Input.Key.Escape
        || key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24;

    /// <summary>
    /// The four action roles, each with the resource key for the label the
    /// Shortcuts page already prints above its row. The duplicate message names
    /// a row, so it has to name it the way the user sees it.
    ///
    /// The role strings themselves ("Toggle", "Cancel", "ChangeMode",
    /// "Streaming") are call-site identifiers and settings keys, NOT display
    /// text: they are never translated. Only the label is.
    ///
    /// A table rather than four branches: once the four sentences became one
    /// template, the four checks differed only by these two fields, and a
    /// copy-paste slip between them would silently name the wrong row in every
    /// language while still compiling and still reading correctly.
    /// </summary>
    private static readonly (string Role, string LabelKey)[] ActionRoles =
    {
        ("Toggle", "settings.shortcuts.toggle.label"),
        ("Cancel", "settings.shortcuts.cancel.label"),
        ("ChangeMode", "settings.shortcuts.changeMode.label"),
        ("Streaming", "settings.shortcuts.streaming.label"),
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

        // Check against each of the four action shortcuts, skipping the one the
        // caller is setting. Order is the page's order, which is the order this
        // used to test them in.
        var assigned = new[] { toggleShortcut, cancelShortcut, changeModeShortcut, streamingShortcut };
        for (var i = 0; i < ActionRoles.Length; i++)
        {
            var (role, labelKey) = ActionRoles[i];
            if (currentRole == role || !shortcut.Equals(assigned[i]))
            {
                continue;
            }

            return Loc.S("settings.shortcuts.error.duplicate",
                Loc.S(labelKey), assigned[i].ToDisplayString());
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
