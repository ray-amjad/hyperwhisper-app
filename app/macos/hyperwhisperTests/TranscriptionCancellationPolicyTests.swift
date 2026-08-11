//
//  TranscriptionCancellationPolicyTests.swift
//  hyperwhisperTests
//
//  Pins the AND (not OR) between `CancellationError` and `Task.isCancelled`
//  that fixes HYPERWHISPER-SQ without hiding real Apple Speech failures.
//

import Foundation
import Testing
@testable import HyperWhisper

struct TranscriptionCancellationPolicyTests {

    @Test func cancelledTaskWithCancellationErrorIsAGenuineCancellation() {
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: CancellationError(),
            isTaskCancelled: true
        )

        #expect(outcome == .genuineCancellation)
    }

    @Test func cancellationErrorOnALiveTaskIsStillAProviderFailure() {
        // Regression test for the self-inflicted route: when
        // `analyzeSequence(from:)` returns no last sample time, the provider
        // calls `cancelAndFinishNow()` while `transcriber.results` is still
        // being consumed, which terminates that stream with a
        // `CancellationError` even though nothing cancelled the task. The user
        // gets no transcript, so this must stay visible in Sentry — treating a
        // bare `CancellationError` as benign is what would re-hide it.
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: CancellationError(),
            isTaskCancelled: false
        )

        #expect(outcome == .providerFailure)
    }

    @Test func cancelledTaskWithURLCancelledErrorIsAGenuineCancellation() {
        let error = NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled)

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: true
        )

        #expect(outcome == .genuineCancellation)
    }

    @Test func unrelatedErrorOnACancelledTaskIsStillAProviderFailure() {
        // A cancellation racing in must never launder an unrelated failure.
        let error = NSError(domain: "com.apple.SpeechAnalyzer", code: 3)

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: true
        )

        #expect(outcome == .providerFailure)
    }

    @Test func unrelatedErrorOnALiveTaskIsAProviderFailure() {
        let error = NSError(domain: "com.apple.SpeechAnalyzer", code: 3)

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: false
        )

        #expect(outcome == .providerFailure)
    }

    @Test func realAppleSpeechFailureReasonIsNotClassifiedTransient() {
        // The provider's `.providerNotAvailable` reason string is deliberately
        // unchanged by the HYPERWHISPER-SQ fix: the pipeline pattern-matches
        // that text to suppress transient provider-availability errors, and new
        // wording risks colliding with "network" / "connection" and silently
        // suppressing genuine defects. Pin that a real failure still reports.
        let error = TranscriptionError.providerNotAvailable(
            provider: "Apple Speech",
            reason: "Transcription failed: The operation could not be completed. (SpeechAnalyzer error 3.)"
        )

        guard case .providerNotAvailable(_, let reason) = error else {
            Issue.record("Expected a .providerNotAvailable error")
            return
        }

        #expect(TranscriptionPipeline.isTransientProviderAvailabilityReason(reason) == false)
    }
}
