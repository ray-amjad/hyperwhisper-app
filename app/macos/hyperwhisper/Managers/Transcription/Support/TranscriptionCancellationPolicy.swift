//
//  TranscriptionCancellationPolicy.swift
//  hyperwhisper
//
//  Decides whether an error thrown out of a transcription provider is a genuine
//  caller-initiated cancellation (benign: no Sentry, no error toast) or a real
//  provider failure that must stay visible.
//
//  A free namespace rather than a member of `AppleSpeechAnalyzerProvider`: that
//  type is `@available(macOS 26.0, *)` inside `#if canImport(Speech)`, which
//  would make this untestable.
//

import Foundation

/// Classifies an error caught in a transcription provider as either a genuine
/// cancellation or a provider failure.
///
/// ## Why AND, not OR
///
/// A `CancellationError` on its own does not mean the caller cancelled. Apple
/// Speech tears its own analyzer down via `cancelAndFinishNow()` while a
/// consumer is still reading `transcriber.results`, which ends that stream with
/// a `CancellationError` on a task nobody cancelled. The user gets no transcript
/// there, so it must stay reported — treating a bare `CancellationError` as
/// benign is what hid **HYPERWHISPER-SQ**
/// (`TranscriptionPipeline.transcribeWithDetails failed`, 8 users).
///
/// So the task-cancellation flag is ANDed in, not ORed:
/// `cancellationErrorOnALiveTaskIsStillAProviderFailure` locks that down.
///
/// - Note: The codebase has several cancellation classifiers with different
///   semantics (e.g. `HyperWhisperCloudManager.isCancellationError(_:)`, which
///   ORs). Consolidating them is a worthwhile follow-up — the tests here define
///   the semantics any consolidation has to preserve.
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
    ///   - isTaskCancelled: The value of `Task.isCancelled`. `Task.isCancelled`
    ///     is task-local, so the caller must read it inside that same `catch`
    ///     block; reading it here would answer for the wrong task.
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
