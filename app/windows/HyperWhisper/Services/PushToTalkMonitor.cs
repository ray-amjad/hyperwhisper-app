using System.Linq;
using System.Windows;
using System.Windows.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Platform;
using HyperWhisper.SharedCore;
using PlatformContracts = HyperWhisper.Platform.Abstractions;
using static HyperWhisper.Services.ModifierChordGate;
using static HyperWhisper.Services.Platform.LowLevelKeyboardHook;

namespace HyperWhisper.Services;

/// <summary>
/// Watches keyboard state for push-to-talk behaviors (modifier or custom shortcut).
///
/// The 5-state machine itself is NOT here. It lives in the shared Rust core
/// (<see cref="PortablePushToTalkCore"/>, issue #287) and is the same transition
/// table the macOS and Linux heads run. This class owns what is genuinely
/// Windows: the WH_KEYBOARD_LL hook (through <see cref="LowLevelKeyboardHook"/>),
/// virtual-key mapping, AltGr handling, the GetAsyncKeyState cross-check,
/// DispatcherTimer and the clock.
///
/// - 250ms activation delay filters keyboard shortcuts (Ctrl+C, Alt+Tab, etc.)
/// - Quick taps (release before timer) enter double-tap lock sequence, not interference
/// - Double-tap detection on keyUp for symmetric lock/unlock behavior
/// - 1500ms window for double-tap (comfortable pace)
/// - 2000ms bounce protection after locking to prevent accidental immediate unlock (wireless RF glitches)
///
/// Emits Pressed (start recording), Released (stop recording), and Interfered (cancel) events.
/// </summary>
public sealed class PushToTalkMonitor : IDisposable, PlatformContracts.IPushToTalkMonitor
{
    // The virtual-key constants and the modifier tests live in ModifierChordGate,
    // shared with KeyboardShortcutService, so both read the generic VK_* codes too.

    private readonly PushToTalkSettings _settings = new();
    private readonly LowLevelKeyboardHook _hook;

    // =========================================================================
    // STATE MACHINE
    // =========================================================================
    // The transition table lives in the shared Rust core (issue #287) and is the
    // same one macOS and Linux run. What is left here is the Windows half:
    // deciding when a raw WM_KEYDOWN/WM_KEYUP is worth reporting as a
    // push-to-talk event at all, running the timers the core asks for, and
    // answering its key-up debounce with a GetAsyncKeyState reading.
    //
    // Two behaviours changed with the move, both deliberately (see the issue):
    // - ResetToIdle from WaitingForActivation is now a full cancel. It used to
    //   return early and leave the 250ms activation timer armed to start a
    //   recording that had already been cancelled.
    // - macOS adopted this head's enteredViaHold behaviour rather than the other
    //   way round, so nothing changes here for hold-then-release.

    private PortablePttMachineState _machine = PortablePushToTalkCore.InitialState();
    private PortablePttConfig _pttConfig =
        PortablePushToTalkCore.Config(MinimumLockDurationMs, KeyUpDebounceMs, false);

    /// <summary>One slot per <see cref="PortablePttTimer"/>, indexed by the enum.</summary>
    private readonly System.Windows.Threading.DispatcherTimer?[] _timers =
        new System.Windows.Threading.DispatcherTimer?[3];

    // Minimum time to stay locked before allowing unlock.
    // 2000ms prevents spurious keyDown+keyUp pairs from wireless keyboards (RF glitches)
    // from accidentally triggering the unlock sequence right after locking.
    // macOS ships 1000ms; unifying the two is a separate decision.
    private const ulong MinimumLockDurationMs = 2000;

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
    // the key is genuinely up before committing the release. That reading is what the
    // core's KeyUpDebounceTimeout event carries.
    private const ulong KeyUpDebounceMs = 100;
    private bool _disposed;

    /// <summary>
    /// The monotonic reading the core measures every interval from. Never
    /// DateTime: this head used to compare wall-clock timestamps, so an NTP step
    /// during a locked recording could make the time-since-lock interval negative
    /// and defeat bounce protection.
    /// </summary>
    private static ulong NowMs => (ulong)Environment.TickCount64;

    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? Interfered;

