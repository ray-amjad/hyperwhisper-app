//
//  PostProcessingPreflightFailureTests.swift
//  hyperwhisperTests
//
//  Issue #1538: a Local LLM mode whose llama-server cannot start lost the
//  finished transcript. The post-processing pre-flight ran alongside
//  transcription, and once the transcript existed the pipeline awaited it with
//  a bare `try await`, so `.localRuntimeUnavailable` left the function and the
//  History row was saved `failed` with no text — while the alert said "your raw
//  transcript was kept".
//
//  The decision now lives in `TranscriptionPipeline.awaitPostProcessingPreflight`,
//  which these tests call with fake checks. The last test is a wiring guard: it
//  reads the pipeline's source to prove the after-transcript await goes through
//  that decision and not around it (`ProductionSource` explains why that is the
//  last resort, and why the decision itself is tested by calling it).
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Post-processing pre-flight failure keeps the transcript (#1538)")
struct PostProcessingPreflightFailureTests {

    private static let pipelineSource =
        "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift"

    // MARK: - The decision

    @Test func localRuntimeUnavailableIsNonFatal() {
        #expect(TranscriptionPipeline.isNonFatalPostProcessingPreflightError(
            TranscriptionError.localRuntimeUnavailable(reason: "Local runtime did not become ready in time.")
        ))
    }

    /// Every error the cloud / BYOK pre-flight can throw
    /// (`TranscriptionProviderRouter.errorForHealthStatus`) stays fatal, and so
    /// does cancellation and any error that is not a `TranscriptionError`.
    @Test func otherPreflightErrorsStayFatal() {
        let fatal: [TranscriptionError] = [
            .unauthorized(provider: "OpenAI"),
            .transientNetwork(details: "Provider unreachable"),
            .modelNotDownloaded,
            .providerNotAvailable(provider: "OpenAI", reason: "Provider health check failed"),
            .localSpeechModelEvicted(model: "base")
        ]
        for error in fatal {
            #expect(
                !TranscriptionPipeline.isNonFatalPostProcessingPreflightError(error),
                "\(error) must stay fatal"
            )
        }
    }

    @Test func cancellationAndForeignErrorsStayFatal() {
        #expect(!TranscriptionPipeline.isNonFatalPostProcessingPreflightError(CancellationError()))
        #expect(!TranscriptionPipeline.isNonFatalPostProcessingPreflightError(URLError(.timedOut)))
    }

    // MARK: - Awaiting the check

    @MainActor
    @Test func aPassingCheckReportsNothing() async throws {
        let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {}
        #expect(failure == nil)
    }

    /// The bug itself: the check throws `.localRuntimeUnavailable` and the
    /// await must hand the error back to report, not throw it.
    @MainActor
    @Test func aLocalRuntimeFailureIsReturnedNotThrown() async throws {
        let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {
            throw TranscriptionError.localRuntimeUnavailable(reason: "dyld: Library not loaded")
        }
        let returned = try #require(failure)
        guard case .localRuntimeUnavailable(let reason) = returned else {
            Issue.record("Expected .localRuntimeUnavailable, got \(returned)")
            return
        }
        #expect(reason == "dyld: Library not loaded")
    }

    /// The same failure, arriving the way the pipeline actually sees it: as the
    /// value of the concurrent pre-flight `Task` started before transcription.
    @MainActor
    @Test func aLocalRuntimeFailureFromTheConcurrentTaskIsReturned() async throws {
        let preflight = Task<Void, Error> {
            throw TranscriptionError.localRuntimeUnavailable(reason: "manager unavailable")
        }
        let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {
            try await preflight.value
        }
        #expect(failure != nil)
    }

    @MainActor
    @Test func aCloudProviderFailureIsStillThrown() async {
        do {
            _ = try await TranscriptionPipeline.awaitPostProcessingPreflight {
                throw TranscriptionError.unauthorized(provider: "Anthropic", statusCode: 401)
            }
            Issue.record("A cloud pre-flight failure must still fail the run")
        } catch let error as TranscriptionError {
            guard case .unauthorized(let provider, let statusCode) = error else {
                Issue.record("Expected the original .unauthorized, got \(error)")
                return
            }
            #expect(provider == "Anthropic")
            #expect(statusCode == 401)
        } catch {
            Issue.record("Expected the original TranscriptionError, got \(error)")
        }
    }

    @MainActor
    @Test func cancellationIsStillThrown() async {
        do {
            _ = try await TranscriptionPipeline.awaitPostProcessingPreflight {
                throw CancellationError()
            }
            Issue.record("Cancellation must still end the run")
        } catch {
            #expect(error is CancellationError)
        }
    }

    // MARK: - Wiring

    /// The after-transcript await in `transcribeWithDetails` goes through the
    /// decision above, and a skipped pass neither runs the AI step nor reads
    /// the stale `didMutateLastRun` flag. On main this region was a bare
    /// `try await preflightHealthCheck.value`.
    @Test func thePipelineAwaitsThePreflightThroughTheDecision() throws {
        let region = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "capturedShouldRunPostProcessing = shouldRunPostProcessing",
            to: "AppLogger.transcription.info(\"🔍 Post-processing check:\")"
        )
        #expect(region.contains("Self.awaitPostProcessingPreflight"))
        #expect(region.contains("reportNonFatalPostProcessingError("))
        #expect(region.contains("let runClientPostProcessing = shouldRunPostProcessing && postProcessingPreflightFailure == nil"))

        let branches = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "let finalText: String",
            to: "markStage(\"cache_result\")"
        )
        #expect(branches.contains("} else if runClientPostProcessing {"))
        #expect(!branches.contains("} else if shouldRunPostProcessing {"))

        let flags = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "let postProcessingSkipped: Bool",
            to: "let result = TranscriptionResult("
        )
        let skipArm = try #require(flags.range(of: "} else if postProcessingPreflightFailure != nil {"))
        let mutationArm = try #require(flags.range(of: "} else if shouldRunPostProcessing {"))
        #expect(skipArm.lowerBound < mutationArm.lowerBound)
    }
}
