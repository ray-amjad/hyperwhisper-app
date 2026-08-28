//! Behavioural tests for [`ptt_step`].
//!
//! The first six are ports of the six push-to-talk tests in
//! `app/linux/HyperWhisper.Linux.Platform.Tests/Program.cs`, which were the only
//! well-tested copy of this machine. They keep the original names so the two
//! suites stay traceable to each other.
//!
//! Two of the six — `PushToTalkSubscriberIsolation` and
//! `PushToTalkInterferencePrivacy` — test the wrapper rather than the machine
//! (that one bad event subscriber cannot starve another, and that an evdev frame
//! never reaches a callback as key data). Those halves cannot move into a pure
//! function and stay in the Linux suite; what is ported here is the machine's
//! share of each.

use super::*;

use std::collections::HashMap;

// ===========================================================================
// Harness
// ===========================================================================

/// The Linux suite's `FakePushToTalkScheduler`, rebuilt around [`ptt_step`]: a
/// virtual clock, the three timers the reducer can ask for, and counters for the
/// three signals.
struct Driver {
    now: u64,
    state: PttMachineState,
    config: PttConfig,
    deadlines: HashMap<u8, u64>,
    pressed: usize,
    released: usize,
    interfered: usize,
    interference_armed: bool,
    keyboard_resets: usize,
    /// What the head's hardware probe would report at the moment a debounce
    /// timer fires. `GetAsyncKeyState` on Windows; always `false` on Linux.
    physically_held: bool,
    last_transition: Option<PttTransition>,
}

fn timer_key(timer: PttTimer) -> u8 {
    match timer {
        PttTimer::Activation => 0,
        PttTimer::Latch => 1,
        PttTimer::KeyUpDebounce => 2,
    }
}

fn timer_event(key: u8, physically_held: bool) -> PttEvent {
    match key {
        0 => PttEvent::ActivationTimeout,
        1 => PttEvent::LatchTimeout,
        _ => PttEvent::KeyUpDebounceTimeout {
            key_physically_held: physically_held,
        },
    }
}

impl Driver {
    /// Windows and Linux: 2000 ms minimum lock, 100 ms key-up debounce.
    fn linux(double_press_lock: bool) -> Self {
        Self::new(PttConfig::new(2000, 100, double_press_lock))
    }

    /// macOS: 1000 ms minimum lock, no key-up debounce.
    fn macos(double_press_lock: bool) -> Self {
        Self::new(PttConfig::new(1000, 0, double_press_lock))
    }

    fn new(config: PttConfig) -> Self {
        Self {
            now: 0,
            state: PttMachineState::idle(),
            config,
            deadlines: HashMap::new(),
            pressed: 0,
            released: 0,
            interfered: 0,
            interference_armed: false,
            keyboard_resets: 0,
            physically_held: false,
            last_transition: None,
        }
    }

    fn send(&mut self, event: PttEvent) -> PttStepResult {
        let result = ptt_step(self.state, event, self.now, self.config);
        self.state = result.state;
        self.last_transition = Some(result.transition);

        for command in &result.timers {
            let key = timer_key(command.timer);
            match command.action {
                PttTimerAction::Start => {
                    self.deadlines.insert(key, self.now + command.delay_ms);
                }
                PttTimerAction::Cancel => {
                    self.deadlines.remove(&key);
                }
            }
        }
        if let Some(armed) = result.arm_interference {
            self.interference_armed = armed;
        }
        if result.reset_keyboard_state {
            self.keyboard_resets += 1;
        }
        match result.signal {
            Some(PttSignal::StartRecording) => self.pressed += 1,
            Some(PttSignal::StopRecording) => self.released += 1,
            Some(PttSignal::Interfered) => self.interfered += 1,
            None => {}
        }
        result
    }

    fn press(&mut self) {
        self.send(PttEvent::KeyDown);
    }

    fn release(&mut self) {
        self.send(PttEvent::KeyUp);
    }

    /// Move the clock forward, firing every timer that comes due on the way — in
    /// time order, at its own deadline, so a timer armed by an earlier one still
    /// fires inside the same advance.
    fn advance(&mut self, delta_ms: u64) {
        let target = self.now + delta_ms;
        loop {
            let due = self
                .deadlines
                .iter()
                .filter(|(_, &at)| at <= target)
                .min_by_key(|(key, &at)| (at, **key))
                .map(|(&key, &at)| (key, at));

            let Some((key, at)) = due else { break };
            self.deadlines.remove(&key);
            self.now = at;
            self.send(timer_event(key, self.physically_held));
        }
        self.now = target;
    }

