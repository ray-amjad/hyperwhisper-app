//
//  TranscriptionPipeline+ErrorClassification.swift
//  hyperwhisper
//
//  Error classification for diagnostics and retry hints.
//

import Foundation

extension TranscriptionPipeline {

    /// Error classification used for Sentry extras and retry decisions.
    struct TranscriptionErrorClassification {
        let category: String
        let kind: String
        let retryable: Bool
        let httpStatus: Int?
    }

    func classifyTranscriptionError(_ error: Error) -> TranscriptionErrorClassification {
        if let transcriptionError = error as? TranscriptionError {
            return classify(transcriptionError)
        }
        if let cloudError = error as? HyperWhisperCloudError {
            return classify(cloudError)
        }
        if let urlError = error as? URLError {
            let retryableCodes: Set<URLError.Code> = [
                .timedOut,
                .cannotFindHost,
                .cannotConnectToHost,
                .networkConnectionLost,
                .dnsLookupFailed,
                .notConnectedToInternet
            ]
            let kind = "url_\(String(describing: urlError.code))"
            return TranscriptionErrorClassification(
                category: "network",
                kind: kind,
                retryable: retryableCodes.contains(urlError.code),
                httpStatus: nil
            )
        }

        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain {
            let kind = "url_\(nsError.code)"
            return TranscriptionErrorClassification(
                category: "network",
                kind: kind,
                retryable: true,
                httpStatus: nil
            )
        }

        return TranscriptionErrorClassification(
            category: "unknown",
            kind: "unknown",
            retryable: false,
            httpStatus: nil
        )
    }

    func classify(_ error: TranscriptionError) -> TranscriptionErrorClassification {
        let retryable = error.isRetryable
        switch error {
        case .providerNotAvailable:
            return TranscriptionErrorClassification(category: "provider", kind: "provider_not_available", retryable: retryable, httpStatus: nil)
        case .modelNotDownloaded:
            return TranscriptionErrorClassification(category: "model", kind: "model_not_downloaded", retryable: retryable, httpStatus: nil)
        case .modelProtected:
            return TranscriptionErrorClassification(category: "model", kind: "model_protected", retryable: retryable, httpStatus: nil)
        case .audioFileNotFound:
            return TranscriptionErrorClassification(category: "audio", kind: "audio_file_not_found", retryable: retryable, httpStatus: nil)
        case .transientNetwork:
            return TranscriptionErrorClassification(category: "network", kind: "transient_network", retryable: retryable, httpStatus: nil)
        case .invalidResponse:
            return TranscriptionErrorClassification(category: "network", kind: "invalid_response", retryable: retryable, httpStatus: nil)
        case .apiKeyMissing:
            return TranscriptionErrorClassification(category: "auth", kind: "api_key_missing", retryable: retryable, httpStatus: nil)
        case .maxRetriesExceeded:
            return TranscriptionErrorClassification(category: "retry", kind: "max_retries_exceeded", retryable: retryable, httpStatus: nil)
        case .unauthorized:
            return TranscriptionErrorClassification(category: "auth", kind: "unauthorized", retryable: retryable, httpStatus: nil)
        case .invalidRequest:
            return TranscriptionErrorClassification(category: "request", kind: "invalid_request", retryable: retryable, httpStatus: nil)
        case .streamingInterrupted:
            return TranscriptionErrorClassification(category: "streaming", kind: "streaming_interrupted", retryable: retryable, httpStatus: nil)
        case .busy:
            return TranscriptionErrorClassification(category: "busy", kind: "busy", retryable: retryable, httpStatus: nil)
        case .invalidAudioFormat:
            return TranscriptionErrorClassification(category: "audio", kind: "invalid_audio_format", retryable: retryable, httpStatus: nil)
        case .audioConversionFailed:
            return TranscriptionErrorClassification(category: "audio", kind: "audio_conversion_failed", retryable: retryable, httpStatus: nil)
        case .audioFileTooLarge:
            return TranscriptionErrorClassification(category: "audio", kind: "audio_file_too_large", retryable: retryable, httpStatus: nil)
        case .serverError(let statusCode, _):
            return TranscriptionErrorClassification(category: "server", kind: "server_error", retryable: retryable, httpStatus: statusCode)
        case .rateLimited:
            return TranscriptionErrorClassification(category: "rate_limit", kind: "rate_limited", retryable: retryable, httpStatus: nil)
        case .insufficientCredits:
            return TranscriptionErrorClassification(category: "billing", kind: "insufficient_credits", retryable: retryable, httpStatus: nil)
        case .quotaExceeded:
            return TranscriptionErrorClassification(category: "billing", kind: "quota_exceeded", retryable: retryable, httpStatus: nil)
        case .timeout:
            return TranscriptionErrorClassification(category: "timeout", kind: "timeout", retryable: retryable, httpStatus: nil)
        case .noSpeechDetected:
            return TranscriptionErrorClassification(category: "audio", kind: "no_speech_detected", retryable: retryable, httpStatus: nil)
        case .localRuntimeUnavailable:
            return TranscriptionErrorClassification(category: "local_runtime", kind: "local_runtime_unavailable", retryable: retryable, httpStatus: nil)
        }
    }

