//
//  TranscriptionError.swift
//  hyperwhisper
//
//  Extracted to centralize error semantics and user guidance.

import Foundation

/// Transcription errors with detailed context for better user messaging
enum TranscriptionError: LocalizedError {
    case providerNotAvailable(provider: String? = nil, reason: String? = nil)
    case modelNotDownloaded
    case modelProtected
    case audioFileNotFound
    /// Transient network failure (no internet, retry exhaustion) — suppressed in Sentry.
    case transientNetwork(details: String? = nil)
    /// Server contract violation (missing HTTPURLResponse, JSON decode fail, unexpected status) — reported to Sentry.
    case invalidResponse(details: String? = nil)
    case apiKeyMissing(provider: String? = nil)
    case maxRetriesExceeded
    case unauthorized(provider: String? = nil)
    case invalidRequest
    case streamingInterrupted
    case busy
    case invalidAudioFormat
    case audioConversionFailed
    /// Audio file exceeds provider's size limit
    /// - Parameters:
    ///   - fileSize: Actual file size in bytes
    ///   - limit: Provider's maximum allowed size in bytes
    ///   - providerName: Display name of the provider (e.g., "ElevenLabs", "OpenAI")
    case audioFileTooLarge(fileSize: Int64, limit: Int64, providerName: String)
    case serverError(statusCode: Int, message: String)
    case rateLimited(retryAfter: Int? = nil)
    case insufficientCredits(remaining: Int, required: Int)
    case quotaExceeded(provider: String, message: String?)
    case timeout(operation: String)
    case noSpeechDetected
    /// Local LLM runtime (llama-server) could not be started for post-processing.
    /// Raw transcript is still returned; this notifies the user that post-processing was skipped.
    case localRuntimeUnavailable(reason: String)

    var errorDescription: String? {
        switch self {
        case .providerNotAvailable(let provider, let reason):
            if let provider = provider, let reason = reason {
                return "transcription.error.providerNotAvailable.detail".localized(arguments: provider, reason)
            } else if let provider = provider {
                return "transcription.error.providerNotAvailable.provider".localized(arguments: provider)
            }
            return "transcription.error.providerNotAvailable".localized
        case .modelNotDownloaded:
            return "transcription.error.modelNotDownloaded".localized
        case .modelProtected:
            return "transcription.error.modelProtected".localized
        case .audioFileNotFound:
            return "transcription.error.audioFileNotFound".localized
        case .transientNetwork(let details), .invalidResponse(let details):
            if let details = details {
                return "transcription.error.network.detail".localized(arguments: details)
            }
            return "transcription.error.network.generic".localized
        case .apiKeyMissing(let provider):
            if let provider = provider {
                return "transcription.error.apiKeyMissing.provider".localized(arguments: provider)
            }
            return "transcription.error.apiKeyMissing.generic".localized
        case .maxRetriesExceeded:
            return "transcription.error.maxRetriesExceeded".localized
        case .unauthorized(let provider):
            if let provider = provider {
                return "transcription.error.unauthorized.provider".localized(arguments: provider)
            }
            return "transcription.error.unauthorized.generic".localized
        case .invalidRequest:
            return "transcription.error.invalidRequest".localized
        case .streamingInterrupted:
            return "transcription.error.streamingInterrupted".localized
        case .busy:
            return "transcription.error.busy".localized
        case .invalidAudioFormat:
            return "transcription.error.invalidAudioFormat".localized
        case .audioConversionFailed:
            return "transcription.error.audioConversionFailed".localized
        case .audioFileTooLarge(let fileSize, let limit, let providerName):
            let fileSizeStr = formatFileSize(fileSize)
            let limitStr = formatFileSize(limit)
            return "transcription.error.audioFileTooLarge".localized(arguments: fileSizeStr, limitStr, providerName)
        case .serverError(let statusCode, let message):
            return "transcription.error.serverError".localized(arguments: statusCode, message)
        case .rateLimited(let retryAfter):
            if let seconds = retryAfter {
                return "transcription.error.rateLimited.seconds".localized(arguments: seconds)
            }
            return "transcription.error.rateLimited.generic".localized
        case .insufficientCredits:
            return "transcription.error.insufficientCredits".localized
        case .quotaExceeded(let provider, let message):
            if let message = message {
                return "transcription.error.quotaExceeded.detail".localized(arguments: provider, message)
            }
            return "transcription.error.quotaExceeded".localized(arguments: provider)
        case .timeout(let operation):
            return "transcription.error.timeout".localized(arguments: operation)
        case .noSpeechDetected:
            return "transcription.error.noSpeechDetected".localized
        case .localRuntimeUnavailable:
            // Plain-language, no llama-server / "health check" jargon. The raw
            // `reason` is logged at the call sites, not shown to the user.
            return "transcription.error.localRuntimeUnavailable".localized
        }
    }

