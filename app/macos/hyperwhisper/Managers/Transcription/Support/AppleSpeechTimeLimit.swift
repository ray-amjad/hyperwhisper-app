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

    // RUN:
    // Returns the operation's value, or rethrows its error, if it ends within
    // `limit`. Otherwise cancels it, starts `onTimeout`, and throws `TimedOut`
    // at once, without waiting for either of them to return.
    //
    // A caller cancellation is forwarded to the operation, and `run` keeps
    // waiting for it, so an operation that honours cancellation ends with its
    // own `CancellationError` exactly as it did before the limit existed. The
    // timer keeps running meanwhile: an operation that ignores the
    // cancellation still ends with `TimedOut` at the limit.
    static func run<T: Sendable>(
        limit: Duration,
        onTimeout: @escaping @Sendable () async -> Void,
        operation: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        let work = Task { try await operation() }
        // Two paths can resume the caller (the work ending, the timer firing);
        // whichever flips this first owns the one resume.
        let finished = ManagedAtomic<Bool>(false)

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<T, Error>) in
                let timer = Task {
                    do {
                        try await Task.sleep(for: limit)
                    } catch {
                        // Cancelled: the work ended first.
                        return
                    }
                    guard finished.exchange(true, ordering: .acquiringAndReleasing) == false else { return }
                    work.cancel()
                    // Not awaited: the hook may touch the very thing that hung.
                    Task { await onTimeout() }
                    continuation.resume(throwing: TimedOut(limit: limit))
                }

                Task {
                    let result = await work.result
                    timer.cancel()
                    guard finished.exchange(true, ordering: .acquiringAndReleasing) == false else { return }
                    continuation.resume(with: result)
                }
            }
        } onCancel: {
            work.cancel()
        }
    }
}
