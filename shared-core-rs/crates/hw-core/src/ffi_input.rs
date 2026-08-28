//! UniFFI surface for the push-to-talk state machine (`hw_input::ptt`, issue #287).
//!
//! Mirrors the leaf crate's records and enums so `hw-input` stays uniffi-free,
//! the same way `ffi_prompt` mirrors `hw-text` and `ffi_live` mirrors
//! `hw_net::live`.
//!
//! Everything here is `ptt_`-prefixed and session-free. There is no Rust-owned
//! object: [`ptt_step`] takes the whole machine record and returns the next one,
//! and the head keeps it between events. That is what lets all three heads share
//! the transition table without any of them giving up its own event source,
//! timer primitive or clock.
//!
//! **`now_ms` must be a monotonic reading** — `ContinuousClock` on macOS,
//! `Environment.TickCount64` on Windows and Linux. Never a wall clock: all three
//! heads used one, so an NTP step during a locked recording could make the "time
//! since lock" interval negative and defeat bounce protection.

use hw_input::ptt;

// ===========================================================================
// Types
// ===========================================================================

/// The five push-to-talk states. Mirrors `ptt::PttState`.
#[derive(uniffi::Enum)]
pub enum HwPttState {
    /// Not recording, no key held.
    Idle,
    /// Key is down; waiting out the activation delay to see whether this is a
    /// hold or the leading modifier of a keyboard shortcut.
    WaitingForActivation,
    /// Recording, started either by a hold or by the first tap of a lock
    /// sequence.
    PttActive,
    /// Recording hands-free after a confirmed double-tap lock.
    LatchActive,
    /// First tap of the unlock sequence seen; still recording.
    UnlatchPending,
}

impl From<HwPttState> for ptt::PttState {
    fn from(s: HwPttState) -> Self {
        match s {
            HwPttState::Idle => ptt::PttState::Idle,
            HwPttState::WaitingForActivation => ptt::PttState::WaitingForActivation,
            HwPttState::PttActive => ptt::PttState::PttActive,
            HwPttState::LatchActive => ptt::PttState::LatchActive,
            HwPttState::UnlatchPending => ptt::PttState::UnlatchPending,
        }
    }
}

impl From<ptt::PttState> for HwPttState {
    fn from(s: ptt::PttState) -> Self {
        match s {
            ptt::PttState::Idle => HwPttState::Idle,
            ptt::PttState::WaitingForActivation => HwPttState::WaitingForActivation,
            ptt::PttState::PttActive => HwPttState::PttActive,
            ptt::PttState::LatchActive => HwPttState::LatchActive,
            ptt::PttState::UnlatchPending => HwPttState::UnlatchPending,
        }
    }
}

/// Everything the machine remembers between events. The head owns one of these
/// and hands it back on the next call. Mirrors `ptt::PttMachineState`.
///
/// `first_tap_ms` does double duty: in `PttActive` it is when recording started
/// (the lock window is measured from it), and in `UnlatchPending` it is when the
/// first unlock tap was released, with `null` as the "not yet tapped" sentinel.
#[derive(uniffi::Record)]
pub struct HwPttMachineState {
    pub state: HwPttState,
    /// Whether the configured shortcut is currently satisfied, as last reported
    /// by the head.
    pub key_down: bool,
    /// `true` when `PttActive` was entered by holding past the activation delay.
    /// A hold always stops on release; a quick tap can latch.
    pub entered_via_hold: bool,
    pub first_tap_ms: Option<u64>,
    /// When `LatchActive` was entered. Bounce protection measures
    /// `minimum_lock_ms` from here.
    pub last_lock_ms: Option<u64>,
}

impl From<HwPttMachineState> for ptt::PttMachineState {
    fn from(s: HwPttMachineState) -> Self {
        ptt::PttMachineState {
            state: s.state.into(),
            key_down: s.key_down,
            entered_via_hold: s.entered_via_hold,
            first_tap_ms: s.first_tap_ms,
            last_lock_ms: s.last_lock_ms,
        }
    }
}

