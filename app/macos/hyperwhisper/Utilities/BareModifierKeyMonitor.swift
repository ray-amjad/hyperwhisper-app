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
/// - State Machine: Simplified from 8 states to 5 states
///
/// Features:
/// - PTT Mode: Hold to record, release to stop (with 250ms activation delay)
/// - Double Tap Lock: Double tap to lock recording (hands-free mode) - triggers on keyUp
/// - Double Tap Unlock: Double tap to unlock/stop recording - triggers on keyUp (symmetric with lock)
/// - Suspension: Can be temporarily suspended to ignore input (e.g., during auto-paste)
/// - Interference Detection: Detects when other keys are pressed during activation/PTT and cancels recording
///
/// State Machine Flow:
/// 1. Idle -> Down -> WaitingForActivation (Starts 250ms timer)
/// 2. WaitingForActivation -> Timer Fired -> PTT Active (Start Recording)
/// 3. WaitingForActivation -> Interference (e.g. 'C' pressed) -> Idle (Cancel PTT)
/// 4. WaitingForActivation -> Up (quick tap) -> PTT Active (First tap of lock sequence)
/// 5. PTT Active -> Up within interval -> Latch Active (Double tap lock confirmed)
/// 6. Latch Active -> Down -> UnlatchPending (reset firstTapTime to 0)
/// 7. UnlatchPending -> Up (first) -> UnlatchPending (set firstTapTime, start timer)
/// 8. UnlatchPending -> Up (second, within interval) -> Idle + Stop (Double tap unlock confirmed)
/// 9. UnlatchPending -> Timeout -> Latch Active (user didn't complete unlock)
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

    private enum MonitorState {
        case idle                   // No recording
        case waitingForActivation   // Waiting 250ms to confirm it's not a shortcut
        case pttActive              // Recording (hold to record)
        case latchActive            // Recording (locked via double-tap)
        case unlatchPending         // First tap of unlock sequence (waiting for 2nd)
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

    /// Current state of the detection state machine
    private var state: MonitorState = .idle

    /// Timer for double-tap detection and unlatch timeout
    private var latchTimer: Timer?

    /// Timer for initial activation delay (to prevent shortcut triggers)
    private var activationTimer: Timer?

    /// Timer for delayed start in double-tap mode (audio engine initialization)
    private var doubleTapStartTimer: Timer?

    /// Timestamp of first tap (for double-tap detection)
    private var firstTapTime: TimeInterval = 0

    /// Timestamp when latch (lock) was activated
    /// Used to prevent immediate unlock from key bounce events
    private var lastLatchActiveTime: TimeInterval = 0

    /// Minimum time (seconds) to stay locked before allowing unlock sequence
    /// This prevents Fn key bounce from immediately triggering unlock after lock
    /// Set to 1.0s to match Parakeet's minimum transcription duration requirement
    private let minimumLockDuration: TimeInterval = 1.0

    /// Configuration: Enable double-tap to lock
    var doublePressEnabled: Bool = true

    /// Configuration: Double-tap interval (seconds)
    /// 1.5s provides a generous window for double-tap detection:
    /// - First tap release to second tap release
    /// - Typical human double-tap takes 250-600ms
    /// - 1.5s accommodates slower/deliberate taps without feeling sluggish
    var doublePressInterval: TimeInterval = 1.5

    /// Configuration: Delay before activating PTT to filter out shortcuts (e.g. Cmd+C)
    /// 250ms allows users to type shortcuts without triggering PTT.
    /// If another key is pressed within this window, PTT activation is cancelled.
    var activationDelay: TimeInterval = 0.25

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
        state = .idle

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

        doubleTapStartTimer?.invalidate()
        doubleTapStartTimer = nil

        // Cancel Fn debounce task
        fnDebounceTask?.cancel()
        fnDebounceTask = nil
        pendingFnKeyState = nil

        // Reset state
        isMonitoring = false
        currentMode = nil
        requiredModifierKeys = nil
        isModifierPressed = false
        state = .idle

        AppLogger.audio.debug("BareModifierKeyMonitor stopped")
    }

    /// Temporarily suspend monitoring (e.g., during auto-paste)
    func setSuspended(_ suspended: Bool) {
        isSuspended = suspended
        if suspended {
            // Reset state to prevent stuck modifiers
            state = .idle
            isModifierPressed = false

            // Invalidate timers (no DispatchQueue wrapper needed)
            activationTimer?.invalidate()
            activationTimer = nil

            latchTimer?.invalidate()
            latchTimer = nil

            // Cancel Fn debounce
            fnDebounceTask?.cancel()
            fnDebounceTask = nil
            pendingFnKeyState = nil

            AppLogger.audio.debug("BareModifierKeyMonitor suspended")
        } else {
            AppLogger.audio.debug("BareModifierKeyMonitor resumed")
        }
    }

    /// Check if monitoring is currently active
    func isRunning() -> Bool {
        return isMonitoring
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
    /// already be in .idle state, so this is a no-op.
    func resetToIdle() {
        // Only reset if we're in a recording-related state
        guard state == .latchActive || state == .unlatchPending || state == .pttActive else {
            return
        }

        AppLogger.audio.debug("BareModifierKeyMonitor resetToIdle from state: \(String(describing: self.state))")

        state = .idle
        isModifierPressed = false

        // Cancel all timers
        activationTimer?.invalidate()
        activationTimer = nil

        latchTimer?.invalidate()
        latchTimer = nil

        doubleTapStartTimer?.invalidate()
        doubleTapStartTimer = nil

        // Cancel Fn debounce
        fnDebounceTask?.cancel()
        fnDebounceTask = nil
        pendingFnKeyState = nil
    }

    // MARK: - Private Implementation (CGEvent Handlers)

    /// Handle flagsChanged events from CGEventTap
    /// This is called when modifier keys are pressed or released.
    private func handleFlagsChangedEvent(_ event: CGEvent) {
        if isSuspended { return }
        guard let currentMode = currentMode else { return }

        let flags = event.flags

        // If we're in activation or active PTT and see extra modifiers, treat as interference.
        if state == .waitingForActivation || state == .pttActive {
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

        // Only hold-to-talk states can get stuck on a dropped release.
        guard state == .waitingForActivation || state == .pttActive else { return }

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

        AppLogger.audio.debug("BareModifierKeyMonitor: reconciling dropped release after tap re-enable from state \(String(describing: self.state))")

        SentryService.addBreadcrumb(
            message: "PTT tap re-enabled — reconciled dropped modifier release",
            category: "ptt.tap",
            data: [
                "mode": String(describing: currentMode),
                "previousState": String(describing: state)
            ]
        )

        // The modifier was released while the tap was disabled. Deterministically tear the
        // recording down instead of routing through handleKeyUp(), which (in pttActive)
        // could mistake the synthesised release for a double-tap and latch instead of stop.
        let wasActive = (state == .pttActive)

        cancelActivationTimer()
        cancelLatchTimer()
        cancelDoubleTapStartTimer()
        fnDebounceTask?.cancel()
        fnDebounceTask = nil
        pendingFnKeyState = nil

        state = .idle
        isModifierPressed = false

        // Only fire the stop callback if recording had actually started (pttActive). In
        // waitingForActivation recording never began, so cancelling the timer is enough.
        if wasActive {
            triggerStop()
        }
    }

    /// Handle keyDown events from CGEventTap
    /// Used for interference detection to cancel activation/recording when the key
    /// is part of a shortcut (e.g., Cmd+C, Cmd+V).
    private func handleKeyDownEvent(_ event: CGEvent) {
        if isSuspended { return }
        guard currentMode != nil else { return }

        // Only treat key presses as interference while we're in the activation window
        // or actively recording in PTT mode (non-latched).
        switch state {
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
        if state == .latchActive || state == .unlatchPending {
            fnDebounceTask?.cancel()
            fnDebounceTask = nil
            pendingFnKeyState = nil
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

        if isPressed {
            handleKeyDown()
        } else {
            handleKeyUp()
        }
    }

    // MARK: - State Machine

    private func handleKeyDown() {
        switch state {
        case .idle:
            // Start activation delay timer (250ms to filter out shortcuts)
            state = .waitingForActivation
            startActivationTimer()

        case .waitingForActivation:
            // Already waiting, ignore duplicate down events
            break

        case .pttActive:
            // DOUBLE-TAP SECOND PRESS: User pressed modifier again while in pttActive
            //
            // This proves the first tap wasn't abandoned - user is actively engaged.
            // Cancel the auto-stop safety timeout since its purpose (detect abandoned tap)
            // no longer applies. The recording will continue until:
            // - Quick release (within 1.5s): locks to latchActive
            // - Slow release (after 1.5s): stops recording (checked in keyUp)
            // - Interference detected: stops recording
            //
            // Trade-off: If user accidentally presses and walks away, no safety timeout.
            // Mitigation: Recording state visible in menu bar UI.
            cancelLatchTimer()
            break

        case .latchActive:
            // BOUNCE PROTECTION FIX (HYPERWHISPER-45):
            // Problem: When users double-tap FN to lock recording, the Fn key sometimes
            // generates spurious bounce events (extra keyDown/keyUp within milliseconds).
            // These bounce events were immediately triggering the unlock sequence,
            // stopping the recording after ~0.6 seconds and causing "Transcription failed"
            // errors because Parakeet requires at least 1 second of audio.
            //
            // Solution: Enforce a 1-second cooldown after locking before allowing any
            // unlock sequence to begin. Bounce events during this window are ignored.
            let now = Date().timeIntervalSince1970
            let timeSinceLock = now - lastLatchActiveTime
            if timeSinceLock < minimumLockDuration {
                AppLogger.audio.debug("Ignoring keyDown - too soon after lock (\(Int(timeSinceLock * 1000))ms < \(Int(self.minimumLockDuration * 1000))ms)")

                // SENTRY BREADCRUMB: Track bounce protection activation
                SentryService.addBreadcrumb(
                    message: "Bounce protection: ignoring early unlock attempt",
                    category: "ptt.doubletap",
                    data: [
                        "mode": String(describing: currentMode),
                        "timeSinceLockMs": Int(timeSinceLock * 1000),
                        "minimumLockDurationMs": Int(minimumLockDuration * 1000)
                    ]
                )
                return
            }

            // In locked mode, first tap of unlock sequence
            // Reset firstTapTime to 0 as sentinel - actual time will be set on first keyUp
            // This makes unlock symmetric with lock (both measure keyUp-to-keyUp)

            // Cancel any pending recording start timer from lock sequence
            // We're entering unlock mode, so we don't want recording to start
            cancelDoubleTapStartTimer()

            state = .unlatchPending
            firstTapTime = 0

        case .unlatchPending:
            // Subsequent keyDown during unlock sequence - just stay in state
            // Don't update firstTapTime as it was set on first keyUp
            break
        }
    }

    private func handleKeyUp() {
        switch state {
        case .idle:
            // Not recording, ignore key up
            break

        case .waitingForActivation:
            // Released before activation timer fired (too quick)
            cancelActivationTimer()

            if doublePressEnabled {
                // DOUBLE-TAP LOCK MODE: First tap of potential double-tap-to-lock sequence
                // We use a short delay before starting recording to allow the audio engine
                // to initialize properly. Without this delay, quick taps may result in
                // empty/silent audio files causing "no speech detected" errors.
                state = .pttActive
                firstTapTime = Date().timeIntervalSince1970

                // SENTRY BREADCRUMB: Track double-tap first tap for debugging
                SentryService.addBreadcrumb(
                    message: "Double-tap first tap detected",
                    category: "ptt.doubletap",
                    data: [
                        "mode": String(describing: currentMode),
                        "previousState": "waitingForActivation",
                        "newState": "pttActive",
                        "doublePressInterval": doublePressInterval,
                        "doubleTapStartDelay": doubleTapStartDelay
                    ]
                )

                startDoubleTapDelayedRecording()
                startLatchTimer() // Wait for second tap
            } else {
                // Double-tap disabled, just ignore short taps
                state = .idle
            }

        case .pttActive:
            // Check if this is a double-tap (second tap within interval)
            let now = Date().timeIntervalSince1970
            let timeSinceFirstTap = now - firstTapTime
            if doublePressEnabled && timeSinceFirstTap <= doublePressInterval {
                // Second tap detected -> Lock recording
                cancelLatchTimer()
                state = .latchActive
                lastLatchActiveTime = Date().timeIntervalSince1970  // Track lock time for bounce protection
                AppLogger.audio.debug("Double tap lock confirmed")

                // SENTRY BREADCRUMB: Track successful double-tap lock
                SentryService.addBreadcrumb(
                    message: "Double-tap lock confirmed",
                    category: "ptt.doubletap",
                    data: [
                        "mode": String(describing: currentMode),
                        "timeSinceFirstTap": timeSinceFirstTap,
                        "withinInterval": true
                    ]
                )
            } else {
                // Single release -> Stop recording
                // SENTRY BREADCRUMB: Track single tap (not a double-tap)
                SentryService.addBreadcrumb(
                    message: "Single tap release - stopping recording",
                    category: "ptt.doubletap",
                    data: [
                        "mode": String(describing: currentMode),
                        "timeSinceFirstTap": timeSinceFirstTap,
                        "doublePressInterval": doublePressInterval,
                        "reason": timeSinceFirstTap > doublePressInterval ? "tooSlow" : "doublePressDisabled"
                    ]
                )

                state = .idle
                triggerStop()
            }

        case .latchActive:
            // In locked mode, release doesn't stop recording
            // User must double-tap to unlock
            break

        case .unlatchPending:
            // SYMMETRIC UNLOCK: Mirror the lock mechanism exactly
            // Lock: first keyUp sets time, second keyUp checks interval
            // Unlock: first keyUp sets time, second keyUp checks interval

            if firstTapTime == 0 {
                // FIRST keyUp of unlock sequence - record time and wait for second tap
                firstTapTime = Date().timeIntervalSince1970
                AppLogger.audio.debug("Double tap unlock: first tap complete, waiting for second")

                // SENTRY BREADCRUMB: Track first tap of unlock sequence
                SentryService.addBreadcrumb(
                    message: "Double-tap unlock first tap detected",
                    category: "ptt.doubletap",
                    data: [
                        "mode": String(describing: currentMode),
                        "previousState": "unlatchPending",
                        "doublePressInterval": doublePressInterval
                    ]
                )

                // Start timer - if user doesn't complete second tap, go back to locked
                startLatchTimer()
                // Stay in unlatchPending, waiting for second keyUp

            } else {
                // SECOND keyUp of unlock sequence - check timing
                let now = Date().timeIntervalSince1970
                let timeSinceFirstTap = now - firstTapTime

                if timeSinceFirstTap <= doublePressInterval {
                    // Second tap release within interval -> Unlock and stop
                    cancelLatchTimer()
                    AppLogger.audio.debug("Double tap unlock confirmed")

                    // SENTRY BREADCRUMB: Track successful double-tap unlock
                    SentryService.addBreadcrumb(
                        message: "Double-tap unlock confirmed",
                        category: "ptt.doubletap",
                        data: [
                            "mode": String(describing: currentMode),
                            "timeSinceFirstTap": timeSinceFirstTap,
                            "withinInterval": true
                        ]
                    )

                    state = .idle
                    triggerStop()
                } else {
                    // Too slow - go back to latchActive and let user try again
                    cancelLatchTimer()
                    AppLogger.audio.debug("Double tap unlock too slow (\(timeSinceFirstTap)s) - resetting to locked")

                    SentryService.addBreadcrumb(
                        message: "Double-tap unlock failed - too slow",
                        category: "ptt.doubletap",
                        data: [
                            "mode": String(describing: currentMode),
                            "timeSinceFirstTap": timeSinceFirstTap,
                            "doublePressInterval": doublePressInterval
                        ]
                    )

                    state = .latchActive
                }
            }
        }
    }

    private func handleActivationTimeout() {
        // Activation timer fired -> User is holding the key -> Start PTT
        guard state == .waitingForActivation else { return }

        state = .pttActive
        firstTapTime = Date().timeIntervalSince1970
        triggerStart()
    }

    private func handleLatchTimeout() {
        // Latch timer expired -> No second tap detected
        // Handles both lock timeout (pttActive) and unlock timeout (unlatchPending)

        if state == .pttActive {
            // LOCK TIMEOUT: User did first tap but not second -> stop recording
            let timeSinceFirstTap = Date().timeIntervalSince1970 - firstTapTime
            SentryService.addBreadcrumb(
                message: "Latch timeout - no second tap detected",
                category: "ptt.doubletap",
                data: [
                    "mode": String(describing: currentMode),
                    "timeSinceFirstTap": timeSinceFirstTap,
                    "doublePressInterval": doublePressInterval,
                    "outcome": "stoppingRecording"
                ]
            )

            state = .idle
            triggerStop()

        } else if state == .unlatchPending {
            // UNLOCK TIMEOUT: User did first tap but not second -> go back to locked
            let timeSinceFirstTap = Date().timeIntervalSince1970 - firstTapTime
            AppLogger.audio.debug("Unlock timeout - going back to locked mode")

            SentryService.addBreadcrumb(
                message: "Unlock timeout - no second tap detected",
                category: "ptt.doubletap",
                data: [
                    "mode": String(describing: currentMode),
                    "timeSinceFirstTap": timeSinceFirstTap,
                    "doublePressInterval": doublePressInterval,
                    "outcome": "backToLocked"
                ]
            )

            state = .latchActive
        }
    }

    /// Handle interference during activation or active PTT.
    /// - For .waitingForActivation: cancels activation and never starts recording.
    /// - For .pttActive: stops recording and notifies the callback so the caller can discard.
    private func handleInterference() {
        switch state {
        case .waitingForActivation:
            cancelActivationTimer()
            cancelDoubleTapStartTimer()

            // SENTRY BREADCRUMB: Track interference during activation
            SentryService.addBreadcrumb(
                message: "Interference detected during activation",
                category: "ptt.interference",
                data: [
                    "mode": String(describing: currentMode),
                    "previousState": "waitingForActivation",
                    "outcome": "cancelled"
                ]
            )

            state = .idle
            isModifierPressed = false
            AppLogger.audio.debug("BareModifierKeyMonitor interference during activation - cancelling PTT start")
            onInterferenceDetected?()

        case .pttActive:
            cancelActivationTimer()
            cancelLatchTimer()
            cancelDoubleTapStartTimer()

            // SENTRY BREADCRUMB: Track interference while recording active
            SentryService.addBreadcrumb(
                message: "Interference detected while PTT active",
                category: "ptt.interference",
                data: [
                    "mode": String(describing: currentMode),
                    "previousState": "pttActive",
                    "outcome": "stoppingRecording"
                ]
            )

            state = .idle
            isModifierPressed = false
            AppLogger.audio.debug("BareModifierKeyMonitor interference while active - stopping PTT")
            onInterferenceDetected?()

        default:
            break
        }
    }

    // MARK: - Timers

    private func startActivationTimer() {
        activationTimer?.invalidate()

        // Timer callback is not @MainActor, so we need to dispatch to main actor
        activationTimer = Timer.scheduledTimer(withTimeInterval: activationDelay, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            Task { @MainActor in
                self.handleActivationTimeout()
            }
        }
    }

    private func cancelActivationTimer() {
        activationTimer?.invalidate()
        activationTimer = nil
    }

    private func startLatchTimer() {
        latchTimer?.invalidate()

        // Timer callback is not @MainActor, so we need to dispatch to main actor
        latchTimer = Timer.scheduledTimer(withTimeInterval: doublePressInterval, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            Task { @MainActor in
                self.handleLatchTimeout()
            }
        }
    }

    private func cancelLatchTimer() {
        latchTimer?.invalidate()
        latchTimer = nil
    }

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
                // Only start if we're still in pttActive state (user didn't cancel)
                guard self.state == .pttActive || self.state == .latchActive else {
                    AppLogger.audio.debug("Double-tap start cancelled - state changed to \(String(describing: self.state))")
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
