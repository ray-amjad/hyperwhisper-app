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
/// `@unchecked Sendable`: no mutable state of its own; `DeadlineGate` owns the
/// lock-guarded attempt state and abandoned count. The deadline protocol lives
/// there, shared with `ClipboardSnapshotReader` (#879), so the two cannot drift.
/// The recorder keeps `.runAnyway`: a start whose caller timed out while it was
/// still queued runs as before, and its recorder goes to `discardLate`.
final class RecorderStartGate: @unchecked Sendable {

    private let gate: DeadlineGate

    init(queue: DispatchQueue, timeout: DispatchTimeInterval) {
        self.gate = DeadlineGate(queue: queue, timeout: timeout, queuedPastDeadline: .runAnyway)
    }

    /// True while a call that already timed out is still blocked on the queue.
    /// Anything submitted now would wait behind it.
    var hasAbandonedWork: Bool {
        gate.hasAbandonedWork
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
        let outcome = await gate.run({ Result { try work() } }, discardLate: { result in
            if case .success(let value) = result {
                discardLate(value)
            }
        })
        switch outcome {
        case .finished(let result):
            return try result.get()
        case .timedOut:
            throw AudioError.audioSystemNotResponding
        }
    }
}
