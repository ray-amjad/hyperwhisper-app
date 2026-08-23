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
/// Stable logical key name used across settings and platform adapters. A name is
/// used instead of an OS enum so the contract can round-trip uncommon keys without
/// exposing WPF, Win32 virtual-key, Linux evdev, or Avalonia values. Adapters own
/// translation to their native key vocabulary and reject names they cannot support.
/// </summary>
public readonly record struct ShortcutKeyCode
{
    private readonly string? _value;

    public ShortcutKeyCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A shortcut key code cannot be empty.", nameof(value));
        }

        _value = value;
    }

    public static ShortcutKeyCode None => default;
    public string Value => _value ?? string.Empty;
    public bool IsNone => string.IsNullOrEmpty(_value);

    public override string ToString() => Value;
}

public sealed record GlobalShortcut(
    ShortcutModifiers Modifiers,
    ShortcutKeyCode Key = default)
{
    public bool IsEmpty => Modifiers == ShortcutModifiers.None && Key.IsNone;
    public bool IsModifierOnly => Modifiers != ShortcutModifiers.None && Key.IsNone;
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
    Control,
    Alt,
    Shift,
    Meta,
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
