//
//  ClipboardSnapshotDeadlineTests.swift
//  hyperwhisperTests
//
//  #879: the clipboard snapshot reads the pasteboard off the main actor and
//  gives up at a deadline. A stub provider stands in for a slow pasteboard owner
//  (a lazy promise the owning app renders on demand), so no test touches the
//  real pasteboard.
//

import AppKit
import Foundation
import os
import Testing
@testable import HyperWhisper

/// Holds a stub read until the test lets it go. Never longer than 5 s, so a
/// failed test cannot wedge the queue for the rest of the run.
private final class SnapshotStubGate: @unchecked Sendable {
    private let semaphore = DispatchSemaphore(value: 0)

    func wait() {
        _ = semaphore.wait(timeout: .now() + 5)
    }

    func release() {
        semaphore.signal()
    }
}

/// What the stub saw: how often it ran, whether any run was on the main thread,
/// whether it is still blocked, and the main-actor tick count when it began.
private final class SnapshotStubProbe: @unchecked Sendable {
    private struct State {
        var calls = 0
        var finished = 0
        var didRunOnMainThread = false
        var ticksAtStart = 0
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var calls: Int { state.withLock { $0.calls } }
    var finished: Int { state.withLock { $0.finished } }
    var didRunOnMainThread: Bool { state.withLock { $0.didRunOnMainThread } }
    var ticksAtStart: Int { state.withLock { $0.ticksAtStart } }

    func begin(ticks: Int) {
        state.withLock {
            $0.calls += 1
            $0.didRunOnMainThread = $0.didRunOnMainThread || Thread.isMainThread
            $0.ticksAtStart = ticks
        }
    }

    func end() {
        state.withLock { $0.finished += 1 }
    }
}

/// Counts main-actor ticks. Written only by a main-actor task, read from any
/// thread, so the stub can note the count at the moment it starts blocking.
private final class MainActorTicks: @unchecked Sendable {
    private let count = OSAllocatedUnfairLock(initialState: 0)

    var value: Int { count.withLock { $0 } }

    func tick() {
        count.withLock { $0 += 1 }
    }
}

private let stubSnapshot: [AccessibilityHelper.ClipboardItemData] = [
    AccessibilityHelper.ClipboardItemData(
        types: [.string],
        data: [.string: Data("clipboard snapshot stub #879".utf8)]
    )
]

@MainActor
@Suite(.serialized)
struct ClipboardSnapshotDeadlineTests {

    /// Short, so the suite stays fast. Production uses 1 s.
    private static let testDeadline: Duration = .milliseconds(150)

    private static func makeReader() -> ClipboardSnapshotReader {
        ClipboardSnapshotReader(
            queue: DispatchQueue(label: "com.hyperwhisper.tests.clipboard-snapshot.\(UUID().uuidString)"),
            deadline: testDeadline
        )
    }

    private static func waitUntil(
        _ condition: @escaping @Sendable () -> Bool
    ) async {
        for _ in 0..<5_000 {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 1_000_000)
        }
        Issue.record("Timed out while waiting for the stub pasteboard read")
    }

    /// Starts a main-actor task that ticks every ~2 ms until cancelled. If the
    /// pasteboard read ran on the main actor, no tick could land while it blocks.
    private static func startTicking(_ ticks: MainActorTicks) -> Task<Void, Never> {
        Task { @MainActor in
            while !Task.isCancelled {
                ticks.tick()
                try? await Task.sleep(nanoseconds: 2_000_000)
            }
        }
    }

    // MARK: - TEMPORARY diagnostics for #1550 (removed before merge)

    private struct SlowScenario {
        var resultIsNil: Bool
        var calls: Int
        var finishedAtReturn: Int
        var elapsed: Duration
        var tickDelta: Int
        var ranOnMain: Bool
        var abandonedAtReturn: Bool
        var recovered: Bool
    }