impl From<ptt::PttMachineState> for HwPttMachineState {
    fn from(s: ptt::PttMachineState) -> Self {
        HwPttMachineState {
            state: s.state.into(),
            key_down: s.key_down,
            entered_via_hold: s.entered_via_hold,
            first_tap_ms: s.first_tap_ms,
            last_lock_ms: s.last_lock_ms,
        }
    }
}

/// The timing constants and the double-tap-lock toggle. Mirrors
/// `ptt::PttConfig`.
///
/// Build one with [`ptt_config`] rather than by hand: it fills in the two
/// constants that already agreed across the heads (250 ms activation delay,
/// 1500 ms double-press window) so they cannot drift again. The other two did
/// not agree and each head keeps what it ships — macOS 1000 ms minimum lock and
/// no key-up debounce; Windows and Linux 2000 ms and 100 ms.
#[derive(uniffi::Record)]
pub struct HwPttConfig {
    pub activation_delay_ms: u64,
    pub double_press_window_ms: u64,
    pub minimum_lock_ms: u64,
    /// `0` commits a release immediately, which is what macOS does.
    pub key_up_debounce_ms: u64,
    pub double_press_lock: bool,
}

impl From<HwPttConfig> for ptt::PttConfig {
    fn from(c: HwPttConfig) -> Self {
        ptt::PttConfig {
            activation_delay_ms: c.activation_delay_ms,
            double_press_window_ms: c.double_press_window_ms,
            minimum_lock_ms: c.minimum_lock_ms,
            key_up_debounce_ms: c.key_up_debounce_ms,
            double_press_lock: c.double_press_lock,
        }
    }
}

impl From<ptt::PttConfig> for HwPttConfig {
    fn from(c: ptt::PttConfig) -> Self {
        HwPttConfig {
            activation_delay_ms: c.activation_delay_ms,
            double_press_window_ms: c.double_press_window_ms,
            minimum_lock_ms: c.minimum_lock_ms,
            key_up_debounce_ms: c.key_up_debounce_ms,
            double_press_lock: c.double_press_lock,
        }
    }
}

/// Everything that can drive the machine. Mirrors `ptt::PttEvent`.
#[derive(uniffi::Enum)]
pub enum HwPttEvent {
    /// The configured shortcut became satisfied.
    KeyDown,
    /// The configured shortcut stopped being satisfied.
    KeyUp,
    /// The activation timer elapsed.
    ActivationTimeout,
    /// The latch timer elapsed.
    LatchTimeout,
    /// The key-up debounce timer elapsed.
    ///
    /// `key_physically_held` is the head's hardware cross-check taken after the
    /// key event has been fully delivered — `GetAsyncKeyState` on Windows. A head
    /// with no such probe passes `false`; the machine still consults its own
    /// `key_down`.
    KeyUpDebounceTimeout { key_physically_held: bool },
    /// Another key was pressed while the shortcut was held, so this was a
    /// keyboard shortcut and not push-to-talk.
    Interference,
    /// Tear the recording down because the head knows the key was released but
    /// never saw the release. macOS raises this after its CGEventTap is
    /// re-enabled.
    ///
    /// Never synthesise a `KeyUp` for this. A synthesised release takes the
    /// quick-tap branch and can latch instead of stopping — the stuck-microphone
    /// bug (#300), spelled out at `BareModifierKeyMonitor.swift:495`.
    ForceStop,
    /// Full reset: configuration changed, or the monitor is starting or
    /// stopping.
    Reset,
    /// Recording was stopped by something other than this machine. Handled
    /// identically to `Reset`, including from `WaitingForActivation`, where
    /// macOS and Windows used to return early and leave the activation timer
    /// armed.
    ResetToIdle,
}

impl From<HwPttEvent> for ptt::PttEvent {
    fn from(e: HwPttEvent) -> Self {
        match e {
            HwPttEvent::KeyDown => ptt::PttEvent::KeyDown,
            HwPttEvent::KeyUp => ptt::PttEvent::KeyUp,
            HwPttEvent::ActivationTimeout => ptt::PttEvent::ActivationTimeout,
            HwPttEvent::LatchTimeout => ptt::PttEvent::LatchTimeout,
            HwPttEvent::KeyUpDebounceTimeout {
                key_physically_held,
            } => ptt::PttEvent::KeyUpDebounceTimeout {
                key_physically_held,
            },
            HwPttEvent::Interference => ptt::PttEvent::Interference,
            HwPttEvent::ForceStop => ptt::PttEvent::ForceStop,
            HwPttEvent::Reset => ptt::PttEvent::Reset,
            HwPttEvent::ResetToIdle => ptt::PttEvent::ResetToIdle,
        }
    }
}

