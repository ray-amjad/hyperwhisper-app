using System;
using System.Runtime.InteropServices;

namespace HyperWhisper.Services;

/// <summary>
/// The Win32 P/Invoke surface SmartPasteService and ClipboardBackup call:
/// foreground-window and focus APIs, the async key state for the held-modifier
/// guard, and the clipboard sequence number. Split out of SmartPasteService
/// (#862); the declarations are unchanged apart from their accessibility.
/// </summary>
internal static class SmartPasteNativeMethods
{
    // =========================================================================
    // WIN32 P/INVOKE DECLARATIONS
    // =========================================================================

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// The clipboard's change counter: every write to the clipboard by any process
    /// bumps it, and reading it does not. 0 when it cannot be read.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left, top, right, bottom;
    }

    // Special value to allow any process to set foreground window
    internal const int ASFW_ANY = -1;
}