    fn reason(&self) -> PttReason {
        self.last_transition.expect("no step taken yet").reason
    }
}

// ===========================================================================
// Ports of the six Linux behavioural tests
// ===========================================================================

/// `PushToTalkPrivacy` — the machine's share: nothing happens until the
/// activation delay has fully elapsed, and the release then stops the recording
/// once the debounce has run.
///
/// The privacy half of the original (only the one configured logical action is
/// ever registered or emitted) is a property of `LinuxGlobalShortcutService` and
/// stays in the Linux suite. It is structurally out of reach here: [`PttEvent`]
/// carries no key identity at all, so the machine cannot leak one.
#[test]
fn push_to_talk_privacy() {
    let mut d = Driver::linux(false);

    d.press();
    d.advance(249);
    assert_eq!(d.pressed, 0);

    d.advance(1);
    assert_eq!(d.pressed, 1);

    d.release();
    d.advance(100);
    assert_eq!((d.pressed, d.released), (1, 1));
    assert_eq!(d.state.state, PttState::Idle);
}

/// `PushToTalkDoubleLock` — double-press latches, the post-lock bounce window
/// swallows an immediate unlock attempt, and a later double-press unlatches.
#[test]
fn push_to_talk_double_lock() {
    let mut d = Driver::linux(true);

    // Quick tap: recording starts once the debounce confirms the release.
    d.press();
    d.release();
    d.advance(100);

    // Second tap inside the window locks.
    d.press();
    d.release();
    assert_eq!((d.pressed, d.released), (1, 0));
    assert_eq!(d.state.state, PttState::LatchActive);

    // Two full taps immediately after locking are ignored: bounce protection.
    d.press();
    d.release();
    d.press();
    d.release();
    assert_eq!(d.released, 0);
    assert_eq!(d.state.state, PttState::LatchActive);

    // Past the 2000 ms lock floor, the same two taps unlatch and stop.
    d.advance(2000);
    d.press();
    d.release();
    d.press();
    d.release();
    assert_eq!(d.released, 1);
    assert_eq!(d.state.state, PttState::Idle);

    // A fresh quick tap with no second tap stops on the latch timeout.
    d.press();
    d.release();
    d.advance(100);
    d.advance(1500);
    assert_eq!((d.pressed, d.released), (2, 2));
    assert_eq!(d.state.state, PttState::Idle);
}

/// `PushToTalkHoldDebounce` — a hold activates, a spurious mid-hold release is
/// swallowed by the debounce, the real release stops, and a quick tap with the
/// lock turned off does nothing at all.
#[test]
fn push_to_talk_hold_debounce() {
    let mut d = Driver::linux(false);

    d.press();
    d.advance(250);
    assert_eq!(d.pressed, 1);

    // Spurious release: the key comes back before the debounce elapses.
    d.release();
    d.advance(50);
    d.press();
    d.advance(100);
    assert_eq!(d.released, 0);
    assert_eq!(d.state.state, PttState::PttActive);

    // Real release.
    d.release();
    d.advance(100);
    assert_eq!(d.released, 1);

    // Quick tap without the lock enabled is silent.
    d.press();
    d.release();
    d.advance(100);
    assert_eq!((d.pressed, d.released), (1, 1));
    assert_eq!(d.reason(), PttReason::QuickTapDiscarded);
}

/// `PushToTalkSubscriberIsolation` — the machine's share.
///
/// The original asserts that a subscriber which throws cannot stop the next
/// subscriber from running. A pure step function cannot have that fault: the
/// decision to start recording is returned as a value, taken before any head
/// code runs, and is unaffected by what the head then does with it. Both heads'
/// dispatch loops stay covered by their own suites.
#[test]
fn push_to_talk_subscriber_isolation() {
    let mut d = Driver::linux(false);

    d.press();
    let result = {
        d.now += 250;
        ptt_step(d.state, PttEvent::ActivationTimeout, d.now, d.config)
    };

    assert_eq!(result.signal, Some(PttSignal::StartRecording));
    // Same input, same answer — the signal is data, not a callback.
    let again = ptt_step(d.state, PttEvent::ActivationTimeout, d.now, d.config);
    assert_eq!(again.signal, result.signal);
    assert_eq!(again.state, result.state);
}

