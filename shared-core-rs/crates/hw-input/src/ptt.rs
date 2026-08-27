//! The push-to-talk state machine, as a pure step function.
//!
//! # Why a step function and not an object
//!
//! There are no long-lived Rust-owned objects in this design. [`ptt_step`] takes
//! the whole machine record, one event, the current time and the platform's
//! configuration, and returns the next record plus the effects the platform must
//! perform. The head stores the record between events.
//!
//! That keeps the crate free of interior mutability, of `Mutex` poisoning under
//! the workspace's `panic = "abort"` profile, and of any assumption about which
//! thread an event arrives on. All three heads already marshal their key events
//! onto one thread (`Task { @MainActor }`, `Dispatcher.BeginInvoke`, and a raise
//! outside `lock (_gate)`), so serialising calls into [`ptt_step`] costs them
//! nothing.
//!
//! # The clock
//!
//! `now_ms` **must** come from a monotonic clock — `mach_absolute_time` /
//! `ContinuousClock` on macOS, `Environment.TickCount64` on Windows and Linux.
//! All three heads used to compare wall-clock timestamps, so an NTP step during a
//! locked recording could make the "time since lock" interval negative and defeat
//! bounce protection. Every interval here is computed with [`u64::saturating_sub`],
//! so a clock that does go backwards degrades to "zero elapsed" instead of
//! wrapping, but the head is still responsible for passing a monotonic reading.
//!
//! # What the platform still owns
//!
//! * The event source (CGEventTap, `WH_KEYBOARD_LL`, evdev/portal shortcuts).
//! * Which physical keys make up the configured shortcut, and therefore when a
//!   [`PttEvent::KeyDown`] / [`PttEvent::KeyUp`] is worth reporting at all.
//! * The timer primitive. The reducer only says *which* timer to start or cancel
//!   and for how long, via [`PttTimerCommand`].
//! * Any delay between the [`PttSignal::StartRecording`] signal and actually
//!   opening the microphone (macOS holds the quick-tap start back by 100 ms to
//!   let the audio engine warm up; it can key that off
//!   [`PttMachineState::entered_via_hold`]).
//! * Telemetry. [`PttStepResult::transition`] carries the from/to states, the
//!   reason and the relevant interval so macOS can keep its Sentry breadcrumbs
//!   native without re-deriving the transition table.

/// The five states. Named identically on all three heads before this crate
/// existed, which is what made the duplication so easy to miss.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum PttState {
    /// Not recording, no key held.
    #[default]
    Idle,
    /// Key is down; waiting out the activation delay to see whether this is a
    /// push-to-talk hold or the leading modifier of a keyboard shortcut.
    WaitingForActivation,
    /// Recording. Reached either by holding past the activation delay
    /// (`entered_via_hold`) or by the first tap of a double-tap lock sequence.
    PttActive,
    /// Recording hands-free after a confirmed double-tap lock. Releasing the key
    /// does not stop it.
    LatchActive,
    /// First tap of the unlock sequence has been seen; still recording.
    UnlatchPending,
}

/// Everything the machine remembers between events. The head owns one of these
/// and passes it back in on the next call.
///
/// `first_tap_ms` does double duty, exactly as it did on all three heads: in
/// [`PttState::PttActive`] it is when recording started (the lock window is
/// measured from it), and in [`PttState::UnlatchPending`] it is when the first
/// unlock tap was released, with `None` as the "not yet tapped" sentinel. The
/// heads spelled that sentinel three different ways — `firstTapTime == 0`,
/// `DateTime.MinValue` and `_firstTap is null`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PttMachineState {
    pub state: PttState,
    /// Whether the configured shortcut is currently satisfied, as last reported
    /// by the head. Read by the activation timeout and by the key-up debounce.
    pub key_down: bool,
    /// `true` when [`PttState::PttActive`] was entered by holding past the
    /// activation delay, `false` when it was entered by a quick tap.
    ///
    /// macOS had no equivalent flag, so a hold longer than the activation delay
    /// released inside the double-press window **latched** into hands-free
    /// recording where Windows and Linux stopped. Unified onto the
    /// Windows/Linux reading: a hold always stops on release.
    pub entered_via_hold: bool,
    /// See the type-level note on the double duty this field does.
    pub first_tap_ms: Option<u64>,
    /// When [`PttState::LatchActive`] was entered. Bounce protection measures
    /// `minimum_lock_ms` from here.
    pub last_lock_ms: Option<u64>,
}

