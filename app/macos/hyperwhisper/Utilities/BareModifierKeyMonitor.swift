import Foundation
import Carbon
import AppKit

// MARK: - CGEventTap Migration Rationale
//
// WHY WE MIGRATED FROM NSEvent TO CGEventTap:
//
// Problem: Users reported that Push-to-Talk (PTT) hotkeys, especially the FN key,
// would not work reliably on some systems. The hotkey would work in other apps but
// not in HyperWhisper.
//
// Root Cause Analysis:
// 1. NSEvent.addGlobalMonitorForEvents is a "read-only" observer - it cannot intercept
//    or modify events, only observe them after other handlers have processed them.
// 2. Apps using CGEventTap (like Karabiner-Elements, BetterTouchTool) receive events
//    BEFORE NSEvent monitors, and can consume events entirely.
// 3. If another app's CGEventTap consumed the modifier key event, our NSEvent monitor
//    would never see it.
//
// Solution: Migrate to CGEventTap with headInsertEventTap placement, which:
// 1. Receives events at higher priority in the event chain
// 2. Can detect when events are being intercepted (tap gets disabled)
// 3. Integrates better with macOS accessibility APIs (CGPreflightListenEventAccess)
// 4. Is the recommended modern approach per Apple documentation
//
// Important Limitations:
// - FN (Globe) key is STILL system-reserved by Apple and may not work reliably
//   regardless of the API used. Apple prioritizes system-level FN handling.
// - Control and Option keys benefit most from this migration as they're not
//   system-reserved and CGEventTap can reliably capture them.
// - Accessibility permission is still required for global event monitoring.
//
// References:
// - https://developer.apple.com/documentation/coregraphics/cgeventtap
// - https://github.com/nikitabobko/AeroSpace/issues/1012
// - https://github.com/keepassxreboot/keepassxc/issues/3393


/// BARE MODIFIER KEY MONITOR (CGEventTap Architecture)
/// Monitors for bare modifier key presses (FN, Control, Option) using CGEventTap.
///
/// This singleton class provides system-wide detection of bare modifier keys for Push to Talk functionality.
/// It uses CGEventTap (low-level API) with an activation delay and interference detection
/// to prevent false triggers when the key is used as part of a shortcut.
///
/// Architecture:
/// - @MainActor: All state access on main thread (eliminates threading bugs)
/// - CGEventTap API: Higher priority than NSEvent, receives events before other observers
/// - 250ms Activation Delay: Prevents keyboard shortcuts (Cmd+C) from triggering PTT
/// - Fn Debouncing: 75ms debounce for reliable Fn key detection
/// - State Machine: SHARED. The five-state transition table lives in the Rust
///   core (`pttStep`, issue #287) and is the same one the Windows and Linux
///   heads run. This class owns the CGEventTap, the Fn debounce, the timers,
///   the monotonic clock, the 100ms audio-engine warm-up on the quick-tap path,
///   and the Sentry breadcrumbs.
///
/// Features:
/// - PTT Mode: Hold to record, release to stop (with 250ms activation delay)
/// - Double Tap Lock: Double tap to lock recording (hands-free mode) - triggers on keyUp
/// - Double Tap Unlock: Double tap to unlock/stop recording - triggers on keyUp (symmetric with lock)
/// - Suspension: Can be temporarily suspended to ignore input (e.g., during auto-paste)
/// - Interference Detection: Detects when other keys are pressed during activation/PTT and cancels recording
///
/// State Machine Flow (owned by the core; reproduced here for orientation):
/// 1. Idle -> Down -> WaitingForActivation (Starts 250ms timer)
/// 2. WaitingForActivation -> Timer Fired -> PTT Active (Start Recording)
/// 3. WaitingForActivation -> Interference (e.g. 'C' pressed) -> Idle (Cancel PTT)
/// 4. WaitingForActivation -> Up (quick tap) -> PTT Active (First tap of lock sequence)
/// 5. PTT Active -> Up, entered via quick tap, within interval -> Latch Active (lock confirmed)
/// 6. Latch Active -> Down -> UnlatchPending (clear firstTapTime)
/// 7. UnlatchPending -> Up (first) -> UnlatchPending (set firstTapTime, start timer)
/// 8. UnlatchPending -> Up (second, within interval) -> Idle + Stop (Double tap unlock confirmed)
/// 9. UnlatchPending -> Timeout -> Latch Active (user didn't complete unlock)
///
/// TWO BEHAVIOUR CHANGES came with the move to the shared table (issue #287):
///
/// 1. A hold past the 250ms activation delay, released inside the 1.5s
///    double-press window, now STOPS. It used to latch into hands-free
///    recording here, and stopped on Windows and Linux. The core tracks how PTT
///    Active was entered (`enteredViaHold`), which this head had no equivalent
///    for.
/// 2. `resetToIdle()` from `waitingForActivation` is now a full cancel. It used
///    to return early and leave the 250ms activation timer armed to start a
///    recording that had already been cancelled.
@MainActor
final class BareModifierKeyMonitor {
    // MARK: - Singleton

