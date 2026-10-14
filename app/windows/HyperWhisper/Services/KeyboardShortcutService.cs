using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Platform;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services;

/// <summary>
/// Registers and tracks global keyboard shortcuts (named).
/// - RegisterHotKey is used for non-modifier combos so the system delivers WM_HOTKEY.
/// - A low-level hook tracks modifier-only shortcuts and key-up events for push-to-talk.
///   A modifier-only shortcut triggers on release, and only when no other key joined it
///   (see <see cref="ModifierChordGate"/>, issue #1497).
/// </summary>
public sealed class KeyboardShortcutService : IDisposable, PlatformContracts.IGlobalShortcutService
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // The virtual-key constants live in ModifierChordGate with the modifier tests,
    // and the WH_KEYBOARD_LL plumbing in LowLevelKeyboardHook.

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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

    private event EventHandler<PlatformContracts.ShortcutTriggeredEventArgs>? PlatformShortcutPressed;
    private event EventHandler<PlatformContracts.ShortcutTriggeredEventArgs>? PlatformShortcutReleased;

    event EventHandler<PlatformContracts.ShortcutTriggeredEventArgs>?
        PlatformContracts.IGlobalShortcutService.ShortcutPressed
    {
        add => PlatformShortcutPressed += value;
        remove => PlatformShortcutPressed -= value;
    }

    event EventHandler<PlatformContracts.ShortcutTriggeredEventArgs>?
        PlatformContracts.IGlobalShortcutService.ShortcutReleased
    {
        add => PlatformShortcutReleased += value;
        remove => PlatformShortcutReleased -= value;
    }

    private readonly Dictionary<string, KeyboardShortcut> _shortcuts = new();
    private readonly HashSet<string> _activeShortcuts = new();

    /// <summary>
    /// One gate per registered modifier-only shortcut, keyed by name. Such a
    /// chord triggers on release, and only when no other key joined it (#1497).
    /// </summary>
    private readonly Dictionary<string, ModifierChordGate> _chordGates = new();

    /// <summary>
    /// Keys whose key-down this hook swallowed (the Win key of a matched Ctrl+Win)
    /// and that have not come up yet. Windows never saw them go down, so
    /// GetAsyncKeyState reads them as up; DropKeysNoLongerDown must not take
    /// that for a lost key-up.
    /// </summary>
    private readonly HashSet<int> _suppressedKeyDowns = new();

    private readonly LowLevelKeyboardHook _hook;

    private HwndSource? _hwndSource;
    private readonly ConcurrentDictionary<int, string> _hotkeyIdToName = new();
    private readonly ConcurrentDictionary<string, int> _nameToHotkeyId = new();
    private int _hotkeyIdCounter = 1;
    private bool _disposed;

    private readonly ConcurrentDictionary<string, Result> _lastRegistrationResults = new();

    /// <summary>
    /// The instance the running app registers its global shortcuts through.
    ///
    /// There is no DI container here and exactly one of these is ever constructed
    /// (MainViewModel's), but a Win32 hotkey is per-thread: registering the same
    /// combination a second time from a second instance fails with 1409 against
    /// ourselves. So anything that wants to know whether the user's shortcut is
    /// live has to ask THIS object rather than make its own attempt.
    /// </summary>
    public static KeyboardShortcutService? Current { get; private set; }

    public KeyboardShortcutService()
    {
        _hook = new LowLevelKeyboardHook(nameof(KeyboardShortcutService), OnHookKey);
        Current = this;
    }

    /// <summary>
    /// The outcome of the most recent <see cref="RegisterShortcut"/> attempt for
    /// <paramref name="name"/>, or null when this shortcut has never been
    /// registered through this service (no window yet, or the app has not reached
    /// RegisterShortcutsFromSettings). Null is "not known", NOT "failed".
    ///
    /// Read-only by design: the app already re-registers everything whenever
    /// settings change, so a caller that wants a fresh answer only has to read
    /// again. Re-registering from a second call site would unregister the live
    /// hotkey first and could leave the user with none.
    /// </summary>
    public Result? GetLastRegistrationResult(string name)
        => _lastRegistrationResults.TryGetValue(name, out var result) ? result : null;

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

    PlatformContracts.PlatformResult PlatformContracts.IGlobalShortcutService.Start()
    {
        if (_disposed)
        {
            return PlatformContracts.PlatformResult.Failure(
                "shortcut.disposed",
                "The Windows shortcut service has been disposed.");
        }

        AttachWindowIfNeeded();
        _hook.Ensure();
        return _hook.IsInstalled
            ? PlatformContracts.PlatformResult.Success()
            : PlatformContracts.PlatformResult.Failure(
                "shortcut.hook_unavailable",
                "The Windows low-level keyboard hook could not be installed.");
    }

    IReadOnlyDictionary<string, PlatformContracts.PlatformResult>
        PlatformContracts.IGlobalShortcutService.RegisterShortcuts(
            IReadOnlyCollection<PlatformContracts.NamedShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        if (_disposed)
        {
            return shortcuts.ToDictionary(
                item => item.Name,
                _ => PlatformContracts.PlatformResult.Failure(
                    "shortcut.disposed",
                    "The Windows shortcut service has been disposed."));
        }

        var windowsShortcuts = new Dictionary<string, KeyboardShortcut>();
        var platformResults = new Dictionary<string, PlatformContracts.PlatformResult>();
        foreach (var item in shortcuts)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                throw new ArgumentException(
                    "Shortcut names cannot be empty.",
                    nameof(shortcuts));
            }

            if (windowsShortcuts.ContainsKey(item.Name) || platformResults.ContainsKey(item.Name))
            {
                throw new ArgumentException(
                    $"Shortcut name '{item.Name}' is duplicated.",
                    nameof(shortcuts));
            }

            var mapped = WindowsShortcutMapper.FromPlatform(item.Shortcut);
            if (mapped.IsFailure)
            {
                platformResults[item.Name] = PlatformContracts.PlatformResult.Failure(
                    mapped.Error!.Code,
                    mapped.Error.Message);
                continue;
            }

            windowsShortcuts[item.Name] = mapped.Value!;
        }

        foreach (var (name, result) in RegisterShortcuts(windowsShortcuts))
        {
            platformResults[name] = result.IsSuccess
                ? PlatformContracts.PlatformResult.Success()
                : PlatformContracts.PlatformResult.Failure(
                    "shortcut.registration_failed",
                    result.Error ?? "Windows shortcut registration failed.");
        }

        return platformResults;
    }

    public Dictionary<string, Result> RegisterShortcuts(Dictionary<string, KeyboardShortcut> shortcuts)
    {
        AttachWindowIfNeeded();
        _hook.Ensure();

        var results = new Dictionary<string, Result>();

        // Unregister removed shortcuts
        var removed = _shortcuts.Keys.Except(shortcuts.Keys).ToList();
        foreach (var name in removed)
        {
            _shortcuts.Remove(name);
            _chordGates.Remove(name);
            _lastRegistrationResults.TryRemove(name, out _);
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
        var result = RegisterShortcutCore(name, shortcut);
        _lastRegistrationResults[name] = result;
        return result;
    }

    private Result RegisterShortcutCore(string name, KeyboardShortcut shortcut)
    {
        _activeShortcuts.Remove(name);
        _chordGates.Remove(name);
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
            if (shortcut.IsModifierOnly)
            {
                _chordGates[name] = new ModifierChordGate(shortcut);
            }
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

        // RegisterHotKey is EXCLUSIVE per chord, machine-wide. In a second
        // instance every call fails with Win32 1409 ("hot key already
        // registered") against the first instance's own registration, and the
        // shell renders that as a conflict banner for a conflict with itself.
        // A secondary instance records the shortcut and registers nothing.
        if (!SingleInstanceGuard.OwnsGlobalInput)
        {
            _shortcuts[name] = shortcut;
            LoggingService.Info(
                $"KeyboardShortcutService: '{name}' = {shortcut} recorded but NOT registered - secondary instance");
            return Result.Success();
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
        _chordGates.Clear();
        _activeShortcuts.Clear();
        _hook.ClearPressedKeys();
        _suppressedKeyDowns.Clear();
        _lastRegistrationResults.Clear();
    }

    /// <summary>
    /// Clears tracked key state to prevent stale keys from causing false shortcut matches.
    /// Call after transcription completes or recording ends to resynchronize state.
    /// </summary>
    public void ResetKeyboardState()
    {
        _hook.ClearPressedKeys();
        _suppressedKeyDowns.Clear();
        _activeShortcuts.Clear();
        foreach (var gate in _chordGates.Values) gate.Reset();
    }

    /// <summary>
    /// True while the modifier-only shortcut <paramref name="name"/> is held down
    /// clean (armed) and has not triggered yet. It triggers on release (#1497), so
    /// a caller that must hold off while the user is pressing it (push-to-talk,
    /// while the toggle chord is down) asks this instead of waiting for an event.
    /// </summary>
    public bool IsModifierOnlyChordHeld(string name)
        => _chordGates.TryGetValue(name, out var gate) && gate.IsArmed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (ReferenceEquals(Current, this)) Current = null;

        Clear();
        _hook.Dispose();
        if (_hwndSource != null)
        {
            // Do NOT dispose _hwndSource: HwndSource.FromHwnd returns the shared
            // HwndSource owned by WPF for the main window. Disposing it would tear
            // down the window's message pump. Removing our hook is sufficient.
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    /// <summary>
    /// The hook's callback: it has already added a key-down to, or removed a
    /// key-up from, the pressed set. Returns true to swallow the key.
    /// </summary>
    private bool OnHookKey(int vkCode, bool isKeyDown)
    {
        if (isKeyDown)
        {
            DropKeysNoLongerDown(exceptVk: vkCode);
            EvaluateShortcuts(justPressedVk: vkCode);
            if (ShouldSuppressMatchedWinShortcutKeyDown(vkCode))
            {
                _suppressedKeyDowns.Add(vkCode);
                LoggingService.Debug("KeyboardShortcutService: Suppressed matched Win-key shortcut key-down");
                return true;
            }
        }
        else
        {
            _suppressedKeyDowns.Remove(vkCode);
            DropKeysNoLongerDown(exceptVk: -1);
            EvaluateShortcuts(justReleasedVk: vkCode);
        }
        return false;
    }

    /// <summary>
    /// Forgets every tracked key that GetAsyncKeyState says is no longer down: a
    /// key-up the hook never saw, because the secure desktop (Ctrl+Alt+Del, UAC)
    /// or a session switch swallowed it. Without this a stale Alt would let the
    /// next lone Ctrl arm and fire a Ctrl+Alt chord.
    ///
    /// <paramref name="exceptVk"/> is the key this callback is delivering: its
    /// async state is not updated until the hook chain completes, so it cannot be
    /// checked here. Every earlier event has finished the chain, so the others can.
    /// </summary>
    private void DropKeysNoLongerDown(int exceptVk)
    {
        if (_hook.PressedKeys.Count == 0) return;

        foreach (var vk in ModifierChordGate.KeysNoLongerDown(
                     _hook.PressedKeys, exceptVk, _suppressedKeyDowns, IsKeyPhysicallyDown))
        {
            _hook.ForgetKey(vk);
        }
    }

    /// <param name="justPressedVk">
    /// The virtual key code that just triggered this evaluation via key-down hook event,
    /// or -1 if triggered by key-up or other context. GetAsyncKeyState is unreliable for
    /// the triggering key inside WH_KEYBOARD_LL callbacks (state updates after the hook
    /// chain completes), so we skip physical validation for this specific key.
    /// </param>
    /// <param name="justReleasedVk">
    /// The virtual key code of the key-up that triggered this evaluation, or -1.
    /// Modifier-only shortcuts trigger on that release (see ModifierChordGate).
    /// </param>
    private void EvaluateShortcuts(int justPressedVk = -1, int justReleasedVk = -1)
    {
        foreach (var kvp in _shortcuts.ToList())
        {
            var name = kvp.Key;
            var shortcut = kvp.Value;

            if (_chordGates.TryGetValue(name, out var gate))
            {
                EvaluateModifierOnlyChord(name, shortcut, gate, justPressedVk, justReleasedVk);
                continue;
            }

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
                RaiseShortcutEvent(
                    ShortcutPressed,
                    PlatformShortcutPressed,
                    name,
                    shortcut);
            }
            else if (!shouldBeActive && isActive)
            {
                _activeShortcuts.Remove(name);
                LoggingService.Debug($"KeyboardShortcutService: Shortcut released '{name}'");
                RaiseShortcutEvent(
                    ShortcutReleased,
                    PlatformShortcutReleased,
                    name,
                    shortcut);
            }
        }
    }

    /// <summary>
    /// A modifier-only chord (Ctrl+Alt) is the prefix of every ordinary shortcut
    /// that starts with it (Ctrl+Alt+Left), so it cannot trigger on press: it
    /// triggers on the release that breaks it, when no other key joined it
    /// (#1497). Pressed and Released are raised back to back there, in that
    /// order, so a handler that pairs them (MainViewModel._toggleShortcutHeld)
    /// still sees a balanced pair.
    /// </summary>
    private void EvaluateModifierOnlyChord(
        string name,
        KeyboardShortcut shortcut,
        ModifierChordGate gate,
        int justPressedVk,
        int justReleasedVk)
    {
        var before = gate.State;

        if (justPressedVk >= 0)
        {
            gate.KeyDown(_hook.PressedKeys);
            if (gate.State != before)
            {
                LoggingService.Debug(
                    $"KeyboardShortcutService: modifier-only '{name}' ({shortcut}) {before} -> {gate.State} on key-down vk=0x{justPressedVk:X2}");
            }
            return;
        }

        if (justReleasedVk < 0) return;

        bool fire = gate.KeyUp(_hook.PressedKeys, justReleasedVk);
        if (!fire)
        {
            if (gate.State != before)
            {
                LoggingService.Debug(
                    $"KeyboardShortcutService: modifier-only '{name}' ({shortcut}) {before} -> {gate.State} on release, not triggered");
            }
            return;
        }

        LoggingService.Info($"KeyboardShortcutService: SHORTCUT PRESSED '{name}' ({shortcut}) on release");
        RaiseShortcutEvent(
            ShortcutPressed,
            PlatformShortcutPressed,
            name,
            shortcut);
        RaiseShortcutEvent(
            ShortcutReleased,
            PlatformShortcutReleased,
            name,
            shortcut);
    }

    // The modifier tests, AltGr handling included, live in ModifierChordGate so
    // the modifier-only chord decision and these matchers can never disagree.
    private static bool IsAnyCtrlDown(IReadOnlySet<int> pressedKeys) => ModifierChordGate.IsAnyCtrlDown(pressedKeys);
    private static bool IsAnyAltDown(IReadOnlySet<int> pressedKeys) => ModifierChordGate.IsAnyAltDown(pressedKeys);
    private static bool IsAnyShiftDown(IReadOnlySet<int> pressedKeys) => ModifierChordGate.IsAnyShiftDown(pressedKeys);
    private static bool IsAnyWinDown(IReadOnlySet<int> pressedKeys) => ModifierChordGate.IsAnyWinDown(pressedKeys);

    /// <summary>
    /// Returns true if the given key is physically held down according to GetAsyncKeyState.
    /// </summary>
    private static bool IsKeyPhysicallyDown(int vk) => LowLevelKeyboardHook.IsKeyPhysicallyDown(vk);

    private bool IsShortcutPressed(KeyboardShortcut shortcut, int justPressedVk = -1)
    {
        if (shortcut.IsEmpty) return false;
        if (shortcut.Control && !IsAnyCtrlDown(_hook.PressedKeys)) return false;
        if (shortcut.Alt && !IsAnyAltDown(_hook.PressedKeys)) return false;
        if (shortcut.Shift && !IsAnyShiftDown(_hook.PressedKeys)) return false;
        if (shortcut.Win && !IsAnyWinDown(_hook.PressedKeys)) return false;

        if (shortcut.Key.HasValue)
        {
            int vk = KeyInterop.VirtualKeyFromKey(shortcut.Key.Value);
            if (!_hook.PressedKeys.Contains(vk)) return false;

            // Validate against actual keyboard state to prevent stale keys from
            // causing false positives (e.g., key-up missed due to focus loss).
            // IMPORTANT: Skip this check for the key that just triggered the hook callback —
            // GetAsyncKeyState is unreliable inside WH_KEYBOARD_LL because the async key state
            // is not updated until after the hook chain completes (see PushToTalkMonitor docs).
            // Without this skip, bare-key shortcuts like Esc fail because GetAsyncKeyState
            // returns "not pressed" for the key that is actively being pressed.
            if (vk != justPressedVk && !IsKeyPhysicallyDown(vk))
            {
                _hook.ForgetKey(vk);
                return false;
            }
        }

        return true;
    }

    private bool IsShortcutPressedExact(KeyboardShortcut shortcut, int justPressedVk = -1)
    {
        if (!IsShortcutPressed(shortcut, justPressedVk)) return false;

        if (!shortcut.Control && IsAnyCtrlDown(_hook.PressedKeys)) return false;
        if (!shortcut.Alt && IsAnyAltDown(_hook.PressedKeys)) return false;
        if (!shortcut.Shift && IsAnyShiftDown(_hook.PressedKeys)) return false;
        if (!shortcut.Win && IsAnyWinDown(_hook.PressedKeys)) return false;

        var shortcutKeyVk = shortcut.Key.HasValue
            ? KeyInterop.VirtualKeyFromKey(shortcut.Key.Value)
            : (int?)null;

        foreach (var pressedVk in _hook.PressedKeys)
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

    private static bool IsModifierVirtualKey(int vk) => ModifierChordGate.IsModifierVirtualKey(vk);
    private static bool IsCtrlVirtualKey(int vk) => ModifierChordGate.IsCtrlVirtualKey(vk);
    private static bool IsAltVirtualKey(int vk) => ModifierChordGate.IsAltVirtualKey(vk);
    private static bool IsShiftVirtualKey(int vk) => ModifierChordGate.IsShiftVirtualKey(vk);
    private static bool IsWinVirtualKey(int vk) => ModifierChordGate.IsWinVirtualKey(vk);

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
                    RaiseShortcutEvent(
                        ShortcutPressed,
                        PlatformShortcutPressed,
                        name,
                        shortcut);
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

    private void RaiseShortcutEvent(
        EventHandler<ShortcutEventArgs>? windowsEvent,
        EventHandler<PlatformContracts.ShortcutTriggeredEventArgs>? platformEvent,
        string name,
        KeyboardShortcut shortcut)
    {
        void Invoke()
        {
            RaiseEach(windowsEvent, new ShortcutEventArgs(name, shortcut));
            RaiseEach(
                platformEvent,
                new PlatformContracts.ShortcutTriggeredEventArgs(
                    name,
                    WindowsShortcutMapper.ToPlatform(shortcut)));
        }

        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(Invoke);
        }
        else
        {
            Invoke();
        }
    }

    private void RaiseEach<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args)
    {
        if (handlers == null) return;

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                LoggingService.Error(
                    $"KeyboardShortcutService: {typeof(TEventArgs).Name} handler failed",
                    ex);
            }
        }
    }
}