impl PttMachineState {
    /// A freshly reset machine: idle, no key held, no timestamps.
    pub fn idle() -> Self {
        Self::default()
    }
}

/// The activation delay. Filters out the leading modifier of a keyboard
/// shortcut (Cmd+C, Ctrl+C, Alt+Tab). Identical on all three heads.
pub const ACTIVATION_DELAY_MS: u64 = 250;

/// The double-press window, used for both the lock and the unlock sequence, and
/// as the "no second tap arrived" timeout. Identical on all three heads.
pub const DOUBLE_PRESS_WINDOW_MS: u64 = 1500;

/// The timing constants and the one user-facing toggle.
///
/// `activation_delay_ms` and `double_press_window_ms` already agreed across the
/// three heads and are filled in by [`PttConfig::new`]. `minimum_lock_ms` and
/// `key_up_debounce_ms` did **not** agree, and this change deliberately does not
/// unify them — each head passes what it ships today:
///
/// | | minimum lock | key-up debounce |
/// |---|---|---|
/// | macOS | 1000 ms | 0 (none) |
/// | Windows | 2000 ms | 100 ms |
/// | Linux | 2000 ms | 100 ms |
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PttConfig {
    pub activation_delay_ms: u64,
    pub double_press_window_ms: u64,
    /// How long [`PttState::LatchActive`] must be held before a key-down may
    /// start the unlock sequence. Guards against key bounce and wireless RF
    /// glitches immediately re-opening the lock.
    pub minimum_lock_ms: u64,
    /// How long to wait after a key-up before committing the release, so a
    /// spurious mid-hold key-up (a dropped 2.4 GHz packet, typically) does not
    /// stop the recording. `0` disables the wait and commits the release
    /// immediately, which is what macOS does.
    pub key_up_debounce_ms: u64,
    /// Whether double-tap-to-lock (hands-free recording) is enabled.
    pub double_press_lock: bool,
}

impl PttConfig {
    /// Build a config with the two shared constants already filled in, so a head
    /// cannot drift on them.
    pub fn new(minimum_lock_ms: u64, key_up_debounce_ms: u64, double_press_lock: bool) -> Self {
        Self {
            activation_delay_ms: ACTIVATION_DELAY_MS,
            double_press_window_ms: DOUBLE_PRESS_WINDOW_MS,
            minimum_lock_ms,
            key_up_debounce_ms,
            double_press_lock,
        }
    }
}

/// Everything that can drive the machine.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PttEvent {
    /// The configured shortcut became satisfied.
    KeyDown,
    /// The configured shortcut stopped being satisfied.
    KeyUp,
    /// The [`PttTimer::Activation`] timer elapsed.
    ActivationTimeout,
    /// The [`PttTimer::Latch`] timer elapsed.
    LatchTimeout,
    /// The [`PttTimer::KeyUpDebounce`] timer elapsed.
    ///
    /// `key_physically_held` is the head's hardware cross-check, taken *after*
    /// the key event has been fully delivered — `GetAsyncKeyState` on Windows.
    /// A head with no such probe passes `false`; the machine still consults its
    /// own [`PttMachineState::key_down`], so a key-down that arrived inside the
    /// debounce window is honoured either way.
    KeyUpDebounceTimeout { key_physically_held: bool },
    /// Another key was pressed while the shortcut was held, so this was a
    /// keyboard shortcut and not push-to-talk.
    Interference,
    /// Tear the recording down because the head knows the key was released but
    /// never saw the release.
    ///
    /// macOS raises this after its CGEventTap is re-enabled: any modifier key-up
    /// delivered while the tap was disabled is dropped for good, and without
    /// this the machine stays in [`PttState::PttActive`] with the microphone
    /// open forever (issue #300).
    ///
    /// It is a distinct event on purpose. Synthesising a [`PttEvent::KeyUp`]
    /// instead would enter the quick-tap branch and could **latch** rather than
    /// stop — see the comment at `BareModifierKeyMonitor.swift:495`.
    ForceStop,
    /// Full reset: configuration changed, the monitor is starting or stopping.
    Reset,
    /// Recording was stopped by something other than this machine — a cancel
    /// shortcut, an error, the auto-paste suspension.
    ///
    /// Handled identically to [`PttEvent::Reset`]; kept separate so the head's
    /// two call sites stay legible and so the breadcrumb reads correctly. macOS
    /// and Windows used to ignore this from
    /// [`PttState::WaitingForActivation`], which left the activation timer armed
    /// to start a recording nobody asked for; Linux's full cancel is now the
    /// shared behaviour.
    ResetToIdle,
}