    func classify(_ error: HyperWhisperCloudError) -> TranscriptionErrorClassification {
        switch error {
        case .insufficientCredits:
            return TranscriptionErrorClassification(category: "billing", kind: "insufficient_credits", retryable: false, httpStatus: nil)
        case .transientNetwork:
            return TranscriptionErrorClassification(category: "network", kind: "transient_network", retryable: true, httpStatus: nil)
        case .invalidResponse:
            return TranscriptionErrorClassification(category: "network", kind: "invalid_response", retryable: true, httpStatus: nil)
        case .serverError:
            return TranscriptionErrorClassification(category: "server", kind: "server_error", retryable: true, httpStatus: nil)
        }
    }

    private static let transientURLErrorCodes: Set<URLError.Code> = [
        .notConnectedToInternet, .networkConnectionLost, .timedOut,
        .dnsLookupFailed, .cannotFindHost, .cannotConnectToHost,
        .dataNotAllowed, .internationalRoamingOff
    ]

    func shouldCaptureTranscriptionErrorInSentry(_ error: Error) -> Bool {
        if let urlError = error as? URLError,
           Self.transientURLErrorCodes.contains(urlError.code) {
            return false
        }

        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain,
           Self.transientURLErrorCodes.contains(URLError.Code(rawValue: nsError.code)) {
            return false
        }

        if let cloudError = error as? HyperWhisperCloudError {
            switch cloudError {
            case .insufficientCredits, .transientNetwork:
                return false
            case .invalidResponse, .serverError:
                return true
            }
        }

        guard let transcriptionError = error as? TranscriptionError else {
            return true
        }

        switch transcriptionError {
        case .transientNetwork, .timeout, .rateLimited,
             .noSpeechDetected, .insufficientCredits, .quotaExceeded,
             .localRuntimeUnavailable, .modelNotDownloaded:
            // User-recoverable state, not a code defect: the selected local model
            // simply isn't on disk. The app already surfaces a user-facing error
            // ("download a model first"), so capturing it inflates a non-actionable
            // Sentry issue. Mirrors .localRuntimeUnavailable (local LLM not downloaded).
            return false
        case .serverError(let statusCode, _):
            return !(500...599).contains(statusCode)
        case .providerNotAvailable(_, let reason):
            return !Self.isTransientProviderAvailabilityReason(reason)
        case .invalidResponse, .modelProtected, .audioFileNotFound,
             .apiKeyMissing, .maxRetriesExceeded, .unauthorized, .invalidRequest,
             .streamingInterrupted, .busy, .invalidAudioFormat, .audioConversionFailed,
             .audioFileTooLarge:
            return true
        }
    }

    nonisolated static func sentryFingerprintForTranscriptionFailure(
        classification: TranscriptionErrorClassification,
        stage: String
    ) -> [String] {
        [
            "transcription-pipeline",
            "transcribe-with-details",
            classification.category,
            classification.kind,
            stage
        ]
    }

    private static func isTransientProviderAvailabilityReason(_ reason: String?) -> Bool {
        guard let reason = reason?.lowercased() else { return false }
        return reason.contains("unreachable")
            || reason.contains("offline")
            || reason.contains("network")
            || reason.contains("connection")
            || reason.contains("provider health check failed")
            || reason.contains("unexpected health status")
    }
}
