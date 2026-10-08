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
//  which these tests call with fake checks. The toast copy for an absorbed cloud
//  failure (it names the post-processing provider and says the raw transcript
//  was kept) comes from `postProcessingPreflightToastMessage`, also called
//  directly. The last three tests are wiring guards: they read the source to
//  prove the after-transcript await goes through that decision and not around
//  it, and that an absorbed failure still reaches Sentry (`ProductionSource`
//  explains why that is the last resort, and why the decision itself is tested
//  by calling it).
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
    /// hands the original error back to report (its case picks the toast copy
    /// and the Sentry classification), it does not throw it. On main this
    /// threw and the row was saved `failed`.
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
        // The toast the user sees for it names the provider and the kept transcript.
        let message = try #require(TranscriptionPipeline.postProcessingPreflightToastMessage(
            for: returned,
            providerDisplayName: "Anthropic",
            localize: english
        ))
        #expect(message == "Anthropic rejected your API key — your raw transcript was kept.")
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

    // MARK: - The toast copy (#1547 review)

    private static let toastKeys = [
        "transcription.error.postProcessingPreflight.unauthorized",
        "transcription.error.postProcessingPreflight.unreachable",
        "transcription.error.postProcessingPreflight.notInstalled",
        "transcription.error.postProcessingPreflight.unavailable",
    ]

    /// The English Base values, so the copy is pinned without depending on the
    /// language the test process happens to run in.
    private func english(_ key: String) -> String {
        switch key {
        case "transcription.error.postProcessingPreflight.unauthorized":
            return "%@ rejected your API key — your raw transcript was kept."
        case "transcription.error.postProcessingPreflight.unreachable":
            return "Couldn't reach %@ — your raw transcript was kept."
        case "transcription.error.postProcessingPreflight.notInstalled":
            return "%@ isn't installed — your raw transcript was kept."
        case "transcription.error.postProcessingPreflight.unavailable":
            return "%@ is unavailable — your raw transcript was kept."
        default:
            return key
        }
    }

    private func toast(_ error: TranscriptionError, provider: String = "Anthropic") -> String? {
        TranscriptionPipeline.postProcessingPreflightToastMessage(
            for: error,
            providerDisplayName: provider,
            localize: english
        )
    }

    /// Each of the four `errorForHealthStatus` outputs gets a toast that names
    /// the post-processing provider, names the problem, and says the raw
    /// transcript was kept — not the failed-dictation copy ("Network error:
    /// Provider unreachable", "Please download a model first"). The provider
    /// comes from the pipeline, so the two cases that carry no provider
    /// (`.transientNetwork`, `.modelNotDownloaded`) still name it.
    @Test func everyCloudHealthCheckErrorGetsAToastNamingTheProviderAndTheKeptTranscript() throws {
        let cases: [(TranscriptionError, String)] = [
            (.unauthorized(provider: "Anthropic"),
             "Anthropic rejected your API key — your raw transcript was kept."),
            (.transientNetwork(details: "Provider unreachable"),
             "Couldn't reach Anthropic — your raw transcript was kept."),
            (.modelNotDownloaded,
             "Anthropic isn't installed — your raw transcript was kept."),
            (.providerNotAvailable(provider: "Anthropic", reason: "Provider health check failed"),
             "Anthropic is unavailable — your raw transcript was kept."),
            (.providerNotAvailable(provider: "Anthropic", reason: "Unexpected health status"),
             "Anthropic is unavailable — your raw transcript was kept."),
        ]
        for (error, expected) in cases {
            let message = try #require(toast(error), "\(error) needs the pre-flight toast")
            #expect(message == expected)
            #expect(message.contains("Anthropic"), "\(error) must name the provider")
            #expect(message.contains("your raw transcript was kept"), "\(error) must say the transcript was kept")
            // Not the failed-dictation copy, and not the raw health detail.
            #expect(message != error.localizedDescription)
            #expect(!message.contains("Provider unreachable"))
            #expect(!message.contains("health check"))
        }
        // The four health-check errors get four different problems.
        let distinct = Set(Self.cloudHealthCheckErrors.compactMap { toast($0) })
        #expect(distinct.count == 4)
    }

    /// The name is the pipeline's post-processing provider, not one the error
    /// happens to carry: a `.transientNetwork` has none.
    @Test func theToastUsesThePostProcessingProviderItIsGiven() {
        #expect(toast(.transientNetwork(details: "Provider unreachable"), provider: "OpenAI")
                == "Couldn't reach OpenAI — your raw transcript was kept.")
    }

    /// Every other error keeps its own copy: `.localRuntimeUnavailable` already
    /// says the transcript was kept, and nothing else reaches this path.
    @Test func otherErrorsKeepTheirOwnCopy() {
        let others: [TranscriptionError] = [
            .localRuntimeUnavailable(reason: "controller unavailable"),
            .localSpeechModelEvicted(model: "base"),
            .noSpeechDetected,
            .cloudAccountRequired(provider: "HyperWhisper Cloud"),
        ]
        for error in others {
            #expect(toast(error) == nil, "\(error) must keep its own copy")
        }
    }

    /// A rejected key still offers Open Settings; the others do not — the
    /// error's own rule, unchanged.
    @Test func onlyTheRejectedKeyOffersSettings() {
        #expect(TranscriptionError.unauthorized(provider: "Anthropic").showSettingsButton)
        #expect(!TranscriptionError.transientNetwork(details: "Provider unreachable").showSettingsButton)
        #expect(!TranscriptionError.modelNotDownloaded.showSettingsButton)
        #expect(!TranscriptionError.providerNotAvailable(provider: "Anthropic", reason: "x").showSettingsButton)
    }

    /// The four keys exist once in every locale, each with exactly one `%@`
    /// (the provider) and no other format specifier, and each reuses that
    /// locale's own "raw transcript was kept" wording from
    /// `transcription.error.localRuntimeUnavailable`.
    @Test func everyLocaleHasTheToastKeysWithOneProviderSpecifier() throws {
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            let runtimePrefix = "\"transcription.error.localRuntimeUnavailable\" = \""
            let runtimeLine = try #require(lines.first(where: { $0.hasPrefix(runtimePrefix) }), "\(locale.lastPathComponent)")
            let keptTail = try #require(runtimeLine.range(of: " — "), "\(locale.lastPathComponent)")
            let kept = String(runtimeLine[keptTail.lowerBound...])

            for key in Self.toastKeys {
                let matches = lines.filter { $0.hasPrefix("\"\(key)\" = \"") }
                #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(key)")
                guard let line = matches.first else { continue }
                #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent) \(key)")
                #expect(line.components(separatedBy: "%").count - 1 == 1, "\(locale.lastPathComponent) \(key)")
                #expect(line.components(separatedBy: "%@").count - 1 == 1, "\(locale.lastPathComponent) \(key)")
                #expect(line.hasSuffix(kept), "\(locale.lastPathComponent) \(key) must say the transcript was kept")
            }
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
        #expect(region.contains("reportNonFatalPostProcessingPreflightFailure("))
        #expect(region.contains("providerDisplayName: resolvedPostProcessingProvider?.displayName"))
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

    /// Issue #1547 review: before the skip, a pre-flight `.unauthorized` left
    /// `transcribeWithDetails` and the `catch` captured it in Sentry (the
    /// HYPERWHISPER-T2 signal). The absorbed failure is still captured, through
    /// the same filter and the same sanitiser, tagged as non-fatal at the
    /// pre-flight stage.
    @Test func anAbsorbedPreflightFailureIsStillCapturedInSentry() throws {
        let region = try ProductionSource.slice(
            of: Self.pipelineSource,
            from: "if let postProcessingPreflightFailure {",
            to: "let runClientPostProcessing = shouldRunPostProcessing && postProcessingPreflightFailure == nil"
        )
        #expect(region.contains("shouldCaptureTranscriptionErrorInSentry(postProcessingPreflightFailure)"))
        #expect(region.contains("SentryService.capture("))
        #expect(region.contains("error: Self.sentrySafeTranscriptionError(postProcessingPreflightFailure)"))
        #expect(region.contains("\"error_stage\": stage"))
        #expect(region.contains("\"post_processing_preflight\": \"non_fatal\""))
        // No user content in the event: not the error text, not the Mode, not
        // the transcript, not the toast copy.
        let capture = try #require(region.range(of: "SentryService.capture("))
        let event = region[capture.lowerBound...]
        for banned in ["localizedDescription", "mode?.", "text", "Text", "providerDisplayName", "message: message"] {
            #expect(!event.contains(banned), "the Sentry event must not carry \(banned)")
        }
        // The capture is outside the server-side-AI-text gate, as the fatal
        // capture was: the toast is conditional, the signal is not.
        let gate = try #require(region.range(of: "if hyperwhisperCloudAIText == nil {"))
        let gateBody = region[gate.upperBound...]
        let gateEnd = try #require(gateBody.range(of: "}"))
        #expect(gateEnd.upperBound <= capture.lowerBound)
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
