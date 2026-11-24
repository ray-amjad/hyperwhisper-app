//
//  AppleSpeechTimeLimit.swift
//  hyperwhisper
//
//  Bounds Apple Speech's analysis step (STEP 5 of
//  `AppleSpeechAnalyzerProvider.transcribe`) so a framework call that never
//  ends cannot hang a dictation forever, or a Local API request until its
//  600 s server timeout (#1701, follow-up of #1515).
//
//  A free namespace rather than a member of `AppleSpeechAnalyzerProvider`: that
//  type is `@available(macOS 26.0, *)` inside `#if canImport(Speech)`, which
//  would make this untestable. Nothing here needs the Speech framework.
//

import Atomics
import Foundation

/// The time limit on Apple Speech's analysis, and the runner that enforces it.
///
/// ## Why not a task group
///
/// A `withThrowingTaskGroup` or `async let` scope waits for EVERY child before
/// it returns, timed out or not. A hung `analyzeSequence` is a framework call
/// that need not check cancellation, so a structured race would throw the
/// timeout and then sit waiting on the hung child anyway. `run` therefore puts
/// the operation in an unstructured `Task` and never waits on it after the
/// deadline: the caller gets the timeout, `onTimeout` is started to end the
/// hung work (the provider cancels its analyzer there), and the abandoned task
/// is left to finish, or not, on its own.
///
/// A caller cancellation likewise returns `CancellationError` at once instead
/// of waiting on work that may ignore it (#1707); the timer stays armed for
/// that abandoned work and runs `onTimeout` at the limit only if it is still
/// running then. The timer sleeps on `SuspendingClock`, so time the Mac spends
/// asleep does not count against the limit.
enum AppleSpeechTimeLimit {

    /// Thrown by `run` when the operation did not finish within the limit.
    struct TimedOut: Error, Equatable {
        let limit: Duration
    }

    /// The fixed part of the limit: analyzer start-up, finalisation, and slack
    /// for a slow machine. Ray's decision on #1701: 60 s + 1x the audio.
    static let baseSeconds: Double = 60

    /// The longest audio the limit counts. A guard, not a policy: a nonsense
    /// header (a tiny positive sample rate) must not make `Duration.seconds`
    /// overflow. 24 hours is far past any real recording.
    static let maxAudioSeconds: Double = 86_400

    // AUDIO DURATION:
    // Seconds of audio in a file of `frameCount` frames at `sampleRate`.
    // Returns 0 for a rate that is 0, negative or not finite, so the limit
    // falls back to the base alone instead of trapping or going infinite.
    static func audioDuration(frameCount: Int64, sampleRate: Double) -> Double {
        guard frameCount > 0, sampleRate.isFinite, sampleRate > 0 else { return 0 }
        let seconds = Double(frameCount) / sampleRate
        guard seconds.isFinite else { return maxAudioSeconds }
        return min(seconds, maxAudioSeconds)
    }

    // LIMIT:
    // 60 s + 1x the audio duration, in seconds. A duration that is negative or
    // not finite counts as 0; one past `maxAudioSeconds` is clamped to it.
    static func limitSeconds(forAudioDuration seconds: Double) -> Double {
        guard seconds.isFinite, seconds > 0 else { return baseSeconds }
        return baseSeconds + min(seconds, maxAudioSeconds)
    }

    // CLOCK:
    // The default timer sleeps on `SuspendingClock`, which stops while the Mac
    // sleeps. `Task.sleep(for:)` uses `ContinuousClock`, which keeps counting
    // through a lid-close, so a long import that spans one could hit the limit
    // on wake while its analysis was still making progress (#1707). The limit
    // is meant to bound work the Mac was awake to do.
    static let clock = SuspendingClock()

    /// The production timer: sleeps `limit` on `clock`, and throws when its
    /// task is cancelled.
    @Sendable static func sleepOnClock(_ limit: Duration) async throws {
        try await clock.sleep(for: limit)
    }

