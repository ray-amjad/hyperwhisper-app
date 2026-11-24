namespace HyperWhisper.Linux;

internal enum ShortcutRecorderVerdict { Accept, Ignore, SingleModifier, TypingKey }

/// <summary>
/// The recorder's verdict on a captured chord, kept free of Avalonia types so it can be exercised
/// without a window.
///
/// The rule this class adds for #628 is a property of the KEY, not of the role and not of the
/// session. A chord with no modifier fires on a plain press of that key:
/// <list type="bullet">
/// <item>On a true Xorg session the chord is grabbed with
/// <c>XGrabKey(..., ownerEvents: false)</c> on the root window (X11GlobalShortcutService.cs:293),
/// which CONSUMES the press -- no other application ever sees it. That is #628 as reported.</item>
/// <item>On a Wayland/evdev session nothing is consumed: the reader opens <c>/dev/input/event*</c>
/// read-only and never calls <c>EVIOCGRAB</c>. But <c>EvdevShortcutFilter</c> matches a binding
/// with no modifier group on every press of its key (EvdevShortcutFilter.cs:69-70), so the ACTION
/// still fires while the user types. Same fault, keystroke left in place.</item>
/// </list>
/// So the rule needs no session argument and no role argument: a bare key that ordinary typing
/// produces is refused everywhere.
///
/// The exception is a key ordinary typing never produces -- F1-F24 and Escape. Both are grabbable
/// on both backends (X11GlobalShortcutService.cs:246,251; EvdevShortcutMapper.MapKey), so a bare
/// F13 toggle and the shipped bare Escape cancel (SettingsViewModel.cs:348) stay recordable for
/// every role, exactly as on main.
///
/// Windows has the same rule since #1631: <c>ShortcutValidationService.ValidateActionShortcut</c>
/// refuses a bare key unless it is F1-F24 or Escape, with the same sentence. There the reason is the
/// ACTION, not the keystroke -- Windows routes a bare key through a NON-CONSUMING low-level keyboard
/// hook (KeyboardShortcutService.cs), so the press still reaches the focused application, but the
/// action still fires on every press of that key while the user types (#1631 saved a bare Tab as
/// the global Toggle shortcut). Keep the two key sets in step.
/// </summary>
internal static class LinuxShortcutRecorderRules
{
    internal static ShortcutRecorderVerdict Evaluate(int modifierCount, string key)
    {
        if (key.Length == 0)
        {
            // Nothing held at all is not a capture yet.
            if (modifierCount == 0) return ShortcutRecorderVerdict.Ignore;
            // A SINGLE bare modifier would steal ordinary typing, but a deliberate multi-modifier
            // chord such as Ctrl+Alt or Ctrl+Win is a real shortcut -- and Ctrl+Alt is the product
            // default toggle chord, so it has to stay recordable. Unchanged from main.
            return modifierCount == 1
                ? ShortcutRecorderVerdict.SingleModifier
                : ShortcutRecorderVerdict.Accept;
        }

        if (modifierCount > 0) return ShortcutRecorderVerdict.Accept;

        // #628: a bare letter was accepted, saved and armed, so every press of that letter fired
        // the action -- and on Xorg never reached the application the user was typing into.
        return IsDedicatedKey(key) ? ShortcutRecorderVerdict.Accept : ShortcutRecorderVerdict.TypingKey;
    }

    /// <summary>
    /// A key that ordinary typing never produces, so holding no modifier is safe. The names are the
    /// ones <c>MainWindow.MapShortcutKey</c> emits; everything else it can emit -- a letter, a
    /// digit, Space, Tab, Enter, Backspace, Delete, Insert, Home, End, PageUp, PageDown, an arrow
    /// and every OEM punctuation key -- appears in a normal keystroke stream.
    /// </summary>
    private static bool IsDedicatedKey(string key)
        => string.Equals(key, "Escape", StringComparison.Ordinal)
            || (key.Length >= 2 && key[0] == 'F'
                && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number)
                && number is >= 1 and <= 24);

    /// <summary>
    /// The catalogue key a verdict paints, or null when the verdict paints nothing. Accept writes
    /// the chord and Ignore is not a capture yet, so those two are the only silent outcomes; every
    /// other verdict refuses a chord and owes the user a sentence. Keeping the map here rather
    /// than in the key-down handler is what lets a test enumerate the enum and fail when a new
    /// rejecting verdict arrives with no message.
    /// </summary>
    internal static string? ErrorMessageKey(ShortcutRecorderVerdict verdict) => verdict switch
    {
        ShortcutRecorderVerdict.SingleModifier => "linux.shortcuts.error.singleModifier",
        ShortcutRecorderVerdict.TypingKey => "linux.shortcuts.error.typingKey",
        _ => null,
    };
}
