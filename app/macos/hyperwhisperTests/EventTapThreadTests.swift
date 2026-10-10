//
//  EventTapThreadTests.swift
//  hyperwhisperTests
//
//  Neither CGEventTap may sit on the main run loop (issue #904).
//
//  Both taps — push-to-talk in `BareModifierKeyMonitor` and the cancel overlay
//  in `RecordingWindowManager` — re-enable themselves with `CGEvent.tapEnable`
//  after macOS disables them. That call is a synchronous WindowServer round
//  trip, and on the main run loop it froze the whole app (Sentry
//  HYPERWHISPER-YY). Both taps now live on an `EventTapThread`.
//
//  A real tap is out of reach here: `CGEvent.tapCreate` needs the Accessibility
//  permission a CI runner does not have, and the freeze needs a WindowServer
//  that stalls. So the lifecycle is driven through the seam the production
//  code uses — `EventTapThread`, with a plain mach port standing in for the
//  tap and an injected enable function standing in for `CGEvent.tapEnable`.
//  What these prove:
//
//  1. The tap's source goes on a run loop that is not the main one, and the
//     enable, re-enable and teardown all happen on that thread.
//  2. stop() removes the source from that same run loop, invalidates the port
//     and the thread exits; start/stop cycles, a stop racing a start and a
//     stop before start leave no live thread.
//  3. stop() returns at once while the tap thread is stuck in an enable — the
//     freeze can no longer reach the caller.
//  4. The re-enable reports a reason slug and elapsed ms, and does nothing
//     once a stop was requested.
//  5. The two real callbacks, called OFF the main thread: the overlay swallows
//     Return/Escape only while its tap is live and runs the handler on main;
//     the push-to-talk callback passes every event through. A key the tap
//     thread swallowed does not run its handler if the overlay ended before
//     the queued main-thread block ran.
//  6. Neither production file attaches anything to the main run loop or calls
//     CGEvent.tapEnable itself (the issue's Done-when check 1, plus the
//     wiring).
//  7. Tap -> main hops reach the main thread in the order the tap thread sent
//     them (a press and its release never swap).
//
//  What no test here can prove: that a real Secure Input prompt no longer
//  freezes the app. That needs a real Mac — see the issue's Done-when 3.
//

import CoreGraphics
import Foundation
import Testing
@testable import HyperWhisper

@Suite(.serialized)
struct EventTapThreadTests {

    // MARK: - Fixtures

    /// Records every call the tap thread makes to "enable the tap".
    final class EnableRecorder: @unchecked Sendable {
        struct Call {
            let enabled: Bool
            let onMainThread: Bool
            let runLoop: CFRunLoop
        }

        private let lock = NSLock()
        private var recorded: [Call] = []
        /// Called before a `true` call is recorded; may block, to stand in for
        /// a WindowServer that does not answer.
        var beforeEnable: (() -> Void)?

        var calls: [Call] {
            lock.lock()
            defer { lock.unlock() }
            return recorded
        }

        var setTapEnabled: EventTapThread.SetTapEnabled {
            { [self] _, enabled in
                if enabled { self.beforeEnable?() }
                let call = Call(enabled: enabled, onMainThread: Thread.isMainThread, runLoop: CFRunLoopGetCurrent())
                self.lock.lock()
                self.recorded.append(call)
                self.lock.unlock()
            }
        }
    }

    /// A plain mach port: a stand-in for a CGEventTap that needs no permission.
    static func makePort() -> CFMachPort {
        var context = CFMachPortContext(version: 0, info: nil, retain: nil, release: nil, copyDescription: nil)
        return CFMachPortCreate(kCFAllocatorDefault, { _, _, _, _ in }, &context, nil)
    }

