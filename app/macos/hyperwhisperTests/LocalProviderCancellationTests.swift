//
//  LocalProviderCancellationTests.swift
//  hyperwhisperTests
//
//  Covers the remaining local-provider arm of HYPERWHISPER-SQ: Parakeet,
//  Nemotron and Qwen3 ASR used to catch every error out of their engine and
//  re-wrap it as `TranscriptionError.providerNotAvailable`, so a user pressing
//  cancel became an `errorKind: provider_not_available` Sentry event plus an
//  error toast. Each of those three catch blocks now runs the same
//  `TranscriptionCancellationPolicy` check that PR #177 added to Apple Speech,
//  and throws `CancellationError()` on a genuine cancellation instead — which
//  the pipeline already maps to `.idle` with no capture.
//
//  Scope note: none of the three `transcribe(...)` methods can be driven here.
//  They build their engine internally (`ParakeetRuntime` / a per-call
//  `StreamingNemotronMultilingualAsrManager` / the `Qwen3AsrManager` actor),
//  with no injected closure or protocol seam, and each needs a multi-hundred-MB
//  downloaded CoreML model plus a real audio file to reach its catch block.
//  Rather than weaken a provider to make it drivable, these tests pin the
//  decision those catch blocks now make, for the error shapes these engines
//  actually throw, in both task states.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalProviderCancellationTests {

    // MARK: - The reported production shape

    @Test func parakeetCancellationErrorOnACancelledTaskIsBenign() {
        // The exact reported shape: FluidAudio propagates the `CancellationError`
        // out of `AsrManager.transcribe(_:decoderState:language:)` when the
        // caller cancels the transcription task, which the provider logged as
        // "Parakeet ... transcription failed: ... Swift.CancellationError error 1"
        // and re-threw as `.providerNotAvailable`.
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: CancellationError(),
            isTaskCancelled: true
        )

        #expect(outcome == .genuineCancellation)
    }

    // MARK: - Bare cancellation on a live task stays a failure

    @Test func bareCancellationErrorOnALiveTaskIsStillAProviderFailure() {
        // The three providers can each surface a `CancellationError` that no
        // caller asked for — `Qwen3AsrProvider.Runtime.ensureLoaded` throws one
        // itself when its load generation moved under a concurrent
        // `invalidateRuntime()`. The user gets no transcript on that route, so it
        // must stay reported. This is why the policy ANDs `Task.isCancelled` in
        // rather than ORing it, and why these three catch blocks must not be
        // "simplified" to a bare `catch is CancellationError`.
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: CancellationError(),
            isTaskCancelled: false
        )

        #expect(outcome == .providerFailure)
    }

    // MARK: - Model/network-backed cancellation

    @Test func urlCancelledErrorOnACancelledTaskIsBenign() {
        // Local providers still touch URLSession on the way to inference (model
        // fetch and preload inside FluidAudio), so a cancelled pass can surface
        // as NSURLErrorCancelled rather than a `CancellationError`.
        let error = NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled)

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: true
        )

        #expect(outcome == .genuineCancellation)
    }

    @Test func urlCancelledErrorOnALiveTaskIsStillAProviderFailure() {
        let error = NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled)

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: false
        )

        #expect(outcome == .providerFailure)
    }

    // MARK: - Ordinary engine failures keep reporting, both task states

    @Test func engineFailureOnALiveTaskIsAProviderFailure() {
        // A genuine decode/model failure out of the engine: unchanged behaviour,
        // still breadcrumbed and still surfaced as `.providerNotAvailable`.
        let error = NSError(
            domain: "FluidAudio",
            code: 5,
            userInfo: [NSLocalizedDescriptionKey: "Model inference failed"]
        )

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: false
        )

        #expect(outcome == .providerFailure)
    }

    @Test func engineFailureOnACancelledTaskIsStillAProviderFailure() {
        // The narrowing that keeps the fix honest: a cancellation racing in must
        // not launder a real engine failure into silence.
        let error = NSError(
            domain: "FluidAudio",
            code: 5,
            userInfo: [NSLocalizedDescriptionKey: "Model inference failed"]
        )

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: true
        )

        #expect(outcome == .providerFailure)
    }

    @Test func providerNotAvailableOnACancelledTaskIsStillAProviderFailure() {
        // These providers throw `.providerNotAvailable` from their own pre-flight
        // and runtime-load paths too. Such an error reaching a cancelled task
        // must keep its meaning rather than be swallowed as a cancellation.
        let error = TranscriptionError.providerNotAvailable(
            provider: "Parakeet",
            reason: "Failed to load Parakeet runtime"
        )

        let outcome = TranscriptionCancellationPolicy.outcome(
            for: error,
            isTaskCancelled: true
        )

        #expect(outcome == .providerFailure)
    }

    @Test func modelNotDownloadedOnACancelledTaskIsStillAProviderFailure() {
        let outcome = TranscriptionCancellationPolicy.outcome(
            for: TranscriptionError.modelNotDownloaded,
            isTaskCancelled: true
        )

        #expect(outcome == .providerFailure)
    }
}