    // RUN:
    // Returns the operation's value, or rethrows its error, if it ends within
    // `limit`. Otherwise cancels it, starts `onTimeout`, and throws `TimedOut`
    // at once, without waiting for either of them to return.
    //
    // A caller cancellation cancels the operation and throws `CancellationError`
    // AT ONCE, without waiting for the operation to end (#1707). The #1515 hang
    // ignores cancellation, and the analyzer's own cancel does not end it
    // either, so waiting would hold a cancelled dictation or import until the
    // limit (about 61 min for a 1 h file). The provider maps that
    // `CancellationError`, read with `Task.isCancelled`, to a genuine
    // cancellation. This holds even when the task was already cancelled before
    // `run` was called.
    //
    // `onTimeout` does NOT run at the moment of a caller cancel: an operation
    // that honours the cancel ends on its own. The timer keeps running for the
    // abandoned operation, though: if the limit is up and it has STILL not
    // ended, `onTimeout` is started for it then (the same analyzer cleanup a
    // timeout does), and nothing is resumed. An operation that ends first,
    // by cancel or otherwise, cancels the timer.
    //
    // `sleep` is the timer. Production uses `sleepOnClock` (see CLOCK); tests
    // pass one they end by hand, so no assertion depends on a loaded runner's
    // clock (#2400). It must throw when its task is cancelled, as `Task.sleep`
    // does.
    static func run<T: Sendable>(
        limit: Duration,
        sleep: @escaping @Sendable (Duration) async throws -> Void = AppleSpeechTimeLimit.sleepOnClock,
        onTimeout: @escaping @Sendable () async -> Void,
        operation: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        let work = Task { try await operation() }
        // Three paths can resume the caller (the work ending, the timer firing,
        // the caller cancelling); whichever flips this first owns the one resume.
        let finished = ManagedAtomic<Bool>(false)
        // Set by the work watcher BEFORE it tries `finished`, so a timer that
        // finds `finished` taken and this still false knows the caller
        // cancelled and the abandoned work is still running.
        let workEnded = ManagedAtomic<Bool>(false)
        // Hands the continuation to `onCancel`, which can run before it exists.
        let cancelBox = ContinuationBox<T>()

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<T, Error>) in
                // `onCancel` already ran (the task was cancelled before `run`):
                // it found no continuation, so end here instead. This comes
                // BEFORE the work watcher starts, so a fast operation cannot
                // claim `finished` first and hand a cancelled caller a value.
                if !cancelBox.install(continuation),
                   finished.exchange(true, ordering: .acquiringAndReleasing) == false {
                    continuation.resume(throwing: CancellationError())
                }

                let timer = Task {
                    do {
                        try await sleep(limit)
                    } catch {
                        // Cancelled: the work ended first.
                        return
                    }
                    if finished.exchange(true, ordering: .acquiringAndReleasing) == false {
                        work.cancel()
                        // Not awaited: the hook may touch the very thing that hung.
                        Task { await onTimeout() }
                        continuation.resume(throwing: TimedOut(limit: limit))
                    } else if workEnded.load(ordering: .acquiring) == false {
                        // The caller cancelled and was resumed already, but the
                        // abandoned work is still running at the limit: clean it
                        // up as a timeout would. Nothing left to resume.
                        Task { await onTimeout() }
                    }
                }

                Task {
                    let result = await work.result
                    workEnded.store(true, ordering: .releasing)
                    timer.cancel()
                    guard finished.exchange(true, ordering: .acquiringAndReleasing) == false else { return }
                    continuation.resume(with: result)
                }
            }
        } onCancel: {
            work.cancel()
            if let continuation = cancelBox.cancel(),
               finished.exchange(true, ordering: .acquiringAndReleasing) == false {
                continuation.resume(throwing: CancellationError())
            }
        }
    }

    /// Holds the caller's continuation for `onCancel`, which may run before
    /// the continuation is made. Whichever side comes second sees the other:
    /// `install` after a cancel returns false, `cancel` after an install
    /// returns the continuation. The resume itself is still guarded by
    /// `finished`.
    private final class ContinuationBox<T>: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<T, Error>?
        private var cancelled = false

        /// Stores `continuation`; false if the caller has already cancelled.
        func install(_ continuation: CheckedContinuation<T, Error>) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            if cancelled { return false }
            self.continuation = continuation
            return true
        }

        /// Marks the caller cancelled; returns the continuation if installed.
        func cancel() -> CheckedContinuation<T, Error>? {
            lock.lock()
            defer { lock.unlock() }
            cancelled = true
            let stored = continuation
            continuation = nil
            return stored
        }
    }
}