    /// Polls `condition` until it holds or `timeout` passes.
    static func eventually(timeout: TimeInterval = 2, _ condition: () -> Bool) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        while Date() < deadline {
            if condition() { return true }
            Thread.sleep(forTimeInterval: 0.005)
        }
        return condition()
    }

    // MARK: - 1. The tap runs on its own run loop

    @Test func theTapSourceIsOnItsOwnRunLoopNotTheMainOne() throws {
        let recorder = EnableRecorder()
        let port = Self.makePort()
        let host = EventTapThread(name: "test.tap", setTapEnabled: recorder.setTapEnabled)
        host.start(tap: port)

        let loop = try #require(host.waitUntilRunning(timeout: 2))
        #expect(loop !== CFRunLoopGetMain())
        // The initial enable runs after the source is added, on the tap thread.
        #expect(Self.eventually { recorder.calls.count >= 1 })
        let source = try #require(host.tapSourceForTesting)
        #expect(CFRunLoopContainsSource(loop, source, .commonModes))
        #expect(!CFRunLoopContainsSource(CFRunLoopGetMain(), source, .commonModes))

        let thread = try #require(host.threadForTesting)
        #expect(thread !== Thread.main)
        #expect(thread.name == "test.tap")

        let enable = try #require(recorder.calls.first)
        #expect(enable.enabled)
        #expect(!enable.onMainThread)
        #expect(enable.runLoop === loop)

        host.stop()
        #expect(host.waitUntilExited(timeout: 2))
    }

    // MARK: - 2. stop() tears down on the same run loop and the thread exits

    @Test func stopRemovesTheSourceFromTheTapRunLoopAndTheThreadExits() throws {
        let recorder = EnableRecorder()
        let port = Self.makePort()
        let host = EventTapThread(name: "test.tap", setTapEnabled: recorder.setTapEnabled)
        host.start(tap: port)
        let loop = try #require(host.waitUntilRunning(timeout: 2))
        #expect(Self.eventually { recorder.calls.count >= 1 })
        let source = try #require(host.tapSourceForTesting)
        let thread = try #require(host.threadForTesting)

        host.stop()

        #expect(host.waitUntilExited(timeout: 2))
        #expect(Self.eventually { thread.isFinished })
        #expect(!CFRunLoopContainsSource(loop, source, .commonModes))
        #expect(!CFMachPortIsValid(port))
        // Enabled once and disabled once, both on the tap thread's run loop.
        #expect(recorder.calls.map(\.enabled) == [true, false])
        #expect(recorder.calls.allSatisfy { $0.runLoop === loop && !$0.onMainThread })
    }

    @Test func startStopCyclesLeaveNoLiveTapThread() throws {
        var hosts: [(EventTapThread, Thread, CFMachPort)] = []
        for _ in 0..<50 {
            let port = Self.makePort()
            let host = EventTapThread(name: "test.tap", setTapEnabled: EnableRecorder().setTapEnabled)
            host.start(tap: port)
            _ = try #require(host.waitUntilRunning(timeout: 2))
            let thread = try #require(host.threadForTesting)
            hosts.append((host, thread, port))
            host.stop()
        }
        for (host, thread, port) in hosts {
            #expect(host.waitUntilExited(timeout: 2))
            #expect(Self.eventually { thread.isFinished })
            #expect(!CFMachPortIsValid(port))
        }
    }

    /// The race the issue's blast radius names: a stop that lands before the
    /// new thread has even reached its run loop.
    @Test func aStopRacingAStartStillEndsTheThread() throws {
        var hosts: [(EventTapThread, Thread, CFMachPort)] = []
        for _ in 0..<200 {
            let port = Self.makePort()
            let host = EventTapThread(name: "test.tap", setTapEnabled: EnableRecorder().setTapEnabled)
            host.start(tap: port)
            host.stop() // no wait between the two
            let thread = try #require(host.threadForTesting)
            hosts.append((host, thread, port))
        }
        for (host, thread, port) in hosts {
            #expect(host.waitUntilExited(timeout: 2))
            #expect(Self.eventually { thread.isFinished })
            #expect(!CFMachPortIsValid(port))
        }
    }

    @Test func aStopBeforeStartNeverEnablesTheTapAndStillExits() throws {
        let recorder = EnableRecorder()
        let port = Self.makePort()
        let host = EventTapThread(name: "test.tap", setTapEnabled: recorder.setTapEnabled)
        host.stop()
        host.start(tap: port)

        #expect(host.waitUntilExited(timeout: 2))
        #expect(!CFMachPortIsValid(port))
        #expect(!recorder.calls.contains { $0.enabled })
    }

    // MARK: - 3. stop() never waits on a stuck tap thread

    /// The freeze in HYPERWHISPER-YY was a `CGEvent.tapEnable` that did not
    /// return. Here the "enable" blocks until released: the caller of stop()
    /// — the main actor in production — must not wait for it.
    @MainActor
    @Test func stopReturnsAtOnceWhileTheTapThreadIsBlockedInAnEnable() async throws {
        let entered = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        let recorder = EnableRecorder()
        recorder.beforeEnable = {
            entered.signal()
            release.wait()
        }
        let port = Self.makePort()
        let host = EventTapThread(name: "test.tap", setTapEnabled: recorder.setTapEnabled)
        host.start(tap: port)
        #expect(await waitOffThePool(for: entered, seconds: 2))

        let startedAt = Date()
        host.stop()
        let stopTook = Date().timeIntervalSince(startedAt)
        #expect(stopTook < 0.25)
        #expect(!host.waitUntilExited(timeout: 0.1)) // still stuck, and that is fine

        release.signal()
        #expect(host.waitUntilExited(timeout: 2))
        #expect(!CFMachPortIsValid(port))
    }

    // MARK: - 4. The re-enable and its metadata

    @Test func theReEnableReportsReasonSlugAndElapsedMs() throws {
        #expect(EventTapThread.DisableReason.timeout.rawValue == "timeout")
        #expect(EventTapThread.DisableReason.userInput.rawValue == "user_input")
        #expect(EventTapThread.DisableReason.userInput.phrase == "user input")

        let recorder = EnableRecorder()
        let port = Self.makePort()
        let host = EventTapThread(name: "test.tap", setTapEnabled: recorder.setTapEnabled)
        host.start(tap: port)
        _ = try #require(host.waitUntilRunning(timeout: 2))
        #expect(Self.eventually { recorder.calls.count >= 1 })

        recorder.beforeEnable = { Thread.sleep(forTimeInterval: 0.05) }
        let reEnable = try #require(host.reEnableAfterSystemDisable(.tapDisabledByUserInput))
        #expect(reEnable.reason == .userInput)
        #expect(reEnable.elapsedMs >= 40)
        recorder.beforeEnable = nil

        #expect(host.reEnableAfterSystemDisable(.tapDisabledByTimeout)?.reason == .timeout)
        #expect(host.reEnableAfterSystemDisable(.keyDown) == nil)

        host.stop()
        #expect(host.reEnableAfterSystemDisable(.tapDisabledByUserInput) == nil)
        #expect(host.waitUntilExited(timeout: 2))
    }

    // MARK: - 5. The real callbacks, called off the main thread

    static let proxy = OpaquePointer(bitPattern: 0x1)!

    static func key(_ keyCode: CGKeyCode, down: Bool) throws -> CGEvent {
        try #require(CGEvent(keyboardEventSource: nil, virtualKey: keyCode, keyDown: down))
    }

    /// Runs `body` on a fresh background thread and returns its result.
    static func offMain<T>(_ body: @escaping () -> T) async -> T {
        await withCheckedContinuation { continuation in
            Thread.detachNewThread {
                precondition(!Thread.isMainThread)
                continuation.resume(returning: body())
            }
        }
    }

    final class Flag: @unchecked Sendable {
        let fired = DispatchSemaphore(value: 0)
        private let lock = NSLock()
        private var onMain = false
        func fire() {
            lock.lock(); onMain = Thread.isMainThread; lock.unlock()
            fired.signal()
        }
        var firedOnMain: Bool { lock.lock(); defer { lock.unlock() }; return onMain }
    }

    @Test func theOverlayCallbackSwallowsEscapeOffMainAndRunsTheHandlerOnMain() async throws {
        let escape = Flag()
        let returnKey = Flag()
        let host = EventTapThread(
            name: "test.overlay",
            context: CancelOverlayTapHandlers(onReturn: { returnKey.fire() }, onEscape: { escape.fire() })
        )
        let info = Unmanaged.passUnretained(host).toOpaque()
        let escDown = try Self.key(53, down: true)
        let escUp = try Self.key(53, down: false)
        let letterUp = try Self.key(0, down: false)

        let swallowedDown = await Self.offMain { cancelOverlayEventTapCallback(Self.proxy, .keyDown, escDown, info) == nil }
        let swallowedUp = await Self.offMain { cancelOverlayEventTapCallback(Self.proxy, .keyUp, escUp, info) == nil }
        let letterPassed = await Self.offMain {
            cancelOverlayEventTapCallback(Self.proxy, .keyUp, letterUp, info)?.takeUnretainedValue() === letterUp
        }
        #expect(swallowedDown)
        #expect(swallowedUp)
        #expect(letterPassed)
        #expect(await waitOffThePool(for: escape.fired, seconds: 2))
        #expect(escape.firedOnMain)

        // Once the overlay ends (the tap is stopped), Escape passes through
        // and no handler runs — the old `isOverlayVisible` guard.
        host.stop()
        let passedAfterStop = await Self.offMain {
            cancelOverlayEventTapCallback(Self.proxy, .keyUp, escUp, info)?.takeUnretainedValue() === escUp
        }
        #expect(passedAfterStop)
        #expect(!(await waitOffThePool(for: escape.fired, seconds: 0.3)))
        #expect(!(await waitOffThePool(for: returnKey.fired, seconds: 0.05)))
    }

    final class Box<T>: @unchecked Sendable {
        var value: T?
    }

    /// Runs `body` on a fresh background thread and BLOCKS the caller until
    /// it returns. From a `@MainActor` test this keeps the main queue from
    /// running anything the callback queued, so the test decides what happens
    /// on main before that block runs.
    static func offMainBlocking<T>(_ body: @escaping () -> T) -> T {
        let done = DispatchSemaphore(value: 0)
        let box = Box<T>()
        Thread.detachNewThread {
            box.value = body()
            done.signal()
        }
        done.wait()
        return box.value!
    }

    /// The user presses Return (or Escape) while main is busy: the tap thread
    /// swallows the key and queues its handler. Before that block runs, main
    /// ends the overlay (clicks "No"; `endOverlayKeyInterception` stops the
    /// tap, and a replacement goes through the same stop). The handler must
    /// not run — e.g. Return must not cancel a recording the user kept.
    @MainActor
    @Test(arguments: [CGKeyCode(53), CGKeyCode(36), CGKeyCode(76)])
    func aSwallowedKeyDoesNotRunItsHandlerOnceTheOverlayEnded(_ keyCode: CGKeyCode) async throws {
        let escape = Flag()
        let returnKey = Flag()
        let host = EventTapThread(
            name: "test.overlay.stale",
            context: CancelOverlayTapHandlers(onReturn: { returnKey.fire() }, onEscape: { escape.fire() })
        )
        let info = Unmanaged.passUnretained(host).toOpaque()
        let keyUp = try Self.key(keyCode, down: false)
        let handlerFired = keyCode == 53 ? escape.fired : returnKey.fired

        // Control (anti-vacuity): main held while the key is swallowed, tap
        // still live when the block runs -> the handler runs, on main.
        let swallowedLive = Self.offMainBlocking {
            cancelOverlayEventTapCallback(Self.proxy, .keyUp, keyUp, info) == nil
        }
        #expect(swallowedLive)
        #expect(await waitOffThePool(for: handlerFired, seconds: 2))
        #expect((keyCode == 53 ? escape : returnKey).firedOnMain)

        // The same key, but the overlay ends between the swallow and the
        // main-thread block.
        let swallowed = Self.offMainBlocking {
            cancelOverlayEventTapCallback(Self.proxy, .keyUp, keyUp, info) == nil
        }
        #expect(swallowed)
        host.stop()
        #expect(!(await waitOffThePool(for: handlerFired, seconds: 0.5)))
        #expect(!(await waitOffThePool(for: escape.fired, seconds: 0.05)))
        #expect(!(await waitOffThePool(for: returnKey.fired, seconds: 0.05)))
    }

    @Test func thePushToTalkCallbackPassesEventsThroughAndReEnablesOnTheCallingThread() async throws {
        let recorder = EnableRecorder()
        let port = Self.makePort()
        let host = EventTapThread(name: "test.ptt", setTapEnabled: recorder.setTapEnabled)
        host.start(tap: port)
        _ = try #require(host.waitUntilRunning(timeout: 2))
        #expect(Self.eventually { recorder.calls.count >= 1 })
        let info = Unmanaged.passUnretained(host).toOpaque()
        let keyDown = try Self.key(0, down: true)

        let passed = await Self.offMain {
            bareModifierEventTapCallback(Self.proxy, .keyDown, keyDown, info)?.takeUnretainedValue() === keyDown
        }
        #expect(passed)

        let disabledPassed = await Self.offMain {
            bareModifierEventTapCallback(Self.proxy, .tapDisabledByUserInput, keyDown, info)?.takeUnretainedValue() === keyDown
        }
        #expect(disabledPassed)
        #expect(recorder.calls.count == 2)
        let reEnable = try #require(recorder.calls.last)
        #expect(reEnable.enabled)
        #expect(!reEnable.onMainThread)

        host.stop()
        #expect(host.waitUntilExited(timeout: 2))
    }

    // MARK: - 6. Neither production tap touches the main run loop

    static let tapFiles = [
        "app/macos/hyperwhisper/Utilities/BareModifierKeyMonitor.swift",
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingWindowManager.swift",
    ]

    @Test(arguments: tapFiles)
    func theProductionTapGoesThroughEventTapThread(_ path: String) throws {
        let code = try ProductionSource.code(of: path)
        // Anti-vacuity: the file still creates a tap.
        #expect(code.contains("CGEvent.tapCreate("))
        #expect(code.contains("EventTapThread("))
        #expect(code.contains(".start(tap: tap)"))
        // Nothing goes on the main run loop, and no enable happens outside
        // the tap thread.
        #expect(!code.contains("CFRunLoopGetMain()"))
        #expect(!code.contains("CFRunLoopAddSource("))
        #expect(!code.contains("CGEvent.tapEnable("))
        // Every tap -> main hop is the ordered one (test 7), not a Task.
        #expect(code.contains("EventTapThread.deliverOnMainInOrder"))
    }

    // MARK: - 7. Tap -> main hops keep their order

    final class OrderLog: @unchecked Sendable {
        let done = DispatchSemaphore(value: 0)
        private let lock = NSLock()
        private let expected: Int
        private var recorded: [Int] = []
        private var offMainCount = 0

        init(expected: Int) { self.expected = expected }

        func append(_ value: Int) {
            lock.lock()
            recorded.append(value)
            if !Thread.isMainThread { offMainCount += 1 }
            let finished = recorded.count == expected
            lock.unlock()
            if finished { done.signal() }
        }

        var values: [Int] { lock.lock(); defer { lock.unlock() }; return recorded }
        var allOnMain: Bool { lock.lock(); defer { lock.unlock() }; return offMainCount == 0 }
    }

    /// A burst of hops from one background thread (the tap thread's shape:
    /// press, release, press, ...) reaches main in exactly the order sent.
    /// A reversed press/release would leave push-to-talk recording.
    @MainActor
    @Test func everyTapHopReachesMainInTheOrderItWasSent() async throws {
        let count = 5_000
        let log = OrderLog(expected: count)
        await Self.offMain {
            for i in 0..<count {
                EventTapThread.deliverOnMainInOrder { log.append(i) }
            }
        }
        #expect(await waitOffThePool(for: log.done, seconds: 10))
        #expect(log.values == Array(0..<count))
        #expect(log.allOnMain)
    }
}
