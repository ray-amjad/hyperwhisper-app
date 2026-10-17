using System.Runtime.InteropServices;

namespace HyperWhisper.Services.Platform;

/// <summary>
/// Sends the app's own synthetic key events through SendInput, each stamped with
/// <see cref="InjectedKeyMarker"/> in <c>dwExtraInfo</c>, so a low-level hook in
/// this process can tell HyperWhisper's keys from everyone else's (#1851).
///
/// SmartPaste's Ctrl+V uses the generic <c>VK_CONTROL</c>, which push-to-talk
/// counts as a primary key since #694. <c>LLKHF_INJECTED</c> alone cannot pick
/// these out: AutoHotkey, Stream Deck and on-screen keyboards inject too, and a
/// user may drive push-to-talk with them. The marker is ours alone.
///
/// The INPUT records match what GregsStack.InputSimulatorStandard built, which
/// this class replaced: a scan code from MapVirtualKey and the extended-key flag
/// for the same key list, so the target sees the same keystroke as before.
/// </summary>
internal static class SyntheticKeyboard
{
    /// <summary>
    /// The <c>dwExtraInfo</c> on every key this class sends: "HWKY" in ASCII.
    /// Any value another program is unlikely to pick will do; it never leaves
    /// this machine's input queue.
    /// </summary>
    internal static readonly IntPtr InjectedKeyMarker = new(0x4857_4B59);

    /// <summary>KBDLLHOOKSTRUCT.flags bit: the event came from SendInput or keybd_event.</summary>
    internal const uint LLKHF_INJECTED = 0x10;

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    internal const int VK_CONTROL = 0x11;
    internal const int VK_V = 0x56;

    /// <summary>
    /// The keys InputSimulatorStandard sent with the extended-key flag. Kept as
    /// it was, generic VK_CONTROL and VK_MENU included, so the paste does not change.
    /// </summary>
    private static readonly HashSet<int> ExtendedKeys = new()
    {
        0x03, // CANCEL
        0x12, // MENU
        0x21, // PRIOR
        0x22, // NEXT
        0x23, // END
        0x24, // HOME
        0x25, // LEFT
        0x26, // UP
        0x27, // RIGHT
        0x28, // DOWN
        0x2C, // SNAPSHOT
        0x2D, // INSERT
        0x2E, // DELETE
        0x6F, // DIVIDE
        0x90, // NUMLOCK
        0xA3, // RCONTROL
        0xA5, // RMENU
        VK_CONTROL,
    };

    /// <summary>Ctrl down, V down, V up, Ctrl up: one SmartPaste paste.</summary>
    internal static readonly IReadOnlyList<PasteKeyEvent> CtrlV = new[]
    {
        new PasteKeyEvent(VK_CONTROL, KeyUp: false),
        new PasteKeyEvent(VK_V, KeyUp: false),
        new PasteKeyEvent(VK_V, KeyUp: true),
        new PasteKeyEvent(VK_CONTROL, KeyUp: true),
    };

    /// <summary>
    /// True when a low-level hook event is one this class sent: injected, and
    /// carrying the marker. A physical key has no LLKHF_INJECTED; another
    /// program's injected key has some other <c>dwExtraInfo</c>.
    /// </summary>
    internal static bool IsOwnInjectedKey(uint hookFlags, IntPtr extraInfo)
        => (hookFlags & LLKHF_INJECTED) != 0 && extraInfo == InjectedKeyMarker;

    /// <summary>
    /// Sends <paramref name="events"/> in one SendInput call, so no other input
    /// is interleaved with them.
    /// </summary>
    /// <exception cref="InvalidOperationException">SendInput inserted fewer events than asked (UIPI, a secure desktop).</exception>
    internal static void Send(IReadOnlyList<PasteKeyEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;

        var inputs = BuildInputs(events);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput inserted {sent} of {inputs.Length} key events (error {Marshal.GetLastWin32Error()}). " +
                "Input to a window at a higher integrity level is blocked by UIPI.");
        }
    }

    /// <summary>The INPUT records <see cref="Send"/> passes to SendInput.</summary>
    internal static INPUT[] BuildInputs(IReadOnlyList<PasteKeyEvent> events)
    {
        var inputs = new INPUT[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            var vk = events[i].Vk;
            uint flags = ExtendedKeys.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
            if (events[i].KeyUp) flags |= KEYEVENTF_KEYUP;

            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = (ushort)(MapVirtualKey((uint)vk, 0) & 0xFF),
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = InjectedKeyMarker,
                    }
                }
            };
        }
        return inputs;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // MOUSEINPUT is the largest member; it is declared only so the union, and so
    // the INPUT size SendInput checks, is right.
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
