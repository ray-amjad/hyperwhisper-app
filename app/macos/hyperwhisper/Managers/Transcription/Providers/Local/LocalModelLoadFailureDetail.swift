import Foundation
import os
import FluidAudio

// LOCAL MODEL LOAD FAILURE DETAIL (#1017):
//
// A failed FluidAudio load used to reach Sentry as the error domain and code
// only. For a `DownloadUtils.HuggingFaceDownloadError` that code is a Swift
// enum case number, and for CoreML it is a generic `0`, so the report could
// not say WHY the load failed. This reads the error chain into fixed
// identifiers that the Parakeet and Nemotron providers both log.
//
// Privacy: every value returned here is a fixed slug, a number or an
// NSError domain (developer-fixed, never user text). It never reads the
// `path` of `downloadFailed` / `modelNotFound`, the `message` of
// `rateLimited`, the `snippet` of `htmlErrorResponse`, `localizedDescription`
// or any userInfo value other than `NSUnderlyingErrorKey`.
enum LocalModelLoadFailureDetail {

    /// The domain FluidAudio 0.15.2 gives the `NSError` it wraps a non-2xx
    /// file response in: `downloadFailed(path:underlying: NSError(domain: "HTTP", code: status))`.
    static let httpErrorDomain = "HTTP"

    /// Why a load failed, as fixed identifiers only.
    struct Fields: Equatable, Sendable {
        /// `DownloadUtils.HuggingFaceDownloadError` case slug; nil for any other error.
        var hfErrorCase: String? = nil
        /// HTTP status from `rateLimited(statusCode:)` or an `"HTTP"` underlying error.
        var httpStatus: Int? = nil
        /// Domain / code of the `downloadFailed` underlying error, or of the
        /// error's `NSUnderlyingErrorKey` (CoreML).
        var underlyingErrorDomain: String? = nil
        var underlyingErrorCode: Int? = nil
    }

    /// The load phase FluidAudio last reported through its progress handler.
    enum LoadPhase: Equatable, Sendable {
        case listing
        case downloading(completedFiles: Int, totalFiles: Int)
        /// `modelSlug` is one of `compilingModelSlug(for:)`'s fixed values.
        case compiling(modelSlug: String)
        /// `loadModels`' closing `.compiling(modelName: "")`: every model compiled.
        case compiled

        init(_ phase: DownloadUtils.DownloadPhase) {
            // No default: a FluidAudio bump that adds a phase fails to compile
            // here instead of silently reporting the wrong phase.
            switch phase {
            case .listing:
                self = .listing
            case .downloading(let completedFiles, let totalFiles):
                self = .downloading(completedFiles: completedFiles, totalFiles: totalFiles)
            case .compiling(let modelName):
                self = modelName.isEmpty
                    ? .compiled
                    : .compiling(modelSlug: LocalModelLoadFailureDetail.compilingModelSlug(for: modelName))
            }
        }

        var slug: String {
            switch self {
            case .listing: return "listing"
            case .downloading: return "downloading"
            case .compiling: return "compiling"
            case .compiled: return "compiled"
            }
        }
    }

    // MARK: - Error chain

    static func fields(for error: Error) -> Fields {
        var fields = Fields()
        if let hfError = error as? DownloadUtils.HuggingFaceDownloadError {
            // No default, for the same reason as `LoadPhase.init`.
            switch hfError {
            case .invalidResponse:
                fields.hfErrorCase = "invalid_response"
            case .rateLimited(let statusCode, _):
                fields.hfErrorCase = "rate_limited"
                fields.httpStatus = statusCode
            case .downloadFailed(_, let underlying):
                fields.hfErrorCase = "download_failed"
                let underlyingError = underlying as NSError
                fields.underlyingErrorDomain = underlyingError.domain
                fields.underlyingErrorCode = underlyingError.code
                if underlyingError.domain == httpErrorDomain {
                    fields.httpStatus = underlyingError.code
                }
            case .modelNotFound:
                fields.hfErrorCase = "model_not_found"
            case .htmlErrorResponse:
                fields.hfErrorCase = "html_error_response"
            }
            return fields
        }

        if let underlyingError = (error as NSError).userInfo[NSUnderlyingErrorKey] as? NSError {
            fields.underlyingErrorDomain = underlyingError.domain
            fields.underlyingErrorCode = underlyingError.code
        }
        return fields
    }

