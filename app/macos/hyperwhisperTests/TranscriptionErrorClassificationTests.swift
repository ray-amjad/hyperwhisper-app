//
//  TranscriptionErrorClassificationTests.swift
//  hyperwhisperTests
//
//  Locks the report-vs-suppress decision into the type system. Adding a new
//  case to `TranscriptionError` or `HyperWhisperCloudError` without picking a
//  side breaks compilation here — that's the point. Do not add a `default:`.
//

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
        (.localRuntimeUnavailable(reason: "llama-server unreachable"), false)
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
             .audioFileTooLarge:
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
