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
/// failed test cannot wedge the queue for the rest of the run. (The slow-read
/// stub first waits up to 10 s for its main-actor job, so that one read can
/// hold the queue for up to 15 s.)
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
/// whether it is still blocked, and what the main-actor job it posted saw.
private final class SnapshotStubProbe: @unchecked Sendable {
    private struct State {
        var calls = 0
        var finished = 0
        var didRunOnMainThread = false
        var callerReturned = false
        /// nil until the stub has its answer: true when its main-actor job ran
        /// before the caller got its result, false when it ran after it or
        /// never ran inside the cap.
        var mainActorJobRanFirst: Bool?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var calls: Int { state.withLock { $0.calls } }
    var finished: Int { state.withLock { $0.finished } }
    var didRunOnMainThread: Bool { state.withLock { $0.didRunOnMainThread } }
    var mainActorJobRanFirst: Bool? { state.withLock { $0.mainActorJobRanFirst } }

    func begin() {
        state.withLock {
            $0.calls += 1
            $0.didRunOnMainThread = $0.didRunOnMainThread || Thread.isMainThread
        }
    }

    /// The test calls this on the main actor as soon as `snapshot` returns.
    func markCallerReturned() {
        state.withLock { $0.callerReturned = true }
    }

    /// Called from inside the blocked read: posts one job to the main actor and
    /// waits for it, then notes whether the caller had its result yet.
    ///
    /// The caller is on the main actor too, and its resume is queued there only
    /// at the deadline. The main queue runs jobs in order, so this job, queued
    /// as the read starts, runs first however busy the main actor is. If the
    /// call held the main actor until the deadline, the job runs after the
    /// caller's result instead. If the read itself ran on the main actor, the
    /// job cannot run until it returns, so the wait hits its cap.
    ///
    /// #1550: this replaced a count of main-actor ticks inside the 150 ms
    /// deadline. In the parallel CI run other suites keep the main actor busy,
    /// so that count fell short while the read was off the main actor.
    func noteWhetherMainActorRunsFirst() {
        let ran = DispatchSemaphore(value: 0)
        Task { @MainActor in
            self.state.withLock { $0.mainActorJobRanFirst = !$0.callerReturned }
            ran.signal()
        }
        if ran.wait(timeout: .now() + 10) == .timedOut {
            state.withLock { $0.mainActorJobRanFirst = false }
        }
    }

    func end() {
        state.withLock { $0.finished += 1 }
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

    /// Polls every ~1 ms, so `iterations` is a floor in milliseconds.
    private static func waitUntil(
        iterations: Int = 5_000,
        _ condition: @escaping @Sendable () -> Bool
    ) async {
        for _ in 0..<iterations {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 1_000_000)
        }
        Issue.record("Timed out while waiting for the stub pasteboard read")
    }

    /// Calls `snapshot` and times it OFF the main actor. #1550: timed on the
    /// main actor, the elapsed time also holds the wait for this test to get the
    /// main actor back, and in the parallel CI run that wait passed 2 s.
    private nonisolated static func timedSnapshot(
        _ reader: ClipboardSnapshotReader,
        caller: String,
        provider: @escaping ClipboardSnapshotReader.Provider
    ) async -> (snapshot: ClipboardSnapshotReader.Snapshot?, elapsed: Duration) {
        let clock = ContinuousClock()
        let started = clock.now
        let snapshot = await reader.snapshot(caller: caller, provider: provider)
        return (snapshot, started.duration(to: clock.now))
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
        defer { gate.release() }

        // Called from the main actor, as production calls it.
        let clock = ContinuousClock()
        let started = clock.now
        let result = await reader.snapshot(caller: "test slow read") { _ in
            probe.begin()
            probe.noteWhetherMainActorRunsFirst()
            gate.wait()
            probe.end()
            return stubSnapshot
        }
        probe.markCallerReturned()
        let elapsed = started.duration(to: clock.now)

        // (a) nil at the deadline, while the stub is still blocked. No upper
        // bound on `elapsed`: timed here, on the main actor, it also holds
        // the wait to get the main actor back, which passed 2 s in the
        // parallel CI run (#1550). `finished == 0` is the real check: the
        // call came back before the stub did.
        #expect(result == nil)
        #expect(probe.calls == 1)
        #expect(probe.finished == 0, "the call waited for the stub to finish instead of returning at the deadline")
        #expect(elapsed >= .milliseconds(100), "returned before the deadline: \(elapsed)")

        // (b) the main actor was free while the stub blocked: the job the stub
        // posted ran before this test got its result. The stub waits up to
        // 10 s for that job, so wait longer than that for its answer.
        #expect(probe.didRunOnMainThread == false)
        await Self.waitUntil(iterations: 15_000) { probe.mainActorJobRanFirst != nil }
        #expect(probe.mainActorJobRanFirst == true,
                "the main actor did not run the stub's job before the call returned")
        #expect(probe.finished == 0)
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
            probe.begin()
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
            stuckProbe.begin()
            gate.wait()
            stuckProbe.end()
            return stubSnapshot
        }
        #expect(first == nil)
        #expect(reader.hasAbandonedRead)

        let skippedProbe = SnapshotStubProbe()
        let (second, elapsed) = await Self.timedSnapshot(reader, caller: "test read behind a stuck read") { _ in
            skippedProbe.begin()
            skippedProbe.end()
            return stubSnapshot
        }

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
                firstProbe.begin()
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
                queuedProbe.begin()
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