    public PushToTalkMonitor()
    {
        // SmartPaste's Ctrl+V uses the generic VK_CONTROL, a primary key here
        // since #694. Skip it, and only it: an AutoHotkey or Stream Deck key is
        // injected too and must still drive push-to-talk (#1851).
        _hook = new LowLevelKeyboardHook(nameof(PushToTalkMonitor), OnHookKey, ignoreOwnInjectedKeys: true);
    }

    /// <summary>The hook, for the smoke suite to feed events into.</summary>
    internal LowLevelKeyboardHook Hook => _hook;

    /// <summary>The shared machine's state, for the smoke suite.</summary>
    internal PortablePttState State => _machine.State;

    public void Configure(PushToTalkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings.Mode = settings.Mode;
        _settings.Modifier = settings.Modifier;
        _settings.DoublePressLock = settings.DoublePressLock;
        _settings.CustomShortcut = settings.CustomShortcut?.Clone();

        _pttConfig = PortablePushToTalkCore.Config(
            MinimumLockDurationMs,
            KeyUpDebounceMs,
            settings.DoublePressLock);
    }

    void PlatformContracts.IPushToTalkMonitor.Configure(PlatformContracts.PushToTalkConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var mapped = WindowsShortcutMapper.FromPlatform(configuration);
        if (mapped.IsFailure)
        {
            throw new ArgumentException(mapped.Error!.Message, nameof(configuration));
        }

        Configure(mapped.Value!);
    }

    public void Start()
    {
        _hook.Ensure();
    }

    PlatformContracts.PlatformResult PlatformContracts.IPushToTalkMonitor.Start()
    {
        if (_disposed)
        {
            return PlatformContracts.PlatformResult.Failure(
                "push_to_talk.disposed",
                "The Windows push-to-talk monitor has been disposed.");
        }

        _hook.Ensure();
        return _hook.IsInstalled
            ? PlatformContracts.PlatformResult.Success()
            : PlatformContracts.PlatformResult.Failure(
                "push_to_talk.hook_unavailable",
                "The Windows low-level keyboard hook could not be installed.");
    }

    public void Reset()
    {
        Dispatch(PortablePttEvent.Reset);
        _hook.ClearPressedKeys();
    }

    /// <summary>
    /// Reset state to idle when recording is stopped externally (cancel, error, etc.).
    /// Prevents stale state from misinterpreting the next key press as part of an
    /// unlock sequence.
    ///
    /// This is a FULL cancel from every state, WaitingForActivation included. It
    /// used to return early from there, which left the 250ms activation timer
    /// armed to start a recording the app had already cancelled.
    /// </summary>
    public void ResetToIdle() => Dispatch(PortablePttEvent.ResetToIdle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Reset(); // cancels every timer the core owns
        _hook.Dispose();
    }

    /// <summary>
    /// The hook's callback: it has already added a key-down to, or removed a
    /// key-up from, the pressed set. Push-to-talk never swallows a key.
    /// </summary>
    private bool OnHookKey(int vkCode, bool isKeyDown)
    {
        if (_settings.Mode == PushToTalkMode.Disabled) return false;

        if (isKeyDown)
        {
            HandleKeyDown(vkCode);
        }
        else
        {
            HandleKeyUp(vkCode);
        }
        return false;
    }

    private void HandleKeyDown(int vkCode)
    {
        TracePttEvent("keyDown", vkCode);

        // Non-shortcut keys pressed during activation or active PTT = interference
        if ((_machine.State == PortablePttState.WaitingForActivation || _machine.State == PortablePttState.PttActive)
            && !IsKeyPartOfShortcut(vkCode))
        {
            Dispatch(PortablePttEvent.Interference);
            return;
        }

        if (!IsPrimaryKey(vkCode)) return;
        if (!IsShortcutSatisfied()) return;

        Dispatch(PortablePttEvent.KeyDown);
    }

    private void HandleKeyUp(int vkCode)
    {
        TracePttEvent("keyUp", vkCode);

        if (!IsPrimaryKey(vkCode)) return;

        // For logical modifiers backed by multiple physical keys (e.g. "ctrl" = LCtrl + RCtrl),
        // only transition when the shortcut is no longer satisfied (both keys released).
        // This matches macOS which checks the logical modifier flag, not individual key-ups.
        // The unlock sequence is the exception: it counts releases, so it must see each one.
        if (IsShortcutSatisfied() && _machine.State != PortablePttState.UnlatchPending) return;

        Dispatch(PortablePttEvent.KeyUp);
    }

