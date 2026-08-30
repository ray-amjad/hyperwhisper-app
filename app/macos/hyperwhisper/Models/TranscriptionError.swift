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
    /// Provider refused the request as unauthenticated or forbidden.
    ///
    /// - Parameter statusCode: the HTTP status the provider actually returned
    ///   (401 or 403), when the throw site knew it. Diagnostic only — nothing
    ///   branches on it. HYPERWHISPER-T2 groups every unauthorized refusal into
    ///   one issue, and without this the report cannot tell a missing/expired
    ///   credential (401) from a credential the server knows but refuses (403),
    ///   which are different faults with different fixes.
    case unauthorized(provider: String? = nil, statusCode: Int? = nil)
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
    /// HyperWhisper Cloud was requested without an account key. The guest /
    /// device-credit path no longer exists server-side, so the upload is
    /// guaranteed to 401 — we refuse locally instead of burning the round-trip
    /// and reporting a non-actionable Sentry error. Fail-closed only: this is
    /// never used to *grant* access, the server stays the sole authority.
    case cloudAccountRequired(provider: String? = nil)
    /// The local speech (whisper.cpp) model was unloaded to reclaim memory and
    /// could not be made resident again for this pass — memory pressure is
    /// sustained and evicted the freshly reloaded runtime too.
    ///
    /// Its OWN case, not a `providerNotAvailable` reason string, because the
    /// fingerprint is `[category, kind, stage]` and `kind` comes from the case
    /// alone: as prose it landed in the very HYPERWHISPER-SQ group it exists to
    /// close, so triage could not tell the fix from the bug. It is also
    /// deliberately CAPTURED in Sentry — do not copy `.localRuntimeUnavailable`,
    /// which is suppressed. If this fires we want to see it.
    ///
    /// - Parameter model: the model that was evicted, for diagnostics.
    ///
    /// ⚠️ APPEND-ONLY ENUM — always add new cases at the END, never in the
    /// middle, and move this warning down to stay on the last case.
    /// `(error as NSError).code` is the Swift-synthesized *positional* case
    /// index and is recorded in Sentry extras as `errorCode`. Inserting a case
    /// mid-enum silently renumbers every case after it and invalidates all
    /// historical Sentry triage.
    case localSpeechModelEvicted(model: String? = nil)

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
        case .unauthorized(let provider, _):
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
        case .cloudAccountRequired:
            // Deliberately ignores the associated `provider`: the message names
            // HyperWhisper Cloud directly and points at Settings → HyperWhisper
            // Cloud, so the string needs no format specifier. The provider value
            // is carried for logs/classification only.
            return "transcription.error.cloudAccountRequired".localized
        case .localSpeechModelEvicted:
            // Deliberately ignores the associated `model`: the user cannot act
            // on the model name, only on the memory pressure. The name is
            // carried for logs and Sentry.
            return "transcription.error.localSpeechModelEvicted".localized
        }
    }

    /// Whether this error is retryable
    var isRetryable: Bool {
        switch self {
        case .transientNetwork(_), .invalidResponse(_), .providerNotAvailable(_, _), .streamingInterrupted, .timeout(_), .serverError(_, _):
            return true
        case .rateLimited(_):
            return true  // Can retry after waiting
        // `.localSpeechModelEvicted` is NOT retryable: the pass already reloaded
        // the model once and lost it again, so the pressure is sustained and an
        // automatic retry would reload and lose it over and over.
        case .audioFileNotFound, .apiKeyMissing(_), .modelNotDownloaded, .modelProtected, .maxRetriesExceeded, .unauthorized, .invalidRequest, .busy, .invalidAudioFormat, .audioConversionFailed, .audioFileTooLarge(_, _, _), .insufficientCredits(_, _), .quotaExceeded(_, _), .noSpeechDetected, .localRuntimeUnavailable(_), .cloudAccountRequired(_), .localSpeechModelEvicted(_):
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
    /// - Cloud account required → user can enter an account key
    ///
    /// **Hide Settings Button For (not fixable in settings):**
    /// - No speech detected → just retry with clearer speech
    /// - Network errors → check internet connection
    /// - Rate limited → wait and retry
    /// - Server errors → transient, retry later
    /// - Timeout errors → transient, retry later
    var showSettingsButton: Bool {
        switch self {
        case .apiKeyMissing, .unauthorized, .insufficientCredits, .quotaExceeded,
             .cloudAccountRequired:
            return true
        case .noSpeechDetected, .transientNetwork, .invalidResponse, .rateLimited, .serverError, .timeout,
             .providerNotAvailable, .modelNotDownloaded, .modelProtected, .audioFileNotFound,
             .maxRetriesExceeded, .invalidRequest, .streamingInterrupted, .busy,
             .invalidAudioFormat, .audioConversionFailed, .audioFileTooLarge(_, _, _),
             .localRuntimeUnavailable(_), .localSpeechModelEvicted(_):
            // Nothing in Settings frees memory; the user's action is elsewhere.
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

}