/// What the head must do to the recording.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PttSignal {
    /// Start recording (`onModifierDown` / `Pressed`).
    StartRecording,
    /// Stop recording (`onModifierUp` / `Released`).
    StopRecording,
    /// Cancel: the key was part of a keyboard shortcut (`onInterferenceDetected`
    /// / `Interfered`). Any audio captured so far is discarded by the head.
    Interfered,
}

/// The three timers. The head supplies the primitive; the machine only ever
/// names one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PttTimer {
    /// Activation delay; fires [`PttEvent::ActivationTimeout`].
    Activation,
    /// Double-press window; fires [`PttEvent::LatchTimeout`]. Does double duty
    /// as the lock timeout (in [`PttState::PttActive`]) and the unlock timeout
    /// (in [`PttState::UnlatchPending`]).
    Latch,
    /// Key-up debounce; fires [`PttEvent::KeyUpDebounceTimeout`].
    KeyUpDebounce,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PttTimerAction {
    Start,
    Cancel,
}

/// One timer instruction. `delay_ms` is meaningless for
/// [`PttTimerAction::Cancel`] and is reported as `0`.
///
/// Starting a timer that is already running replaces it, and cancelling one that
/// is not running is a no-op — that is how all three heads already behaved.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PttTimerCommand {
    pub timer: PttTimer,
    pub action: PttTimerAction,
    pub delay_ms: u64,
}

/// Why the machine did what it did. Carried purely so the heads can log and
/// report without inspecting the transition table themselves; macOS keeps its
/// Sentry breadcrumbs native and reads its payloads off
/// [`PttStepResult::transition`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PttReason {
    /// The event did not apply in this state.
    Ignored,
    /// Key went down from idle; the activation delay is now running.
    ActivationArmed,
    /// Held past the activation delay — recording starts.
    HoldActivated,
    /// A key-up was seen; the debounce is running before it is committed.
    ReleasePending,
    /// The debounce elapsed and the key turned out to be still held. The key-up
    /// was spurious and has been discarded.
    SpuriousKeyUpIgnored,
    /// A hold was released — recording stops. Never latches, on any platform.
    HoldReleaseStopped,
    /// Released before the activation delay elapsed: the first tap of a possible
    /// double-tap lock. Recording starts.
    QuickTapStarted,
    /// Released before the activation delay elapsed with double-tap lock turned
    /// off, so nothing happens at all.
    QuickTapDiscarded,
    /// Key came back down while recording; the auto-stop timeout is cancelled.
    Reacquired,
    /// Second tap released inside the double-press window — recording locks.
    DoubleTapLocked,
    /// Quick-tap recording released too late to lock — recording stops.
    SingleTapStopped,
    /// No second tap arrived inside the double-press window — recording stops.
    LatchTimeoutStopped,
    /// A key-down arrived too soon after locking to be believable. Ignored.
    BounceProtected,
    /// A key-down was accepted as the start of the unlock sequence.
    UnlockArmed,
    /// First tap of the unlock sequence released; waiting for the second.
    UnlockFirstTap,
    /// Second unlock tap released inside the window — recording stops.
    UnlockConfirmed,
    /// Second unlock tap released outside the window — stays locked.
    UnlockTooSlow,
    /// No second unlock tap arrived — stays locked.
    UnlockTimedOut,
    /// A key press proved the shortcut was a keyboard shortcut, before recording
    /// began.
    InterferenceCancelled,
    /// The same, but recording had already begun and is now discarded.
    InterferenceStopped,
    /// A release the head never saw has been reconciled; the recording is torn
    /// down deterministically.
    ForceStopped,
    /// The head reset the machine.
    ExternalReset,
}

