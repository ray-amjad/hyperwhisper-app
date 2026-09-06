namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// One spelling for a keyboard chord, shared by every surface that shows one.
///
/// Windows builds its labels in <c>Models/KeyboardShortcut.ToDisplayString()</c>: modifiers in a
/// fixed Ctrl, Alt, Shift, Win order, joined with "+", and a handful of key names shortened to the
/// conventional form ("Esc", ".", ","). The shortcut services store the web-style names that
/// X11GlobalShortcutService and EvdevShortcutFilter grab by ("Control", "Escape", "Period",
/// "Digit1", "ArrowUp"), so a label built straight from storage read "Control+Shift+Period" where
/// Windows reads "Ctrl+Shift+.".
///
/// This is the one place that translates storage names into the Windows spelling. It is not a
/// parser: <see cref="Format"/> only ever produces display text, and the stored names are
/// untouched, so the shortcut services keep grabbing exactly the keys they did before.
/// </summary>
public static class ShortcutDisplay
{
    /// <summary>Windows emits modifiers in this order regardless of the order they were stored in.</summary>
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    /// <summary>
    /// Storage spells the modifiers the way the X11 and evdev services do. "None" is a
    /// placeholder for "no modifier" and is not a key, so Cancel reads "Esc", not "None+Esc".
    /// </summary>
    private static string? MapModifier(string name) => name.ToLowerInvariant() switch
    {
        "control" or "ctrl" => "Ctrl",
        "alt" or "option" => "Alt",
        "shift" => "Shift",
        "meta" or "win" or "super" or "command" => "Win",
        _ => null,
    };

    /// <summary>
    /// The key names Windows rewrites. Everything else is already spelled the same on both
    /// sides, so it passes through untouched rather than being guessed at.
    /// </summary>
    private static string MapKey(string key)
    {
        // Digits are stored "Digit1" and shown "1", the way Windows turns Key.D1 into "1".
        if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(key[5]))
            return key[5].ToString();

        return key switch
        {
            "Escape" => "Esc",
            "Period" => ".",
            "Comma" => ",",
            // WPF's Key enum names, which is what Windows prints for these.
            "ArrowUp" => "Up",
            "ArrowDown" => "Down",
            "ArrowLeft" => "Left",
            "ArrowRight" => "Right",
            "Enter" => "Return",
            "Backspace" => "Back",
            _ => key,
        };
    }

    /// <summary>
    /// Formats a stored modifier list and key as Windows would show them, e.g. ("Control,Shift",
    /// "Period") becomes "Ctrl+Shift+.". Returns an empty string when nothing is assigned.
    /// </summary>
    public static string Format(string? modifiers, string? key)
    {
        var parts = new List<string>(4);

        var present = (modifiers ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MapModifier)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in ModifierOrder)
            if (present.Contains(name))
                parts.Add(name);

        if (!string.IsNullOrWhiteSpace(key))
            parts.Add(MapKey(key.Trim()));

        return string.Join("+", parts);
    }
}
