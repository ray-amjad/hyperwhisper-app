//
//  TranscriptionErrorClassificationTests.swift
//  hyperwhisperTests
//
//  Locks the report-vs-suppress decision into the type system. Adding a new
//  case to `TranscriptionError` or `HyperWhisperCloudError` without picking a
//  side breaks compilation here — that's the point. Do not add a `default:`.
//

import Foundation
import Testing
@testable import HyperWhisper

struct TranscriptionErrorClassificationTests {

    @Test func transcriptionErrorSentryDecisionIsExhaustive() {
        for (error, shouldReport) in Self.transcriptionErrorCases {
            #expect(
                Self.shouldCaptureInSentry(error) == shouldReport,
                "TranscriptionError.\(error) report decision changed"
            )
        }
    }

    @Test func cloudErrorSentryDecisionIsExhaustive() {
        for (error, shouldReport) in Self.cloudErrorCases {
            #expect(
                Self.shouldCaptureInSentry(error) == shouldReport,
                "HyperWhisperCloudError.\(error) report decision changed"
            )
        }
    }

    @Test func transientURLErrorCodesIsTheSingleCanonicalSetForConnectivityCodes() {
        // Regression test for the review-round-2 fix: `classifyTranscriptionError`'s
        // bare-`URLError` branch used to keep its own narrower, independent
        // 6-code list instead of reusing `transientURLErrorCodes` (missing
        // `.dataNotAllowed` / `.internationalRoamingOff`), even though the doc
        // comment already called this the shared canonical set. Both
        // `classifyTranscriptionError` and `shouldCaptureTranscriptionErrorInSentry`
        // now derive from this same property — pin its exact membership so a
        // future edit can't silently narrow it again without this test catching it.
        let expected: Set<URLError.Code> = [
            .notConnectedToInternet, .networkConnectionLost, .timedOut,
            .dnsLookupFailed, .cannotFindHost, .cannotConnectToHost,
            .dataNotAllowed, .internationalRoamingOff
        ]
        #expect(TranscriptionPipeline.transientURLErrorCodes == expected)
    }

    /// The three `TranscriptionError` switches outside the pipeline, for the
    /// case this change appended.
    ///
    /// Not retryable: the pass already reloaded the model once and lost it
    /// again, so under sustained pressure an automatic retry reloads and loses
    /// it over and over — the same livelock `ResidentRuntimeClaim` refuses to
    /// enter. No Settings button: nothing in Settings frees memory.
    @Test func anEvictedLocalSpeechModelIsNotRetryableAndHasNoSettingsAction() {
        let error = TranscriptionError.localSpeechModelEvicted(model: "base.en")

        #expect(error.isRetryable == false)
        #expect(error.showSettingsButton == false)
        // `NSLocalizedString` hands back the key when the entry is missing, so
        // this also catches a case appended without its Localizable.strings row.
        #expect(error.errorDescription != "transcription.error.localSpeechModelEvicted")
        #expect(error.errorDescription?.isEmpty == false)
    }

    @Test func transcriptionFailureFingerprintIncludesClassificationAndStage() {
        let classification = TranscriptionPipeline.TranscriptionErrorClassification(
            category: "auth",
            kind: "unauthorized",
            retryable: false,
            httpStatus: nil
        )

        #expect(
            TranscriptionPipeline.sentryFingerprintForTranscriptionFailure(
                classification: classification,
                stage: "transcribe"
            ) == [
                "transcription-pipeline",
                "transcribe-with-details",
                "auth",
                "unauthorized",
                "transcribe"
            ]
        )
    }

    // MARK: - HYPERWHISPER-T2: the refusal status has to survive the boundary

    /// The core classifies a 401 and a 403 into the same `.Unauthorized` and
    /// keeps neither code, so the mapper is the only place the status can be
    /// preserved. Every HYPERWHISPER-T2 event was statusless because this hop
    /// dropped it.
    @Test func unauthorizedMappingKeepsTheHttpStatusItWasGiven() {
        for status in [401, 403] {
            let mapped = RustCoreMapping.mapTranscriptionError(
                .Unauthorized,
                providerName: "HyperWhisper Cloud",
                httpStatus: status
            )
            guard case .unauthorized(let provider, let statusCode) = mapped else {
                Issue.record("HwTranscriptionError.Unauthorized no longer maps to .unauthorized")
                return
            }
            #expect(provider == "HyperWhisper Cloud")
            #expect(statusCode == status)
        }
    }

    /// A caller with no response to read leaves the status absent rather than
    /// inventing one. `nil` and "401" must stay tellable apart in the report.
    @Test func unauthorizedMappingLeavesTheStatusAbsentWhenTheCallerHasNone() {
        let mapped = RustCoreMapping.mapTranscriptionError(
            .Unauthorized,
            providerName: "HyperWhisper Cloud"
        )
        guard case .unauthorized(_, let statusCode) = mapped else {
            Issue.record("HwTranscriptionError.Unauthorized no longer maps to .unauthorized")
            return
        }
        #expect(statusCode == nil)
    }

    /// A Cloud 403 is a temporary network block. Retrying can work after the
    /// block clears, while opening Settings cannot change the network state.
    @Test func forbiddenStatusChangesGuidanceAndRecoveryActions() {
        let bare = TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
        let with401 = TranscriptionError.unauthorized(provider: "HyperWhisper Cloud", statusCode: 401)
        let with403 = TranscriptionError.unauthorized(provider: "HyperWhisper Cloud", statusCode: 403)

        #expect(with401.isRetryable == bare.isRetryable)
        #expect(with401.showSettingsButton == bare.showSettingsButton)
        #expect(with403.isRetryable)
        #expect(!with403.showSettingsButton)
        #expect(with403.shouldSurfaceInline)
        #expect(with401.errorDescription == bare.errorDescription)
        #expect(with403.errorDescription != bare.errorDescription)
        #expect(with403.errorDescription?.localizedCaseInsensitiveContains("temporarily blocked") == true)
    }

    /// The fingerprint is built from `category` / `kind` / `stage` and must NOT
    /// read the status — otherwise carrying it would split HYPERWHISPER-T2 into
    /// a 401 issue and a 403 issue, losing the history the field exists to
    /// explain.
    @Test func carryingTheStatusDoesNotSplitTheTranscriptionFailureFingerprint() {
        func fingerprint(status: Int?) -> [String] {
            TranscriptionPipeline.sentryFingerprintForTranscriptionFailure(
                classification: TranscriptionPipeline.TranscriptionErrorClassification(
                    category: "auth",
                    kind: "unauthorized",
                    retryable: false,
                    httpStatus: status
                ),
                stage: "transcribe"
            )
        }

        #expect(fingerprint(status: 401) == fingerprint(status: nil))
        #expect(fingerprint(status: 403) == fingerprint(status: 401))
    }

    /// The outcome slugs are a reporting contract — a Sentry search saved
    /// against one has to keep matching. A duplicate raw value would silently
    /// merge two different give-up branches into one bucket, which is exactly
    /// the ambiguity the trace exists to remove.
    @Test func cloudAuthRecoveryOutcomeSlugsAreDistinct() {
        let slugs = Self.recoveryOutcomes.map(\.rawValue)

        #expect(Set(slugs).count == Self.recoveryOutcomes.count)
        #expect(slugs.allSatisfy { !$0.isEmpty && $0 != CloudAuthRecoveryTrace.slugNone })
    }

    /// Sentry's stock `@password:filter` scrubber matches the VALUE of an extra,
    /// not only its key: on a live HYPERWHISPER-T2 event the `errorCategory`
    /// extra (value "auth") and `errorKind` (value "unauthorized") both arrive
    /// as "[Filtered]" with `_meta.rule_id = "@password:filter"`, while the
    /// `error_class` TAG holding the same string survives.
    ///
    /// So a slug or an extra key that says "auth" here is scrubbed away in
    /// production while the event still arrives looking fine — the failure is
    /// silent, which is why it is worth a test rather than a comment.
    @Test func recoveryTraceNamesAvoidTheSentryPasswordScrubber() {
        let scrubbed = ["auth", "unauthorized", "password", "secret", "token", "credential"]

        for slug in Self.recoveryOutcomes.map(\.rawValue) {
            #expect(
                scrubbed.allSatisfy { !slug.contains($0) },
                "outcome slug \(slug) contains a term @password:filter redacts"
            )
        }
        for key in [CloudAuthRecoveryTrace.extraPrefix, CloudAuthRecoveryTrace.outcomeTagKey] {
            #expect(
                scrubbed.allSatisfy { !key.contains($0) },
                "key \(key) contains a term @password:filter redacts"
            )
        }
    }

    /// Every case, listed so a new one added without a slug review breaks here.
    private static let recoveryOutcomes: [CloudAuthRecoveryTrace.Outcome] = [
        .succeededFirstSend, .otherErrorFirstSend, .notLicensedNoRetry,
        .revalidationInvalid, .reresolvedIdentityUnlicensed,
        .serverCacheRefreshFailed, .retrySendRefusedAgain,
        .retrySendFailedOther, .succeededAfterRepair, .cancelled
    ]

    // MARK: - Cases under test

    /// One value per `TranscriptionError` case + expected Sentry-capture decision.
    /// Build will fail in the `decision(for:)` switch below if a new case lands
    /// without a paired entry here.
    private static let transcriptionErrorCases: [(TranscriptionError, Bool)] = [
        (.providerNotAvailable(provider: "p", reason: "boom"), true),
        (.providerNotAvailable(provider: "p", reason: "unreachable"), false),
        (.providerNotAvailable(provider: "p", reason: "Provider health check failed"), false),
        (.providerNotAvailable(provider: "p", reason: "Unexpected health status"), false),
        (.modelNotDownloaded, true),
        (.modelProtected, true),
        (.audioFileNotFound, true),
        (.transientNetwork(details: "offline"), false),
        (.invalidResponse(details: "bad json"), true),
        (.apiKeyMissing(provider: "p"), true),
        (.maxRetriesExceeded, true),
        (.unauthorized(provider: "p"), true),
        (.invalidRequest, true),
        (.streamingInterrupted, true),
        (.busy, true),
        (.invalidAudioFormat, true),
        (.audioConversionFailed, true),
        (.audioFileTooLarge(fileSize: 1, limit: 2, providerName: "p"), true),
        (.serverError(statusCode: 502, message: "x"), false),
        (.serverError(statusCode: 400, message: "x"), true),
        (.rateLimited(retryAfter: nil), false),
        (.insufficientCredits(remaining: 0, required: 1), false),
        (.quotaExceeded(provider: "p", message: nil), false),
        (.timeout(operation: "x"), false),
        (.noSpeechDetected, false),
        (.localRuntimeUnavailable(reason: "llama-server unreachable"), false),
        // Client-side entitlement refusal (HYPERWHISPER-T2): expected,
        // user-recoverable, never reported. Its sibling `.unauthorized` above
        // stays `true` on purpose — a 401 on a request we believed was licensed
        // is still a real signal.
        (.cloudAccountRequired(provider: "HyperWhisper Cloud"), false),
        (.cloudAccountRequired(provider: nil), false),
        // Memory-pressure eviction the pass could not recover from. REPORTED,
        // unlike its lookalike `.localRuntimeUnavailable` above: it is the only
        // signal that says the eviction policy is costing users transcriptions.
        (.localSpeechModelEvicted(model: "base.en"), true),
        (.localSpeechModelEvicted(model: nil), true)
    ]

    private static let cloudErrorCases: [(HyperWhisperCloudError, Bool)] = [
        (.insufficientCredits(remaining: 0, required: 1), false),
        (.transientNetwork("offline"), false),
        (.invalidResponse("bad json"), true),
        (.serverError("boom"), true)
    ]

    // MARK: - Compile-time exhaustiveness lock

    /// Mirrors `TranscriptionPipeline.shouldCaptureTranscriptionErrorInSentry`
    /// but without the URLError/NSError prelude — the test only exercises the
    /// enum branch, which is the part that has historically rotted.
    /// **Do not add a `default:` case.** A new enum case must fail the build.
    private static func shouldCaptureInSentry(_ error: TranscriptionError) -> Bool {
        switch error {
        case .cloudAccountRequired:
            return false
        case .transientNetwork, .timeout, .rateLimited,
             .noSpeechDetected, .insufficientCredits, .quotaExceeded,
             .localRuntimeUnavailable(_):
            return false
        case .serverError(let statusCode, _):
            return !(500...599).contains(statusCode)
        case .providerNotAvailable(_, let reason):
            let lowered = reason?.lowercased() ?? ""
            let isTransient = lowered.contains("unreachable")
                || lowered.contains("offline")
                || lowered.contains("network")
                || lowered.contains("connection")
                || lowered.contains("provider health check failed")
                || lowered.contains("unexpected health status")
            return !isTransient
        case .invalidResponse, .modelNotDownloaded, .modelProtected, .audioFileNotFound,
             .apiKeyMissing, .maxRetriesExceeded, .unauthorized, .invalidRequest,
             .streamingInterrupted, .busy, .invalidAudioFormat, .audioConversionFailed,
             .audioFileTooLarge, .localSpeechModelEvicted:
            return true
        }
    }

    private static func shouldCaptureInSentry(_ error: HyperWhisperCloudError) -> Bool {
        switch error {
        case .insufficientCredits, .transientNetwork:
            return false
        case .invalidResponse, .serverError:
            return true
        }
    }
}
