//
//  RecorderStartGate.swift
//  hyperwhisper
//
//  Puts a deadline on the blocking CoreAudio work that starts a recorder.
//

import Foundation
import os

/// Runs blocking recorder-start work on a serial queue, and gives up waiting for it
/// after a deadline.
///
/// **Why this exists:** `AVAudioRecorder(url:settings:)` and `record()` block in
/// `mach_msg` on `coreaudiod`. When the daemon wedges (its IO thread fails to start,
/// `StartIOThread ... Error: 0x3C`), that call has been seen to block for minutes.
/// `SimpleRecorder` already runs it off the main actor, but nothing bounded the wait,
/// and every later hotkey press queued behind the stuck call on the same serial
/// queue. The user saw a shortcut that silently did nothing until the daemon
/// recovered, then a burst of superseded starts.
///
/// **What it does:**
/// - The caller stops waiting after `timeout` and gets
///   `AudioError.audioSystemNotResponding`, so the start fails with a message.
/// - The blocked work cannot be cancelled (CoreAudio offers no way), so it keeps
///   running. When it finally returns, its value goes to `discardLate` instead of
///   to anyone's recorder, so a late recorder never holds the microphone open.
/// - `hasAbandonedWork` stays true until every timed-out call has returned, so a
///   new start can fail at once instead of queueing behind a call already known
///   to be stuck.
///
/// `@unchecked Sendable`: the only mutable state is `abandonedCount`, behind a lock.
final class RecorderStartGate: @unchecked Sendable {

    private enum AttemptState: Sendable {
        case pending
        case finished
        case timedOut
    }

    private let queue: DispatchQueue
    private let timeout: DispatchTimeInterval

    /// Timed-out calls that are still blocked on `queue`.
    private let abandonedCount = OSAllocatedUnfairLock(initialState: 0)

    init(queue: DispatchQueue, timeout: DispatchTimeInterval) {
        self.queue = queue
        self.timeout = timeout
    }

    /// True while a call that already timed out is still blocked on the queue.
    /// Anything submitted now would wait behind it.
    var hasAbandonedWork: Bool {
        abandonedCount.withLock { $0 > 0 }
    }

    /// Run `work` on the queue and return its result, or throw
    /// `AudioError.audioSystemNotResponding` once `timeout` passes.
    ///
    /// - Parameter discardLate: receives the value of a call that succeeded only
    ///   after the caller had stopped waiting. Runs on the queue. A late call
    ///   that throws needs no cleanup here; `work` owns that.
    func run<T>(
        _ work: @escaping () throws -> T,
        discardLate: @escaping (T) -> Void
    ) async throws -> T {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<T, Error>) in
            // The attempt state and `abandonedCount` change in one critical section,
            // so the queue can never see `.timedOut` before the count went up. That
            // is why this is a lock and not the usual `ManagedAtomic<Bool>` guard.
            let state = OSAllocatedUnfairLock(initialState: AttemptState.pending)

            queue.async {
                let result = Result { try work() }
                let arrivedLate = state.withLock { current -> Bool in
                    if current == .timedOut { return true }
                    current = .finished
                    return false
                }

                guard arrivedLate else {
                    continuation.resume(with: result)
                    return
                }

                if case .success(let value) = result {
                    discardLate(value)
                }
                // MUTATION: decrement removed
            }

            DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + timeout) {
                let timedOut = state.withLock { current -> Bool in
                    guard current == .pending else { return false }
                    current = .timedOut
                    self.abandonedCount.withLock { $0 += 1 }
                    return true
                }
                if timedOut {
                    continuation.resume(throwing: AudioError.audioSystemNotResponding)
                }
            }
        }
    }
}