/// What changed, for logs and telemetry.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PttTransition {
    pub from: PttState,
    pub to: PttState,
    pub reason: PttReason,
    /// The interval the decision turned on, when there was one: time since the
    /// first tap for the tap reasons, time since the lock for
    /// [`PttReason::BounceProtected`] and [`PttReason::UnlockArmed`].
    pub elapsed_ms: Option<u64>,
}

/// The next state and everything the head must do about it.
///
/// At most one [`PttSignal`] is produced per step — no transition in the table
/// both starts and stops a recording.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PttStepResult {
    pub state: PttMachineState,
    pub signal: Option<PttSignal>,
    pub timers: Vec<PttTimerCommand>,
    /// `Some(true)` to start watching for interfering key presses, `Some(false)`
    /// to stop, `None` to leave the head's current arming alone.
    ///
    /// Linux performed this inside its reducer at nearly every transition. It is
    /// reported here so the Linux wrapper does not have to re-derive the
    /// transition table to know when to arm — which is the duplication this
    /// crate exists to delete. macOS and Windows watch unconditionally and
    /// ignore the field.
    pub arm_interference: Option<bool>,
    /// Clear any keyboard state the head is holding on the machine's behalf.
    /// Linux calls `IGlobalShortcutService.ResetKeyboardState()`; the other two
    /// heads have no equivalent and ignore it.
    pub reset_keyboard_state: bool,
    pub transition: PttTransition,
}

/// Advance the push-to-talk machine by one event.
///
/// `now_ms` must be a monotonic reading — see the module note on the clock.
pub fn ptt_step(
    state: PttMachineState,
    event: PttEvent,
    now_ms: u64,
    config: PttConfig,
) -> PttStepResult {
    let mut step = Step::new(state);

    match event {
        PttEvent::KeyDown => step.key_down(now_ms, &config),
        PttEvent::KeyUp => step.key_up(now_ms, &config),
        PttEvent::ActivationTimeout => step.activation_timeout(now_ms),
        PttEvent::LatchTimeout => step.latch_timeout(now_ms),
        PttEvent::KeyUpDebounceTimeout {
            key_physically_held,
        } => step.commit_key_up(now_ms, &config, key_physically_held),
        PttEvent::Interference => step.interference(),
        PttEvent::ForceStop => step.force_stop(),
        PttEvent::Reset | PttEvent::ResetToIdle => {
            step.full_reset();
            step.reason = PttReason::ExternalReset;
        }
    }

    step.finish()
}

// ===========================================================================
// Reducer
// ===========================================================================

/// Accumulates the effects of one step. Nothing here is public: the head sees
/// only [`PttStepResult`].
struct Step {
    st: PttMachineState,
    from: PttState,
    signal: Option<PttSignal>,
    timers: Vec<PttTimerCommand>,
    arm_interference: Option<bool>,
    reset_keyboard_state: bool,
    reason: PttReason,
    elapsed_ms: Option<u64>,
}

impl Step {
    fn new(st: PttMachineState) -> Self {
        Self {
            st,
            from: st.state,
            signal: None,
            timers: Vec::new(),
            arm_interference: None,
            reset_keyboard_state: false,
            reason: PttReason::Ignored,
            elapsed_ms: None,
        }
    }

    fn finish(self) -> PttStepResult {
        PttStepResult {
            state: self.st,
            signal: self.signal,
            timers: self.timers,
            arm_interference: self.arm_interference,
            reset_keyboard_state: self.reset_keyboard_state,
            transition: PttTransition {
                from: self.from,
                to: self.st.state,
                reason: self.reason,
                elapsed_ms: self.elapsed_ms,
            },
        }
    }

    // --- effect helpers ---------------------------------------------------

    fn start_timer(&mut self, timer: PttTimer, delay_ms: u64) {
        self.timers.push(PttTimerCommand {
            timer,
            action: PttTimerAction::Start,
            delay_ms,
        });
    }

    fn cancel_timer(&mut self, timer: PttTimer) {
        self.timers.push(PttTimerCommand {
            timer,
            action: PttTimerAction::Cancel,
            delay_ms: 0,
        });
    }