/// What the head must do to the recording. Mirrors `ptt::PttSignal`.
#[derive(uniffi::Enum)]
pub enum HwPttSignal {
    /// Start recording (`onModifierDown` / `Pressed`).
    StartRecording,
    /// Stop recording (`onModifierUp` / `Released`).
    StopRecording,
    /// Cancel and discard: the key was part of a keyboard shortcut.
    Interfered,
}

impl From<ptt::PttSignal> for HwPttSignal {
    fn from(s: ptt::PttSignal) -> Self {
        match s {
            ptt::PttSignal::StartRecording => HwPttSignal::StartRecording,
            ptt::PttSignal::StopRecording => HwPttSignal::StopRecording,
            ptt::PttSignal::Interfered => HwPttSignal::Interfered,
        }
    }
}

/// The three timers the machine can ask for. Mirrors `ptt::PttTimer`.
#[derive(uniffi::Enum)]
pub enum HwPttTimer {
    /// Fires `ActivationTimeout` after `activation_delay_ms`.
    Activation,
    /// Fires `LatchTimeout` after `double_press_window_ms`. Does double duty as
    /// the lock timeout and the unlock timeout.
    Latch,
    /// Fires `KeyUpDebounceTimeout` after `key_up_debounce_ms`.
    KeyUpDebounce,
}

impl From<ptt::PttTimer> for HwPttTimer {
    fn from(t: ptt::PttTimer) -> Self {
        match t {
            ptt::PttTimer::Activation => HwPttTimer::Activation,
            ptt::PttTimer::Latch => HwPttTimer::Latch,
            ptt::PttTimer::KeyUpDebounce => HwPttTimer::KeyUpDebounce,
        }
    }
}

#[derive(uniffi::Enum)]
pub enum HwPttTimerAction {
    Start,
    Cancel,
}

impl From<ptt::PttTimerAction> for HwPttTimerAction {
    fn from(a: ptt::PttTimerAction) -> Self {
        match a {
            ptt::PttTimerAction::Start => HwPttTimerAction::Start,
            ptt::PttTimerAction::Cancel => HwPttTimerAction::Cancel,
        }
    }
}

/// One timer instruction. Mirrors `ptt::PttTimerCommand`.
///
/// `delay_ms` is meaningless for `Cancel` and is reported as `0`. Starting a
/// timer that is already running replaces it; cancelling one that is not running
/// is a no-op.
#[derive(uniffi::Record)]
pub struct HwPttTimerCommand {
    pub timer: HwPttTimer,
    pub action: HwPttTimerAction,
    pub delay_ms: u64,
}

impl From<ptt::PttTimerCommand> for HwPttTimerCommand {
    fn from(c: ptt::PttTimerCommand) -> Self {
        HwPttTimerCommand {
            timer: c.timer.into(),
            action: c.action.into(),
            delay_ms: c.delay_ms,
        }
    }
}

/// Why the machine did what it did. Mirrors `ptt::PttReason`.
///
/// Carried purely for logs and telemetry — macOS keeps its Sentry breadcrumbs
/// native and reads their payloads off [`HwPttTransition`] rather than
/// re-deriving the transition table.
#[derive(uniffi::Enum)]
pub enum HwPttReason {
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
    ExternalReset,
}