/// `PushToTalkInterferencePrivacy` — the machine's share: interference produces
/// the cancel signal and carries no key data.
///
/// The original drives two real evdev frames through `LinuxGlobalShortcutService`
/// to prove the interfering key never reaches a callback. That is an event-source
/// property and stays in the Linux suite; here it holds by construction, because
/// [`PttEvent::Interference`] has no fields.
#[test]
fn push_to_talk_interference_privacy() {
    let mut d = Driver::linux(false);

    d.press();
    let result = d.send(PttEvent::Interference);

    assert_eq!(result.signal, Some(PttSignal::Interfered));
    assert_eq!(d.interfered, 1);
    assert_eq!(d.state, PttMachineState::idle());
    assert_eq!(result.transition.reason, PttReason::InterferenceCancelled);
    // Linux clears the shortcut service's keyboard state inside its reducer;
    // the effect is reported rather than performed.
    assert!(result.reset_keyboard_state);
}

/// `PushToTalkActiveInterference` — interference while recording stops it, and
/// the interference watch is armed on the way in and disarmed on the way out.
#[test]
fn push_to_talk_active_interference() {
    let mut d = Driver::linux(true);

    d.press();
    d.release();
    d.advance(100);
    assert_eq!(d.pressed, 1);
    assert!(d.interference_armed);

    d.send(PttEvent::Interference);
    assert_eq!(d.interfered, 1);
    assert!(!d.interference_armed);
    assert_eq!(d.reason(), PttReason::InterferenceStopped);

    // The latch timeout the quick tap armed was cancelled with everything else.
    d.advance(1500);
    assert_eq!(d.released, 0);
    assert_eq!(d.state.state, PttState::Idle);
}

// ===========================================================================
// The two behaviours this change deliberately unifies
// ===========================================================================

/// Unified decision 1: a hold past the activation delay, released inside the
/// double-press window, STOPS. It must never latch.
///
/// macOS had no `enteredViaHold` flag, so this exact sequence latched there and
/// stopped on Windows and Linux. Asserted under the macOS config, which is where
/// the behaviour changes.
#[test]
fn hold_released_inside_double_press_window_stops_and_never_latches() {
    let mut d = Driver::macos(true);

    d.press();
    d.advance(250);
    assert_eq!(d.pressed, 1);
    assert!(d.state.entered_via_hold);

    // 300 ms of hold, well inside the 1500 ms window that used to latch.
    d.advance(300);
    d.release();

    assert_eq!(d.released, 1);
    assert_eq!(d.state.state, PttState::Idle);
    assert_eq!(d.reason(), PttReason::HoldReleaseStopped);
}

/// The quick-tap path still latches — the fix above must not take hands-free
/// recording with it.
#[test]
fn quick_tap_released_inside_double_press_window_still_latches() {
    let mut d = Driver::macos(true);

    d.press();
    d.release();
    assert_eq!(d.pressed, 1);
    assert!(!d.state.entered_via_hold);

    d.advance(300);
    d.press();
    d.release();

    assert_eq!(d.released, 0);
    assert_eq!(d.state.state, PttState::LatchActive);
    assert_eq!(d.reason(), PttReason::DoubleTapLocked);
}

/// Unified decision 2: `ResetToIdle` from `WaitingForActivation` is a full
/// cancel.
///
/// macOS and Windows returned early from that state, leaving the activation
/// timer armed. It would then fire and start a recording after the app had
/// already decided to stop.
#[test]
fn reset_to_idle_from_waiting_for_activation_cancels_the_armed_timer() {
    let mut d = Driver::linux(false);

    d.press();
    assert_eq!(d.state.state, PttState::WaitingForActivation);

    let result = d.send(PttEvent::ResetToIdle);
    assert!(result.timers.contains(&PttTimerCommand {
        timer: PttTimer::Activation,
        action: PttTimerAction::Cancel,
        delay_ms: 0,
    }));

    // Nothing left to fire: no orphaned recording start.
    d.advance(1000);
    assert_eq!(d.pressed, 0);
    assert_eq!(d.state, PttMachineState::idle());
}

