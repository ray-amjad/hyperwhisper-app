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
        // `URLError` is what URLSession actually throws, and it bridges to
        // NSURLErrorDomain / NSURLErrorCancelled — matching the precedent in
        // `HyperWhisperCloudLicenseRecoveryTests`.
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: URLError(.cancelled),
            isTaskCancelled: true
        )

        #expect(outcome == .genuineCancellation)
    }

    @Test func cancelledTaskWithANonCancelledURLErrorIsStillAProviderFailure() {
        // Pins the code check, not just the domain check: a request that timed
        // out while the task happened to be cancelled is a real failure.
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: URLError(.timedOut),
            isTaskCancelled: true
        )

        #expect(outcome == .providerFailure)
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
}