impl From<ptt::PttReason> for HwPttReason {
    fn from(r: ptt::PttReason) -> Self {
        match r {
            ptt::PttReason::Ignored => HwPttReason::Ignored,
            ptt::PttReason::ActivationArmed => HwPttReason::ActivationArmed,
            ptt::PttReason::HoldActivated => HwPttReason::HoldActivated,
            ptt::PttReason::ReleasePending => HwPttReason::ReleasePending,
            ptt::PttReason::SpuriousKeyUpIgnored => HwPttReason::SpuriousKeyUpIgnored,
            ptt::PttReason::HoldReleaseStopped => HwPttReason::HoldReleaseStopped,
            ptt::PttReason::QuickTapStarted => HwPttReason::QuickTapStarted,
            ptt::PttReason::QuickTapDiscarded => HwPttReason::QuickTapDiscarded,
            ptt::PttReason::Reacquired => HwPttReason::Reacquired,
            ptt::PttReason::DoubleTapLocked => HwPttReason::DoubleTapLocked,
            ptt::PttReason::SingleTapStopped => HwPttReason::SingleTapStopped,
            ptt::PttReason::LatchTimeoutStopped => HwPttReason::LatchTimeoutStopped,
            ptt::PttReason::BounceProtected => HwPttReason::BounceProtected,
            ptt::PttReason::UnlockArmed => HwPttReason::UnlockArmed,
            ptt::PttReason::UnlockFirstTap => HwPttReason::UnlockFirstTap,
            ptt::PttReason::UnlockConfirmed => HwPttReason::UnlockConfirmed,
            ptt::PttReason::UnlockTooSlow => HwPttReason::UnlockTooSlow,
            ptt::PttReason::UnlockTimedOut => HwPttReason::UnlockTimedOut,
            ptt::PttReason::InterferenceCancelled => HwPttReason::InterferenceCancelled,
            ptt::PttReason::InterferenceStopped => HwPttReason::InterferenceStopped,
            ptt::PttReason::ForceStopped => HwPttReason::ForceStopped,
            ptt::PttReason::ExternalReset => HwPttReason::ExternalReset,
        }
    }
}

/// What changed, for logs and telemetry. Mirrors `ptt::PttTransition`.
#[derive(uniffi::Record)]
pub struct HwPttTransition {
    pub from: HwPttState,
    pub to: HwPttState,
    pub reason: HwPttReason,
    /// The interval the decision turned on, when there was one: time since the
    /// first tap for the tap reasons, time since the lock for `BounceProtected`
    /// and `UnlockArmed`.
    pub elapsed_ms: Option<u64>,
}

impl From<ptt::PttTransition> for HwPttTransition {
    fn from(t: ptt::PttTransition) -> Self {
        HwPttTransition {
            from: t.from.into(),
            to: t.to.into(),
            reason: t.reason.into(),
            elapsed_ms: t.elapsed_ms,
        }
    }
}

/// The next state and everything the head must do about it. Mirrors
/// `ptt::PttStepResult`.
///
/// At most one signal per step — no transition both starts and stops a
/// recording.
#[derive(uniffi::Record)]
pub struct HwPttStepResult {
    /// Store this and pass it back in on the next event.
    pub state: HwPttMachineState,
    pub signal: Option<HwPttSignal>,
    /// Apply in order. Starting a running timer replaces it.
    pub timers: Vec<HwPttTimerCommand>,
    /// `true` to start watching for interfering key presses, `false` to stop,
    /// `null` to leave the head's current arming alone.
    ///
    /// Linux performed this inside its own reducer at nearly every transition.
    /// Reporting it here is what keeps the Linux wrapper from re-deriving the
    /// transition table. macOS and Windows watch unconditionally and ignore it.
    pub arm_interference: Option<bool>,
    /// Clear any keyboard state the head holds on the machine's behalf — Linux's
    /// `IGlobalShortcutService.ResetKeyboardState()`. The other two heads have no
    /// equivalent and ignore it.
    pub reset_keyboard_state: bool,
    pub transition: HwPttTransition,
}

impl From<ptt::PttStepResult> for HwPttStepResult {
    fn from(r: ptt::PttStepResult) -> Self {
        HwPttStepResult {
            state: r.state.into(),
            signal: r.signal.map(Into::into),
            timers: r.timers.into_iter().map(Into::into).collect(),
            arm_interference: r.arm_interference,
            reset_keyboard_state: r.reset_keyboard_state,
            transition: r.transition.into(),
        }
    }
}

// ===========================================================================
// Functions
// ===========================================================================

/// A freshly reset machine: idle, no key held, no timestamps.
#[uniffi::export]
pub fn ptt_initial_state() -> HwPttMachineState {
    ptt::PttMachineState::idle().into()
}