    private static func runSlowScenario() async -> SlowScenario {
        let reader = makeReader()
        let probe = SnapshotStubProbe()
        let gate = SnapshotStubGate()
        let ticks = MainActorTicks()
        let ticker = startTicking(ticks)
        defer {
            ticker.cancel()
            gate.release()
        }
        let clock = ContinuousClock()
        let started = clock.now
        let result = await reader.snapshot(caller: "diag slow read") { _ in
            probe.begin(ticks: ticks.value)
            gate.wait()
            probe.end()
            return stubSnapshot
        }
        let elapsed = started.duration(to: clock.now)
        let ticksAtReturn = ticks.value
        let s = SlowScenario(
            resultIsNil: result == nil,
            calls: probe.calls,
            finishedAtReturn: probe.finished,
            elapsed: elapsed,
            tickDelta: ticksAtReturn - probe.ticksAtStart,
            ranOnMain: probe.didRunOnMainThread,
            abandonedAtReturn: reader.hasAbandonedRead,
            recovered: false
        )
        gate.release()
        for _ in 0..<5_000 {
            if probe.finished == 1 && !reader.hasAbandonedRead { break }
            try? await Task.sleep(nanoseconds: 1_000_000)
        }
        var out = s
        out.recovered = probe.finished == 1 && !reader.hasAbandonedRead
        return out
    }

    private static var cached: SlowScenario?

    private static func cachedScenario() async -> SlowScenario {
        if let c = cached { return c }
        let s = await runSlowScenario()
        cached = s
        return s
    }

    @Test func diag1550_A_stubStartedBeforeDeadline() async {
        let s = await Self.cachedScenario()
        #expect(s.calls == 1)
    }

    @Test func diag1550_B_resultNilAndStubStillBlocked() async {
        let s = await Self.cachedScenario()
        #expect(s.resultIsNil)
        #expect(s.finishedAtReturn == 0)
    }

    @Test func diag1550_C_elapsedAtLeast100ms() async {
        let s = await Self.cachedScenario()
        #expect(s.elapsed >= .milliseconds(100))
    }

    @Test func diag1550_D_elapsedUnder2s() async {
        let s = await Self.cachedScenario()
        #expect(s.elapsed < .seconds(2))
    }

    @Test func diag1550_E_mainActorTickedFiveTimes() async {
        let s = await Self.cachedScenario()
        #expect(s.tickDelta >= 5)
    }

    @Test func diag1550_F_mainActorTickedOnce() async {
        let s = await Self.cachedScenario()
        #expect(s.tickDelta >= 1)
    }

    @Test func diag1550_G_notOnMainThread() async {
        let s = await Self.cachedScenario()
        #expect(s.ranOnMain == false)
    }

    @Test func diag1550_H_abandonedAtReturn() async {
        let s = await Self.cachedScenario()
        #expect(s.abandonedAtReturn)
    }

    @Test func diag1550_I_recovered() async {
        let s = await Self.cachedScenario()
        #expect(s.recovered)
    }

    @Test func productionDeadlineIsOneSecond() {
        // Ray, 2026-10-07 (#879, inbox ask #227): 1 s on both paths.
        #expect(ClipboardSnapshotReader.productionDeadline == .seconds(1))
    }

    /// Done when (#879): with a stub that blocks past the deadline, the call
    /// returns nil AT the deadline, not after the stub finishes, and the main
    /// actor keeps running while the stub blocks.
    @Test func slowReadReturnsNilAtDeadlineWhileMainActorKeepsRunning() async {
        let reader = Self.makeReader()
        let probe = SnapshotStubProbe()
        let gate = SnapshotStubGate()
        let ticks = MainActorTicks()
        let ticker = Self.startTicking(ticks)
        defer {
            ticker.cancel()
            gate.release()
        }

        let clock = ContinuousClock()
        let started = clock.now
        let result = await reader.snapshot(caller: "test slow read") { _ in
            probe.begin(ticks: ticks.value)
            gate.wait()
            probe.end()
            return stubSnapshot
        }
        let elapsed = started.duration(to: clock.now)
        let ticksAtReturn = ticks.value

        // (a) nil at the deadline, while the stub is still blocked.
        #expect(result == nil)
        #expect(probe.calls == 1)
        #expect(probe.finished == 0, "the call waited for the stub to finish instead of returning at the deadline")
        #expect(elapsed >= .milliseconds(100), "returned before the deadline: \(elapsed)")
        #expect(elapsed < .seconds(2), "returned long after the deadline: \(elapsed)")

        // (b) the main actor was free while the stub blocked.
        #expect(probe.didRunOnMainThread == false)
        #expect(ticksAtReturn - probe.ticksAtStart >= 5,
                "the main actor ticked \(ticksAtReturn - probe.ticksAtStart) times while the stub blocked")
        MainActor.assertIsolated()

