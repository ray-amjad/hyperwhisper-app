using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// The five push-to-talk states. Mirrors the core's <c>HwPttState</c> (issue #287).
/// </summary>
public enum PortablePttState
{
    /// <summary>Not recording, no key held.</summary>
    Idle,

    /// <summary>
    /// Key is down; waiting out the activation delay to see whether this is a
    /// hold or the leading modifier of a keyboard shortcut.
    /// </summary>
    WaitingForActivation,

    /// <summary>
    /// Recording, started either by a hold or by the first tap of a lock sequence.
    /// </summary>
    PttActive,

    /// <summary>Recording hands-free after a confirmed double-tap lock.</summary>
    LatchActive,

    /// <summary>First tap of the unlock sequence seen; still recording.</summary>
    UnlatchPending
}

/// <summary>
/// What the head must do to the recording. Mirrors the core's <c>HwPttSignal</c>.
/// </summary>
public enum PortablePttSignal
{
    StartRecording,
    StopRecording,

    /// <summary>Cancel and discard: the key was part of a keyboard shortcut.</summary>
    Interfered
}

/// <summary>
/// The three timers the machine can ask for. The head supplies the primitive.
/// Mirrors the core's <c>HwPttTimer</c>.
/// </summary>
public enum PortablePttTimer
{
    Activation,

    /// <summary>
    /// The double-press window. Does double duty as the lock timeout (in
    /// <see cref="PortablePttState.PttActive"/>) and the unlock timeout (in
    /// <see cref="PortablePttState.UnlatchPending"/>).
    /// </summary>
    Latch,
    KeyUpDebounce
}

/// <summary>
/// Everything that can drive the machine. Mirrors the core's <c>HwPttEvent</c>,
/// flattened to a plain enum because only one variant carries a payload — see
/// the <c>keyPhysicallyHeld</c> argument on
/// <see cref="PortablePushToTalkCore.Step"/>.
/// </summary>
public enum PortablePttEvent
{
    KeyDown,
    KeyUp,
    ActivationTimeout,
    LatchTimeout,
    KeyUpDebounceTimeout,
    Interference,

    /// <summary>
    /// Tear the recording down because the head knows the key was released but
    /// never saw the release. Never synthesise a <see cref="KeyUp"/> for this:
    /// a synthesised release takes the quick-tap branch and can latch instead of
    /// stopping, which is the stuck-microphone bug (#300).
    /// </summary>
    ForceStop,
    Reset,

    /// <summary>
    /// Recording was stopped by something other than this machine. A full
    /// cancel, including from <see cref="PortablePttState.WaitingForActivation"/>,
    /// where Windows used to return early and leave the activation timer armed.
    /// </summary>
    ResetToIdle
}

/// <summary>
/// Why the machine did what it did, for logs and telemetry. Mirrors the core's
/// <c>HwPttReason</c>.
/// </summary>
public enum PortablePttReason
{
    Ignored,
    ActivationArmed,
    HoldActivated,
    ReleasePending,
    SpuriousKeyUpIgnored,
    HoldReleaseStopped,
    QuickTapStarted,
    QuickTapDiscarded,
    Reacquired,
    DoubleTapLocked,
    SingleTapStopped,
    LatchTimeoutStopped,
    BounceProtected,
    UnlockArmed,
    UnlockFirstTap,
    UnlockConfirmed,
    UnlockTooSlow,
    UnlockTimedOut,
    InterferenceCancelled,
    InterferenceStopped,
    ForceStopped,
    ExternalReset
}

/// <summary>
/// Everything the machine remembers between events. The head owns one of these
/// and hands it back on the next call.
/// </summary>
/// <param name="FirstTapMs">
/// Double duty: in <see cref="PortablePttState.PttActive"/> it is when recording
/// started, and in <see cref="PortablePttState.UnlatchPending"/> it is when the
/// first unlock tap was released, with <c>null</c> as the "not yet tapped"
/// sentinel.
/// </param>
public readonly record struct PortablePttMachineState(
    PortablePttState State,
    bool KeyDown,
    bool EnteredViaHold,
    ulong? FirstTapMs,
    ulong? LastLockMs);

/// <summary>
/// The timing constants and the double-tap-lock toggle. Build one with
/// <see cref="PortablePushToTalkCore.Config"/>, which fills in the two constants
/// the platforms already agreed on.
/// </summary>
public readonly record struct PortablePttConfig(
    ulong ActivationDelayMs,
    ulong DoublePressWindowMs,
    ulong MinimumLockMs,
    ulong KeyUpDebounceMs,
    bool DoublePressLock);

/// <summary>
/// One timer instruction. <paramref name="DelayMs"/> is meaningless when
/// <paramref name="Start"/> is <c>false</c>. Starting a running timer replaces
/// it; cancelling one that is not running is a no-op.
/// </summary>
public readonly record struct PortablePttTimerCommand(
    PortablePttTimer Timer,
    bool Start,
    ulong DelayMs);