    /// Maps a FluidAudio compile-phase model name (`"JointDecisionv3.mlmodelc"`,
    /// `"EncoderInt4.mlmodelc"`, …) to a fixed slug, so a renamed or unknown
    /// file never reaches telemetry as raw text.
    static func compilingModelSlug(for modelName: String) -> String {
        let name = modelName.lowercased()
        if name.hasPrefix("preprocessor") { return "preprocessor" }
        if name.hasPrefix("encoder") { return "encoder" }
        if name.hasPrefix("decoder") { return "decoder" }
        if name.hasPrefix("joint") { return "joint" }
        return "other"
    }

    // MARK: - Sentry extras

    /// Every key is `<keyPrefix>_…`; each String-valued one must be on
    /// `SentryService.allowedExtraKeys` or it arrives as `[redacted]`.
    static func extras(for fields: Fields, keyPrefix: String) -> [String: Any] {
        var extras: [String: Any] = [:]
        if let hfErrorCase = fields.hfErrorCase {
            extras["\(keyPrefix)_hf_error_case"] = hfErrorCase
        }
        if let httpStatus = fields.httpStatus {
            extras["\(keyPrefix)_http_status"] = httpStatus
        }
        if let underlyingErrorDomain = fields.underlyingErrorDomain {
            extras["\(keyPrefix)_underlying_error_domain"] = underlyingErrorDomain
        }
        if let underlyingErrorCode = fields.underlyingErrorCode {
            extras["\(keyPrefix)_underlying_error_code"] = underlyingErrorCode
        }
        return extras
    }

    /// `last_phase` is `"none"` when the progress handler never fired.
    static func extras(for phase: LoadPhase?, keyPrefix: String) -> [String: Any] {
        var extras: [String: Any] = ["\(keyPrefix)_last_phase": phase?.slug ?? "none"]
        switch phase {
        case .downloading(let completedFiles, let totalFiles):
            extras["\(keyPrefix)_files_completed"] = completedFiles
            extras["\(keyPrefix)_files_total"] = totalFiles
        case .compiling(let modelSlug):
            extras["\(keyPrefix)_compiling_model"] = modelSlug
        case .listing, .compiled, nil:
            break
        }
        return extras
    }

    // MARK: - os_log

    /// One public-safe string for an `os.Logger` line (every part is fixed).
    static func logSummary(for fields: Fields) -> String {
        let hfErrorCase = fields.hfErrorCase ?? "none"
        let httpStatus = fields.httpStatus.map { String($0) } ?? "none"
        let underlyingDomain = fields.underlyingErrorDomain ?? "none"
        let underlyingCode = fields.underlyingErrorCode.map { String($0) } ?? "none"
        return "hfErrorCase=\(hfErrorCase) httpStatus=\(httpStatus) underlyingErrorDomain=\(underlyingDomain) underlyingErrorCode=\(underlyingCode)"
    }

    static func logSummary(for phase: LoadPhase?) -> String {
        switch phase {
        case .downloading(let completedFiles, let totalFiles):
            return "lastPhase=downloading filesCompleted=\(completedFiles) filesTotal=\(totalFiles)"
        case .compiling(let modelSlug):
            return "lastPhase=compiling compilingModel=\(modelSlug)"
        case .listing:
            return "lastPhase=listing"
        case .compiled:
            return "lastPhase=compiled"
        case nil:
            return "lastPhase=none"
        }
    }
}

/// Holds only the LAST phase a FluidAudio progress handler reported. The
/// handler is `@Sendable` and "called on an unspecified queue", so the state
/// sits behind a lock. Nothing is logged per tick.
final class LocalModelLoadPhaseRecorder: Sendable {
    private let state = OSAllocatedUnfairLock<LocalModelLoadFailureDetail.LoadPhase?>(initialState: nil)

    func record(_ progress: DownloadUtils.DownloadProgress) {
        let phase = LocalModelLoadFailureDetail.LoadPhase(progress.phase)
        state.withLock { $0 = phase }
    }

    var lastPhase: LocalModelLoadFailureDetail.LoadPhase? {
        state.withLock { $0 }
    }
}
