namespace HyperWhisper.Platform.Abstractions;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Meta = 1 << 3
}

/// <summary>
/// Stable logical key codes used in settings and translated by each platform
/// adapter. These deliberately do not expose WPF Key, Win32 virtual-key values,
/// Linux evdev codes, or Avalonia key types.
/// </summary>
public enum ShortcutKeyCode
{
    None,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    Escape,
    Space,
    Enter,
    Tab,
    Backspace,
    Delete,
    Insert,
    Home,
    End,
    PageUp,
    PageDown,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Period,
    Comma,
    Minus,
    Equal,
    Slash,
    Backslash,
    Semicolon,
    Quote,
    LeftBracket,
    RightBracket,
    Grave
}

public sealed record GlobalShortcut(
    ShortcutModifiers Modifiers,
    ShortcutKeyCode Key = ShortcutKeyCode.None)
{
    public bool IsEmpty => Modifiers == ShortcutModifiers.None && Key == ShortcutKeyCode.None;
    public bool IsModifierOnly => Modifiers != ShortcutModifiers.None && Key == ShortcutKeyCode.None;
}

public sealed record NamedShortcut(string Name, GlobalShortcut Shortcut);

public sealed class ShortcutTriggeredEventArgs(
    string name,
    GlobalShortcut shortcut) : EventArgs
{
    public string Name { get; } = name;
    public GlobalShortcut Shortcut { get; } = shortcut;
}

public interface IGlobalShortcutService : IDisposable
{
    event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;

    PlatformResult Start();
    IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(
        IReadOnlyCollection<NamedShortcut> shortcuts);
    void Clear();
    void ResetKeyboardState();
}

public enum PushToTalkMode
{
    Disabled,
    Modifier,
    CustomShortcut
}

public enum ModifierSide
{
    LeftControl,
    RightControl,
    LeftAlt,
    RightAlt,
    LeftShift,
    RightShift,
    LeftMeta,
    RightMeta
}

public sealed record PushToTalkConfiguration(
    PushToTalkMode Mode,
    ModifierSide Modifier = ModifierSide.LeftAlt,
    GlobalShortcut? CustomShortcut = null,
    bool DoublePressLock = false);

/// <summary>
/// Monitors only the configured push-to-talk input. Implementations must discard
/// non-configured key events at the module boundary and must never log or emit them.
/// </summary>
public interface IPushToTalkMonitor : IDisposable
{
    event EventHandler? Pressed;
    event EventHandler? Released;
    event EventHandler? Interfered;

    void Configure(PushToTalkConfiguration configuration);
    PlatformResult Start();
    void Reset();
    void ResetToIdle();
}