#[test]
fn reset_to_idle_from_every_recording_state_goes_idle() {
    for state in [
        PttState::PttActive,
        PttState::LatchActive,
        PttState::UnlatchPending,
    ] {
        let machine = PttMachineState {
            state,
            key_down: true,
            entered_via_hold: true,
            first_tap_ms: Some(10),
            last_lock_ms: Some(20),
        };
        let result = ptt_step(
            machine,
            PttEvent::ResetToIdle,
            5_000,
            PttConfig::new(2000, 100, true),
        );
        assert_eq!(result.state, PttMachineState::idle(), "from {state:?}");
        // Resetting is not a stop — the head already stopped the recording, which
        // is why it is calling this at all.
        assert_eq!(result.signal, None, "from {state:?}");
        assert_eq!(result.arm_interference, Some(false), "from {state:?}");
        assert!(result.reset_keyboard_state, "from {state:?}");
    }
}

// ===========================================================================
// ForceStop — the tap-re-enable reconcile (issue #300)
// ===========================================================================

/// `ForceStop` from `PttActive` stops the recording. Feeding a synthesised
/// `KeyUp` instead would take the quick-tap branch and could latch, which is the
/// stuck-microphone bug.
#[test]
fn force_stop_while_recording_stops_and_never_latches() {
    let mut d = Driver::macos(true);

    d.press();
    d.release(); // quick tap: entered_via_hold == false, the latching branch
    assert_eq!(d.pressed, 1);

    d.send(PttEvent::ForceStop);

    assert_eq!(d.released, 1);
    assert_eq!(d.state.state, PttState::Idle);
    assert_eq!(d.reason(), PttReason::ForceStopped);
}

/// The same sequence with a synthesised release latches instead of stopping —
/// the regression `ForceStop` exists to prevent, pinned so the two paths cannot
/// silently converge.
#[test]
fn synthesised_key_up_would_latch_where_force_stop_stops() {
    let mut d = Driver::macos(true);

    d.press();
    d.release();
    d.send(PttEvent::KeyUp);

    assert_eq!(d.released, 0);
    assert_eq!(d.state.state, PttState::LatchActive);
}

/// `ForceStop` before recording began cancels quietly: there is nothing to stop.
#[test]
fn force_stop_during_activation_cancels_without_stopping() {
    let mut d = Driver::macos(false);

    d.press();
    let result = d.send(PttEvent::ForceStop);

    assert_eq!(result.signal, None);
    assert_eq!(d.state, PttMachineState::idle());
    d.advance(1000);
    assert_eq!(d.pressed, 0);
}

/// A locked recording is hands-free, so a released modifier is expected and
/// `ForceStop` must leave it alone.
#[test]
fn force_stop_in_latch_active_is_ignored() {
    let mut d = Driver::macos(true);

    d.press();
    d.release();
    d.advance(300);
    d.press();
    d.release();
    assert_eq!(d.state.state, PttState::LatchActive);

    let result = d.send(PttEvent::ForceStop);

    assert_eq!(result.signal, None);
    assert_eq!(d.state.state, PttState::LatchActive);
    assert_eq!(result.transition.reason, PttReason::Ignored);
}

// ===========================================================================
// Per-platform config, and the clock
// ===========================================================================

/// The minimum-lock floor comes from the config, so macOS keeps 1000 ms while
/// Windows and Linux keep 2000 ms. Unifying the two is a separate decision.
#[test]
fn bounce_protection_uses_the_configured_minimum_lock() {
    let locked = PttMachineState {
        state: PttState::LatchActive,
        key_down: false,
        entered_via_hold: false,
        first_tap_ms: None,
        last_lock_ms: Some(0),
    };

    let macos = ptt_step(
        locked,
        PttEvent::KeyDown,
        1_200,
        PttConfig::new(1000, 0, true),
    );
    assert_eq!(macos.state.state, PttState::UnlatchPending);
    assert_eq!(macos.transition.reason, PttReason::UnlockArmed);

    let linux = ptt_step(
        locked,
        PttEvent::KeyDown,
        1_200,
        PttConfig::new(2000, 100, true),
    );
    assert_eq!(linux.state.state, PttState::LatchActive);
    assert_eq!(linux.transition.reason, PttReason::BounceProtected);
    assert_eq!(linux.transition.elapsed_ms, Some(1_200));
}

/// With `key_up_debounce_ms == 0` the release commits in the same step — no
/// debounce timer is asked for. That is macOS's shipped behaviour.
#[test]
fn zero_debounce_commits_the_release_in_one_step() {
    let mut d = Driver::macos(false);

    d.press();
    d.advance(250);
    let result = d.send(PttEvent::KeyUp);

    assert_eq!(result.signal, Some(PttSignal::StopRecording));
    assert!(!result
        .timers
        .iter()
        .any(|c| c.timer == PttTimer::KeyUpDebounce && c.action == PttTimerAction::Start));
}

