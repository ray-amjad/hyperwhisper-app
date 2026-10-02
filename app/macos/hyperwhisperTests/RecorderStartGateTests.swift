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

    private static func makeGate(timeoutMs: Int = 100) -> RecorderStartGate {
        RecorderStartGate(
            queue: DispatchQueue(label: "RecorderStartGateTests.\(UUID().uuidString)"),
            timeout: .milliseconds(timeoutMs)
        )
    }

    /// Wait on a thread of its own, so a blocked semaphore never starves the
    /// cooperative pool that every other suite shares.
    private static func wait(_ semaphore: DispatchSemaphore, seconds: Double = 5) async -> Bool {
        await waitOffThePool(for: semaphore, seconds: seconds)
    }

    @Test func workThatFinishesInTimeReturnsItsValue() async throws {
        let gate = Self.makeGate(timeoutMs: 2_000)
        let discarded = OSAllocatedUnfairLock(initialState: false)

        let value = try await gate.run({ 42 }, discardLate: { _ in discarded.withLock { $0 = true } })

        #expect(value == 42)
        #expect(discarded.withLock { $0 } == false)
        #expect(gate.hasAbandonedWork == false)
    }

    @Test func workThatThrowsInTimeRethrowsItsError() async {
        let gate = Self.makeGate(timeoutMs: 2_000)

        await #expect(throws: WorkFailed.self) {
            _ = try await gate.run({ () throws -> Int in throw WorkFailed() }, discardLate: { _ in })
        }
        #expect(gate.hasAbandonedWork == false)
    }

    @Test func blockedWorkTimesOutThenItsLateValueIsDiscarded() async throws {
        let gate = Self.makeGate()
        let release = DispatchSemaphore(value: 0)
        let discardedValue = OSAllocatedUnfairLock<Int?>(initialState: nil)
        let discardRan = DispatchSemaphore(value: 0)

        let started = ContinuousClock.now
        do {
            _ = try await gate.run({ () -> Int in
                _ = release.wait(timeout: .now() + 5)
                return 7
            }, discardLate: { value in
                discardedValue.withLock { $0 = value }
                discardRan.signal()
            })
            Issue.record("Expected the blocked start to time out")
        } catch AudioError.audioSystemNotResponding {
            // Expected.
        }

        // Returned at the deadline, not when the work finished.
        #expect(ContinuousClock.now - started < .seconds(3))
        #expect(gate.hasAbandonedWork)

        release.signal()
        #expect(await Self.wait(discardRan))
        #expect(discardedValue.withLock { $0 } == 7)
        // `discardLate` signals before the gate decrements its count on the queue.
        // A no-op run behind it on the same serial queue returns only after that.
        _ = try await gate.run({ 0 }, discardLate: { _ in })
        #expect(gate.hasAbandonedWork == false)
    }

    @Test func blockedWorkThatLaterThrowsIsNotDiscardedButStillClears() async throws {
        let gate = Self.makeGate()
        let release = DispatchSemaphore(value: 0)
        let discarded = OSAllocatedUnfairLock(initialState: false)

        await #expect(throws: AudioError.self) {
            _ = try await gate.run({ () throws -> Int in
                _ = release.wait(timeout: .now() + 5)
                throw WorkFailed()
            }, discardLate: { _ in discarded.withLock { $0 = true } })
        }
        #expect(gate.hasAbandonedWork)

        release.signal()
        // The late block runs on the gate's queue; a no-op run behind it on the same
        // serial queue returns only after the abandoned block has finished.
        _ = try await gate.run({ 0 }, discardLate: { _ in })

        #expect(discarded.withLock { $0 } == false)
        #expect(gate.hasAbandonedWork == false)
    }
}
