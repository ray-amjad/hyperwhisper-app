using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace HyperWhisper.Services;

/// <summary>
/// GLOBAL HOTKEY SERVICE (Low-Level Keyboard Hook Version)
///
/// Uses SetWindowsHookEx with WH_KEYBOARD_LL to detect Ctrl+Win combo.
/// This approach allows detecting modifier-only combinations, which is not
/// possible with the RegisterHotKey API (which requires a non-modifier key).
///
/// HOW IT WORKS:
/// 1. Install a low-level keyboard hook via SetWindowsHookEx
/// 2. Monitor WM_KEYDOWN/WM_KEYUP for VK_CONTROL and VK_LWIN/VK_RWIN
/// 3. When both are pressed simultaneously, fire the HotkeyPressed event
/// 4. Track key states to avoid repeated firing while held
///
/// IMPORTANT:
/// - Hook callback must return quickly to avoid system lag
/// - Must unhook on disposal to avoid resource leaks
/// - Works even when app is not focused (global hook)
/// - The _hookCallback delegate must be stored as a field to prevent GC
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // ============================================================================
    // WIN32 CONSTANTS
    // ============================================================================

    // Hook type for low-level keyboard (system-wide, works across all processes)
    private const int WH_KEYBOARD_LL = 13;

    // Key event messages
    private const int WM_KEYDOWN = 0x0100;     // Regular key down
    private const int WM_KEYUP = 0x0101;       // Regular key up
    private const int WM_SYSKEYDOWN = 0x0104;  // System key down (Alt, F10, etc.)
    private const int WM_SYSKEYUP = 0x0105;    // System key up

    // Virtual key codes for Ctrl
    private const int VK_CONTROL = 0x11;   // Generic Ctrl
    private const int VK_LCONTROL = 0xA2;  // Left Ctrl
    private const int VK_RCONTROL = 0xA3;  // Right Ctrl

    // Virtual key codes for Windows key
    private const int VK_LWIN = 0x5B;  // Left Windows key
    private const int VK_RWIN = 0x5C;  // Right Windows key

    // ============================================================================
    // P/INVOKE DECLARATIONS
    // ============================================================================

    /// <summary>
    /// Delegate for the low-level keyboard hook callback.
    /// Must match the LowLevelKeyboardProc signature.
    /// </summary>
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Installs a hook procedure to monitor low-level keyboard events.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>
    /// Removes the hook procedure installed by SetWindowsHookEx.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>
    /// Passes the hook information to the next hook in the chain.
    /// MUST be called at the end of our hook callback.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Gets a handle to the specified module (DLL/EXE).
    /// Used to get our module handle for the hook.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Structure containing information about a low-level keyboard input event.
    /// Passed to the hook callback via lParam.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;       // Virtual key code
        public uint scanCode;     // Hardware scan code
        public uint flags;        // Event flags (extended key, injected, etc.)
        public uint time;         // Timestamp
        public IntPtr dwExtraInfo; // Extra info from the event
    }

    // ============================================================================
    // INSTANCE FIELDS
    // ============================================================================

    // Hook handle - IntPtr.Zero means no hook installed
    private IntPtr _hookId = IntPtr.Zero;

    // CRITICAL: Must keep a reference to prevent garbage collection of the delegate
    // If this gets GC'd while hook is active, the app will crash
    private readonly LowLevelKeyboardProc _hookCallback;

    // Track modifier key states
    private bool _ctrlPressed;
    private bool _winPressed;

    // Prevent repeated firing while keys are held down
    // Reset when either Ctrl or Win is released
    private bool _hotkeyFired;

    // ============================================================================
    // PUBLIC PROPERTIES AND EVENTS
    // ============================================================================

    /// <summary>
    /// Fired when the Ctrl+Win hotkey combo is detected.
    /// Always invoked on the UI thread via BeginInvoke.
    /// </summary>
    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Human-readable description of the registered hotkey.
    /// Used for display in the UI.
    /// </summary>
    public string RegisteredHotkey => "Ctrl+Win";

    // ============================================================================
    // CONSTRUCTOR
    // ============================================================================

    public GlobalHotkeyService()
    {
        // Store the delegate as a field to prevent garbage collection
        // This is critical - if the delegate is collected, the hook callback
        // will point to invalid memory and crash the app
        _hookCallback = HookCallback;
    }

    // ============================================================================
    // PUBLIC METHODS
    // ============================================================================

    /// <summary>
    /// Installs the low-level keyboard hook.
    /// Call this after the form is created to start listening for Ctrl+Win.
    /// </summary>
    /// <returns>True if hook was installed successfully</returns>
    public bool RegisterToggleHotkey()
    {
        // Don't install multiple hooks
        if (_hookId != IntPtr.Zero)
        {
            LoggingService.Warn("GlobalHotkeyService: Hook already installed");
            return true;
        }

        // Get handle to our module (the EXE)
        // For WH_KEYBOARD_LL, we need the module handle
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;

        _hookId = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _hookCallback,
            GetModuleHandle(curModule?.ModuleName),
            0);  // 0 = hook all threads (global hook)

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            LoggingService.Error($"GlobalHotkeyService: Failed to install hook (error {error})");
            return false;
        }

        LoggingService.Info($"GlobalHotkeyService: Registered hotkey: {RegisteredHotkey}");
        return true;
    }

    /// <summary>
    /// Removes the keyboard hook and cleans up resources.
    /// </summary>
    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            LoggingService.Info("GlobalHotkeyService: Hook uninstalled");
        }
    }

    // ============================================================================
    // PRIVATE METHODS
    // ============================================================================

    /// <summary>
    /// Low-level keyboard hook callback.
    /// Called for every keyboard event system-wide.
    ///
    /// CRITICAL: This must return quickly! Any delay here affects
    /// keyboard responsiveness for the entire system.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 means we should pass it along without processing
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)hookStruct.vkCode;

            // Determine if this is a key down or key up event
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // Track Ctrl key state (any Ctrl key - left, right, or generic)
            if (vkCode == VK_CONTROL || vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
            {
                _ctrlPressed = isKeyDown;
                // Reset fired flag when Ctrl is released so user can press combo again
                if (isKeyUp) _hotkeyFired = false;
            }
            // Track Windows key state (left or right Win key)
            else if (vkCode == VK_LWIN || vkCode == VK_RWIN)
            {
                _winPressed = isKeyDown;
                // Reset fired flag when Win is released so user can press combo again
                if (isKeyUp) _hotkeyFired = false;
            }

            // Check if both Ctrl and Win are pressed simultaneously
            // The _hotkeyFired flag prevents repeated firing while keys are held
            if (_ctrlPressed && _winPressed && !_hotkeyFired)
            {
                _hotkeyFired = true;
                LoggingService.Debug("GlobalHotkeyService: Ctrl+Win detected");

                // Fire event on UI thread using WPF Dispatcher
                // This ensures the event handler runs on the correct thread
                // and doesn't block the hook callback
                WpfApplication.Current?.Dispatcher.BeginInvoke(() =>
                {
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                });
            }
        }

        // CRITICAL: Always call the next hook in the chain
        // Failing to do this will break keyboard input for other apps
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
}