    static let shared = BareModifierKeyMonitor()

    // MARK: - Types

    enum ModifierKey: Hashable {
        case fn
        case control
        case leftOption
        case rightOption
    }

    // MARK: - Properties

    /// CGEventTap for monitoring modifier key changes (flagsChanged events)
    /// This replaces the previous NSEvent monitors for more reliable event capture.
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?

    /// The current mode being monitored (fn, control, leftOption, rightOption)
    private var currentMode: ModifierKey?

    /// For combo modes: the set of modifier keys that must ALL be held simultaneously
    private var requiredModifierKeys: Set<ModifierKey>?

    /// Whether the monitor is currently active
    private var isMonitoring = false

    /// Whether we detected the modifier key is currently pressed
    private var isModifierPressed = false

    /// The shared state machine's record. Owned here; the core is a pure
    /// function over it.
    private var machine: HwPttMachineState = pttInitialState()

    /// Timer for double-tap detection and unlatch timeout
    private var latchTimer: Timer?

    /// Timer for initial activation delay (to prevent shortcut triggers)
    private var activationTimer: Timer?

    /// Timer for the core's key-up debounce. Never armed on this head — see
    /// `keyUpDebounceMs` — but wired up so turning the debounce on works.
    private var keyUpDebounceTimer: Timer?

    /// Timer for delayed start in double-tap mode (audio engine initialization)
    private var doubleTapStartTimer: Timer?

    /// Minimum time (ms) to stay locked before allowing the unlock sequence.
    /// Prevents Fn key bounce from immediately triggering unlock after lock.
    /// 1000ms matches Parakeet's minimum transcription duration requirement.
    ///
    /// Windows and Linux both ship 2000ms. The two are deliberately NOT unified
    /// by the move to the shared machine — each platform passes what it ships.
    private static let minimumLockMs: UInt64 = 1_000

    /// This head commits a key-up immediately: it has no key-up debounce, unlike
    /// Windows and Linux (100ms). It debounces the Fn key on BOTH edges instead,
    /// before the event ever reaches the machine — see `handleFnKeyEvent`.
    private static let keyUpDebounceMs: UInt64 = 0

    /// Configuration: Enable double-tap to lock
    var doublePressEnabled: Bool = true {
        didSet { pttConfiguration = Self.makeConfiguration(doublePressEnabled) }
    }

    /// The timing constants the core measures against. The 250ms activation
    /// delay and the 1.5s double-press window are filled in by the core and are
    /// identical on all three platforms; only the two values above are ours.
    private var pttConfiguration: HwPttConfig = BareModifierKeyMonitor.makeConfiguration(true)

    private static func makeConfiguration(_ doublePressLock: Bool) -> HwPttConfig {
        pttConfig(
            minimumLockMs: BareModifierKeyMonitor.minimumLockMs,
            keyUpDebounceMs: BareModifierKeyMonitor.keyUpDebounceMs,
            doublePressLock: doublePressLock
        )
    }

    /// A MONOTONIC millisecond reading. Never `Date()`: this head compared
    /// wall-clock timestamps, so an NTP step during a locked recording could
    /// make the time-since-lock interval negative and defeat bounce protection.
    private var nowMs: UInt64 {
        DispatchTime.now().uptimeNanoseconds / 1_000_000
    }

