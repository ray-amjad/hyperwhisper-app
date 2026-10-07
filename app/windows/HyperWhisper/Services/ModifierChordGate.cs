using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>Where one modifier-only chord stands. See <see cref="ModifierChordGate"/>.</summary>
internal enum ModifierChordState
{
    /// <summary>The chord's modifiers are not all down.</summary>
    Idle,

    /// <summary>The chord is down and nothing else has joined it. Releasing it triggers.</summary>
    Armed,

    /// <summary>
    /// The chord is down, but another key (a non-modifier, or a modifier the chord
    /// does not name) is or was down with it. The user is typing a different
    /// shortcut, so releasing the chord does nothing.
    /// </summary>
    Spoiled
}

/// <summary>
/// Decides when a modifier-only shortcut (Ctrl+Alt, Ctrl+Win, ...) was pressed
/// ON ITS OWN, so Ctrl+Alt+Left is not taken for Ctrl+Alt (issue #1497).
///
/// A modifier-only chord is also the prefix of every ordinary shortcut that
/// starts with the same modifiers, so at key-down it is impossible to know which
/// one the user means. The chord therefore triggers on its RELEASE: the first
/// key-up that breaks it, when it was armed by a key-down and nothing else
/// joined it while it was held.
///
/// Pure: no Win32 call, so the smoke tests can drive it with plain key sets.
/// KeyboardShortcutService feeds it the set of virtual keys its hook tracks,
/// after it has dropped any key GetAsyncKeyState says is no longer down. That
/// pruning is what keeps a key-up lost to the secure desktop (Ctrl+Alt+Del) or
/// to a focus change from arming or firing a stale chord.
/// </summary>
internal sealed class ModifierChordGate
{
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;      // Generic Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;     // Alt
    internal const int VK_RMENU = 0xA5;     // Right Alt, AltGr on many layouts

    private readonly KeyboardShortcut _shortcut;

    public ModifierChordGate(KeyboardShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (!shortcut.IsModifierOnly)
            throw new ArgumentException($"'{shortcut}' is not a modifier-only shortcut.", nameof(shortcut));

        _shortcut = shortcut.Clone();
    }

    public ModifierChordState State { get; private set; } = ModifierChordState.Idle;

    /// <summary>True while the chord is held and still clean.</summary>
    public bool IsArmed => State == ModifierChordState.Armed;

    /// <summary>
    /// Call after every key-down, with the key already added to <paramref name="pressedKeys"/>.
    /// A key-down never triggers: it can only arm, spoil, or (when pruning found
    /// the chord's keys were stale) quietly drop the chord.
    /// </summary>
    public void KeyDown(IReadOnlySet<int> pressedKeys)
    {
        if (!IsSatisfied(_shortcut, pressedKeys))
        {
            State = ModifierChordState.Idle;
            return;
        }

        bool exact = IsExact(_shortcut, pressedKeys);
        State = State switch
        {
            // Arming needs the chord AND nothing else: Left held first, then
            // Ctrl+Alt, is Ctrl+Alt+Left too.
            ModifierChordState.Idle => exact ? ModifierChordState.Armed : ModifierChordState.Spoiled,
            ModifierChordState.Armed => exact ? ModifierChordState.Armed : ModifierChordState.Spoiled,
            // Once spoiled, the chord stays spoiled until it is let go, even if the
            // extra key is released first.
            _ => ModifierChordState.Spoiled
        };
    }

    /// <summary>
    /// Call after every key-up, with <paramref name="releasedVk"/> already removed
    /// from <paramref name="pressedKeys"/>. Returns true when this release is the
    /// shortcut: the chord was armed, this key-up broke it, and the rest of the
    /// chord is still genuinely down (a chord whose other keys were pruned as
    /// stale is dropped, not fired).
    /// </summary>
    public bool KeyUp(IReadOnlySet<int> pressedKeys, int releasedVk)
    {
        if (IsSatisfied(_shortcut, pressedKeys)) return false;

        var withReleased = new HashSet<int>(pressedKeys) { releasedVk };
        bool fire = State == ModifierChordState.Armed && IsSatisfied(_shortcut, withReleased);
        State = ModifierChordState.Idle;
        return fire;
    }

