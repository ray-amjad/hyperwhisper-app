//
//  DeadlineGateTests.swift
//  hyperwhisperTests
//
//  `DeadlineGate` is shared by `RecorderStartGate` and `ClipboardSnapshotReader`
//  (#879). Their own suites cover the shared protocol; this pins the one policy
//  that differs between them: work whose caller timed out while it was still
//  queued.
//

import Foundation
import os
import Testing
@testable import HyperWhisper

struct DeadlineGateTests {

    private static let blockCap: DispatchTimeInterval = .seconds(60)

    private static func drain(_ queue: DispatchQueue) async -> Bool {
        let drained = DispatchSemaphore(value: 0)
        queue.async { drained.signal() }
        return await waitOffThePool(for: drained, seconds: 30)
    }

    /// Blocks the queue past the deadline with a first call, then queues a second
    /// call that also times out before it reaches the front. Returns whether the
    /// second call's work ran and whether its value reached `discardLate`.
    private static func queuedCallPastDeadline(
        policy: DeadlineGate.QueuedPastDeadline
    ) async -> (ran: Bool, discarded: Bool) {
        let queue = DispatchQueue(label: "DeadlineGateTests.\(UUID().uuidString)")
        let gate = DeadlineGate(queue: queue, timeout: .milliseconds(100), queuedPastDeadline: policy)
        let release = DispatchSemaphore(value: 0)
        let ran = OSAllocatedUnfairLock(initialState: false)
        let discarded = OSAllocatedUnfairLock(initialState: false)

        let started = DispatchSemaphore(value: 0)
        let first = Task {
            await gate.run({ () -> Int in
                started.signal()
                _ = release.wait(timeout: .now() + blockCap)
                return 1
            }, discardLate: { _ in })
        }
        // The second call must queue BEHIND the blocked first one.
        #expect(await waitOffThePool(for: started, seconds: 30))
        let second = Task {
            await gate.run({ () -> Int in
                ran.withLock { $0 = true }
                return 2
            }, discardLate: { _ in discarded.withLock { $0 = true } })
        }

        let firstOutcome = await first.value
        let secondOutcome = await second.value
        if case .finished = firstOutcome { Issue.record("Expected the blocked call to time out") }
        if case .finished = secondOutcome { Issue.record("Expected the queued call to time out") }

        release.signal()
        #expect(await drain(queue))
        #expect(gate.hasAbandonedWork == false)
        return (ran.withLock { $0 }, discarded.withLock { $0 })
    }

    /// The recorder's policy: unchanged from before the gate was shared.
    @Test func runAnywayRunsQueuedWorkAndDiscardsItsValue() async {
        let result = await Self.queuedCallPastDeadline(policy: .runAnyway)
        #expect(result.ran)
        #expect(result.discarded)
    }

    /// The clipboard reader's policy: a queued read whose caller gave up never runs.
    @Test func skipNeverRunsQueuedWork() async {
        let result = await Self.queuedCallPastDeadline(policy: .skip)
        #expect(result.ran == false)
        #expect(result.discarded == false)
    }
}