    /// Configuration: Delay before starting recording in double-tap mode
    /// This short delay (100ms) allows the audio engine to initialize properly before capture begins.
    /// Without this delay, quick double-taps may result in empty/silent audio files.
    private let doubleTapStartDelay: TimeInterval = 0.1

    /// Whether monitoring is temporarily suspended (e.g., during auto-paste)
    private var isSuspended = false

    /// Debounce task for Fn key (prevents false triggers)
    private var fnDebounceTask: Task<Void, Never>?
    private var pendingFnKeyState: Bool?

    /// Configuration: Fn key debounce delay (milliseconds)
    private let fnDebounceDelay: UInt64 = 75_000_000 // 75ms

    // MARK: - Callbacks

    /// Called when recording should start
    var onModifierDown: (() -> Void)?

    /// Called when recording should stop
    var onModifierUp: (() -> Void)?

    /// Called when another key is pressed while the modifier is held (interference)
    var onInterferenceDetected: (() -> Void)?

    // MARK: - Initialization

    private init() {}

    // MARK: - Public Interface

    /// Start monitoring for a specific modifier key
    /// - Parameter mode: Which modifier key to monitor
    func start(mode: ModifierKey) {
        // Stop existing monitoring if any
        if isMonitoring {
            stop()
        }

        currentMode = mode
        isMonitoring = true
        isModifierPressed = false
        machine = pttInitialState()

        // CGEVENTTAP SETUP:
        // We monitor both flagsChanged (for modifier keys) and keyDown (for interference detection).
        // Using headInsertEventTap placement ensures we receive events before other event taps.
        //
        // Event mask includes:
        // - flagsChanged: Detects modifier key press/release (FN, Control, Option)
        // - keyDown: Detects regular key presses for interference detection (e.g., Cmd+C)
        let eventMask = (1 << CGEventType.flagsChanged.rawValue) | (1 << CGEventType.keyDown.rawValue)

        // C-style callback for CGEventTap - must be a static function or closure that doesn't capture self
        // We pass self via userInfo and retrieve it in the callback
        let callback: CGEventTapCallBack = { proxy, type, event, userInfo in
            guard let userInfo = userInfo else {
                return Unmanaged.passUnretained(event)
            }

            let monitor = Unmanaged<BareModifierKeyMonitor>.fromOpaque(userInfo).takeUnretainedValue()

            // CRITICAL: Handle tap being disabled by system
            // CGEventTap can be automatically disabled in two scenarios:
            // 1. .tapDisabledByTimeout - callback took too long to return
            // 2. .tapDisabledByUserInput - system disabled due to Secure Input (password fields, etc.)
            // We must re-enable the tap in both cases to continue receiving events.
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                if let tap = monitor.eventTap {
                    CGEvent.tapEnable(tap: tap, enable: true)
                    let reason = type == .tapDisabledByTimeout ? "timeout" : "user input"
                    AppLogger.audio.debug("BareModifierKeyMonitor: CGEventTap re-enabled after \(reason)")
                }
                // STATE RECONCILIATION: While the tap was disabled, the modifier's
                // keyUp may have been delivered and dropped. No flagsChanged event will
                // ever arrive for that already-completed release, so without reconciling
                // we'd stay stuck in pttActive (recording + mic open) forever.
                // Query the live modifier flags and synthesise the missed release.
                Task { @MainActor in
                    monitor.reconcileModifierStateAfterTapReEnable()
                }
                return Unmanaged.passUnretained(event)
            }

            // Dispatch to main actor for thread-safe state access
            // We don't block the callback - just schedule the work
            Task { @MainActor in
                if type == .flagsChanged {
                    monitor.handleFlagsChangedEvent(event)
                } else if type == .keyDown {
                    monitor.handleKeyDownEvent(event)
                }
            }

            // Always pass the event through (we're observing, not blocking)
            return Unmanaged.passUnretained(event)
        }

