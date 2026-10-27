//
//  TranscriptionError.swift
//  hyperwhisper
//
//  Extracted to centralize error semantics and user guidance.

import Foundation

/// Transcription errors with detailed context for better user messaging
enum TranscriptionError: LocalizedError, CustomNSError {
    case providerNotAvailable(provider: String? = nil, reason: String? = nil)
    /// The local model a pass needs is not on disk (issue #1518).
    ///
    /// - Parameters:
    ///   - model: the model's display name, as Model Library shows it
    ///     ("Tiny (English-only)"), never a file name or id. Set by the local
    ///     engine that found it missing.
    ///   - mode: the name of the mode that uses it. The engine does not know the
    ///     mode, so the flows add it (`withMode(_:)`) before the alert and the
    ///     History reason are made. User-typed text: never log or report it.
    ///
    /// Both are optional: a bare `.modelNotDownloaded()` keeps the old
    /// "Please download a model first", and a `case .modelNotDownloaded:`
    /// pattern (the Local API mapping) still matches. Giving the case values
    /// would have moved Swift's synthesized `NSError` code (payload cases are
    /// numbered before payload-less ones), so the code is pinned to its old
    /// value, 15, in `errorCode` below.
    case modelNotDownloaded(model: String? = nil, mode: String? = nil)
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
    ///   (401 or 403), when the throw site knew it. Error classification and
    ///   reporting do not branch on it. User guidance distinguishes a forbidden
    ///   request from an invalid credential. HYPERWHISPER-T2 groups every refusal into
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
    case localSpeechModelEvicted(model: String? = nil)
    /// The audio is too short for a local engine to transcribe (Nemotron,
    /// Parakeet and Qwen3 ASR refuse a file under 5,000 bytes).
    ///
    /// Its own case, not a `providerNotAvailable` reason (issue #1427): that
    /// case renders as "<engine> is not available", which told a user with a
    /// working engine that it was broken. The description keeps the dictation
    /// wording; a file import shows its own "file is too short" message.
    ///
    /// - Parameter fileSize: the audio file's size in bytes, for logs.
    ///
    /// ⚠️ APPEND-ONLY ENUM — always add new cases at the END, never in the
    /// middle, and move this warning down to stay on the last case.
    /// `(error as NSError).code` is recorded in Sentry extras as `errorCode`
    /// and in logs. It is pinned per case in `errorCode` (the `CustomNSError`
    /// conformance below), not left to Swift's synthesized index, which
    /// changes whenever a case is inserted or gains or loses a payload. A new
    /// case appends the next unused number there (25 next); never reuse or
    /// change a number, or historical Sentry triage stops matching.
    case audioTooShort(fileSize: Int64)

    var errorDescription: String? {
        switch self {
        case .providerNotAvailable(let provider, let reason):
            if let provider = provider, let reason = reason {
                return "transcription.error.providerNotAvailable.detail".localized(arguments: provider, reason)
            } else if let provider = provider {
                return "transcription.error.providerNotAvailable.provider".localized(arguments: provider)
            }
            return "transcription.error.providerNotAvailable".localized
        case .modelNotDownloaded(let model, let mode):
            return Self.modelNotDownloadedMessage(model: model, mode: mode)
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
        case .unauthorized(let provider, let statusCode):
            if provider == "HyperWhisper Cloud" {
                if statusCode == 403 {
                    return "transcription.error.forbidden.hyperWhisperCloud".localized
                }
                return "transcription.error.unauthorized.hyperWhisperCloud".localized
            }
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
        case .audioTooShort:
            return "transcription.error.audioTooShort".localized
        }
    }

