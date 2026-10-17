import Foundation
import CoreGraphics

/// Runs ONE CGEventTap on a dedicated thread with its own CFRunLoop (issue #904).
///
/// WHY: a tap whose mach-port source sits on the MAIN run loop has its callback
/// called on the main thread. When macOS disables the tap
/// (`.tapDisabledByTimeout`, or `.tapDisabledByUserInput` under Secure Input)
/// the callback re-enables it with `CGEvent.tapEnable`, which is a synchronous
/// round trip into the WindowServer. Sentry HYPERWHISPER-YY caught that round
/// trip blocking the main thread for over 10 seconds. On this thread it can
/// only ever block the tap's own thread.
///
/// Lifecycle, and the rules that keep it leak-free:
/// - `start(tap:)` spawns the thread and returns at once. It never waits for the
///   thread, so the caller never waits on anything the WindowServer does.
/// - Everything that touches the tap or the run loop happens ON the tap thread:
///   adding the source, the initial enable, the re-enable, and the teardown
///   (remove the source from THIS run loop, disable, invalidate, stop the run
///   loop). Because the callback and the teardown run on one thread, they can
///   never overlap.
/// - `stop()` only flags the stop and signals a private run-loop source, so it
///   returns immediately even while the tap thread is stuck in a
///   `CGEvent.tapEnable`. The thread exits as soon as that call returns.
/// - A `stop()` that races `start(tap:)` is safe: the stop flag is read under
///   the lock when the thread begins, and the stop source stays signalled
///   until the run loop services it, so a stop can never be lost.
/// - The thread retains this object until it exits, so an in-flight callback
///   never reads a freed `userInfo`.
///
/// Used by `BareModifierKeyMonitor` (push-to-talk) and
/// `RecordingWindowManager` (cancel overlay). The tap callbacks receive this
/// object as their `userInfo`; `context` carries anything else a callback needs.
final class EventTapThread: @unchecked Sendable {

    /// Enables or disables a tap. Injectable so tests can drive the whole
    /// lifecycle with a plain mach port: `CGEvent.tapCreate` needs the
    /// Accessibility permission, which a CI runner does not have.
    typealias SetTapEnabled = (CFMachPort, Bool) -> Void

    static let systemSetTapEnabled: SetTapEnabled = { tap, enabled in
        CGEvent.tapEnable(tap: tap, enable: enabled)
    }

    /// Why macOS disabled the tap. Metadata only: the slug goes to logs and
    /// breadcrumbs, never anything about the key that was pressed.
    enum DisableReason: String, Equatable {
        case timeout = "timeout"
        case userInput = "user_input"

        init?(_ type: CGEventType) {
            switch type {
            case .tapDisabledByTimeout: self = .timeout
            case .tapDisabledByUserInput: self = .userInput
            default: return nil
            }
        }

        /// The words the existing log lines used ("re-enabled after user input").
        var phrase: String {
            switch self {
            case .timeout: return "timeout"
            case .userInput: return "user input"
            }
        }
    }

    struct ReEnable: Equatable {
        let reason: DisableReason
        let elapsedMs: Int
    }

    let name: String
    /// Extra, immutable state a tap callback needs (e.g. the overlay's handlers).
    let context: AnyObject?

    private let setTapEnabled: SetTapEnabled
    private let condition = NSCondition()

    // Guarded by `condition`.
    private var stopRequested = false
    private var started = false
    private var exited = false
    private var runLoop: CFRunLoop?
    private var thread: Thread?
    private var tap: CFMachPort?
    private var tapSource: CFRunLoopSource?
    private var stopSource: CFRunLoopSource?

    // Touched only on the tap thread.
    private var tornDown = false

    init(name: String, context: AnyObject? = nil, setTapEnabled: @escaping SetTapEnabled = EventTapThread.systemSetTapEnabled) {
        self.name = name
        self.context = context
        self.setTapEnabled = setTapEnabled
    }

    // MARK: - Lifecycle (any thread)

    /// Attach `tap` to a new thread's run loop and enable it there. Returns
    /// without waiting for the thread. Call at most once.
    func start(tap: CFMachPort) {
        condition.lock()
        guard !started else {
            condition.unlock()
            assertionFailure("EventTapThread.start(tap:) called twice")
            return
        }
        started = true
        self.tap = tap
        tapSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        stopSource = makeStopSource()
        let thread = Thread { [self] in
            self.runOnTapThread()
        }
        thread.name = name
        // Every keyboard event in the session passes through the tap, so it
        // must not queue behind background work.
        thread.qualityOfService = .userInteractive
        self.thread = thread
        condition.unlock()
        thread.start()
    }

    /// Ask the tap thread to tear the tap down and exit. Never blocks.
    func stop() {
        condition.lock()
        guard !stopRequested else {
            condition.unlock()
            return
        }
        stopRequested = true
        let loop = runLoop
        let source = stopSource
        condition.unlock()

        if let source { CFRunLoopSourceSignal(source) }
        if let loop { CFRunLoopWakeUp(loop) }
    }