/// Windows resolves a debounced release against `GetAsyncKeyState`. When the
/// hardware says the key is still held, the release is discarded and the
/// activation delay restarts — including repairing `key_down`, or the restarted
/// activation timeout would find the key up and refuse to start.
#[test]
fn hardware_held_key_discards_a_spurious_release_during_activation() {
    let mut d = Driver::linux(false);
    d.press();

    // The head loses the key-down that came back inside the debounce window, so
    // only the hardware probe knows the key is held.
    d.state.key_down = false;
    d.release();
    d.physically_held = true;
    d.advance(100);

    assert_eq!(d.reason(), PttReason::SpuriousKeyUpIgnored);
    assert!(d.state.key_down);
    assert_eq!(d.state.state, PttState::WaitingForActivation);

    d.physically_held = false;
    d.advance(250);
    assert_eq!(d.pressed, 1);
    assert!(d.state.entered_via_hold);
}

/// The activation timeout must not start a recording for a key that is no longer
/// held — a stale timer that outran its cancel.
#[test]
fn activation_timeout_with_the_key_up_starts_nothing() {
    let machine = PttMachineState {
        state: PttState::WaitingForActivation,
        key_down: false,
        ..PttMachineState::idle()
    };
    let result = ptt_step(
        machine,
        PttEvent::ActivationTimeout,
        500,
        PttConfig::new(2000, 100, false),
    );

    assert_eq!(result.signal, None);
    assert_eq!(result.state.state, PttState::WaitingForActivation);
    assert_eq!(result.transition.reason, PttReason::Ignored);
}

/// `now_ms` is documented as monotonic, but a head that ever hands back a
/// smaller reading must not wrap an interval into ~584 million years and defeat
/// bounce protection. All three heads used wall clock before this change, so an
/// NTP step could do exactly that.
#[test]
fn a_clock_that_goes_backwards_does_not_wrap_an_interval() {
    let locked = PttMachineState {
        state: PttState::LatchActive,
        key_down: false,
        entered_via_hold: false,
        first_tap_ms: None,
        last_lock_ms: Some(10_000),
    };

    // 5 s earlier than the lock.
    let result = ptt_step(
        locked,
        PttEvent::KeyDown,
        5_000,
        PttConfig::new(2000, 100, true),
    );

    assert_eq!(result.transition.elapsed_ms, Some(0));
    assert_eq!(result.state.state, PttState::LatchActive);
    assert_eq!(result.transition.reason, PttReason::BounceProtected);
}

// ===========================================================================
// Interference arming — the effect Linux used to perform inside its reducer
// ===========================================================================

#[test]
fn interference_arming_follows_the_recording() {
    let mut d = Driver::linux(true);

    // Armed as soon as the key goes down.
    let down = d.send(PttEvent::KeyDown);
    assert_eq!(down.arm_interference, Some(true));

    // Quick tap into recording leaves it armed.
    d.release();
    d.advance(100);
    assert!(d.interference_armed);

    // Locking disarms: hands-free recording expects other keys.
    d.press();
    d.release();
    assert_eq!(d.state.state, PttState::LatchActive);
    assert!(!d.interference_armed);

    // And a locked recording ignores interference outright.
    let while_locked = d.send(PttEvent::Interference);
    assert_eq!(while_locked.signal, None);
    assert_eq!(d.state.state, PttState::LatchActive);
}

/// Stopping always disarms, whichever way the recording ended.
#[test]
fn every_stop_disarms_interference() {
    // Hold release.
    let mut d = Driver::linux(false);
    d.press();
    d.advance(250);
    d.release();
    d.advance(100);
    assert_eq!(d.released, 1);
    assert!(!d.interference_armed);

    // Latch timeout with no second tap.
    let mut d = Driver::linux(true);
    d.press();
    d.release();
    d.advance(100);
    d.advance(1500);
    assert_eq!(d.released, 1);
    assert!(!d.interference_armed);

    // Single tap released too late to lock.
    let mut d = Driver::linux(true);
    d.press();
    d.release();
    d.advance(100);
    d.advance(1400);
    d.press();
    d.advance(200); // 1600 ms since the first tap: past the window
    d.release();
    assert_eq!(d.released, 1);
    assert!(!d.interference_armed);
    assert_eq!(d.reason(), PttReason::SingleTapStopped);
}

// ===========================================================================
// Unlock sequence
// ===========================================================================

