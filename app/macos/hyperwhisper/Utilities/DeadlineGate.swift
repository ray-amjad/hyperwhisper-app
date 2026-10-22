//
//  DeadlineGate.swift
//  hyperwhisper
//
//  Runs blocking work on a serial queue and stops waiting for it at a deadline.
//

import Foundation
import os

/// Runs blocking work on a serial queue, and gives up waiting for it after a
/// deadline. The ONE implementation of that protocol: `RecorderStartGate`
/// (CoreAudio recorder start) and `ClipboardSnapshotReader` (#879, pasteboard
/// read) are thin wrappers around it, so a fix to the race or timing logic
/// here reaches both.
///
/// - The caller stops waiting after `timeout` and gets `.timedOut`.
/// - Blocking work cannot be cancelled, so it keeps running. When it finally
///   returns, its value goes to `discardLate` instead of to the caller.
/// - `hasAbandonedWork` stays true until every timed-out call has left the
///   queue, so a wrapper can refuse new work at once instead of queueing it
///   behind a call already known to be stuck.
/// - `queuedPastDeadline` decides what happens to work whose caller timed out
///   before the work even reached the front of the queue: `.runAnyway` runs it
///   (and hands its value to `discardLate`), `.skip` never runs it.
///
/// `@unchecked Sendable`: the only mutable state is `abandonedCount`, behind a lock.
final class DeadlineGate: @unchecked Sendable {

    enum Outcome<T> {
        case finished(T)
        case timedOut
    }

    /// What to do with work whose caller already timed out while it was still
    /// waiting on the queue.
    enum QueuedPastDeadline: Sendable {
        /// Run it anyway; a success goes to `discardLate`.
        case runAnyway
        /// Do not run it. `discardLate` is not called.
        case skip
    }

    private enum AttemptState: Sendable {
        case pending
        case finished
        case timedOut
    }

    private let queue: DispatchQueue
    private let timeout: DispatchTimeInterval
    private let queuedPastDeadline: QueuedPastDeadline

    /// Timed-out calls whose block has not left `queue` yet.
    private let abandonedCount = OSAllocatedUnfairLock(initialState: 0)

    init(queue: DispatchQueue, timeout: DispatchTimeInterval, queuedPastDeadline: QueuedPastDeadline) {
        self.queue = queue
        self.timeout = timeout
        self.queuedPastDeadline = queuedPastDeadline
    }

    /// True while a call that already timed out is still on the queue.
    /// Anything submitted now would wait behind it.
    var hasAbandonedWork: Bool {
        abandonedCount.withLock { $0 > 0 }
    }

    /// Run `work` on the queue and return `.finished` with its value, or
    /// `.timedOut` once `timeout` passes.
    ///
    /// - Parameter discardLate: receives the value of a call that finished only
    ///   after the caller had stopped waiting. Runs on the queue, before the
    ///   abandoned count goes down.
    func run<T>(
        _ work: @escaping () -> T,
        discardLate: @escaping (T) -> Void
    ) async -> Outcome<T> {
        await withCheckedContinuation { (continuation: CheckedContinuation<Outcome<T>, Never>) in
            // The attempt state and `abandonedCount` change in one critical section,
            // so the queue can never see `.timedOut` before the count went up. That
            // is why this is a lock and not the usual `ManagedAtomic<Bool>` guard.
            let state = OSAllocatedUnfairLock(initialState: AttemptState.pending)

            queue.async {
                // `.timedOut` is terminal, so a check here cannot race the timer
                // into a double resume: the timer already resumed the caller.
                if self.queuedPastDeadline == .skip, state.withLock({ $0 == .timedOut }) {
                    self.abandonedCount.withLock { $0 -= 1 }
                    return
                }

                let value = work()
                let arrivedLate = state.withLock { current -> Bool in
                    if current == .timedOut { return true }
                    current = .finished
                    return false
                }

                guard arrivedLate else {
                    continuation.resume(returning: .finished(value))
                    return
                }

                discardLate(value)
                self.abandonedCount.withLock { $0 -= 1 }
            }

            DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + timeout) {
                let timedOut = state.withLock { current -> Bool in
                    guard current == .pending else { return false }
                    current = .timedOut
                    self.abandonedCount.withLock { $0 += 1 }
                    return true
                }
                if timedOut {
                    continuation.resume(returning: .timedOut)
                }
            }
        }
    }
}