    /// Whether this error is retryable
    var isRetryable: Bool {
        if case .unauthorized(let provider, let statusCode) = self,
           provider == "HyperWhisper Cloud",
           statusCode == 403 {
            return true
        }
        switch self {
        case .transientNetwork(_), .invalidResponse(_), .providerNotAvailable(_, _), .streamingInterrupted, .timeout(_), .serverError(_, _):
            return true
        case .rateLimited(_):
            return true  // Can retry after waiting
        // `.localSpeechModelEvicted` is NOT retryable: the pass already reloaded
        // the model once and lost it again, so the pressure is sustained and an
        // automatic retry would reload and lose it over and over.
        case .audioFileNotFound, .apiKeyMissing(_), .modelNotDownloaded, .modelProtected, .maxRetriesExceeded, .unauthorized, .invalidRequest, .busy, .invalidAudioFormat, .audioConversionFailed, .audioFileTooLarge(_, _, _), .insufficientCredits(_, _), .quotaExceeded(_, _), .noSpeechDetected, .localRuntimeUnavailable(_), .cloudAccountRequired(_), .localSpeechModelEvicted(_), .audioTooShort(_):
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
        if case .unauthorized(let provider, let statusCode) = self,
           provider == "HyperWhisper Cloud",
           statusCode == 403 {
            return false
        }
        switch self {
        case .apiKeyMissing, .unauthorized, .insufficientCredits, .quotaExceeded,
             .cloudAccountRequired:
            return true
        case .noSpeechDetected, .transientNetwork, .invalidResponse, .rateLimited, .serverError, .timeout,
             .providerNotAvailable, .modelNotDownloaded, .modelProtected, .audioFileNotFound,
             .maxRetriesExceeded, .invalidRequest, .streamingInterrupted, .busy,
             .invalidAudioFormat, .audioConversionFailed, .audioFileTooLarge(_, _, _),
             .localRuntimeUnavailable(_), .localSpeechModelEvicted(_), .audioTooShort(_):
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
        case .unauthorized, .localRuntimeUnavailable:
            return true
        default:
            return false
        }
    }

    /// The HTTP status of a `serverError`, as a log/Sentry-safe string, or
    /// `"none"` for any other error. Carries no part of the server message.
    static func serverStatus(of error: Error) -> String {
        if case .serverError(let statusCode, _)? = error as? TranscriptionError {
            return String(statusCode)
        }
        return "none"
    }

    // MARK: - NSError bridging

    /// The `NSError` code of each case, pinned to the numbers Swift's
    /// synthesized bridging produced before issue #1518 (payload cases first in
    /// declaration order, then payload-less ones). Exhaustive with no
    /// `default:`, so a new case must pick the next number. `errorDomain` and
    /// `errorUserInfo` keep their defaults: the domain stays
    /// `String(reflecting: TranscriptionError.self)` and `LocalizedError`
    /// still supplies `localizedDescription`.
    var errorCode: Int {
        switch self {
        case .providerNotAvailable: return 0
        case .transientNetwork: return 1
        case .invalidResponse: return 2
        case .apiKeyMissing: return 3
        case .unauthorized: return 4
        case .audioFileTooLarge: return 5
        case .serverError: return 6
        case .rateLimited: return 7
        case .insufficientCredits: return 8
        case .quotaExceeded: return 9
        case .timeout: return 10
        case .localRuntimeUnavailable: return 11
        case .cloudAccountRequired: return 12
        case .localSpeechModelEvicted: return 13
        case .audioTooShort: return 14
        case .modelNotDownloaded: return 15
        case .modelProtected: return 16
        case .audioFileNotFound: return 17
        case .maxRetriesExceeded: return 18
        case .invalidRequest: return 19
        case .streamingInterrupted: return 20
        case .busy: return 21
        case .invalidAudioFormat: return 22
        case .audioConversionFailed: return 23
        case .noSpeechDetected: return 24
        }
    }

    // MARK: - Missing model (issue #1518)

    /// The text for a missing local model: names the mode and the model when
    /// both are known ("W15 uses Tiny (English-only), which is not downloaded.
    /// Download it in Model Library."), the model alone when the mode is not,
    /// and the old "Please download a model first" when there is no model name.
    ///
    /// - Parameter localize: key → format string; tests pass the Base values so
    ///   the result does not depend on the test process's language.
    static func modelNotDownloadedMessage(
        model: String?,
        mode: String?,
        localize: (String) -> String = { $0.localized }
    ) -> String {
        guard let model = nonEmpty(model) else {
            return localize("transcription.error.modelNotDownloaded")
        }
        if let mode = nonEmpty(mode) {
            return String(format: localize("transcription.error.modelNotDownloaded.modelAndMode"), mode, model)
        }
        return String(format: localize("transcription.error.modelNotDownloaded.model"), model)
    }

    /// This error with the mode's name filled in, when it is a
    /// `.modelNotDownloaded` that names its model and does not name a mode yet.
    /// Every other error comes back unchanged. The engines throw without the
    /// mode; the flows know it and call this before they show the alert or
    /// write the History reason.
    func withMode(_ modeName: String?) -> TranscriptionError {
        guard case .modelNotDownloaded(let model, let mode) = self,
              Self.nonEmpty(model) != nil,
              Self.nonEmpty(mode) == nil,
              let modeName = Self.nonEmpty(modeName) else {
            return self
        }
        return .modelNotDownloaded(model: model, mode: modeName)
    }

    /// `withMode(_:)` for any `Error`: a non-`TranscriptionError` comes back
    /// as it is.
    static func attachingMode(_ modeName: String?, to error: Error) -> Error {
        guard let transcriptionError = error as? TranscriptionError else { return error }
        return transcriptionError.withMode(modeName)
    }

    /// The missing model's display name, or `nil` for any other error.
    var missingModelName: String? {
        guard case .modelNotDownloaded(let model, _) = self else { return nil }
        return Self.nonEmpty(model)
    }

    private static func nonEmpty(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty else { return nil }
        return trimmed
    }
}