    /// True once `stop()` was called. A tap callback reads this, under the
    /// lock, instead of any main-actor state.
    var isStopRequested: Bool {
        condition.lock()
        defer { condition.unlock() }
        return stopRequested
    }

    // MARK: - Hop to main (tap thread)

    /// Run `work` on the main actor, in the order the tap thread called this.
    ///
    /// Every tap -> main hop goes through here. A separate
    /// `Task { @MainActor in }` per event does NOT promise FIFO order between
    /// tasks, so a modifier press and its release could reach the main actor
    /// reversed and leave push-to-talk recording. The main dispatch queue is
    /// serial and FIFO, and its blocks run on the main thread, which is what
    /// `MainActor.assumeIsolated` (macOS 14+, the deployment target) asserts.
    static func deliverOnMainInOrder(_ work: @escaping @MainActor () -> Void) {
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                work()
            }
        }
    }

    // MARK: - Re-enable (tap thread)

    /// Re-enable the tap after macOS disabled it, and time the call. Called
    /// from the tap callback, so the WindowServer round trip blocks only this
    /// thread. Returns nil for any other event type, or once a stop was
    /// requested (the teardown disables the tap anyway).
    func reEnableAfterSystemDisable(_ type: CGEventType) -> ReEnable? {
        guard let reason = DisableReason(type) else { return nil }
        condition.lock()
        let tap = stopRequested ? nil : self.tap
        condition.unlock()
        guard let tap else { return nil }

        let startNs = DispatchTime.now().uptimeNanoseconds
        setTapEnabled(tap, true)
        let elapsedNs = DispatchTime.now().uptimeNanoseconds &- startNs
        return ReEnable(reason: reason, elapsedMs: Int(elapsedNs / 1_000_000))
    }

    // MARK: - Test seams

    /// The tap thread's run loop, once the thread has started (nil if it exits
    /// first). Blocks the caller, so tests only.
    func waitUntilRunning(timeout: TimeInterval) -> CFRunLoop? {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }
        while runLoop == nil && !exited {
            if !condition.wait(until: deadline) { break }
        }
        return runLoop
    }

    /// True once the tap thread has returned. Blocks the caller, so tests only.
    func waitUntilExited(timeout: TimeInterval) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }
        while !exited {
            if !condition.wait(until: deadline) { break }
        }
        return exited
    }

    var threadForTesting: Thread? {
        condition.lock()
        defer { condition.unlock() }
        return thread
    }

    var tapSourceForTesting: CFRunLoopSource? {
        condition.lock()
        defer { condition.unlock() }
        return tapSource
    }

    // MARK: - Tap thread

    private func runOnTapThread() {
        let loop: CFRunLoop = CFRunLoopGetCurrent()

        condition.lock()
        runLoop = loop
        let stopEarly = stopRequested
        let tap = self.tap
        let tapSource = self.tapSource
        let stopSource = self.stopSource
        condition.broadcast()
        condition.unlock()

        if !stopEarly, let tap, let tapSource, let stopSource {
            // The stop source also keeps the run loop alive: a run loop with
            // no sources returns from CFRunLoopRunInMode at once.
            CFRunLoopAddSource(loop, stopSource, .commonModes)
            CFRunLoopAddSource(loop, tapSource, .commonModes)
            setTapEnabled(tap, true)
            while !tornDown {
                _ = CFRunLoopRunInMode(.defaultMode, 1.0e10, false)
            }
        }
        tearDownOnTapThread()

        condition.lock()
        exited = true
        condition.broadcast()
        condition.unlock()
    }

    /// Idempotent. Runs from the stop source's perform callback, or directly
    /// when the stop arrived before the run loop ever ran.
    private func tearDownOnTapThread() {
        guard !tornDown else { return }
        tornDown = true

        condition.lock()
        let loop = runLoop
        let tap = self.tap
        let tapSource = self.tapSource
        let stopSource = self.stopSource
        condition.unlock()

        if let loop, let tapSource {
            CFRunLoopRemoveSource(loop, tapSource, .commonModes)
        }
        if let tap {
            setTapEnabled(tap, false)
            CFMachPortInvalidate(tap)
        }
        if let stopSource {
            if let loop { CFRunLoopRemoveSource(loop, stopSource, .commonModes) }
            CFRunLoopSourceInvalidate(stopSource)
        }
        if let loop { CFRunLoopStop(loop) }
    }

    private func makeStopSource() -> CFRunLoopSource? {
        // `info` is unretained: the thread closure retains `self` for as long
        // as the source can fire, and the teardown invalidates the source.
        var sourceContext = CFRunLoopSourceContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: nil,
            release: nil,
            copyDescription: nil,
            equal: nil,
            hash: nil,
            schedule: nil,
            cancel: nil,
            perform: { info in
                guard let info else { return }
                Unmanaged<EventTapThread>.fromOpaque(info).takeUnretainedValue().tearDownOnTapThread()
            }
        )
        return CFRunLoopSourceCreate(kCFAllocatorDefault, 0, &sourceContext)
    }
}