    public void Reset() => State = ModifierChordState.Idle;

    /// <summary>Every modifier the shortcut names is down. Other keys may be down too.</summary>
    internal static bool IsSatisfied(KeyboardShortcut shortcut, IReadOnlySet<int> pressedKeys)
    {
        if (shortcut.IsEmpty) return false;
        if (shortcut.Control && !IsAnyCtrlDown(pressedKeys)) return false;
        if (shortcut.Alt && !IsAnyAltDown(pressedKeys)) return false;
        if (shortcut.Shift && !IsAnyShiftDown(pressedKeys)) return false;
        if (shortcut.Win && !IsAnyWinDown(pressedKeys)) return false;
        return true;
    }

    /// <summary>
    /// The shortcut's modifiers are down and nothing else is: no modifier it does
    /// not name, and no non-modifier key.
    /// </summary>
    internal static bool IsExact(KeyboardShortcut shortcut, IReadOnlySet<int> pressedKeys)
    {
        if (!IsSatisfied(shortcut, pressedKeys)) return false;
        if (!shortcut.Control && IsAnyCtrlDown(pressedKeys)) return false;
        if (!shortcut.Alt && IsAnyAltDown(pressedKeys)) return false;
        if (!shortcut.Shift && IsAnyShiftDown(pressedKeys)) return false;
        if (!shortcut.Win && IsAnyWinDown(pressedKeys)) return false;

        foreach (var vk in pressedKeys)
        {
            if (!IsModifierVirtualKey(vk)) return false;
        }

        return true;
    }

    /// <summary>
    /// AltGr is down (VK_RMENU). Windows then injects a synthetic VK_LCONTROL,
    /// which is not a real Ctrl press.
    /// </summary>
    internal static bool IsAltGrActive(IReadOnlySet<int> pressedKeys) => pressedKeys.Contains(VK_RMENU);

    internal static bool IsAnyCtrlDown(IReadOnlySet<int> pressedKeys)
    {
        if (IsAltGrActive(pressedKeys))
        {
            // AltGr sends synthetic VK_LCONTROL — only count RCtrl or generic Ctrl as real
            return pressedKeys.Contains(VK_CONTROL) || pressedKeys.Contains(VK_RCONTROL);
        }
        return pressedKeys.Contains(VK_CONTROL) || pressedKeys.Contains(VK_LCONTROL) || pressedKeys.Contains(VK_RCONTROL);
    }

    internal static bool IsAnyAltDown(IReadOnlySet<int> pressedKeys)
    {
        if (IsAltGrActive(pressedKeys))
        {
            // AltGr is not a real Alt press — only count LAlt or generic Alt
            return pressedKeys.Contains(VK_MENU) || pressedKeys.Contains(VK_LMENU);
        }
        return pressedKeys.Contains(VK_MENU) || pressedKeys.Contains(VK_LMENU) || pressedKeys.Contains(VK_RMENU);
    }

    internal static bool IsAnyShiftDown(IReadOnlySet<int> pressedKeys) =>
        pressedKeys.Contains(VK_SHIFT) || pressedKeys.Contains(VK_LSHIFT) || pressedKeys.Contains(VK_RSHIFT);

    internal static bool IsAnyWinDown(IReadOnlySet<int> pressedKeys) =>
        pressedKeys.Contains(VK_LWIN) || pressedKeys.Contains(VK_RWIN);

    internal static bool IsModifierVirtualKey(int vk) =>
        IsCtrlVirtualKey(vk) || IsAltVirtualKey(vk) || IsShiftVirtualKey(vk) || IsWinVirtualKey(vk);

    internal static bool IsCtrlVirtualKey(int vk) => vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;
    internal static bool IsAltVirtualKey(int vk) => vk is VK_MENU or VK_LMENU or VK_RMENU;
    internal static bool IsShiftVirtualKey(int vk) => vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT;
    internal static bool IsWinVirtualKey(int vk) => vk is VK_LWIN or VK_RWIN;
}