        // Create the event tap at session level with head insertion for higher priority
        // - tap: .cgSessionEventTap - Monitor events for this login session
        // - place: .headInsertEventTap - Receive events before other taps
        // - options: .defaultTap - We can observe and optionally modify events
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: CGEventMask(eventMask),
            callback: callback,
            userInfo: UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        ) else {
            AppLogger.audio.error("BareModifierKeyMonitor: Failed to create CGEventTap - check Accessibility permissions")
            isMonitoring = false
            return
        }

        eventTap = tap

        // Add the tap to the run loop so it receives events
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)

        // Enable the tap
        CGEvent.tapEnable(tap: tap, enable: true)

        AppLogger.audio.debug("BareModifierKeyMonitor started for \(String(describing: mode)) using CGEventTap")
    }

    /// Start monitoring for a combo of modifier keys (e.g., FN+Control)
    /// All keys in the combo must be held simultaneously to activate.
    /// - Parameter combo: Set of modifier keys that must all be held
    func start(combo: Set<ModifierKey>) {
        guard let primary = combo.first else { return }
        // start(mode:) calls stop() which clears requiredModifierKeys,
        // so we set it AFTER the CGEventTap is created
        start(mode: primary)
        requiredModifierKeys = combo
    }

    /// Stop monitoring for modifier key presses
    func stop() {
        // Reset suspension state when fully stopped
        isSuspended = false

        // Remove CGEventTap from run loop and clean up
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
            runLoopSource = nil
        }

        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
            CFMachPortInvalidate(tap)
            eventTap = nil
        }

        // Invalidate timers (no DispatchQueue wrapper needed - we're already @MainActor)
        activationTimer?.invalidate()
        activationTimer = nil

        latchTimer?.invalidate()
        latchTimer = nil

        keyUpDebounceTimer?.invalidate()
        keyUpDebounceTimer = nil

        doubleTapStartTimer?.invalidate()
        doubleTapStartTimer = nil

        // Cancel Fn debounce task
        cancelFnDebounce()

        // Reset state
        isMonitoring = false
        currentMode = nil
        requiredModifierKeys = nil
        isModifierPressed = false
        machine = pttInitialState()

        AppLogger.audio.debug("BareModifierKeyMonitor stopped")
    }

    /// Temporarily suspend monitoring (e.g., during auto-paste)
    func setSuspended(_ suspended: Bool) {
        isSuspended = suspended
        if suspended {
            // Reset state to prevent stuck modifiers
            isModifierPressed = false
            cancelFnDebounce()
            step(.reset)

            AppLogger.audio.debug("BareModifierKeyMonitor suspended")
        } else {
            AppLogger.audio.debug("BareModifierKeyMonitor resumed")
        }
    }

    /// Reset state to idle when recording is stopped externally
    ///
    /// **When to Call:**
    /// When recording is cancelled via cancel shortcut, error, or any mechanism
    /// other than the monitor's own double-tap unlock sequence.
    ///
    /// **Why This Is Needed:**
    /// When in latchActive (double-tap locked) mode and recording is cancelled externally,
    /// the monitor doesn't know the recording stopped. Without this reset, the next
    /// modifier press would be interpreted as part of an "unlock" sequence instead of
    /// starting a new recording.
    ///
    /// **Safe to Call:**
    /// If the monitor already triggered the stop (via double-tap unlock), it will
    /// already be idle, so this changes nothing.
    ///
    /// **Changed with issue #287:** this is now a full cancel from
    /// `waitingForActivation` too. It used to return early from there, leaving
    /// the 250ms activation timer armed to start a recording that had already
    /// been cancelled.
    func resetToIdle() {
        AppLogger.audio.debug("BareModifierKeyMonitor resetToIdle from state: \(String(describing: self.machine.state))")

        isModifierPressed = false
        cancelDoubleTapStartTimer()
        cancelFnDebounce()
        step(.resetToIdle)
    }

    // MARK: - Private Implementation (CGEvent Handlers)

    /// Handle flagsChanged events from CGEventTap
    /// This is called when modifier keys are pressed or released.
    private func handleFlagsChangedEvent(_ event: CGEvent) {
        if isSuspended { return }
        guard let currentMode = currentMode else { return }

        let flags = event.flags

        // If we're in activation or active PTT and see extra modifiers, treat as interference.
        if machine.state == .waitingForActivation || machine.state == .pttActive {
            if hasInterferingModifiers(flags, mode: currentMode) {
                handleInterference()
                return
            }
        }

        // Check if our target modifier(s) are pressed
        let isPressed: Bool
        if let combo = requiredModifierKeys {
            // Combo mode: ALL modifiers must be held simultaneously
            isPressed = combo.allSatisfy { isModifierKeyPressed(flags, mode: $0) }
        } else {
            // Single modifier mode (existing behavior)
            isPressed = isModifierKeyPressed(flags, mode: currentMode)
        }

        // For combo modes, skip Fn debounce — the combo itself disambiguates
        // For single Fn key, apply debouncing to prevent false triggers
        if requiredModifierKeys == nil && currentMode == .fn {
            handleFnKeyEvent(isPressed: isPressed)
            return
        }

        // For other keys and combos, process immediately
        processKeyPress(isPressed: isPressed)
    }

    /// Reconcile our tracked modifier state against the system after the CGEventTap
    /// was disabled and re-enabled.
    ///
    /// **Why this is needed:**
    /// When the tap is disabled (`.tapDisabledByTimeout` or `.tapDisabledByUserInput`),
    /// any modifier `keyUp` delivered during the disabled window is dropped. Because the
    /// key is already up by the time the tap comes back, no `flagsChanged` event will ever
    /// arrive for that release. In hold-to-talk states this leaves us stuck in `pttActive`
    /// with `isModifierPressed == true`, so recording — and the microphone — stay on
    /// indefinitely (issue #300).
    ///
    /// We only act on the hold-to-talk states (`waitingForActivation`, `pttActive`) because
    /// those are the only states whose continuation depends on the key being physically
    /// held. In `latchActive`/`unlatchPending` (hands-free lock) releasing the modifier is
    /// expected and must not stop recording.
    private func reconcileModifierStateAfterTapReEnable() {
        if isSuspended { return }
        guard isMonitoring, let currentMode = currentMode else { return }

        // Only hold-to-talk states can get stuck on a dropped release. The core
        // enforces the same guard on `.forceStop`; this early return just avoids
        // the log line and the flags query.
        guard machine.state == .waitingForActivation || machine.state == .pttActive else { return }

        // Query the live modifier flags directly from the system rather than relying on a
        // (now-missing) event.
        let flags = CGEventSource.flagsState(.combinedSessionState)

        let isPressed: Bool
        if let combo = requiredModifierKeys {
            isPressed = combo.allSatisfy { isModifierKeyPressed(flags, mode: $0) }
        } else {
            isPressed = isModifierKeyPressed(flags, mode: currentMode)
        }

        // If the system agrees the modifier is still held, nothing was missed.
        guard !isPressed else { return }

        AppLogger.audio.debug("BareModifierKeyMonitor: reconciling dropped release after tap re-enable from state \(String(describing: self.machine.state))")

        SentryService.addBreadcrumb(
            message: "PTT tap re-enabled — reconciled dropped modifier release",
            category: "ptt.tap",
            data: [
                "mode": String(describing: currentMode),
                "previousState": String(describing: machine.state)
            ]
        )

        // The modifier was released while the tap was disabled. `.forceStop` tears
        // the recording down deterministically. It is a distinct event and NOT a
        // synthesised `.keyUp`, which (in pttActive) could be mistaken for a
        // double-tap and latch instead of stop.
        cancelDoubleTapStartTimer()
        cancelFnDebounce()
        isModifierPressed = false
        step(.forceStop)
    }

    /// Handle keyDown events from CGEventTap
    /// Used for interference detection to cancel activation/recording when the key
    /// is part of a shortcut (e.g., Cmd+C, Cmd+V).
    private func handleKeyDownEvent(_ event: CGEvent) {
        if isSuspended { return }
        guard currentMode != nil else { return }

        // Only treat key presses as interference while we're in the activation window
        // or actively recording in PTT mode (non-latched).
        switch machine.state {
        case .waitingForActivation, .pttActive:
            handleInterference()
        default:
            break
        }
    }

    private func handleFnKeyEvent(isPressed: Bool) {
        // FN KEY UNLOCK SEQUENCE FIX:
        // When in latchActive or unlatchPending states, we MUST process ALL Fn
        // events immediately (both keyDown AND keyUp) to allow the unlock sequence
        // to proceed correctly. The 75ms debounce is designed to filter false Fn
        // triggers during normal operation, but once we're in a locked recording,
        // users need to be able to unlock with quick double-taps.
        //
        // Without this fix, quick Fn taps (< 75ms) during unlock would:
        // 1. keyDown processed immediately (enters unlatchPending)
        // 2. keyUp goes through debounce, not processed yet
        // 3. Second keyDown arrives, but isModifierPressed is still true
        // 4. processKeyPress guard fails → keyDown ignored
        // 5. State machine gets confused, unlock fails
        //
        // The fix: bypass debounce for ALL Fn events when unlocking, so the state
        // machine can accurately track press/release during the unlock sequence.
        if machine.state == .latchActive || machine.state == .unlatchPending {
            cancelFnDebounce()
            processKeyPress(isPressed: isPressed)
            return
        }

        // Normal debounce path for all other cases
        // Debounce Fn key
        pendingFnKeyState = isPressed
        fnDebounceTask?.cancel()

        let pendingState = isPressed
        fnDebounceTask = Task { [weak self] in
            do {
                try await Task.sleep(nanoseconds: self?.fnDebounceDelay ?? 75_000_000)

                guard let self = self, !Task.isCancelled else { return }

                // Only process if state hasn't changed
                if self.pendingFnKeyState == pendingState {
                    self.processKeyPress(isPressed: pendingState)
                }
            } catch {
                // Task was cancelled
            }
        }
    }

    private func processKeyPress(isPressed: Bool) {
        // State transition only when key state changes
        guard isPressed != isModifierPressed else { return }
        isModifierPressed = isPressed

        step(isPressed ? .keyDown : .keyUp)
    }

    private func cancelFnDebounce() {
        fnDebounceTask?.cancel()
        fnDebounceTask = nil
        pendingFnKeyState = nil
    }

    // MARK: - State Machine (shared — `hw-input` via `pttStep`)

    /// Feed one event to the shared machine, store the record it returns and do
    /// what it asks for.
    ///
    /// Everything that calls this is on the main actor, so the machine is never
    /// stepped concurrently and needs no lock of its own.
    private func step(_ event: HwPttEvent) {
        let result = pttStep(
            state: machine,
            event: event,
            nowMs: nowMs,
            config: pttConfiguration
        )
        machine = result.state

        for command in result.timers {
            apply(command)
        }

        if result.transition.reason == .unlockArmed {
            // Entering the unlock sequence. Drop any pending quick-tap start so a
            // recording the user is unlocking out of cannot begin behind it.
            cancelDoubleTapStartTimer()
        }

        // `armInterference` and `resetKeyboardState` are Linux's effects. This
        // head watches for interference unconditionally and holds no keyboard
        // state on the machine's behalf, so both are deliberately ignored.

        recordBreadcrumb(for: result.transition)

        guard let signal = result.signal else { return }
        switch signal {
        case .startRecording:
            if machine.enteredViaHold {
                triggerStart()
            } else {
                // Quick taps wait for the audio engine; holds have already had
                // the 250ms activation delay to warm it up.
                startDoubleTapDelayedRecording()
            }
        case .stopRecording:
            triggerStop()
        case .interfered:
            onInterferenceDetected?()
        }
    }

    private func apply(_ command: HwPttTimerCommand) {
        let starting = command.action == .start

        switch command.timer {
        case .activation:
            activationTimer?.invalidate()
            activationTimer = starting ? scheduleTimer(command.delayMs, event: .activationTimeout) : nil

        case .latch:
            latchTimer?.invalidate()
            latchTimer = starting ? scheduleTimer(command.delayMs, event: .latchTimeout) : nil

        case .keyUpDebounce:
            // Unreachable today: `keyUpDebounceMs` is 0 here, so the core commits
            // a release in the same step and never asks for this timer. Wired up
            // anyway so that turning the debounce on cannot silently do nothing.
            keyUpDebounceTimer?.invalidate()
            keyUpDebounceTimer = starting
                ? scheduleTimer(command.delayMs, event: .keyUpDebounceTimeout(keyPhysicallyHeld: false))
                : nil
        }
    }

    private func scheduleTimer(_ delayMs: UInt64, event: HwPttEvent) -> Timer {
        // Timer callbacks are not @MainActor, so hop back before stepping.
        Timer.scheduledTimer(withTimeInterval: TimeInterval(delayMs) / 1000, repeats: false) { [weak self] _ in
            Task { @MainActor in
                self?.step(event)
            }
        }
    }

    /// Handle interference during activation or active PTT.
    /// - For `.waitingForActivation`: cancels activation and never starts recording.
    /// - For `.pttActive`: stops recording and notifies the callback so the caller can discard.
    /// - In a locked recording: ignored, because hands-free recording expects other keys.
    private func handleInterference() {
        cancelDoubleTapStartTimer()
        isModifierPressed = false
        step(.interference)
    }

    // MARK: - Telemetry

    /// The Sentry breadcrumbs stay native. The core reports which transition it
    /// took, why, and the interval the decision turned on, so nothing here has
    /// to re-derive the transition table to build a payload.
    private func recordBreadcrumb(for transition: HwPttTransition) {
        var category = "ptt.doubletap"
        var data: [String: Any] = [
            "mode": String(describing: currentMode),
            "previousState": String(describing: transition.from),
            "newState": String(describing: transition.to)
        ]
        if let elapsedMs = transition.elapsedMs {
            data["elapsedMs"] = Int(elapsedMs)
        }

        let message: String
        switch transition.reason {
        case .bounceProtected:
            // BOUNCE PROTECTION (HYPERWHISPER-45): the Fn key sometimes emits a
            // spurious keyDown/keyUp pair milliseconds after a lock, which used
            // to unlock immediately and cut the recording to ~0.6s — below
            // Parakeet's one-second floor.
            message = "Bounce protection: ignoring early unlock attempt"
            data["minimumLockDurationMs"] = Int(pttConfiguration.minimumLockMs)

        case .quickTapStarted:
            message = "Double-tap first tap detected"
            data["doublePressWindowMs"] = Int(pttConfiguration.doublePressWindowMs)
            data["doubleTapStartDelay"] = doubleTapStartDelay

        case .doubleTapLocked:
            message = "Double-tap lock confirmed"
            data["withinInterval"] = true

        case .singleTapStopped:
            message = "Single tap release - stopping recording"
            data["doublePressWindowMs"] = Int(pttConfiguration.doublePressWindowMs)

        case .holdReleaseStopped:
            // Changed in issue #287. A hold released inside the double-press
            // window stops now; it used to latch here and stopped on Windows and
            // Linux. Breadcrumbed so the change is visible in the field.
            message = "Hold release - stopping recording"

        case .unlockFirstTap:
            message = "Double-tap unlock first tap detected"
            data["doublePressWindowMs"] = Int(pttConfiguration.doublePressWindowMs)

        case .unlockConfirmed:
            message = "Double-tap unlock confirmed"
            data["withinInterval"] = true

        case .unlockTooSlow:
            message = "Double-tap unlock failed - too slow"
            data["doublePressWindowMs"] = Int(pttConfiguration.doublePressWindowMs)

        case .latchTimeoutStopped:
            message = "Latch timeout - no second tap detected"
            data["outcome"] = "stoppingRecording"

        case .unlockTimedOut:
            message = "Unlock timeout - no second tap detected"
            data["outcome"] = "backToLocked"

        case .interferenceCancelled:
            category = "ptt.interference"
            message = "Interference detected during activation"
            data["outcome"] = "cancelled"

        case .interferenceStopped:
            category = "ptt.interference"
            message = "Interference detected while PTT active"
            data["outcome"] = "stoppingRecording"

        default:
            // Everything else is routine and was never breadcrumbed.
            return
        }

        AppLogger.audio.debug("BareModifierKeyMonitor: \(message, privacy: .public)")
        SentryService.addBreadcrumb(message: message, category: category, data: data)
    }

    // MARK: - Timers

    /// Start recording after a short delay in double-tap mode
    /// This delay allows the audio engine to initialize properly before capture begins.
    /// AUDIO ENGINE INITIALIZATION FIX:
    /// Quick double-taps were starting recording immediately, before the audio engine
    /// had time to warm up. This resulted in empty/silent audio files and "no speech detected"
    /// errors. The 100ms delay gives the audio engine time to initialize while remaining
    /// imperceptible to users.
    private func startDoubleTapDelayedRecording() {
        cancelDoubleTapStartTimer()

        doubleTapStartTimer = Timer.scheduledTimer(withTimeInterval: doubleTapStartDelay, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            Task { @MainActor in
                // Only start if we're still recording (user didn't cancel). The
                // second tap of a lock sequence can land inside this window, so
                // latchActive counts too.
                guard self.machine.state == .pttActive || self.machine.state == .latchActive else {
                    AppLogger.audio.debug("Double-tap start cancelled - state changed to \(String(describing: self.machine.state))")
                    return
                }
                AppLogger.audio.debug("Double-tap delayed recording start triggered")
                self.triggerStart()
            }
        }
    }

    private func cancelDoubleTapStartTimer() {
        doubleTapStartTimer?.invalidate()
        doubleTapStartTimer = nil
    }

    // MARK: - Triggers

    private func triggerStart() {
        // No DispatchQueue wrapper needed - we're @MainActor
        onModifierDown?()
    }

    private func triggerStop() {
        // No DispatchQueue wrapper needed - we're @MainActor
        onModifierUp?()
    }

    // MARK: - Helpers (CGEvent Flag Detection)

    /// Check if the target modifier key is pressed using CGEvent flags.
    ///
    /// CGEvent.CGEventFlags differ from NSEvent.ModifierFlags:
    /// - .maskSecondaryFn corresponds to the Fn/Globe key
    /// - .maskControl corresponds to Control
    /// - .maskAlternate corresponds to Option (both left and right)
    ///
    /// For left/right Option distinction, we check the raw flag bits directly.
    private func isModifierKeyPressed(_ flags: CGEventFlags, mode: ModifierKey) -> Bool {
        switch mode {
        case .fn:
            // .maskSecondaryFn (0x800000) is the Fn/Globe key in CGEvent
            return flags.contains(.maskSecondaryFn)
        case .control:
            return flags.contains(.maskControl)
        case .leftOption, .rightOption:
            // CGEventFlags doesn't directly distinguish left/right Option
            // We check the raw value for side-specific detection
            // Left Alt/Option: bit 0x20 (NX_DEVICELALTKEYMASK)
            // Right Alt/Option: bit 0x40 (NX_DEVICERALTKEYMASK)
            let rawFlags = flags.rawValue
            if mode == .leftOption {
                let leftAltMask: UInt64 = 0x20
                return (rawFlags & leftAltMask) != 0
            } else {
                let rightAltMask: UInt64 = 0x40
                return (rawFlags & rightAltMask) != 0
            }
        }
    }

    /// Returns true if there are modifiers pressed that should be treated as interference
    /// for the current Push to Talk mode (i.e., additional modifiers beyond the PTT key).
    private func hasInterferingModifiers(_ flags: CGEventFlags, mode: ModifierKey) -> Bool {
        // Base set of modifiers we consider as potential interference.
        var interferingFlags: [CGEventFlags] = [.maskCommand, .maskShift, .maskAlternate, .maskControl]

        // For combo modes, exempt ALL combo members from interference
        let keysToExempt: [ModifierKey]
        if let combo = requiredModifierKeys {
            keysToExempt = Array(combo)
        } else {
            keysToExempt = [mode]
        }

        for key in keysToExempt {
            switch key {
            case .fn:
                // Fn is represented by .maskSecondaryFn; not in interferingFlags list
                break
            case .control:
                interferingFlags.removeAll { $0 == .maskControl }
            case .leftOption, .rightOption:
                interferingFlags.removeAll { $0 == .maskAlternate }
            }
        }

        return interferingFlags.contains(where: { flags.contains($0) })
    }
}