    /// <summary>
    /// Step the shared machine and apply what it asks for.
    ///
    /// Every caller is already on the UI thread — WH_KEYBOARD_LL delivers to the
    /// thread that installed the hook, and DispatcherTimer ticks there too — so
    /// this needs no lock, exactly as the hand-written machine did not.
    /// </summary>
    /// <param name="fired">
    /// The timer whose tick is driving this event, if any. Its slot is cleared
    /// before the step runs so a cancel in the same step cannot stop a timer the
    /// step itself re-armed.
    /// </param>
    private void Dispatch(PortablePttEvent @event, PortablePttTimer? fired = null)
    {
        if (fired is { } spent)
        {
            _timers[(int)spent]?.Stop();
            _timers[(int)spent] = null;
        }

        // The core's debounce asks whether the key is genuinely up. GetKeyboardState
        // is one event behind inside the hook callback, so only a timer tick — after
        // the hook has returned — can answer honestly.
        var keyPhysicallyHeld =
            @event == PortablePttEvent.KeyUpDebounceTimeout && IsPhysicallyHeld();

        var result = PortablePushToTalkCore.Step(_machine, @event, NowMs, _pttConfig, keyPhysicallyHeld);
        _machine = result.State;

        foreach (var command in result.Timers)
        {
            ApplyTimer(command);
        }

        if (result.Transition.Reason != PortablePttReason.Ignored)
        {
            var elapsed = result.Transition.ElapsedMs is { } ms ? $" after {ms}ms" : string.Empty;
            LoggingService.Debug(
                $"PushToTalkMonitor: {result.Transition.From} -> {result.Transition.To} ({result.Transition.Reason}{elapsed})");
        }

        switch (result.Signal)
        {
            case PortablePttSignal.StartRecording:
                RaiseSafe(Pressed);
                break;
            case PortablePttSignal.StopRecording:
                RaiseSafe(Released);
                break;
            case PortablePttSignal.Interfered:
                RaiseSafe(Interfered);
                break;
        }
    }

