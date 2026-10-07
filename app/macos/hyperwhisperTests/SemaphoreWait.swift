//
//  SemaphoreWait.swift
//  hyperwhisperTests
//

import Foundation

/// Waits for `semaphore` on a thread of its own, and returns whether it was
/// signalled within `seconds`.
///
/// Do not wait on a semaphore inside `Task.detached` in a test. That runs on
/// the Swift concurrency pool, which has one thread per core and never adds a
/// thread for a blocked one. A few tests parked there for seconds stall every
/// suite that runs beside them: a main-actor test's nonisolated `async` call,
/// a continuation resumed by a timer, another test's `Task`. On a 3-core CI
/// runner that failed unrelated tests at random (PR #1201).
func waitOffThePool(for semaphore: DispatchSemaphore, seconds: Double) async -> Bool {
    await withCheckedContinuation { continuation in
        Thread.detachNewThread {
            continuation.resume(returning: semaphore.wait(timeout: .now() + seconds) == .success)
        }
    }
}