/// <summary>What changed, for logs and telemetry.</summary>
/// <param name="ElapsedMs">
/// The interval the decision turned on, when there was one.
/// </param>
public readonly record struct PortablePttTransition(
    PortablePttState From,
    PortablePttState To,
    PortablePttReason Reason,
    ulong? ElapsedMs);

/// <summary>
/// The next state and everything the head must do about it. At most one signal
/// per step — no transition both starts and stops a recording.
/// </summary>
/// <param name="ArmInterference">
/// <c>true</c> to start watching for interfering key presses, <c>false</c> to
/// stop, <c>null</c> to leave the head's current arming alone. Linux performed
/// this inside its own reducer; reporting it keeps the wrapper from re-deriving
/// the transition table. Windows watches unconditionally and ignores it.
/// </param>
/// <param name="ResetKeyboardState">
/// Clear any keyboard state the head holds on the machine's behalf — Linux's
/// <c>IGlobalShortcutService.ResetKeyboardState()</c>. Windows has no equivalent.
/// </param>
public sealed record PortablePttStepResult(
    PortablePttMachineState State,
    PortablePttSignal? Signal,
    IReadOnlyList<PortablePttTimerCommand> Timers,
    bool? ArmInterference,
    bool ResetKeyboardState,
    PortablePttTransition Transition);

/// <summary>
/// The shared push-to-talk state machine (issue #287). One transition table for
/// macOS, Windows and Linux, held in <c>hw-input</c> and reached through the
/// UniFFI core.
///
/// The head owns the event source, the timer primitive and the clock; this type
/// owns only the transition table. Feed every event through
/// <see cref="Step"/>, store the returned <see cref="PortablePttStepResult.State"/>
/// and apply the returned effects.
///
/// <para>
/// <b>The clock must be monotonic.</b> Pass
/// <c>(ulong)Environment.TickCount64</c>, never a <c>DateTime</c>. Windows and
/// Linux both compared wall-clock timestamps before this change, so an NTP step
/// during a locked recording could make the "time since lock" interval negative
/// and defeat bounce protection.
/// </para>
/// </summary>
public static class PortablePushToTalkCore
{
    /// <summary>A freshly reset machine: idle, no key held, no timestamps.</summary>
    public static PortablePttMachineState InitialState() =>
        FromCore(HyperwhisperCoreMethods.PttInitialState());

    /// <summary>
    /// Build a config. The activation delay (250 ms) and the double-press window
    /// (1500 ms) already agreed across the platforms and are filled in by the
    /// core, so a head cannot drift on them. The other two did not agree and stay
    /// per-platform: Windows and Linux pass 2000 ms and 100 ms.
    /// </summary>
    public static PortablePttConfig Config(
        ulong minimumLockMs,
        ulong keyUpDebounceMs,
        bool doublePressLock)
    {
        var config = HyperwhisperCoreMethods.PttConfig(minimumLockMs, keyUpDebounceMs, doublePressLock);
        return new PortablePttConfig(
            config.@activationDelayMs,
            config.@doublePressWindowMs,
            config.@minimumLockMs,
            config.@keyUpDebounceMs,
            config.@doublePressLock);
    }

    /// <summary>
    /// Advance the machine by one event.
    /// </summary>
    /// <param name="nowMs">
    /// A monotonic reading — <c>Environment.TickCount64</c>, never a wall clock.
    /// </param>
    /// <param name="keyPhysicallyHeld">
    /// Only read for <see cref="PortablePttEvent.KeyUpDebounceTimeout"/>: the
    /// head's hardware cross-check, taken after the key event has been fully
    /// delivered. Windows passes <c>GetAsyncKeyState</c>; a head with no such
    /// probe passes <c>false</c> and the machine falls back to its own
    /// <see cref="PortablePttMachineState.KeyDown"/>.
    /// </param>
    public static PortablePttStepResult Step(
        PortablePttMachineState state,
        PortablePttEvent @event,
        ulong nowMs,
        PortablePttConfig config,
        bool keyPhysicallyHeld = false)
    {
        var result = HyperwhisperCoreMethods.PttStep(
            ToCore(state),
            ToCore(@event, keyPhysicallyHeld),
            nowMs,
            ToCore(config));

        var timers = new List<PortablePttTimerCommand>(result.@timers.Count);
        foreach (var command in result.@timers)
        {
            timers.Add(new PortablePttTimerCommand(
                FromCore(command.@timer),
                command.@action == HwPttTimerAction.Start,
                command.@delayMs));
        }

        return new PortablePttStepResult(
            FromCore(result.@state),
            result.@signal is { } signal ? FromCore(signal) : null,
            timers,
            result.@armInterference,
            result.@resetKeyboardState,
            new PortablePttTransition(
                FromCore(result.@transition.@from),
                FromCore(result.@transition.@to),
                FromCore(result.@transition.@reason),
                result.@transition.@elapsedMs));
    }

    private static HwPttMachineState ToCore(PortablePttMachineState state) =>
        new(ToCore(state.State), state.KeyDown, state.EnteredViaHold, state.FirstTapMs, state.LastLockMs);

    private static HwPttConfig ToCore(PortablePttConfig config) =>
        new(
            config.ActivationDelayMs,
            config.DoublePressWindowMs,
            config.MinimumLockMs,
            config.KeyUpDebounceMs,
            config.DoublePressLock);