    private void ApplyTimer(PortablePttTimerCommand command)
    {
        var slot = (int)command.Timer;
        _timers[slot]?.Stop();
        _timers[slot] = null;

        if (!command.Start) return;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(command.DelayMs)
        };
        var kind = command.Timer;
        timer.Tick += (s, args) =>
        {
            TracePttEvent($"{kind}Timer", null);
            Dispatch(TimeoutFor(kind), kind);
        };
        _timers[slot] = timer;
        timer.Start();
    }

    private static PortablePttEvent TimeoutFor(PortablePttTimer timer) => timer switch
    {
        PortablePttTimer.Activation => PortablePttEvent.ActivationTimeout,
        PortablePttTimer.Latch => PortablePttEvent.LatchTimeout,
        _ => PortablePttEvent.KeyUpDebounceTimeout
    };

    private bool IsShortcutSatisfied()
    {
        return _settings.Mode switch
        {
            PushToTalkMode.Modifier => IsModifierNameDown(_settings.Modifier, _hook.PressedKeys),
            PushToTalkMode.Custom => IsCustomShortcutPressed(),
            _ => false
        };
    }

    private bool IsCustomShortcutPressed()
    {
        if (_settings.CustomShortcut == null) return false;
        if (!IsSatisfied(_settings.CustomShortcut, _hook.PressedKeys)) return false;

        if (_settings.CustomShortcut.Key.HasValue)
        {
            int vk = KeyInterop.VirtualKeyFromKey(_settings.CustomShortcut.Key.Value);
            if (!_hook.PressedKeys.Contains(vk)) return false;
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
            return VirtualKeysForModifierName(_settings.Modifier);
        }

        if (_settings.Mode == PushToTalkMode.Custom && _settings.CustomShortcut != null)
        {
            if (_settings.CustomShortcut.Key.HasValue)
            {
                return new[] { KeyInterop.VirtualKeyFromKey(_settings.CustomShortcut.Key.Value) };
            }

            if (_settings.CustomShortcut.Control) return CtrlVirtualKeys;
            if (_settings.CustomShortcut.Alt) return AltVirtualKeys;
            if (_settings.CustomShortcut.Shift) return ShiftVirtualKeys;
            if (_settings.CustomShortcut.Win) return WinVirtualKeys;
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

            // Check if the key is the opposite side, or the sideless generic code, of the same modifier family
            return IsOtherSideOfModifierName(_settings.Modifier, vkCode);
        }

        if (_settings.Mode == PushToTalkMode.Custom && _settings.CustomShortcut != null)
        {
            if (_settings.CustomShortcut.Key.HasValue &&
                vkCode == KeyInterop.VirtualKeyFromKey(_settings.CustomShortcut.Key.Value))
            {
                return true;
            }

            if (IsCtrlVirtualKey(vkCode)) return _settings.CustomShortcut.Control;
            if (IsAltVirtualKey(vkCode)) return _settings.CustomShortcut.Alt;
            if (IsShiftVirtualKey(vkCode)) return _settings.CustomShortcut.Shift;
            if (IsWinVirtualKey(vkCode)) return _settings.CustomShortcut.Win;
            return false;
        }

        return false;
    }

    // =========================================================================
    // INTERFERENCE & TIMERS
    // =========================================================================

    /// <summary>
    /// Returns true if the PTT key (or any key satisfying the shortcut) is physically held
    /// according to GetAsyncKeyState. Only reliable when called from the UI thread AFTER
    /// the WH_KEYBOARD_LL hook callback has returned (state updates asynchronously).
    /// </summary>
    private bool IsPhysicallyHeld()
    {
        if (_settings.Mode == PushToTalkMode.Modifier)
        {
            return VirtualKeysForModifierName(_settings.Modifier).Any(IsKeyPhysicallyDown);
        }

        if (_settings.Mode == PushToTalkMode.Custom && _settings.CustomShortcut != null)
        {
            var shortcut = _settings.CustomShortcut;
            var hasAnyKey = shortcut.Control || shortcut.Alt || shortcut.Shift || shortcut.Win || shortcut.Key.HasValue;

            if (!hasAnyKey)
                return false;

            if (shortcut.Control && !CtrlVirtualKeys.Any(IsKeyPhysicallyDown)) return false;
            if (shortcut.Alt && !AltVirtualKeys.Any(IsKeyPhysicallyDown)) return false;
            if (shortcut.Shift && !ShiftVirtualKeys.Any(IsKeyPhysicallyDown)) return false;
            if (shortcut.Win && !WinVirtualKeys.Any(IsKeyPhysicallyDown)) return false;

            if (shortcut.Key.HasValue)
            {
                int vk = KeyInterop.VirtualKeyFromKey(shortcut.Key.Value);
                return IsKeyPhysicallyDown(vk);
            }

            return true;
        }

        return false;
    }

    private void TracePttEvent(string eventName, int? vkCode)
    {
        var isPrimary = vkCode.HasValue && IsPrimaryKey(vkCode.Value);
        if (_machine.State == PortablePttState.Idle && !isPrimary && (eventName == "keyDown" || eventName == "keyUp"))
        {
            return;
        }

        var now = NowMs;
        string sinceFirstTap = _machine.FirstTapMs is { } firstTap ? $"{now - firstTap}ms" : "none";
        string sinceLock = _machine.LastLockMs is { } lastLock ? $"{now - lastLock}ms" : "none";
        var keyPart = vkCode.HasValue ? $" vk={vkCode.Value}" : "";

        LoggingService.Debug(
            $"PushToTalkMonitor: trace {eventName}{keyPart} state={_machine.State} enteredViaHold={_machine.EnteredViaHold} shortcutSatisfied={IsShortcutSatisfied()} sinceFirstTap={sinceFirstTap} sinceLock={sinceLock}");
    }

    private void RaiseSafe(EventHandler? evt)
    {
        void Invoke()
        {
            if (evt == null) return;

            foreach (EventHandler handler in evt.GetInvocationList())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    LoggingService.Error("PushToTalkMonitor: event handler failed", ex);
                }
            }
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
}