    /// Whether this error is retryable
    var isRetryable: Bool {
        switch self {
        case .transientNetwork(_), .invalidResponse(_), .providerNotAvailable(_, _), .streamingInterrupted, .timeout(_), .serverError(_, _):
            return true
        case .rateLimited(_):
            return true  // Can retry after waiting
        case .audioFileNotFound, .apiKeyMissing(_), .modelNotDownloaded, .modelProtected, .maxRetriesExceeded, .unauthorized(_), .invalidRequest, .busy, .invalidAudioFormat, .audioConversionFailed, .audioFileTooLarge(_, _, _), .insufficientCredits(_, _), .quotaExceeded(_, _), .noSpeechDetected, .localRuntimeUnavailable(_):
            return false
        }
    }

    /// Whether this error should show the "Open Settings" button in inline error toast
    ///
    /// **Show Settings Button For (actionable in settings):**
    /// - API key missing/required errors → user can add key
    /// - Unauthorized errors (invalid API key) → user can fix key
    /// - Insufficient credits → user can check subscription
    /// - Quota exceeded → user can check subscription
    ///
    /// **Hide Settings Button For (not fixable in settings):**
    /// - No speech detected → just retry with clearer speech
    /// - Network errors → check internet connection
    /// - Rate limited → wait and retry
    /// - Server errors → transient, retry later
    /// - Timeout errors → transient, retry later
    var showSettingsButton: Bool {
        switch self {
        case .apiKeyMissing, .unauthorized, .insufficientCredits, .quotaExceeded:
            return true
        case .noSpeechDetected, .transientNetwork, .invalidResponse, .rateLimited, .serverError, .timeout,
             .providerNotAvailable, .modelNotDownloaded, .modelProtected, .audioFileNotFound,
             .maxRetriesExceeded, .invalidRequest, .streamingInterrupted, .busy,
             .invalidAudioFormat, .audioConversionFailed, .audioFileTooLarge(_, _, _),
             .localRuntimeUnavailable(_):
            return false
        }
    }

    /// Whether this error should be surfaced to the user as an inline toast/banner,
    /// even when no settings button is shown. Credential errors qualify because the user
    /// has a clear action; `localRuntimeUnavailable` qualifies because the user needs
    /// to know post-processing was skipped (raw transcript was still returned).
    var shouldSurfaceInline: Bool {
        if showSettingsButton { return true }
        switch self {
        case .localRuntimeUnavailable:
            return true
        default:
            return false
        }
    }

    /// User guidance for resolving the error
    var userGuidance: String? {
        switch self {
        case .providerNotAvailable(let provider, _):
            if let provider = provider {
                return "transcription.guidance.providerNotAvailable.provider".localized(arguments: provider)
            }
            return "transcription.guidance.providerNotAvailable".localized
        case .apiKeyMissing(let provider):
            if let provider = provider {
                return "transcription.guidance.apiKeyMissing.provider".localized(arguments: provider)
            }
            return "transcription.guidance.apiKeyMissing.generic".localized
        case .modelNotDownloaded:
            return "transcription.guidance.modelNotDownloaded".localized
        case .modelProtected:
            return "transcription.guidance.modelProtected".localized
        case .audioFileNotFound:
            return "transcription.guidance.audioFileNotFound".localized
        case .transientNetwork(_), .invalidResponse(_):
            return "transcription.guidance.networkError".localized
        case .unauthorized(let provider):
            if let provider = provider {
                return "transcription.guidance.unauthorized.provider".localized(arguments: provider)
            }
            return "transcription.guidance.unauthorized.generic".localized
        case .invalidRequest:
            return "transcription.guidance.invalidRequest".localized
        case .invalidAudioFormat:
            return "transcription.guidance.invalidAudioFormat".localized
        case .audioConversionFailed:
            return "transcription.guidance.audioConversionFailed".localized
        case .audioFileTooLarge(_, _, _):
            return "transcription.guidance.audioFileTooLarge".localized
        case .serverError(_, _):
            return "transcription.guidance.serverError".localized
        case .rateLimited(_):
            return "transcription.guidance.rateLimited".localized
        case .insufficientCredits(_, _):
            return "transcription.guidance.insufficientCredits".localized
        case .quotaExceeded(let provider, _):
            return "transcription.guidance.quotaExceeded".localized(arguments: provider)
        case .timeout(_):
            return "transcription.guidance.timeout".localized
        case .noSpeechDetected:
            return "transcription.guidance.noSpeechDetected".localized
        case .localRuntimeUnavailable:
            return "transcription.guidance.localRuntimeUnavailable".localized
        default:
            return nil
        }
    }
}

// MARK: - Private Helpers

/// Format file size in bytes to human-readable string (e.g., "25 MB", "1.5 GB")
private func formatFileSize(_ bytes: Int64) -> String {
    if bytes >= 1024 * 1024 * 1024 {
        let gb = Double(bytes) / (1024.0 * 1024.0 * 1024.0)
        return String(format: "%.1f GB", gb)
    } else {
        let mb = bytes / (1024 * 1024)
        return "\(mb) MB"
    }
}