    private static HwPttEvent ToCore(PortablePttEvent @event, bool keyPhysicallyHeld) => @event switch
    {
        PortablePttEvent.KeyDown => new HwPttEvent.KeyDown(),
        PortablePttEvent.KeyUp => new HwPttEvent.KeyUp(),
        PortablePttEvent.ActivationTimeout => new HwPttEvent.ActivationTimeout(),
        PortablePttEvent.LatchTimeout => new HwPttEvent.LatchTimeout(),
        PortablePttEvent.KeyUpDebounceTimeout => new HwPttEvent.KeyUpDebounceTimeout(keyPhysicallyHeld),
        PortablePttEvent.Interference => new HwPttEvent.Interference(),
        PortablePttEvent.ForceStop => new HwPttEvent.ForceStop(),
        PortablePttEvent.Reset => new HwPttEvent.Reset(),
        PortablePttEvent.ResetToIdle => new HwPttEvent.ResetToIdle(),
        _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, "Unknown push-to-talk event.")
    };

    private static HwPttState ToCore(PortablePttState state) => state switch
    {
        PortablePttState.Idle => HwPttState.Idle,
        PortablePttState.WaitingForActivation => HwPttState.WaitingForActivation,
        PortablePttState.PttActive => HwPttState.PttActive,
        PortablePttState.LatchActive => HwPttState.LatchActive,
        PortablePttState.UnlatchPending => HwPttState.UnlatchPending,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown push-to-talk state.")
    };

    private static PortablePttMachineState FromCore(HwPttMachineState state) =>
        new(
            FromCore(state.@state),
            state.@keyDown,
            state.@enteredViaHold,
            state.@firstTapMs,
            state.@lastLockMs);

    private static PortablePttState FromCore(HwPttState state) => state switch
    {
        HwPttState.Idle => PortablePttState.Idle,
        HwPttState.WaitingForActivation => PortablePttState.WaitingForActivation,
        HwPttState.PttActive => PortablePttState.PttActive,
        HwPttState.LatchActive => PortablePttState.LatchActive,
        HwPttState.UnlatchPending => PortablePttState.UnlatchPending,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown push-to-talk state.")
    };

    private static PortablePttSignal FromCore(HwPttSignal signal) => signal switch
    {
        HwPttSignal.StartRecording => PortablePttSignal.StartRecording,
        HwPttSignal.StopRecording => PortablePttSignal.StopRecording,
        HwPttSignal.Interfered => PortablePttSignal.Interfered,
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown push-to-talk signal.")
    };

    private static PortablePttTimer FromCore(HwPttTimer timer) => timer switch
    {
        HwPttTimer.Activation => PortablePttTimer.Activation,
        HwPttTimer.Latch => PortablePttTimer.Latch,
        HwPttTimer.KeyUpDebounce => PortablePttTimer.KeyUpDebounce,
        _ => throw new ArgumentOutOfRangeException(nameof(timer), timer, "Unknown push-to-talk timer.")
    };

    private static PortablePttReason FromCore(HwPttReason reason) => reason switch
    {
        HwPttReason.Ignored => PortablePttReason.Ignored,
        HwPttReason.ActivationArmed => PortablePttReason.ActivationArmed,
        HwPttReason.HoldActivated => PortablePttReason.HoldActivated,
        HwPttReason.ReleasePending => PortablePttReason.ReleasePending,
        HwPttReason.SpuriousKeyUpIgnored => PortablePttReason.SpuriousKeyUpIgnored,
        HwPttReason.HoldReleaseStopped => PortablePttReason.HoldReleaseStopped,
        HwPttReason.QuickTapStarted => PortablePttReason.QuickTapStarted,
        HwPttReason.QuickTapDiscarded => PortablePttReason.QuickTapDiscarded,
        HwPttReason.Reacquired => PortablePttReason.Reacquired,
        HwPttReason.DoubleTapLocked => PortablePttReason.DoubleTapLocked,
        HwPttReason.SingleTapStopped => PortablePttReason.SingleTapStopped,
        HwPttReason.LatchTimeoutStopped => PortablePttReason.LatchTimeoutStopped,
        HwPttReason.BounceProtected => PortablePttReason.BounceProtected,
        HwPttReason.UnlockArmed => PortablePttReason.UnlockArmed,
        HwPttReason.UnlockFirstTap => PortablePttReason.UnlockFirstTap,
        HwPttReason.UnlockConfirmed => PortablePttReason.UnlockConfirmed,
        HwPttReason.UnlockTooSlow => PortablePttReason.UnlockTooSlow,
        HwPttReason.UnlockTimedOut => PortablePttReason.UnlockTimedOut,
        HwPttReason.InterferenceCancelled => PortablePttReason.InterferenceCancelled,
        HwPttReason.InterferenceStopped => PortablePttReason.InterferenceStopped,
        HwPttReason.ForceStopped => PortablePttReason.ForceStopped,
        HwPttReason.ExternalReset => PortablePttReason.ExternalReset,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown push-to-talk reason.")
    };
}