        // The late result is thrown away, and the reader is clear afterwards.
        #expect(reader.hasAbandonedRead)
        gate.release()
        await Self.waitUntil { probe.finished == 1 && !reader.hasAbandonedRead }
        #expect(reader.hasAbandonedRead == false)
    }

    /// A healthy read returns its data, and reads off the main thread.
    @Test func fastReadReturnsItsSnapshot() async {
        let reader = Self.makeReader()
        let probe = SnapshotStubProbe()

        let result = await reader.snapshot(caller: "test fast read") { _ in
            probe.begin(ticks: 0)
            probe.end()
            return stubSnapshot
        }

        #expect(result?.count == 1)
        #expect(result?.first?.data[.string] == Data("clipboard snapshot stub #879".utf8))
        #expect(probe.didRunOnMainThread == false)
        #expect(reader.hasAbandonedRead == false)
    }

    /// While a read that passed its deadline is still blocked, the next call
    /// returns nil at once instead of queueing behind it, and the next recording
    /// after the stuck read returns gets a real snapshot again.
    @Test func stuckReadDoesNotHoldUpTheNextCall() async {
        let reader = Self.makeReader()
        let stuckProbe = SnapshotStubProbe()
        let gate = SnapshotStubGate()
        defer { gate.release() }

        let first = await reader.snapshot(caller: "test stuck read") { _ in
            stuckProbe.begin(ticks: 0)
            gate.wait()
            stuckProbe.end()
            return stubSnapshot
        }
        #expect(first == nil)
        #expect(reader.hasAbandonedRead)

        let skippedProbe = SnapshotStubProbe()
        let clock = ContinuousClock()
        let started = clock.now
        let second = await reader.snapshot(caller: "test read behind a stuck read") { _ in
            skippedProbe.begin(ticks: 0)
            skippedProbe.end()
            return stubSnapshot
        }
        let elapsed = started.duration(to: clock.now)

        #expect(second == nil)
        #expect(skippedProbe.calls == 0, "the call queued a read behind the stuck one")
        #expect(elapsed < Self.testDeadline, "the call waited behind the stuck read: \(elapsed)")

        gate.release()
        await Self.waitUntil { !reader.hasAbandonedRead }

        let third = await reader.snapshot(caller: "test read after recovery") { _ in stubSnapshot }
        #expect(third?.count == 1)
    }

    /// A call that queued behind a slow read and passed its own deadline before
    /// reaching the front never touches the pasteboard, so reads do not pile up
    /// on the serial queue.
    @Test func queuedReadThatPassedItsDeadlineSkipsTheRead() async {
        let reader = Self.makeReader()
        let firstProbe = SnapshotStubProbe()
        let queuedProbe = SnapshotStubProbe()
        let gate = SnapshotStubGate()
        defer { gate.release() }

        let first = Task {
            await reader.snapshot(caller: "test slow read") { _ in
                firstProbe.begin(ticks: 0)
                gate.wait()
                firstProbe.end()
                return stubSnapshot
            }
        }
        await Self.waitUntil { firstProbe.calls == 1 }

        // The first read is in flight but not yet past its deadline, so this one
        // queues behind it.
        let queued = Task {
            await reader.snapshot(caller: "test queued read") { _ in
                queuedProbe.begin(ticks: 0)
                queuedProbe.end()
                return stubSnapshot
            }
        }

        let firstResult = await first.value
        let queuedResult = await queued.value
        #expect(firstResult == nil)
        #expect(queuedResult == nil)

        gate.release()
        await Self.waitUntil { !reader.hasAbandonedRead }

        #expect(firstProbe.finished == 1)
        #expect(queuedProbe.calls == 0, "a read whose caller had given up still ran")
    }

}
