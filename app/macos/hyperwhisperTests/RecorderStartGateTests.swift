//
//  RecorderStartGateTests.swift
//  hyperwhisperTests
//

import Foundation
import os
import Testing
@testable import HyperWhisper

/// A wedged `coreaudiod` blocked `AVAudioRecorder.record()` for minutes, and every
/// hotkey press queued silently behind it. These pin the three things the gate
/// promises: the caller stops waiting at the deadline, a late result is handed to
/// `discardLate` and never to the caller, and new work is refused while a timed-out
/// call is still blocking the queue.
struct RecorderStartGateTests {

    private struct WorkFailed: Error {}

    /// How long blocked work waits for the test to release it. Only the test's
    /// `release.signal()` should end the block, so this is far past any deadline a
    /// loaded CI runner can miss (#1216).
    private static let blockCap: DispatchTimeInterval = .seconds(60)

    private static func makeQueue() -> DispatchQueue {
        DispatchQueue(label: "RecorderStartGateTests.\(UUID().uuidString)")
    }

    private static func makeGate(on queue: DispatchQueue = makeQueue(), timeoutMs: Int = 100) -> RecorderStartGate {
        RecorderStartGate(queue: queue, timeout: .milliseconds(timeoutMs))
    }

    /// Wait on a thread of its own, so a blocked semaphore never starves the
    /// cooperative pool that every other suite shares.
    private static func wait(_ semaphore: DispatchSemaphore, seconds: Double = 30) async -> Bool {
        await waitOffThePool(for: semaphore, seconds: seconds)
    }

    /// True once every block already on `queue` has finished. The queue is serial,
    /// so the marker runs only after an abandoned call and its count decrement.
    /// Not a `gate.run`: that has its own 100 ms deadline, which a loaded runner
    /// can miss.
    private static func drain(_ queue: DispatchQueue) async -> Bool {
        let drained = DispatchSemaphore(value: 0)
        queue.async { drained.signal() }
        return await wait(drained)
    }

    @Test func workThatFinishesInTimeReturnsItsValue() async throws {
        let gate = Self.makeGate(timeoutMs: 30_000)
        let discarded = OSAllocatedUnfairLock(initialState: false)

        let value = try await gate.run({ 42 }, discardLate: { _ in discarded.withLock { $0 = true } })

        #expect(value == 42)
        #expect(discarded.withLock { $0 } == false)
        #expect(gate.hasAbandonedWork == false)
    }

    @Test func workThatThrowsInTimeRethrowsItsError() async {
        let gate = Self.makeGate(timeoutMs: 30_000)

        await #expect(throws: WorkFailed.self) {
            _ = try await gate.run({ () throws -> Int in throw WorkFailed() }, discardLate: { _ in })
        }
        #expect(gate.hasAbandonedWork == false)
    }

    @Test func blockedWorkTimesOutThenItsLateValueIsDiscarded() async throws {
        let queue = Self.makeQueue()
        let gate = Self.makeGate(on: queue)
        let release = DispatchSemaphore(value: 0)
        let discardedValue = OSAllocatedUnfairLock<Int?>(initialState: nil)
        let discardRan = DispatchSemaphore(value: 0)

        do {
            _ = try await gate.run({ () -> Int in
                _ = release.wait(timeout: .now() + Self.blockCap)
                return 7
            }, discardLate: { value in
                discardedValue.withLock { $0 = value }
                discardRan.signal()
            })
            Issue.record("Expected the blocked start to time out")
        } catch AudioError.audioSystemNotResponding {
            // Expected.
        }

        // Returned at the deadline, not when the work finished: the work cannot
        // finish before `release` is signalled below. A wall-clock bound here
        // measured the CI runner's load, not the gate (#1216).
        #expect(gate.hasAbandonedWork)

        release.signal()
        #expect(await Self.wait(discardRan))
        #expect(discardedValue.withLock { $0 } == 7)
        // `discardLate` signals before the gate decrements its count on the queue,
        // so let the queue finish that block before reading the count.
        #expect(await Self.drain(queue))
        #expect(gate.hasAbandonedWork == false)
    }

    @Test func blockedWorkThatLaterThrowsIsNotDiscardedButStillClears() async throws {
        let queue = Self.makeQueue()
        let gate = Self.makeGate(on: queue)
        let release = DispatchSemaphore(value: 0)
        let discarded = OSAllocatedUnfairLock(initialState: false)

        await #expect(throws: AudioError.self) {
            _ = try await gate.run({ () throws -> Int in
                _ = release.wait(timeout: .now() + Self.blockCap)
                throw WorkFailed()
            }, discardLate: { _ in discarded.withLock { $0 = true } })
        }
        #expect(gate.hasAbandonedWork)

        release.signal()
        // The late block runs on the gate's queue; wait for it to finish.
        #expect(await Self.drain(queue))

        #expect(discarded.withLock { $0 } == false)
        #expect(gate.hasAbandonedWork == false)
    }
}
