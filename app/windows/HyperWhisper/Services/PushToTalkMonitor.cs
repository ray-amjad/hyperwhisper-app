using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Watches keyboard state for push-to-talk behaviors (modifier or custom shortcut).
/// Mirrors macOS BareModifierKeyMonitor's 5-state machine:
///
/// - 250ms activation delay filters keyboard shortcuts (Ctrl+C, Alt+Tab, etc.)
/// - Quick taps (release before timer) enter double-tap lock sequence, not interference
/// - Double-tap detection on keyUp for symmetric lock/unlock behavior
/// - 1500ms window for double-tap (comfortable pace)
/// - 2000ms bounce protection after locking to prevent accidental immediate unlock (wireless RF glitches)
///
/// Emits Pressed (start recording), Released (stop recording), and Interfered (cancel) events.
/// </summary>
public sealed class PushToTalkMonitor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;   // Alt
    private const int VK_RMENU = 0xA5;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    // Matches macOS BareModifierKeyMonitor timing:
    // - 250ms activation delay filters out keyboard shortcuts (e.g. Ctrl+C)
    // - 1500ms double-press window allows comfortable double-tap lock/unlock
    private const int ActivationDelayMs = 250;
    private const int DoublePressIntervalMs = 1500;

    private readonly PushToTalkSettings _settings = new();
    private readonly HashSet<int> _pressedKeys = new();

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

    // Returns the high-order bit set if the key is physically down at the time of the call.
    // Reliable when called from the UI thread AFTER the hook callback has returned
    // (the async state is updated after the hook chain completes, not inside it).
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

    private readonly LowLevelKeyboardProc _hookCallback;
    private IntPtr _hookId = IntPtr.Zero;

    // =========================================================================
    // STATE MACHINE
    // =========================================================================
    // Mirrors macOS BareModifierKeyMonitor's 5-state machine:
    //
    //   Idle → (key down) → WaitingForActivation → (250ms timer) → PttActive → (key up) → Idle + stop
    //                                             → (key up, quick tap) → PttActive (first tap of double-tap)
    //                                             → (interference) → Idle
    //   PttActive → (key up within interval) → LatchActive (double-tap lock)
    //   LatchActive → (key down) → UnlatchPending
    //   UnlatchPending → (first key up) → set firstTapTime, wait → (second key up within interval) → Idle + stop
    //                                                             → (timeout) → LatchActive
    //
    // Key design decisions matching macOS:
    // - Double-tap detection is on keyUp (not keyDown) for symmetric lock/unlock
    // - Quick taps (release before 250ms timer) enter double-tap sequence, NOT interference
    // - No minimum press duration — the 250ms activation delay handles filtering

    private enum MonitorState
    {
        Idle,
        WaitingForActivation,
        PttActive,
        LatchActive,
        UnlatchPending
    }

    private MonitorState _state = MonitorState.Idle;
    private System.Windows.Threading.DispatcherTimer? _activationTimer;
    private System.Windows.Threading.DispatcherTimer? _latchTimer;
    private System.Windows.Threading.DispatcherTimer? _keyUpDebounceTimer;
    private DateTime _firstTapTimeUtc;
    private DateTime _lastLatchActiveTimeUtc;
    private bool _enteredViaHold; // true = activated via hold (250ms timer), false = quick-tap

    // Minimum time to stay locked before allowing unlock.
    // 2000ms prevents spurious keyDown+keyUp pairs from wireless keyboards (RF glitches)
    // from accidentally triggering the unlock sequence right after locking.
    private const int MinimumLockDurationMs = 2000;

    // Debounce window for spurious WM_KEYUP events from wireless keyboards.
    //
    // Logitech Unifying/Bolt receivers synthesize a WM_KEYUP when the 2.4 GHz RF link
    // briefly drops a packet mid-hold. The receiver's retransmission cycle can take up
    // to ~80ms, so 30ms was insufficient. 100ms covers the Logitech HID++ retransmit
    // window while remaining imperceptible as deliberate-release latency to the user.
    //
    // Additionally, GetKeyboardState is one event behind inside the hook callback, so
    // we cannot verify the true physical state there. Instead we call GetAsyncKeyState
    // from the timer callback (UI thread, after the hook has returned) to confirm whether
    // the key is genuinely up before committing the release.
    private const int KeyUpDebounceMs = 100;

    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? Interfered;

    public PushToTalkMonitor()
    {
        _hookCallback = HookCallback;
    }

    public void Configure(PushToTalkSettings settings)
    {
        _settings.Mode = settings.Mode;
        _settings.Modifier = settings.Modifier;
        _settings.DoublePressLock = settings.DoublePressLock;
        _settings.CustomShortcut = settings.CustomShortcut?.Clone();
    }

    public void Start()
    {
        EnsureHook();
    }

    public void Reset()
    {
        _state = MonitorState.Idle;
        CancelActivationTimer();
        CancelLatchTimer();
        CancelKeyUpDebounce();
        _pressedKeys.Clear();
    }

    /// <summary>
    /// Reset state to idle when recording is stopped externally (cancel, error, etc.).
    /// Matches macOS BareModifierKeyMonitor.resetToIdle() — prevents stale state
    /// from misinterpreting the next key press as part of an unlock sequence.
    /// </summary>
    public void ResetToIdle()
    {
        if (_state == MonitorState.PttActive || _state == MonitorState.LatchActive || _state == MonitorState.UnlatchPending)
        {
            LoggingService.Debug($"PushToTalkMonitor: ResetToIdle from {_state}");
            _state = MonitorState.Idle;
            CancelActivationTimer();
            CancelLatchTimer();
            CancelKeyUpDebounce();
        }
    }

    public void Dispose()
    {
        Reset(); // also calls CancelKeyUpDebounce
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private void EnsureHook()
    {
        if (_hookId != IntPtr.Zero) return;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _hookCallback,
            GetModuleHandle(curModule?.ModuleName),
            0);
        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            LoggingService.Error($"PushToTalkMonitor: Failed to install hook (error {error})");
        }
        else
        {
            LoggingService.Debug("PushToTalkMonitor: Low-level keyboard hook installed");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_settings.Mode == PushToTalkMode.Disabled) return CallNextHookEx(_hookId, nCode, wParam, lParam);

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
                HandleKeyDown(vkCode);
            }
            else if (isKeyUp)
            {
                _pressedKeys.Remove(vkCode);
                HandleKeyUp(vkCode);
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void HandleKeyDown(int vkCode)
    {
        TracePttEvent("keyDown", vkCode);

        // Non-shortcut keys pressed during activation or active PTT = interference
        if ((_state == MonitorState.WaitingForActivation || _state == MonitorState.PttActive)
            && !IsKeyPartOfShortcut(vkCode))
        {
            HandleInterference();
            return;
        }

        if (!IsPrimaryKey(vkCode)) return;
        if (!IsShortcutSatisfied()) return;

        switch (_state)
        {
            case MonitorState.Idle:
                // Start 250ms activation delay to filter out keyboard shortcuts
                _state = MonitorState.WaitingForActivation;
                StartActivationTimer();
                break;

            case MonitorState.WaitingForActivation:
                // Already waiting, ignore duplicate down events
                break;

            case MonitorState.PttActive:
                // Key came back during debounce window (wireless keyboard bounce) or
                // user is actively re-pressing — cancel both timers, stay recording.
                CancelKeyUpDebounce();
                CancelLatchTimer();
                break;

            case MonitorState.LatchActive:
                // Bounce protection: ignore key-down too soon after locking
                var timeSinceLock = (DateTime.UtcNow - _lastLatchActiveTimeUtc).TotalMilliseconds;
                if (timeSinceLock < MinimumLockDurationMs)
                {
                    LoggingService.Debug($"PushToTalkMonitor: Ignoring keyDown - too soon after lock ({timeSinceLock:F0}ms < {MinimumLockDurationMs}ms)");
                    return;
                }

                // First tap of unlock sequence
                _state = MonitorState.UnlatchPending;
                _firstTapTimeUtc = DateTime.MinValue; // sentinel — actual time set on first keyUp
                break;

            case MonitorState.UnlatchPending:
                // Subsequent keyDown during unlock sequence — stay in state
                break;
        }
    }

    private void HandleKeyUp(int vkCode)
    {
        TracePttEvent("keyUp", vkCode);

        if (!IsPrimaryKey(vkCode)) return;

        // For logical modifiers backed by multiple physical keys (e.g. "ctrl" = LCtrl + RCtrl),
        // only transition when the shortcut is no longer satisfied (both keys released).
        // This matches macOS which checks the logical modifier flag, not individual key-ups.
        if (IsShortcutSatisfied() && _state != MonitorState.UnlatchPending) return;

        switch (_state)
        {
            case MonitorState.Idle:
                break;

            case MonitorState.WaitingForActivation:
                // Released before 250ms timer fired.
                // Wireless keyboards can fire a spurious WM_KEYUP mid-hold that would
                // reset the activation timer. Debounce here too: schedule a check and
                // only exit WaitingForActivation if the key is confirmed physically up.
                CancelActivationTimer();
                StartKeyUpDebounce();
                break;

            case MonitorState.PttActive:
                if (_enteredViaHold)
                {
                    // Normal hold-to-record: debounce the release to guard against wireless
                    // keyboard RF bounce firing a spurious WM_KEYUP mid-hold.
                    // If no WM_KEYDOWN arrives within KeyUpDebounceMs, treat as real release.
                    StartKeyUpDebounce();
                }
                else
                {
                    // Quick-tap path: check if this is a double-tap (second tap within interval)
                    var timeSinceFirstTap = (DateTime.UtcNow - _firstTapTimeUtc).TotalMilliseconds;
                    if (_settings.DoublePressLock && timeSinceFirstTap <= DoublePressIntervalMs)
                    {
                        // Second tap → lock recording (hands-free mode)
                        CancelLatchTimer();
                        _state = MonitorState.LatchActive;
                        _lastLatchActiveTimeUtc = DateTime.UtcNow;
                        LoggingService.Debug("PushToTalkMonitor: Double-tap lock confirmed");
                    }
                    else
                    {
                        // Single tap timeout or too slow → stop recording
                        CancelLatchTimer();
                        _state = MonitorState.Idle;
                        RaiseSafe(Released);
                    }
                }
                break;

            case MonitorState.LatchActive:
                // In locked mode, release doesn't stop — user must double-tap to unlock
                break;

            case MonitorState.UnlatchPending:
                if (_firstTapTimeUtc == DateTime.MinValue)
                {
                    // First keyUp of unlock sequence — record time and wait for second tap
                    _firstTapTimeUtc = DateTime.UtcNow;
                    StartLatchTimer();
                    LoggingService.Debug("PushToTalkMonitor: Unlock first tap, waiting for second");
                }
                else
                {
                    // Second keyUp — check timing
                    var elapsed = (DateTime.UtcNow - _firstTapTimeUtc).TotalMilliseconds;
                    CancelLatchTimer();

                    if (elapsed <= DoublePressIntervalMs)
                    {
                        // Unlock confirmed → stop recording
                        _state = MonitorState.Idle;
                        RaiseSafe(Released);
                        LoggingService.Debug("PushToTalkMonitor: Double-tap unlock confirmed");
                    }
                    else
                    {
                        // Too slow — go back to locked
                        _state = MonitorState.LatchActive;
                        LoggingService.Debug($"PushToTalkMonitor: Unlock too slow ({elapsed:F0}ms), back to locked");
                    }
                }
                break;
        }
    }

    private bool IsShortcutSatisfied()
    {
        return _settings.Mode switch
        {
            PushToTalkMode.Modifier => IsModifierPressed(_settings.Modifier),
            PushToTalkMode.Custom => IsCustomShortcutPressed(),
            _ => false
        };
    }

    private bool IsCustomShortcutPressed()
    {
        if (_settings.CustomShortcut == null || _settings.CustomShortcut.IsEmpty) return false;

        if (_settings.CustomShortcut.Control && !IsAnyCtrlDown()) return false;
        if (_settings.CustomShortcut.Alt && !IsAnyAltDown()) return false;
        if (_settings.CustomShortcut.Shift && !IsAnyShiftDown()) return false;
        if (_settings.CustomShortcut.Win && !IsAnyWinDown()) return false;

        if (_settings.CustomShortcut.Key.HasValue)
        {
            int vk = KeyInterop.VirtualKeyFromKey(_settings.CustomShortcut.Key.Value);
            if (!_pressedKeys.Contains(vk)) return false;
        }

        return true;
    }

    private bool IsPrimaryKey(int vkCode)
    {
        foreach (var vk in GetPrimaryKeyCodes())
        {
            if (vk == vkCode) return true;
        }
        return false;
    }

    private IEnumerable<int> GetPrimaryKeyCodes()
    {
        if (_settings.Mode == PushToTalkMode.Modifier)
        {
            return _settings.Modifier.ToLowerInvariant() switch
            {
                "leftalt" => new[] { VK_LMENU },
                "rightalt" => new[] { VK_RMENU },
                "alt" => new[] { VK_LMENU, VK_RMENU },
                "shift" => new[] { VK_LSHIFT, VK_RSHIFT },
                "win" => new[] { VK_LWIN, VK_RWIN },
                _ => new[] { VK_LCONTROL, VK_RCONTROL }
            };
        }

        if (_settings.Mode == PushToTalkMode.Custom && _settings.CustomShortcut != null)
        {
            if (_settings.CustomShortcut.Key.HasValue)
            {
                return new[] { KeyInterop.VirtualKeyFromKey(_settings.CustomShortcut.Key.Value) };
            }

            if (_settings.CustomShortcut.Control) return new[] { VK_LCONTROL, VK_RCONTROL };
            if (_settings.CustomShortcut.Alt) return new[] { VK_LMENU, VK_RMENU };
            if (_settings.CustomShortcut.Shift) return new[] { VK_LSHIFT, VK_RSHIFT };
            if (_settings.CustomShortcut.Win) return new[] { VK_LWIN, VK_RWIN };
        }

        return Array.Empty<int>();
    }

    private bool IsKeyPartOfShortcut(int vkCode)
    {
        if (_settings.Mode == PushToTalkMode.Modifier)
        {
            // For side-specific modifier modes (e.g. "leftalt"), the opposite-side key
            // (right alt) should NOT be treated as interference — matches macOS behavior
            // where .maskAlternate is removed from interference checks for both Option modes.
            if (GetPrimaryKeyCodes().Contains(vkCode)) return true;

            // Check if the key is the opposite side of the same modifier family
            return _settings.Modifier.ToLowerInvariant() switch
            {
                "leftalt" => vkCode == VK_RMENU,
                "rightalt" => vkCode == VK_LMENU,
                _ => false
            };
        }

        if (_settings.Mode == PushToTalkMode.Custom && _settings.CustomShortcut != null)
        {
            if (_settings.CustomShortcut.Key.HasValue &&
                vkCode == KeyInterop.VirtualKeyFromKey(_settings.CustomShortcut.Key.Value))
            {
                return true;
            }

            return vkCode switch
            {
                VK_LCONTROL or VK_RCONTROL => _settings.CustomShortcut.Control,
                VK_LMENU or VK_RMENU => _settings.CustomShortcut.Alt,
                VK_LSHIFT or VK_RSHIFT => _settings.CustomShortcut.Shift,
                VK_LWIN or VK_RWIN => _settings.CustomShortcut.Win,
                _ => false
            };
        }

        return false;
    }

    private bool IsModifierPressed(string modifier) => modifier.ToLowerInvariant() switch
    {
        "leftalt" => _pressedKeys.Contains(VK_LMENU),
        "rightalt" => _pressedKeys.Contains(VK_RMENU),
        "alt" => IsAnyAltDown(),
        "shift" => IsAnyShiftDown(),
        "win" => IsAnyWinDown(),
        _ => IsAnyCtrlDown()
    };

    /// <summary>
    /// Returns true if AltGr is currently active (VK_RMENU is pressed).
    /// When AltGr is active, VK_LCONTROL is a synthetic press injected by Windows.
    /// </summary>
    private bool IsAltGrActive() => _pressedKeys.Contains(VK_RMENU);

    private bool IsAnyCtrlDown()
    {
        if (IsAltGrActive())
        {
            // AltGr sends synthetic VK_LCONTROL — only count RCtrl as real
            return _pressedKeys.Contains(VK_RCONTROL);
        }
        return _pressedKeys.Contains(VK_LCONTROL) || _pressedKeys.Contains(VK_RCONTROL);
    }

    private bool IsAnyAltDown()
    {
        if (IsAltGrActive())
        {
            // AltGr is not a real Alt press — only count LAlt
            return _pressedKeys.Contains(VK_LMENU);
        }
        return _pressedKeys.Contains(VK_LMENU) || _pressedKeys.Contains(VK_RMENU);
    }
    private bool IsAnyShiftDown() => _pressedKeys.Contains(VK_LSHIFT) || _pressedKeys.Contains(VK_RSHIFT);
    private bool IsAnyWinDown() => _pressedKeys.Contains(VK_LWIN) || _pressedKeys.Contains(VK_RWIN);

    // =========================================================================
    // INTERFERENCE & TIMERS
    // =========================================================================

    private void HandleInterference()
    {
        TracePttEvent("interference", null);
        CancelActivationTimer();
        CancelLatchTimer();
        CancelKeyUpDebounce();
        _state = MonitorState.Idle;
        LoggingService.Debug("PushToTalkMonitor: Interference detected, cancelling PTT");
        RaiseSafe(Interfered);
    }

    private void StartActivationTimer()
    {
        CancelActivationTimer();
        _activationTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ActivationDelayMs)
        };
        _activationTimer.Tick += (s, args) =>
        {
            TracePttEvent("activationTimer", null);
            CancelActivationTimer();
            if (_state == MonitorState.WaitingForActivation && IsShortcutSatisfied())
            {
                // User held the key past 250ms → start PTT recording.
                // Mark as hold-activated so key-up stops recording instead of entering latch.
                _state = MonitorState.PttActive;
                _enteredViaHold = true;
                _firstTapTimeUtc = DateTime.UtcNow;
                RaiseSafe(Pressed);
            }
        };
        _activationTimer.Start();
    }

    private void CancelActivationTimer()
    {
        _activationTimer?.Stop();
        _activationTimer = null;
    }

    private void StartLatchTimer()
    {
        CancelLatchTimer();
        _latchTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DoublePressIntervalMs)
        };
        _latchTimer.Tick += (s, args) =>
        {
            TracePttEvent("latchTimer", null);
            CancelLatchTimer();
            HandleLatchTimeout();
        };
        _latchTimer.Start();
    }

    private void CancelLatchTimer()
    {
        _latchTimer?.Stop();
        _latchTimer = null;
    }

    private void StartKeyUpDebounce()
    {
        CancelKeyUpDebounce();
        _keyUpDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(KeyUpDebounceMs)
        };
        _keyUpDebounceTimer.Tick += (s, e) =>
        {
            TracePttEvent("keyUpDebounceTimer", null);
            CancelKeyUpDebounce();

            // Cross-check actual physical key state now that the hook has returned
            // and GetAsyncKeyState reflects real hardware state.
            // If the key is still physically held, the WM_KEYUP was spurious (RF bounce).
            if (IsPhysicallyHeld())
            {
                LoggingService.Debug("PushToTalkMonitor: Spurious key-up confirmed via GetAsyncKeyState — resuming hold");
                if (_state == MonitorState.WaitingForActivation)
                {
                    // Resume the activation timer from the start
                    StartActivationTimer();
                }
                // PttActive: state and recording are unchanged — just stay in PttActive
                return;
            }

            // Key is genuinely up — commit the release
            if (_state == MonitorState.PttActive && _enteredViaHold)
            {
                _state = MonitorState.Idle;
                RaiseSafe(Released);
                LoggingService.Debug("PushToTalkMonitor: Key-up confirmed (hold release), stopping recording");
            }
            else if (_state == MonitorState.WaitingForActivation)
            {
                // Real quick tap while waiting — apply normal quick-tap logic
                if (_settings.DoublePressLock)
                {
                    _state = MonitorState.PttActive;
                    _enteredViaHold = false;
                    _firstTapTimeUtc = DateTime.UtcNow;
                    RaiseSafe(Pressed);
                    StartLatchTimer();
                }
                else
                {
                    _state = MonitorState.Idle;
                }
            }
        };
        _keyUpDebounceTimer.Start();
    }

    private void CancelKeyUpDebounce()
    {
        _keyUpDebounceTimer?.Stop();
        _keyUpDebounceTimer = null;
    }

    /// <summary>
    /// Returns true if the PTT key (or any key satisfying the shortcut) is physically held
    /// according to GetAsyncKeyState. Only reliable when called from the UI thread AFTER
    /// the WH_KEYBOARD_LL hook callback has returned (state updates asynchronously).
    /// </summary>
    private bool IsPhysicallyHeld()
    {
        // GetAsyncKeyState high-order bit = key physically down right now
        const short down = unchecked((short)0x8000);

        if (_settings.Mode == PushToTalkMode.Modifier)
        {
            return _settings.Modifier.ToLowerInvariant() switch
            {
                "leftalt"  => (GetAsyncKeyState(VK_LMENU)    & down) != 0,
                "rightalt" => (GetAsyncKeyState(VK_RMENU)     & down) != 0,
                "alt"      => (GetAsyncKeyState(VK_LMENU)     & down) != 0 || (GetAsyncKeyState(VK_RMENU)    & down) != 0,
                "shift"    => (GetAsyncKeyState(VK_LSHIFT)    & down) != 0 || (GetAsyncKeyState(VK_RSHIFT)   & down) != 0,
                "win"      => (GetAsyncKeyState(VK_LWIN)      & down) != 0 || (GetAsyncKeyState(VK_RWIN)     & down) != 0,
                _          => (GetAsyncKeyState(VK_LCONTROL)  & down) != 0 || (GetAsyncKeyState(VK_RCONTROL) & down) != 0,
            };
        }

        if (_settings.Mode == PushToTalkMode.Custom && _settings.CustomShortcut != null)
        {
            var shortcut = _settings.CustomShortcut;
            var hasAnyKey = shortcut.Control || shortcut.Alt || shortcut.Shift || shortcut.Win || shortcut.Key.HasValue;

            if (!hasAnyKey)
                return false;

            if (shortcut.Control && (GetAsyncKeyState(VK_LCONTROL) & down) == 0 && (GetAsyncKeyState(VK_RCONTROL) & down) == 0)
                return false;

            if (shortcut.Alt && (GetAsyncKeyState(VK_LMENU) & down) == 0 && (GetAsyncKeyState(VK_RMENU) & down) == 0)
                return false;

            if (shortcut.Shift && (GetAsyncKeyState(VK_LSHIFT) & down) == 0 && (GetAsyncKeyState(VK_RSHIFT) & down) == 0)
                return false;

            if (shortcut.Win && (GetAsyncKeyState(VK_LWIN) & down) == 0 && (GetAsyncKeyState(VK_RWIN) & down) == 0)
                return false;

            if (shortcut.Key.HasValue)
            {
                int vk = KeyInterop.VirtualKeyFromKey(shortcut.Key.Value);
                return (GetAsyncKeyState(vk) & down) != 0;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Latch timer expired — no second tap detected.
    /// In PttActive: stop recording (user did first tap but not second).
    /// In UnlatchPending: go back to locked (user didn't complete unlock).
    /// </summary>
    private void HandleLatchTimeout()
    {
        TracePttEvent("latchTimeout", null);

        if (_state == MonitorState.PttActive)
        {
            _state = MonitorState.Idle;
            RaiseSafe(Released);
            LoggingService.Debug("PushToTalkMonitor: Latch timeout, stopping recording");
        }
        else if (_state == MonitorState.UnlatchPending)
        {
            _state = MonitorState.LatchActive;
            LoggingService.Debug("PushToTalkMonitor: Unlock timeout, back to locked");
        }
    }

    private void TracePttEvent(string eventName, int? vkCode)
    {
        var isPrimary = vkCode.HasValue && IsPrimaryKey(vkCode.Value);
        if (_state == MonitorState.Idle && !isPrimary && (eventName == "keyDown" || eventName == "keyUp"))
        {
            return;
        }

        string sinceFirstTap = _firstTapTimeUtc == default
            ? "none"
            : $"{(DateTime.UtcNow - _firstTapTimeUtc).TotalMilliseconds:F0}ms";
        string sinceLock = _lastLatchActiveTimeUtc == default
            ? "none"
            : $"{(DateTime.UtcNow - _lastLatchActiveTimeUtc).TotalMilliseconds:F0}ms";
        var keyPart = vkCode.HasValue ? $" vk={vkCode.Value}" : "";

        LoggingService.Debug(
            $"PushToTalkMonitor: trace {eventName}{keyPart} state={_state} enteredViaHold={_enteredViaHold} shortcutSatisfied={IsShortcutSatisfied()} sinceFirstTap={sinceFirstTap} sinceLock={sinceLock}");
    }

    private void RaiseSafe(EventHandler? evt)
    {
        void Invoke() => evt?.Invoke(this, EventArgs.Empty);
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
