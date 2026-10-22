using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HyperWhisper.Services;

/// <summary>
/// Keeps the user's held modifier keys out of the simulated Ctrl+V (issue #1495).
///
/// THE FAULT. A paste is a synthetic Ctrl+V, and Windows combines synthetic input
/// with the keys the user is still physically holding. The default toggle chord is
/// Ctrl+Alt: a user who stops a recording and keeps the chord down for ~250 ms is
/// still holding Alt when an on-device transcription (110-170 ms) reaches the
/// paste. The target then receives Ctrl+Alt+V, pastes nothing (or runs Paste
/// Special), and the log still says pasted. The same holds for any chord the user
/// configures, for a push-to-talk combo whose other keys are still down, and for
/// the streaming stop chord.
///
/// WHICH KEYS. Shift, Alt and Win, left and right. Each one turns Ctrl+V into a
/// different command. Ctrl is NOT on the list: Ctrl+V with Ctrl already down is
/// still Ctrl+V, so waiting on it would only delay the paste. AltGr is Right Alt
/// plus a synthetic Left Ctrl, so it is covered by Right Alt.
///
/// THE STATE READ. The reader is GetAsyncKeyState in production: the state the
/// input stream will combine the synthetic keys with. A key that our own
/// low-level hook swallowed (the Win key of a Ctrl+Win chord) never reaches that
/// state and never reaches the target, so it is correctly not waited on.
///
/// TWO STEPS, both here so the smoke suite can pin them without a desktop:
/// 1. <see cref="WaitForReleaseAsync"/>: an ASYNC, bounded poll for the keys to
///    come up. It must never block the thread: both WH_KEYBOARD_LL hooks live on
///    the UI thread, and a blocked UI thread cannot see the key-up it is waiting
///    for (and freezes the desktop keyboard until the hook times out).
/// 2. <see cref="PlanRelease"/>: what to inject when a key is still down at the
///    moment the Ctrl+V is sent (the wait gave up, the key went down after the
///    wait, or a path that cannot wait). A key-up for each held key, preceded by
///    one tap of an unassigned key when Alt or Win is held, so the target never
///    sees a lone Alt (menu bar) or a lone Win (Start menu) come up.
/// </summary>
internal static class PasteModifierGuard
{
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    /// <summary>
    /// The "menu mask" key: 0xE8 is unassigned in the Windows virtual-key table, so
    /// no application binds it. Tapped while Alt or Win is down, it makes the next
    /// Alt or Win key-up a chord release rather than a lone tap, so the target does
    /// not activate its menu bar and the shell does not open Start. AutoHotkey uses
    /// the same technique for the same reason.
    /// </summary>
    internal const int MenuMaskVk = 0xE8;

    /// <summary>
    /// The longest a paste waits for the user to let go. A normal key press is
    /// released well inside this; a key that is still down after it is treated as
    /// held on purpose or stuck, and the paste releases it instead of waiting.
    /// </summary>
    internal static readonly TimeSpan DefaultReleaseTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>One system timer tick: the poll costs nothing and adds at most ~16 ms.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>The keys that change what Ctrl+V means, in a fixed order for stable logs.</summary>
    internal static IReadOnlyList<int> ConflictingModifierVks { get; } = new[]
    {
        VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN
    };

    /// <summary>The conflicting modifiers the reader reports as down, in <see cref="ConflictingModifierVks"/> order.</summary>
    internal static IReadOnlyList<int> HeldConflictingModifiers(Func<int, bool> isKeyDown)
    {
        ArgumentNullException.ThrowIfNull(isKeyDown);

        var held = new List<int>(capacity: 2);
        foreach (var vk in ConflictingModifierVks)
        {
            if (isKeyDown(vk))
                held.Add(vk);
        }

        return held;
    }

    /// <summary>
    /// The synthetic key events that take <paramref name="held"/> out of the next
    /// Ctrl+V: one menu-mask tap first when an Alt or a Win key is among them, then
    /// a key-up for each held key. Empty when nothing is held. The keys are NOT
    /// pressed again afterwards: re-pressing them would replay the user's own
    /// chord into both low-level hooks and could start a new recording.
    /// </summary>
    internal static IReadOnlyList<PasteKeyEvent> PlanRelease(IReadOnlyList<int> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (held.Count == 0)
            return Array.Empty<PasteKeyEvent>();

        var plan = new List<PasteKeyEvent>(held.Count + 2);
        if (held.Any(IsMenuOrWinKey))
        {
            plan.Add(new PasteKeyEvent(MenuMaskVk, KeyUp: false));
            plan.Add(new PasteKeyEvent(MenuMaskVk, KeyUp: true));
        }

        foreach (var vk in held)
            plan.Add(new PasteKeyEvent(vk, KeyUp: true));

        return plan;
    }

    /// <summary>
    /// Polls <paramref name="isKeyDown"/> until no conflicting modifier is down, or
    /// until <paramref name="timeout"/> worth of polls has passed. Never blocks:
    /// every pause is an awaited <paramref name="delay"/>. Returns at once, without
    /// a single delay, when nothing is held, which is the common case.
    ///
    /// The budget counts poll intervals rather than wall time, so a test with an
    /// instant delay is deterministic and the loop is bounded whatever the clock
    /// does: at most timeout / pollInterval polls.
    /// </summary>
    internal static async Task<ModifierReleaseWait> WaitForReleaseAsync(
        Func<int, bool> isKeyDown,
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isKeyDown);
        ArgumentNullException.ThrowIfNull(delay);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The poll interval must be positive.");
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout cannot be negative.");

        var initiallyHeld = HeldConflictingModifiers(isKeyDown);
        if (initiallyHeld.Count == 0)
            return new ModifierReleaseWait(initiallyHeld, Array.Empty<int>(), WaitedMs: 0);

        var waited = TimeSpan.Zero;
        var stillHeld = initiallyHeld;
        while (stillHeld.Count > 0 && waited < timeout)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await delay(pollInterval, cancellationToken).ConfigureAwait(true);
            waited += pollInterval;
            stillHeld = HeldConflictingModifiers(isKeyDown);
        }

        return new ModifierReleaseWait(initiallyHeld, stillHeld, (int)waited.TotalMilliseconds);
    }

    /// <summary>"LAlt+RShift" for a log line; "none" for an empty list.</summary>
    internal static string Describe(IEnumerable<int> vks)
    {
        var names = vks.Select(KeyName).ToList();
        return names.Count == 0 ? "none" : string.Join("+", names);
    }

    private static bool IsMenuOrWinKey(int vk) =>
        vk is VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    private static string KeyName(int vk) => vk switch
    {
        VK_LMENU => "LAlt",
        VK_RMENU => "RAlt",
        VK_LSHIFT => "LShift",
        VK_RSHIFT => "RShift",
        VK_LWIN => "LWin",
        VK_RWIN => "RWin",
        _ => $"0x{vk:X2}"
    };
}

/// <summary>One synthetic key event of a <see cref="PasteModifierGuard.PlanRelease"/> plan.</summary>
internal readonly record struct PasteKeyEvent(int Vk, bool KeyUp);

/// <summary>How a <see cref="PasteModifierGuard.WaitForReleaseAsync"/> call ended.</summary>
internal readonly record struct ModifierReleaseWait(
    IReadOnlyList<int> InitiallyHeld,
    IReadOnlyList<int> StillHeld,
    int WaitedMs)
{
    /// <summary>Something was held when the wait began.</summary>
    public bool WasHeld => InitiallyHeld.Count > 0;

    /// <summary>The wait ended with every conflicting modifier up.</summary>
    public bool Released => StillHeld.Count == 0;
}