    /// Cancel every timer, drop every timestamp and go idle. This is the shared
    /// body of `Reset`, `ResetToIdle`, `Interference` and `ForceStop` — Linux's
    /// `ResetState()`, including the keyboard-state clear the other two heads
    /// ignore.
    fn full_reset(&mut self) {
        self.cancel_timer(PttTimer::Activation);
        self.cancel_timer(PttTimer::Latch);
        self.cancel_timer(PttTimer::KeyUpDebounce);
        self.st = PttMachineState::idle();
        self.arm_interference = Some(false);
        self.reset_keyboard_state = true;
    }

    // --- events -----------------------------------------------------------

    fn key_down(&mut self, now_ms: u64, config: &PttConfig) {
        self.st.key_down = true;

        match self.from {
            PttState::Idle => {
                self.st.state = PttState::WaitingForActivation;
                self.arm_interference = Some(true);
                self.start_timer(PttTimer::Activation, config.activation_delay_ms);
                self.reason = PttReason::ActivationArmed;
            }
            // Already counting down; a repeat key-down changes nothing.
            PttState::WaitingForActivation => {}
            PttState::PttActive => {
                // Either wireless bounce inside the debounce window or the user
                // genuinely pressing again. Either way the key is down, so drop
                // the pending release and the auto-stop timeout and keep
                // recording.
                self.cancel_timer(PttTimer::KeyUpDebounce);
                self.cancel_timer(PttTimer::Latch);
                self.reason = PttReason::Reacquired;
            }
            PttState::LatchActive => {
                let since_lock = self.st.last_lock_ms.map(|t| now_ms.saturating_sub(t));
                self.elapsed_ms = since_lock;
                if since_lock.is_none_or(|d| d >= config.minimum_lock_ms) {
                    self.st.state = PttState::UnlatchPending;
                    // Sentinel: the unlock window is measured key-up to key-up,
                    // symmetrically with the lock, so the time is set on the
                    // first release and not here.
                    self.st.first_tap_ms = None;
                    self.reason = PttReason::UnlockArmed;
                } else {
                    self.reason = PttReason::BounceProtected;
                }
            }
            // The unlock sequence tracks releases, not presses.
            PttState::UnlatchPending => {}
        }
    }

    fn key_up(&mut self, now_ms: u64, config: &PttConfig) {
        self.st.key_down = false;

        match self.from {
            PttState::Idle => {}
            PttState::WaitingForActivation => {
                self.cancel_timer(PttTimer::Activation);
                self.debounce_or_commit(now_ms, config);
            }
            PttState::PttActive => {
                if self.st.entered_via_hold {
                    // A hold always stops on release, whatever the double-press
                    // window says. macOS used to latch here.
                    self.debounce_or_commit(now_ms, config);
                } else {
                    self.cancel_timer(PttTimer::Latch);
                    let since_first = self.st.first_tap_ms.map(|t| now_ms.saturating_sub(t));
                    self.elapsed_ms = since_first;

                    let locks = config.double_press_lock
                        && since_first.is_some_and(|d| d <= config.double_press_window_ms);
                    if locks {
                        self.st.state = PttState::LatchActive;
                        self.st.last_lock_ms = Some(now_ms);
                        self.arm_interference = Some(false);
                        self.reason = PttReason::DoubleTapLocked;
                    } else {
                        self.st.state = PttState::Idle;
                        self.arm_interference = Some(false);
                        self.signal = Some(PttSignal::StopRecording);
                        self.reason = PttReason::SingleTapStopped;
                    }
                }
            }
            // Locked recording ignores releases; only the unlock sequence stops it.
            PttState::LatchActive => {}
            PttState::UnlatchPending => match self.st.first_tap_ms {
                None => {
                    self.st.first_tap_ms = Some(now_ms);
                    self.start_timer(PttTimer::Latch, config.double_press_window_ms);
                    self.reason = PttReason::UnlockFirstTap;
                }
                Some(first) => {
                    let since_first = now_ms.saturating_sub(first);
                    self.elapsed_ms = Some(since_first);
                    self.cancel_timer(PttTimer::Latch);
                    if since_first <= config.double_press_window_ms {
                        self.st.state = PttState::Idle;
                        self.signal = Some(PttSignal::StopRecording);
                        self.reason = PttReason::UnlockConfirmed;
                    } else {
                        self.st.state = PttState::LatchActive;
                        self.reason = PttReason::UnlockTooSlow;
                    }
                }
            },
        }
    }

