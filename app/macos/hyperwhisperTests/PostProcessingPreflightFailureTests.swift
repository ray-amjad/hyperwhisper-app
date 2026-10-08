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
//  Issue #1547 (Ray, inbox #336): the same applies to a CLOUD / BYOK
//  post-processing provider that fails its health check after the speech engine
//  finished — keep the raw transcript, skip the AI step, show the provider
//  error inline. Cancellation stays fatal, and the SPEECH provider's health
//  check (same `errorForHealthStatus`, but in `selectProvider`, before any
//  transcript exists) is untouched.
//
//  The decision now lives in `TranscriptionPipeline.awaitPostProcessingPreflight`,
//  which these tests call with fake checks. The last two tests are wiring guards:
//  they read the source to prove the after-transcript await goes through
//  that decision and not around it (`ProductionSource` explains why that is the
//  last resort, and why the decision itself is tested by calling it).
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Post-processing pre-flight failure keeps the transcript (#1538, #1547)")
struct PostProcessingPreflightFailureTests {

    private static let pipelineSource =
        "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift"
    private static let routerSource =
        "app/macos/hyperwhisper/Managers/Transcription/Coordinators/TranscriptionProviderRouter.swift"

    /// Every error `TranscriptionProviderRouter.errorForHealthStatus` can return
    /// for an unhealthy post-processing provider, one per `ProviderHealth` arm.
    private static let cloudHealthCheckErrors: [TranscriptionError] = [
        .unauthorized(provider: "OpenAI"),
        .transientNetwork(details: "Provider unreachable"),
        .modelNotDownloaded,
        .providerNotAvailable(provider: "OpenAI", reason: "Provider health check failed"),
        .providerNotAvailable(provider: "OpenAI", reason: "Unexpected health status")
    ]

    // MARK: - The decision

    @Test func localRuntimeUnavailableIsNonFatal() {
        #expect(TranscriptionPipeline.isNonFatalPostProcessingPreflightError(
            TranscriptionError.localRuntimeUnavailable(reason: "Local runtime did not become ready in time.")
        ))
    }

    /// Issue #1547: every error the cloud / BYOK pre-flight can throw
    /// (`TranscriptionProviderRouter.errorForHealthStatus`) is now non-fatal
    /// once the transcript exists. On main each of these failed the run.
    @Test func cloudHealthCheckErrorsAreNonFatal() {
        for error in Self.cloudHealthCheckErrors {
            #expect(
                TranscriptionPipeline.isNonFatalPostProcessingPreflightError(error),
                "\(error) must keep the transcript"
            )
        }
    }

    /// A `TranscriptionError` the post-processing health check never produces
    /// stays fatal: a new throw site has to opt in, not inherit the skip.
    @Test func errorsTheHealthCheckDoesNotProduceStayFatal() {
        let fatal: [TranscriptionError] = [
            .localSpeechModelEvicted(model: "base"),
            .noSpeechDetected,
            .audioFileNotFound,
            .insufficientCredits(remaining: 0, required: 1)
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

    /// Issue #1547, the bug itself: a BYOK post-processing provider whose key
    /// is bad fails its health check after the transcript exists. The await
    /// hands the original error back to report (it names the provider), it
    /// does not throw it. On main this threw and the row was saved `failed`.
    @MainActor
    @Test func aCloudHealthCheckFailureIsReturnedNotThrown() async throws {
        let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {
            throw TranscriptionError.unauthorized(provider: "Anthropic", statusCode: 401)
        }
        let returned = try #require(failure)
        guard case .unauthorized(let provider, let statusCode) = returned else {
            Issue.record("Expected the original .unauthorized, got \(returned)")
            return
        }
        #expect(provider == "Anthropic")
        #expect(statusCode == 401)
        // The non-fatal error the user sees names the provider problem.
        #expect(returned.localizedDescription == TranscriptionError.unauthorized(provider: "Anthropic", statusCode: 401).localizedDescription)
    }

    /// The endpoint-down case, arriving as the value of the concurrent
    /// pre-flight `Task` the pipeline starts before transcription.
    @MainActor
    @Test func anUnreachableCloudProviderFromTheConcurrentTaskIsReturned() async throws {
        let preflight = Task<Void, Error> {
            throw TranscriptionError.transientNetwork(details: "Provider unreachable")
        }
        let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {
            try await preflight.value
        }
        let returned = try #require(failure)
        guard case .transientNetwork(let details) = returned else {
            Issue.record("Expected the original .transientNetwork, got \(returned)")
            return
        }
        #expect(details == "Provider unreachable")
    }

    /// Every `errorForHealthStatus` output is handed back, none thrown.
    @MainActor
    @Test func everyCloudHealthCheckErrorIsReturned() async throws {
        for error in Self.cloudHealthCheckErrors {
            let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {
                throw error
            }
            #expect(failure != nil, "\(error) must be returned, not thrown")
        }
    }

    /// An error the health check does not produce still fails the run.
    @MainActor
    @Test func anErrorTheHealthCheckDoesNotProduceIsStillThrown() async {
        do {
            _ = try await TranscriptionPipeline.awaitPostProcessingPreflight {
                throw TranscriptionError.localSpeechModelEvicted(model: "base")
            }
            Issue.record("A non-health-check error must still fail the run")
        } catch let error as TranscriptionError {
            guard case .localSpeechModelEvicted = error else {
                Issue.record("Expected the original .localSpeechModelEvicted, got \(error)")
                return
            }
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

    /// A cloud health-check failure that lands after the user cancelled the
    /// run is NOT absorbed: the cancel must not become a saved `completed`
    /// row. The task is cancelled before its body starts (both run on the
    /// main actor, and nothing suspends between `Task {}` and `cancel()`).
    @MainActor
    @Test func aCloudHealthCheckFailureInACancelledRunIsStillThrown() async {
        let run = Task { () async throws -> Bool in
            let failure = try await TranscriptionPipeline.awaitPostProcessingPreflight {
                throw TranscriptionError.unauthorized(provider: "OpenAI")
            }
            return failure != nil
        }
        run.cancel()
        switch await run.result {
        case .success(let returnedAFailure):
            Issue.record("A cancelled run must not keep the transcript; returned a failure: \(returnedAFailure)")
        case .failure(let error):
            guard let transcriptionError = error as? TranscriptionError,
                  case .unauthorized(let provider, _) = transcriptionError else {
                Issue.record("Expected the original .unauthorized, got \(error)")
                return
            }
            #expect(provider == "OpenAI")
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

    /// Issue #1547: only the POST-PROCESSING pre-flight became non-fatal. The
    /// speech provider's health check — the same `errorForHealthStatus`, in
    /// `selectProvider` and the Local API's `resolveProvider` — still throws
    /// straight out, and the pipeline still awaits `selectProvider` with a bare
    /// `try`, before the one place the decision is used.
    @Test func theSpeechProviderHealthCheckStaysFatal() throws {
        let router = try ProductionSource.code(of: Self.routerSource)
        let speechThrow = "throw errorForHealthStatus(status, providerDisplayName: cloudProviderType.displayName)"
        #expect(router.components(separatedBy: speechThrow).count - 1 == 2)

        let pipeline = try ProductionSource.code(of: Self.pipelineSource)
        let selectCall = "let selection = try await providerCoordinator.selectProvider(for: mode, vocabulary: vocabulary)"
        let select = try #require(pipeline.range(of: selectCall))
        let decisionUses = pipeline.components(separatedBy: "Self.awaitPostProcessingPreflight").count - 1
        #expect(decisionUses == 1)
        let decision = try #require(pipeline.range(of: "Self.awaitPostProcessingPreflight"))
        #expect(select.lowerBound < decision.lowerBound)
    }
}
