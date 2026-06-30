using System.Globalization;
using System.Windows.Input;

namespace HyperWhisper.Models;

/// <summary>
/// Represents a normalized keyboard shortcut.
/// Stored in settings as a string (e.g., "Ctrl+Alt+S", "Ctrl+Win").
/// Supports modifier-only shortcuts for push-to-talk.
/// </summary>
public class KeyboardShortcut
{
    public bool Control { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public Key? Key { get; set; }

    public bool IsEmpty => !Control && !Alt && !Shift && !Win && Key == null;
    public bool IsModifierOnly => Key == null && (Control || Alt || Shift || Win);
    public int ModifierCount => (Control ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0) + (Win ? 1 : 0);
    public bool IsSingleBareModifier => IsModifierOnly && ModifierCount == 1;
    public bool IsIntentionalModifierChord => IsModifierOnly && ModifierCount >= 2;

    public KeyboardShortcut Clone() => new()
    {
        Control = Control,
        Alt = Alt,
        Shift = Shift,
        Win = Win,
        Key = Key
    };

    public override string ToString() => ToDisplayString();

    /// <summary>
    /// Returns a human-friendly string in a consistent order.
    /// </summary>
    public string ToDisplayString()
    {
        if (IsEmpty) return "Unassigned";

        List<string> parts = new();
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        if (Key.HasValue) parts.Add(NormalizeKeyName(Key.Value));

        return string.Join("+", parts);
    }

    public string ToPersistedString() => IsEmpty ? string.Empty : ToDisplayString();

    public static KeyboardShortcut FromPersistedString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new KeyboardShortcut();

        var shortcut = new KeyboardShortcut();
        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    shortcut.Control = true;
                    break;
                case "alt":
                    shortcut.Alt = true;
                    break;
                case "shift":
                    shortcut.Shift = true;
                    break;
                case "win":
                case "windows":
                case "cmd":
                case "command":
                    shortcut.Win = true;
                    break;
                default:
                    shortcut.Key = ParseKey(part);
                    break;
            }
        }
        return shortcut;
    }

    private static Key? ParseKey(string raw)
    {
        try
        {
            // Common aliases that don't match the Key enum name
            if (raw.Equals("Esc", StringComparison.OrdinalIgnoreCase))
                return System.Windows.Input.Key.Escape;

            // Oem punctuation keys: "." and "," persist as symbols, not enum names
            if (raw == ".") return System.Windows.Input.Key.OemPeriod;
            if (raw == ",") return System.Windows.Input.Key.OemComma;

            if (Enum.TryParse<Key>(raw, true, out var key))
            {
                return key;
            }

            // Single character keys (e.g., "S")
            if (raw.Length == 1)
            {
                char c = raw.ToUpper(CultureInfo.InvariantCulture)[0];
                if (c >= 'A' && c <= 'Z')
                {
                    return (Key)Enum.Parse(typeof(Key), c.ToString(), true);
                }
                if (char.IsDigit(c))
                {
                    return (Key)Enum.Parse(typeof(Key), $"D{c}", true);
                }
            }
        }
        catch
        {
            // Fall through and return null
        }
        return null;
    }

    private static string NormalizeKeyName(System.Windows.Input.Key key)
    {
        // Normalize digit keys to just the digit (e.g., D1 -> 1)
        if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
        {
            int digit = key - System.Windows.Input.Key.D0;
            return digit.ToString(CultureInfo.InvariantCulture);
        }

        // Display "Esc" instead of "Escape" (conventional short form)
        if (key == System.Windows.Input.Key.Escape)
            return "Esc";

        // Display Oem punctuation as symbols (e.g., OemPeriod -> ".", OemComma -> ",")
        if (key == System.Windows.Input.Key.OemPeriod) return ".";
        if (key == System.Windows.Input.Key.OemComma) return ",";

        return key.ToString();
    }

    // =========================================================================
    // EQUALITY COMPARISON
    // Enables duplicate detection for keyboard shortcuts.
    // Two shortcuts are equal if all modifiers and key match.
    // =========================================================================

    public bool Equals(KeyboardShortcut? other)
    {
        if (other == null) return false;
        return Control == other.Control
            && Alt == other.Alt
            && Shift == other.Shift
            && Win == other.Win
            && Key == other.Key;
    }

    public override bool Equals(object? obj)
        => obj is KeyboardShortcut other && Equals(other);

    public override int GetHashCode()
    {
        return HashCode.Combine(Control, Alt, Shift, Win, Key);
    }

    public static bool operator ==(KeyboardShortcut? left, KeyboardShortcut? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }

    public static bool operator !=(KeyboardShortcut? left, KeyboardShortcut? right)
        => !(left == right);
}
