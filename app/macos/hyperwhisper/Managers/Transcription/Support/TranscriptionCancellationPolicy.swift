//
//  TranscriptionCancellationPolicy.swift
//  hyperwhisper
//
//  CANCELLATION VS FAILURE DECISION
//  Decides whether an error thrown out of a transcription provider is a
//  genuine caller-initiated cancellation (benign, no Sentry, no error toast)
//  or a real provider failure that must stay visible.
//
//  Key Features:
//  - Pure function: the task-cancellation flag is injected, never read here
//  - `CancellationError` alone is NOT enough — the task must also be cancelled
//  - Also treats `NSURLErrorCancelled` as a cancellation, for URL-backed work
//
//  Architecture Notes:
//  - Deliberately a free namespace rather than a member of
//    `AppleSpeechAnalyzerProvider`: that type is `@available(macOS 26.0, *)`
//    inside `#if canImport(Speech)`, which would make this untestable.
//  - `isTaskCancelled` must be read by the caller, at the point the error is
//    caught: `Task.isCancelled` is task-local, so reading it in here would
//    answer for the wrong task.
//

import Foundation

/// Classifies an error caught in a transcription provider as either a genuine
/// cancellation or a provider failure.
///
/// ## Why this is not `HyperWhisperCloudManager.isCancellationError(_:)`
///
/// That helper (`Managers/HyperWhisperCloudManager.swift`) **ORs**
/// `Task.isCancelled` in: `error is CancellationError` on its own already
/// returns `true` there, regardless of whether anything actually cancelled the
/// task. Those are exactly the semantics that produced **HYPERWHISPER-SQ**
/// (`TranscriptionPipeline.transcribeWithDetails failed`, 8 users): a
/// `CancellationError` can also surface with `Task.isCancelled == false` when
/// the Apple Speech provider tears its own analyzer down via
/// `cancelAndFinishNow()` while a consumer is still reading
/// `transcriber.results`. That is a real failure — the user gets no transcript
/// — and must not be swallowed.
///
/// This policy therefore **ANDs** the flag in: a bare `CancellationError` with
/// a live (non-cancelled) task is a `.providerFailure`.
///
/// - Important: The two must **not** be "unified" later. The difference in
///   boolean operator is the fix, not an inconsistency to clean up.
enum TranscriptionCancellationPolicy {

    /// The outcome of classifying a caught transcription error.
    enum Outcome: Equatable {
        /// The caller cancelled the work; report nothing and stay silent.
        case genuineCancellation
        /// The provider failed; log, breadcrumb and surface the error.
        case providerFailure
    }

    /// Classifies a caught error.
    ///
    /// - Parameters:
    ///   - error: The error caught in the provider's `catch` block.
    ///   - isTaskCancelled: The value of `Task.isCancelled`, read once by the
    ///     caller inside that same `catch` block.
    /// - Returns: `.genuineCancellation` only when the task really was
    ///   cancelled *and* the error is a cancellation error.
    static func outcome(for error: Error, isTaskCancelled: Bool) -> Outcome {
        guard isTaskCancelled else { return .providerFailure }
        if error is CancellationError { return .genuineCancellation }
        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
            return .genuineCancellation
        }
        return .providerFailure
    }
}
