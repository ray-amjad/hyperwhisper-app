using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Registers and tracks global keyboard shortcuts (named).
/// - RegisterHotKey is used for non-modifier combos so the system delivers WM_HOTKEY.
/// - A low-level hook tracks modifier-only shortcuts and key-up events for push-to-talk.
/// </summary>
public sealed class KeyboardShortcutService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;   // Alt
    private const int VK_RMENU = 0xA5;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    // Generic virtual key codes (some keyboards/drivers send these instead of L/R variants)
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;     // Generic Alt
    private const int VK_SHIFT = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public sealed class ShortcutEventArgs : EventArgs
    {
        public ShortcutEventArgs(string name, KeyboardShortcut shortcut)
        {
            Name = name;
            Shortcut = shortcut;
        }

        public string Name { get; }
        public KeyboardShortcut Shortcut { get; }
    }

    public event EventHandler<ShortcutEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutEventArgs>? ShortcutReleased;

    private readonly Dictionary<string, KeyboardShortcut> _shortcuts = new();
    private readonly HashSet<string> _activeShortcuts = new();
    private readonly HashSet<int> _pressedKeys = new();

    private readonly LowLevelKeyboardProc _hookCallback;
    private IntPtr _hookId = IntPtr.Zero;

    private HwndSource? _hwndSource;
    private readonly ConcurrentDictionary<int, string> _hotkeyIdToName = new();
    private readonly ConcurrentDictionary<string, int> _nameToHotkeyId = new();
    private int _hotkeyIdCounter = 1;

    public KeyboardShortcutService()
    {
        _hookCallback = HookCallback;
    }

    public void AttachWindowIfNeeded()
    {
        if (_hwndSource != null) return;
        var window = WpfApplication.Current?.MainWindow;
        if (window == null)
        {
            LoggingService.Warn("KeyboardShortcutService: MainWindow is null, cannot attach");
            return;
        }

        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            // Window handle not ready yet - this can happen if called too early
            // Try to ensure the handle is created
            handle = helper.EnsureHandle();
            if (handle == IntPtr.Zero)
            {
                LoggingService.Warn("KeyboardShortcutService: Window handle is zero even after EnsureHandle");
                return;
            }
        }

        _hwndSource = HwndSource.FromHwnd(handle);
        if (_hwndSource != null)
        {
            _hwndSource.AddHook(WndProc);
            LoggingService.Info($"KeyboardShortcutService: Attached to window handle 0x{handle:X}");
        }
        else
        {
            LoggingService.Warn("KeyboardShortcutService: Failed to create HwndSource from handle");
        }
    }

    public Dictionary<string, Result> RegisterShortcuts(Dictionary<string, KeyboardShortcut> shortcuts)
    {
        AttachWindowIfNeeded();
        EnsureHook();

        var results = new Dictionary<string, Result>();

        // Unregister removed shortcuts
        var removed = _shortcuts.Keys.Except(shortcuts.Keys).ToList();
        foreach (var name in removed)
        {
            _shortcuts.Remove(name);
            UnregisterHotKeyForName(name);
        }

        // Register shortcuts and collect results
        foreach (var kvp in shortcuts)
        {
            var result = RegisterShortcut(kvp.Key, kvp.Value);
            results[kvp.Key] = result;
        }

        return results;
    }

    public Result RegisterShortcut(string name, KeyboardShortcut shortcut)
    {
        _activeShortcuts.Remove(name);
        UnregisterHotKeyForName(name);
        _shortcuts.Remove(name);

        LoggingService.Info($"KeyboardShortcutService: Registering '{name}' = {shortcut} (IsModifierOnly={shortcut.IsModifierOnly}, HasKey={shortcut.Key.HasValue}, HwndSource={_hwndSource != null})");

        if (shortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"KeyboardShortcutService: Rejecting unsafe single-modifier shortcut '{name}' = {shortcut}");
            return Result.Failure($"Unsafe single modifier shortcut: {shortcut}");
        }

        // Shortcuts that cannot/should-not be registered with RegisterHotKey are handled via low-level hook.
        // This includes:
        // - Multi-modifier shortcuts like Ctrl+Win (RegisterHotKey requires a non-modifier key)
        // - Bare keys like Esc/F1/etc. with no modifiers (RegisterHotKey would reserve the key globally
        //   and suppress normal behavior in other apps, e.g., Esc no longer closing dialogs)
        bool requiresHookOnly = RequiresHookOnly(shortcut);
        if (requiresHookOnly)
        {
            _shortcuts[name] = shortcut;
            LoggingService.Info($"KeyboardShortcutService: '{name}' uses low-level hook only (Shortcut={shortcut})");
            return Result.Success();
        }

        if (!shortcut.Key.HasValue)
        {
            return Result.Success();
        }

        if (_hwndSource == null)
        {
            LoggingService.Error($"KeyboardShortcutService: Cannot register '{name}' - HwndSource is null!");
            return Result.Failure($"Cannot register '{name}' - HwndSource is null");
        }

        // Attempt Win32 RegisterHotKey
        var modifiers = BuildHotkeyModifierMask(shortcut);
        var vk = (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key.Value);
        int id = _hotkeyIdCounter++;

        if (RegisterHotKey(_hwndSource.Handle, id, modifiers, vk))
        {
            _shortcuts[name] = shortcut;
            _hotkeyIdToName[id] = name;
            _nameToHotkeyId[name] = id;
            LoggingService.Info($"KeyboardShortcutService: SUCCESS - Registered hotkey {shortcut} ({name}) with id={id}");
            return Result.Success();
        }
        else
        {
            int win32Error = Marshal.GetLastWin32Error();
            LoggingService.Error($"KeyboardShortcutService: FAILED to register hotkey {shortcut} ({name}), Win32 error={win32Error}");
            return Result.Failure($"Failed to register hotkey {shortcut} ({name}), Win32 error={win32Error}");
        }
    }

    public void Clear()
    {
        foreach (var id in _hotkeyIdToName.Keys)
        {
            if (_hwndSource != null) UnregisterHotKey(_hwndSource.Handle, id);
        }
        _hotkeyIdToName.Clear();
        _nameToHotkeyId.Clear();
        _shortcuts.Clear();
        _activeShortcuts.Clear();
        _pressedKeys.Clear();
    }

    /// <summary>
    /// Clears tracked key state to prevent stale keys from causing false shortcut matches.
    /// Call after transcription completes or recording ends to resynchronize state.
    /// </summary>
    public void ResetKeyboardState()
    {
        _pressedKeys.Clear();
        _activeShortcuts.Clear();
    }

    public void Dispose()
    {
        Clear();
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        if (_hwndSource != null)
        {
            // Do NOT dispose _hwndSource: HwndSource.FromHwnd returns the shared
            // HwndSource owned by WPF for the main window. Disposing it would tear
            // down the window's message pump. Removing our hook is sufficient.
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private void EnsureHook()
    {
        if (_hookId != IntPtr.Zero) return;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _hookCallback,
            GetModuleHandle(curModule?.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            LoggingService.Error($"KeyboardShortcutService: Failed to install hook (error {error})");
        }
        else
        {
            LoggingService.Debug("KeyboardShortcutService: Low-level keyboard hook installed");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = (int)hookStruct.vkCode;

                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isKeyDown)
                {
                    _pressedKeys.Add(vkCode);
                    EvaluateShortcuts(justPressedVk: vkCode);
                    if (ShouldSuppressMatchedWinShortcutKeyDown(vkCode))
                    {
                        LoggingService.Debug("KeyboardShortcutService: Suppressed matched Win-key shortcut key-down");
                        return new IntPtr(1);
                    }
                }
                else if (isKeyUp)
                {
                    _pressedKeys.Remove(vkCode);
                    EvaluateShortcuts();
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("KeyboardShortcutService: HookCallback error", ex);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <param name="justPressedVk">
    /// The virtual key code that just triggered this evaluation via key-down hook event,
    /// or -1 if triggered by key-up or other context. GetAsyncKeyState is unreliable for
    /// the triggering key inside WH_KEYBOARD_LL callbacks (state updates after the hook
    /// chain completes), so we skip physical validation for this specific key.
    /// </param>
    private void EvaluateShortcuts(int justPressedVk = -1)
    {
        foreach (var kvp in _shortcuts.ToList())
        {
            var name = kvp.Key;
            var shortcut = kvp.Value;
            bool shouldBeActive = IsShortcutPressed(shortcut, justPressedVk);
            bool isActive = _activeShortcuts.Contains(name);

            // For shortcuts registered via RegisterHotKey, WM_HOTKEY handles press detection.
            // The hook should only detect *release* for these shortcuts to avoid a race condition
            // where the hook fires press before WM_HOTKEY arrives (posted to message queue),
            // causing duplicate or stale activations.
            bool hasRegisteredHotkey = _nameToHotkeyId.ContainsKey(name);

            if (shouldBeActive && !isActive && !hasRegisteredHotkey)
            {
                _activeShortcuts.Add(name);
                LoggingService.Info($"KeyboardShortcutService: SHORTCUT PRESSED '{name}' ({shortcut})");
                RaiseShortcutEvent(ShortcutPressed, name, shortcut);
            }
            else if (!shouldBeActive && isActive)
            {
                _activeShortcuts.Remove(name);
                LoggingService.Debug($"KeyboardShortcutService: Shortcut released '{name}'");
                RaiseShortcutEvent(ShortcutReleased, name, shortcut);
            }
        }
    }

    /// <summary>
    /// Returns true if AltGr is currently active (VK_RMENU is pressed).
    /// When AltGr is active, VK_LCONTROL is a synthetic press injected by Windows,
    /// not a real key press.
    /// </summary>
    private bool IsAltGrActive() => _pressedKeys.Contains(VK_RMENU);

    private bool IsAnyCtrlDown(HashSet<int> pressedKeys)
    {
        if (IsAltGrActive())
        {
            // AltGr sends synthetic VK_LCONTROL — only count RCtrl or generic Ctrl as real
            return pressedKeys.Contains(VK_CONTROL) || pressedKeys.Contains(VK_RCONTROL);
        }
        return pressedKeys.Contains(VK_CONTROL) || pressedKeys.Contains(VK_LCONTROL) || pressedKeys.Contains(VK_RCONTROL);
    }

    private bool IsAnyAltDown(HashSet<int> pressedKeys)
    {
        if (IsAltGrActive())
        {
            // AltGr is not a real Alt press — only count LAlt or generic Alt
            return pressedKeys.Contains(VK_MENU) || pressedKeys.Contains(VK_LMENU);
        }
        return pressedKeys.Contains(VK_MENU) || pressedKeys.Contains(VK_LMENU) || pressedKeys.Contains(VK_RMENU);
    }

    private static bool IsAnyShiftDown(HashSet<int> pressedKeys) =>
        pressedKeys.Contains(VK_SHIFT) || pressedKeys.Contains(VK_LSHIFT) || pressedKeys.Contains(VK_RSHIFT);

    private static bool IsAnyWinDown(HashSet<int> pressedKeys) =>
        pressedKeys.Contains(VK_LWIN) || pressedKeys.Contains(VK_RWIN);

    /// <summary>
    /// Returns true if the given key is physically held down according to GetAsyncKeyState.
    /// </summary>
    private static bool IsKeyPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool IsShortcutPressed(KeyboardShortcut shortcut, int justPressedVk = -1)
    {
        if (shortcut.IsEmpty) return false;
        if (shortcut.Control && !IsAnyCtrlDown(_pressedKeys)) return false;
        if (shortcut.Alt && !IsAnyAltDown(_pressedKeys)) return false;
        if (shortcut.Shift && !IsAnyShiftDown(_pressedKeys)) return false;
        if (shortcut.Win && !IsAnyWinDown(_pressedKeys)) return false;

        if (shortcut.Key.HasValue)
        {
            int vk = KeyInterop.VirtualKeyFromKey(shortcut.Key.Value);
            if (!_pressedKeys.Contains(vk)) return false;

            // Validate against actual keyboard state to prevent stale keys from
            // causing false positives (e.g., key-up missed due to focus loss).
            // IMPORTANT: Skip this check for the key that just triggered the hook callback —
            // GetAsyncKeyState is unreliable inside WH_KEYBOARD_LL because the async key state
            // is not updated until after the hook chain completes (see PushToTalkMonitor docs).
            // Without this skip, bare-key shortcuts like Esc fail because GetAsyncKeyState
            // returns "not pressed" for the key that is actively being pressed.
            if (vk != justPressedVk && !IsKeyPhysicallyDown(vk))
            {
                _pressedKeys.Remove(vk);
                return false;
            }
        }

        return true;
    }

    private bool IsShortcutPressedExact(KeyboardShortcut shortcut, int justPressedVk = -1)
    {
        if (!IsShortcutPressed(shortcut, justPressedVk)) return false;

        if (!shortcut.Control && IsAnyCtrlDown(_pressedKeys)) return false;
        if (!shortcut.Alt && IsAnyAltDown(_pressedKeys)) return false;
        if (!shortcut.Shift && IsAnyShiftDown(_pressedKeys)) return false;
        if (!shortcut.Win && IsAnyWinDown(_pressedKeys)) return false;

        var shortcutKeyVk = shortcut.Key.HasValue
            ? KeyInterop.VirtualKeyFromKey(shortcut.Key.Value)
            : (int?)null;

        foreach (var pressedVk in _pressedKeys)
        {
            if (IsModifierVirtualKey(pressedVk)) continue;
            if (shortcutKeyVk.HasValue && pressedVk == shortcutKeyVk.Value) continue;
            return false;
        }

        return true;
    }

    private bool ShouldSuppressMatchedWinShortcutKeyDown(int justPressedVk)
    {
        if (!IsWinVirtualKey(justPressedVk))
        {
            return false;
        }

        // Suppress only when the Win key-down completes an exact registered chord
        // such as Ctrl+Win. If Win is pressed first, pass it through so Windows
        // shell shortcuts like Win+R/Win+L keep their normal behavior.
        foreach (var shortcut in _shortcuts.Values.ToList())
        {
            if (!shortcut.Win || !RequiresHookOnly(shortcut)) continue;
            if (!ShortcutContainsVirtualKey(shortcut, justPressedVk)) continue;
            if (IsShortcutPressedExact(shortcut, justPressedVk)) return true;
        }

        return false;
    }

    private static bool HasAnyModifier(KeyboardShortcut shortcut) =>
        shortcut.Control || shortcut.Alt || shortcut.Shift || shortcut.Win;

    private static bool RequiresHookOnly(KeyboardShortcut shortcut) =>
        shortcut.IsModifierOnly || !HasAnyModifier(shortcut);

    private static bool ShortcutContainsVirtualKey(KeyboardShortcut shortcut, int vk)
    {
        if (shortcut.Control && IsCtrlVirtualKey(vk)) return true;
        if (shortcut.Alt && IsAltVirtualKey(vk)) return true;
        if (shortcut.Shift && IsShiftVirtualKey(vk)) return true;
        if (shortcut.Win && IsWinVirtualKey(vk)) return true;
        return shortcut.Key.HasValue && KeyInterop.VirtualKeyFromKey(shortcut.Key.Value) == vk;
    }

    private static bool IsModifierVirtualKey(int vk) =>
        IsCtrlVirtualKey(vk) || IsAltVirtualKey(vk) || IsShiftVirtualKey(vk) || IsWinVirtualKey(vk);

    private static bool IsCtrlVirtualKey(int vk) => vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;
    private static bool IsAltVirtualKey(int vk) => vk is VK_MENU or VK_LMENU or VK_RMENU;
    private static bool IsShiftVirtualKey(int vk) => vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT;
    private static bool IsWinVirtualKey(int vk) => vk is VK_LWIN or VK_RWIN;

    private uint BuildHotkeyModifierMask(KeyboardShortcut shortcut)
    {
        uint mask = 0;
        if (shortcut.Alt) mask |= MOD_ALT;
        if (shortcut.Control) mask |= MOD_CONTROL;
        if (shortcut.Shift) mask |= MOD_SHIFT;
        if (shortcut.Win) mask |= MOD_WIN;
        return mask;
    }

    private void UnregisterHotKeyForName(string name)
    {
        if (_hwndSource == null) return;
        if (_nameToHotkeyId.TryRemove(name, out var id))
        {
            _hotkeyIdToName.TryRemove(id, out _);
            UnregisterHotKey(_hwndSource.Handle, id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            LoggingService.Info($"KeyboardShortcutService: WM_HOTKEY received, id={id}");

            if (_hotkeyIdToName.TryGetValue(id, out var name) && _shortcuts.TryGetValue(name, out var shortcut))
            {
                if (!_activeShortcuts.Contains(name))
                {
                    _activeShortcuts.Add(name);
                    LoggingService.Info($"KeyboardShortcutService: WM_HOTKEY triggered '{name}' ({shortcut})");
                    RaiseShortcutEvent(ShortcutPressed, name, shortcut);
                }
                handled = true;
            }
            else
            {
                LoggingService.Warn($"KeyboardShortcutService: WM_HOTKEY id={id} not found in registered hotkeys");
            }
        }
        return IntPtr.Zero;
    }

    private void RaiseShortcutEvent(EventHandler<ShortcutEventArgs>? evt, string name, KeyboardShortcut shortcut)
    {
        void Invoke() => evt?.Invoke(this, new ShortcutEventArgs(name, shortcut));
        if (WpfApplication.Current?.Dispatcher != null)
        {
            WpfApplication.Current.Dispatcher.BeginInvoke(Invoke);
        }
        else
        {
            Invoke();
        }
    }
}