/// Build a config with the two shared constants already filled in.
///
/// Pass the head's own `minimum_lock_ms` and `key_up_debounce_ms`: macOS 1000
/// and 0, Windows and Linux 2000 and 100. Those two are deliberately still
/// per-platform.
#[uniffi::export]
pub fn ptt_config(
    minimum_lock_ms: u64,
    key_up_debounce_ms: u64,
    double_press_lock: bool,
) -> HwPttConfig {
    ptt::PttConfig::new(minimum_lock_ms, key_up_debounce_ms, double_press_lock).into()
}

/// Advance the push-to-talk machine by one event.
///
/// `now_ms` must be a monotonic reading — see the module note on the clock.
#[uniffi::export]
pub fn ptt_step(
    state: HwPttMachineState,
    event: HwPttEvent,
    now_ms: u64,
    config: HwPttConfig,
) -> HwPttStepResult {
    ptt::ptt_step(state.into(), event.into(), now_ms, config.into()).into()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The mirror must not reorder or drop a field on the way across. One full
    /// round trip through a transition that touches every part of the result.
    #[test]
    fn mirrors_a_full_step_result() {
        let config = ptt_config(2000, 100, true);
        assert_eq!(config.activation_delay_ms, ptt::ACTIVATION_DELAY_MS);
        assert_eq!(config.double_press_window_ms, ptt::DOUBLE_PRESS_WINDOW_MS);

        let down = ptt_step(
            ptt_initial_state(),
            HwPttEvent::KeyDown,
            0,
            ptt_config(2000, 100, true),
        );
        assert!(matches!(down.state.state, HwPttState::WaitingForActivation));
        assert!(down.state.key_down);
        assert_eq!(down.arm_interference, Some(true));
        assert_eq!(down.timers.len(), 1);
        assert!(matches!(down.timers[0].timer, HwPttTimer::Activation));
        assert!(matches!(down.timers[0].action, HwPttTimerAction::Start));
        assert_eq!(down.timers[0].delay_ms, 250);
        assert!(matches!(
            down.transition.reason,
            HwPttReason::ActivationArmed
        ));

        let active = ptt_step(
            down.state,
            HwPttEvent::ActivationTimeout,
            250,
            ptt_config(2000, 100, true),
        );
        assert!(matches!(active.signal, Some(HwPttSignal::StartRecording)));
        assert!(active.state.entered_via_hold);
        assert_eq!(active.state.first_tap_ms, Some(250));

        // A hold stops on release; it never latches.
        let released = ptt_step(
            active.state,
            HwPttEvent::KeyUp,
            400,
            ptt_config(2000, 100, true),
        );
        assert!(matches!(
            released.transition.reason,
            HwPttReason::ReleasePending
        ));

        let stopped = ptt_step(
            released.state,
            HwPttEvent::KeyUpDebounceTimeout {
                key_physically_held: false,
            },
            500,
            ptt_config(2000, 100, true),
        );
        assert!(matches!(stopped.signal, Some(HwPttSignal::StopRecording)));
        assert!(matches!(stopped.state.state, HwPttState::Idle));
        assert!(matches!(
            stopped.transition.reason,
            HwPttReason::HoldReleaseStopped
        ));
        assert_eq!(stopped.transition.elapsed_ms, Some(250));
    }

    /// `ForceStop` reaches the reducer as its own event, not as a release.
    #[test]
    fn force_stop_crosses_as_its_own_event() {
        let config = ptt_config(1000, 0, true);
        let down = ptt_step(
            ptt_initial_state(),
            HwPttEvent::KeyDown,
            0,
            ptt_config(1000, 0, true),
        );
        // Quick tap into the latching branch.
        let tapped = ptt_step(down.state, HwPttEvent::KeyUp, 50, ptt_config(1000, 0, true));
        assert!(matches!(tapped.signal, Some(HwPttSignal::StartRecording)));
        assert!(!tapped.state.entered_via_hold);

        let forced = ptt_step(tapped.state, HwPttEvent::ForceStop, 100, config);
        assert!(matches!(forced.signal, Some(HwPttSignal::StopRecording)));
        assert!(matches!(forced.state.state, HwPttState::Idle));
        assert!(forced.reset_keyboard_state);
    }
}