/// A too-slow second unlock tap returns to the locked state rather than stopping.
#[test]
fn slow_second_unlock_tap_stays_locked() {
    let mut d = Driver::linux(true);

    d.press();
    d.release();
    d.advance(100);
    d.press();
    d.release();
    assert_eq!(d.state.state, PttState::LatchActive);

    d.advance(2000);
    d.press();
    d.release(); // first unlock tap, arms the 1500 ms window
    assert_eq!(d.state.state, PttState::UnlatchPending);

    // Second tap 1600 ms later, and the window timer has already fired.
    d.advance(1600);
    assert_eq!(d.state.state, PttState::LatchActive);
    assert_eq!(d.reason(), PttReason::UnlockTimedOut);
    assert_eq!(d.released, 0);
}

/// The unlock window is measured release-to-release, symmetrically with the
/// lock, so the sentinel must survive the intervening key-down.
#[test]
fn unlock_window_is_measured_release_to_release() {
    let mut d = Driver::linux(true);

    d.press();
    d.release();
    d.advance(100);
    d.press();
    d.release();
    d.advance(2000);

    d.press();
    assert_eq!(d.state.first_tap_ms, None);
    d.release();
    assert_eq!(d.state.first_tap_ms, Some(d.now));

    d.advance(400);
    d.press();
    assert_eq!(d.state.first_tap_ms, Some(d.now - 400));
    d.release();

    assert_eq!(d.released, 1);
    assert_eq!(d.reason(), PttReason::UnlockConfirmed);
    assert_eq!(d.state.state, PttState::Idle);
}

// ===========================================================================
// Invariants
// ===========================================================================

/// No transition both starts and stops a recording, which is why
/// [`PttStepResult::signal`] is a single optional value.
#[test]
fn every_reachable_step_produces_at_most_one_signal() {
    let states = [
        PttState::Idle,
        PttState::WaitingForActivation,
        PttState::PttActive,
        PttState::LatchActive,
        PttState::UnlatchPending,
    ];
    let events = [
        PttEvent::KeyDown,
        PttEvent::KeyUp,
        PttEvent::ActivationTimeout,
        PttEvent::LatchTimeout,
        PttEvent::KeyUpDebounceTimeout {
            key_physically_held: false,
        },
        PttEvent::KeyUpDebounceTimeout {
            key_physically_held: true,
        },
        PttEvent::Interference,
        PttEvent::ForceStop,
        PttEvent::Reset,
        PttEvent::ResetToIdle,
    ];

    for state in states {
        for key_down in [false, true] {
            for entered_via_hold in [false, true] {
                for first_tap_ms in [None, Some(0), Some(9_000)] {
                    for event in events {
                        for config in [
                            PttConfig::new(1000, 0, true),
                            PttConfig::new(2000, 100, false),
                        ] {
                            let machine = PttMachineState {
                                state,
                                key_down,
                                entered_via_hold,
                                first_tap_ms,
                                last_lock_ms: Some(0),
                            };
                            // Exercises every branch; the assertion is that the
                            // call is total and the result well-formed.
                            let result = ptt_step(machine, event, 5_000, config);
                            assert_eq!(result.transition.from, state);
                            assert_eq!(result.transition.to, result.state.state);
                            if result.transition.reason == PttReason::Ignored {
                                // An ignored event may still record that the key
                                // went down or up — that bookkeeping is what
                                // makes the next event correct — but it must
                                // produce nothing the head has to act on.
                                assert_eq!(result.signal, None);
                                assert_eq!(result.state.state, machine.state);
                                assert!(result.timers.is_empty());
                                assert_eq!(result.arm_interference, None);
                                assert!(!result.reset_keyboard_state);
                                assert_eq!(
                                    PttMachineState {
                                        key_down: machine.key_down,
                                        ..result.state
                                    },
                                    machine
                                );
                            }
                        }
                    }
                }
            }
        }
    }
}

/// The two constants that already agreed across the heads are not settable, so
/// they cannot drift again.
#[test]
fn shared_constants_are_filled_in_by_the_constructor() {
    let config = PttConfig::new(1234, 56, true);
    assert_eq!(config.activation_delay_ms, ACTIVATION_DELAY_MS);
    assert_eq!(config.double_press_window_ms, DOUBLE_PRESS_WINDOW_MS);
    assert_eq!(config.minimum_lock_ms, 1234);
    assert_eq!(config.key_up_debounce_ms, 56);
    assert!(config.double_press_lock);
}