    /// Either arm the debounce timer or, when the head has no debounce, commit
    /// the release straight away.
    fn debounce_or_commit(&mut self, now_ms: u64, config: &PttConfig) {
        if config.key_up_debounce_ms > 0 {
            self.start_timer(PttTimer::KeyUpDebounce, config.key_up_debounce_ms);
            self.reason = PttReason::ReleasePending;
        } else {
            self.commit_key_up(now_ms, config, false);
        }
    }

    /// Resolve a pending release. Shared by [`PttEvent::KeyUpDebounceTimeout`]
    /// and by the zero-debounce path above.
    fn commit_key_up(&mut self, now_ms: u64, config: &PttConfig, key_physically_held: bool) {
        if self.st.key_down || key_physically_held {
            // The release was spurious. Repair the record from the hardware
            // reading before restarting the activation delay, or the activation
            // timeout would find `key_down == false` and refuse to start.
            self.st.key_down = true;
            if self.st.state == PttState::WaitingForActivation {
                self.start_timer(PttTimer::Activation, config.activation_delay_ms);
            }
            self.reason = PttReason::SpuriousKeyUpIgnored;
            return;
        }

        match self.st.state {
            PttState::PttActive if self.st.entered_via_hold => {
                self.elapsed_ms = self.st.first_tap_ms.map(|t| now_ms.saturating_sub(t));
                self.st.state = PttState::Idle;
                self.arm_interference = Some(false);
                self.signal = Some(PttSignal::StopRecording);
                self.reason = PttReason::HoldReleaseStopped;
            }
            PttState::WaitingForActivation => {
                if config.double_press_lock {
                    self.st.state = PttState::PttActive;
                    self.st.entered_via_hold = false;
                    self.st.first_tap_ms = Some(now_ms);
                    self.signal = Some(PttSignal::StartRecording);
                    self.start_timer(PttTimer::Latch, config.double_press_window_ms);
                    self.reason = PttReason::QuickTapStarted;
                } else {
                    self.st.state = PttState::Idle;
                    self.arm_interference = Some(false);
                    self.reason = PttReason::QuickTapDiscarded;
                }
            }
            _ => {}
        }
    }

    fn activation_timeout(&mut self, now_ms: u64) {
        if self.from != PttState::WaitingForActivation || !self.st.key_down {
            return;
        }
        self.st.state = PttState::PttActive;
        self.st.entered_via_hold = true;
        self.st.first_tap_ms = Some(now_ms);
        self.signal = Some(PttSignal::StartRecording);
        self.reason = PttReason::HoldActivated;
    }

    fn latch_timeout(&mut self, now_ms: u64) {
        self.elapsed_ms = self.st.first_tap_ms.map(|t| now_ms.saturating_sub(t));
        match self.from {
            PttState::PttActive => {
                self.st.state = PttState::Idle;
                self.arm_interference = Some(false);
                self.signal = Some(PttSignal::StopRecording);
                self.reason = PttReason::LatchTimeoutStopped;
            }
            PttState::UnlatchPending => {
                self.st.state = PttState::LatchActive;
                self.reason = PttReason::UnlockTimedOut;
            }
            _ => self.elapsed_ms = None,
        }
    }

    fn interference(&mut self) {
        // A locked recording is hands-free by definition, so other keys are
        // expected and must not cancel it.
        if !matches!(
            self.from,
            PttState::WaitingForActivation | PttState::PttActive
        ) {
            return;
        }
        self.full_reset();
        self.signal = Some(PttSignal::Interfered);
        self.reason = if self.from == PttState::WaitingForActivation {
            PttReason::InterferenceCancelled
        } else {
            PttReason::InterferenceStopped
        };
    }

    fn force_stop(&mut self) {
        // Only the hold-to-talk states can be left stuck by a dropped release.
        // In a locked recording, releasing the modifier is expected.
        if !matches!(
            self.from,
            PttState::WaitingForActivation | PttState::PttActive
        ) {
            return;
        }
        let was_recording = self.from == PttState::PttActive;
        self.full_reset();
        if was_recording {
            self.signal = Some(PttSignal::StopRecording);
        }
        self.reason = PttReason::ForceStopped;
    }
}

#[cfg(test)]
mod tests;
