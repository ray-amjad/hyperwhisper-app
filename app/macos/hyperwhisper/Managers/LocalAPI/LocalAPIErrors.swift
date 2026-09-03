//
//  LocalAPIErrors.swift
//  hyperwhisper
//
//  Maps thrown errors from transcription/provider code into APIFailureEnvelope
//  responses, and provides small helpers for shaping JSON HTTPResponses with
//  the standard `{ok:true, ...}` / `{ok:false, error:{...}}` shape.
//

import Foundation
import FlyingFox

enum LocalAPIResponder {

    /// JSON encoder used for every API response. ISO-8601 dates so MCP
    /// clients don't have to know about Foundation's reference-date epoch.
    static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.withoutEscapingSlashes, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    /// Encode an `Encodable` payload into a successful HTTP 200 JSON response.
    static func ok<T: Encodable>(_ payload: T) -> HTTPResponse {
        do {
            let data = try encoder.encode(payload)
            return HTTPResponse(
                statusCode: .ok,
                headers: [.contentType: "application/json; charset=utf-8"],
                body: data
            )
        } catch {
            AppLogger.network.error("LocalAPI encoding failure · \(error.localizedDescription, privacy: .public)")
            // Fallback: minimal failure envelope so the client still gets JSON.
            return failure(code: .transcriptionFailed, message: "Failed to encode response", hint: nil)
        }
    }

    /// Standard `{ok:false, error:{...}}` response (HTTP 200 by design — see
    /// the rationale in the plan: MCP wrappers can't surface error text from
    /// an empty 500).
    static func failure(code: LocalAPIErrorCode, message: String, hint: String? = nil) -> HTTPResponse {
        let envelope = APIFailureEnvelope(code: code, message: message, hint: hint)
        do {
            let data = try encoder.encode(envelope)
            return HTTPResponse(
                statusCode: .ok,
                headers: [.contentType: "application/json; charset=utf-8"],
                body: data
            )
        } catch {
            // Truly defensive — if a 3-key envelope won't encode, return raw text.
            return HTTPResponse(
                statusCode: .internalServerError,
                headers: [.contentType: "text/plain; charset=utf-8"],
                body: Data("{\"ok\":false,\"error\":{\"code\":\"TRANSCRIPTION_FAILED\",\"message\":\"encoder broken\"}}".utf8)
            )
        }
    }

    /// Render a shared-core failure as an `HTTPResponse` (issue #289).
    ///
    /// The status code comes from Rust rather than from this file. That is the
    /// point of the envelope half of #289: Linux returned 404/413/503/408 for
    /// business outcomes the docs mandate 200 for, and it did so because each
    /// platform decided the status itself.
    ///
    /// The JSON is still encoded here, through the same `JSONEncoder` every
    /// other response uses, so key order and escaping do not change on the
    /// wire. `extraHeaders` carries the pieces that are not part of the
    /// envelope — `WWW-Authenticate` on a 401.
    static func response(
        for failure: HwLocalApiFailure,
        extraHeaders: [HTTPHeader: String] = [:]
    ) -> HTTPResponse {
        let envelope = APIFailureEnvelope(
            code: LocalAPIErrorCode(shared: failure.code),
            message: failure.message,
            hint: failure.hint
        )
        var headers: [HTTPHeader: String] = [.contentType: "application/json; charset=utf-8"]
        headers.merge(extraHeaders) { _, extra in extra }
        return HTTPResponse(
            statusCode: statusCode(failure.httpStatus),
            headers: headers,
            body: (try? encoder.encode(envelope)) ?? Data()
        )
    }

    /// The four statuses `hw_localapi` can ask for, as FlyingFox constants.
    ///
    /// `FailureKind` has exactly four cases and this switch covers all four;
    /// the default exists because the value crosses an FFI boundary as a
    /// `UInt16` and Swift cannot see that it is closed. A fifth status would
    /// be a contract change that needs this arm updated, not a 500.
    private static func statusCode(_ status: UInt16) -> HTTPStatusCode {
        switch status {
        case 200: return .ok
        case 400: return .badRequest
        case 401: return .unauthorized
        case 403: return .forbidden
        default: return .internalServerError
        }
    }

    /// Reserved for genuine protocol failures (malformed JSON body, etc.).
    /// Per the design, we keep these as HTTP 400 to distinguish from
    /// successful-but-unsuccessful business outcomes.
    static func badRequest(message: String, hint: String? = nil) -> HTTPResponse {
        let envelope = APIFailureEnvelope(code: .invalidRequest, message: message, hint: hint)
        let data = (try? encoder.encode(envelope)) ?? Data()
        return HTTPResponse(
            statusCode: .badRequest,
            headers: [.contentType: "application/json; charset=utf-8"],
            body: data
        )
    }

    /// Translate a thrown `TranscriptionError` into the corresponding
    /// `LocalAPIErrorCode` plus a human-readable message + hint pair.
    /// This is the single mapping point used by `/transcribe` so the
    /// caller never sees raw `TranscriptionError` text leaking through.
    ///
    /// THE TABLE ITSELF IS THE SHARED CORE'S (issue #356 item 4). This head
    /// used to hold one of three hand-maintained `(code, message, hint)` tables
    /// — Windows and the portable head had the other two, and they disagreed on
    /// wording, on hints, and in two places on the code. All this function does
    /// now is name the reason, fill the interpolation slots off the associated
    /// values, and pass in the one hint that has to name a macOS surface.
    ///
    /// The wording that comes back is macOS's on almost every row: this is the
    /// only head whose strings are under test
    /// (`HyperWhisperCloudEntitlementTests`), so the reconciliation was done
    /// toward it. `hw-localapi/src/transcription.rs` records the rows that went
    /// the other way.
    ///
    /// Not to be confused with `RustCoreMapping.mapTranscriptionError`
    /// (`RustRetry.swift`), which turns a Rust `HwTranscriptionError` into the
    /// `TranscriptionError` this function consumes, and is upstream of it.
    static func mapTranscriptionError(_ error: Error) -> (LocalAPIErrorCode, String, String?) {
        let (reason, params) = transcriptionFailure(for: error)
        let failure = localApiMapTranscriptionError(reason: reason, params: params)
        return (LocalAPIErrorCode(shared: failure.code), failure.message, failure.hint)
    }

    /// The shared reason for a thrown error, and the slots its message and hint
    /// interpolate.
    private static func transcriptionFailure(
        for error: Error
    ) -> (HwLocalApiTranscriptionFailureReason, HwLocalApiTranscriptionFailureParams) {
        guard let txError = error as? TranscriptionError else {
            return (.transcriptionFailed, slots(detail: error.localizedDescription))
        }
        switch txError {
        case .modelNotDownloaded:
            return (.modelNotInstalled, slots())
        case .modelProtected:
            return (.modelProtected, slots())
        case .apiKeyMissing(let provider):
            return (.apiKeyMissing, slots(provider: provider, hint: "Add the API key in Settings → API Keys."))
        case .unauthorized(let provider, let statusCode):
            // A HyperWhisper Cloud 403 is the abuse guard, not a credential
            // fault: `transcribe`, `post-process`, `usage` and `assistant`
            // all answer 403 only for "Your IP has been temporarily blocked
            // due to abuse". Reporting MISSING_API_KEY there tells an API
            // client to rotate a key that is in fact valid, so this maps to
            // the temporary code instead. It is its own shared reason, and a
            // test pins that it stays RATE_LIMITED with no Settings hint.
            if provider == "HyperWhisper Cloud", statusCode == 403 {
                return (.cloudRequestForbidden, slots(provider: provider))
            }
            let settingsDestination = provider == "HyperWhisper Cloud"
                ? "Settings → HyperWhisper Cloud"
                : "Settings → API Keys"
            // `statusCode` is deliberately not passed: the crate renders an
            // HTTP status only for `.providerServerError`, and a 401 in the
            // message would say nothing an "invalid or expired" key does not.
            return (.apiKeyInvalid, slots(provider: provider, hint: "Update the API key in \(settingsDestination)."))
        case .audioFileNotFound:
            return (.audioFileNotFound, slots())
        case .invalidAudioFormat, .audioConversionFailed:
            return (.audioDecodeFailed, slots())
        case .audioFileTooLarge(_, let limit, let providerName):
            return (.audioFileTooLarge, slots(provider: providerName, limitBytes: UInt64(exactly: limit)))
        case .rateLimited(let retryAfter):
            // This head threw the `retryAfter` away and always said "Try again
            // in a moment."; the shared row renders "Retry after N seconds."
            // when the provider told us how long, which is Windows's hint.
            return (.rateLimited, slots(retryAfterSeconds: retryAfter.flatMap { UInt32(exactly: $0) }))
        case .timeout:
            return (.timeout, slots())
        case .providerNotAvailable(let provider, let reason):
            return (.engineUnavailable, slots(provider: provider, detail: reason))
        case .transientNetwork(let details):
            return (.networkUnavailable, slots(detail: details))
        case .invalidResponse(let details):
            return (.invalidProviderResponse, slots(detail: details))
        case .invalidRequest:
            return (.invalidRequest, slots())
        case .serverError(let statusCode, let message):
            return (.providerServerError, slots(detail: message, httpStatus: UInt16(exactly: statusCode)))
        case .noSpeechDetected:
            return (.noSpeechDetected, slots())
        case .cloudAccountRequired:
            // Reuses MISSING_API_KEY rather than adding a code: LocalAPIErrorCode
            // is a closed enum decoded by cross-platform clients, and this IS a
            // missing-credential condition. Without an arm it fell through to
            // the generic TRANSCRIPTION_FAILED, which is exactly the leak this
            // function exists to prevent.
            return (.cloudAccountRequired, slots(hint: "Add your account key in Settings → HyperWhisper Cloud."))
        case .localSpeechModelEvicted(let model):
            // ENGINE_UNAVAILABLE, not the generic TRANSCRIPTION_FAILED the
            // `default:` below would give it: the engine genuinely was not
            // resident, and a Local API client can act on that (retry later)
            // in a way it cannot act on "transcription failed".
            return (.localModelEvicted, slots(model: model))
        case .quotaExceeded(let provider, _):
            // No arm before this change: both of these reached the wire as the
            // generic TRANSCRIPTION_FAILED plus a localized
            // `localizedDescription`, and RATE_LIMITED is the code the closed
            // set has for exactly this. Windows already answered RATE_LIMITED.
            // The associated `message` is dropped: it is the provider's own
            // billing prose, and the shared row carries the action in its hint.
            return (.quotaExceeded, slots(provider: provider))
        case .insufficientCredits:
            // Same row. The `remaining`/`required` counts are dropped for the
            // same reason the provider message is: the shared row's hint says
            // what to do, and no other head has the numbers to render.
            return (.quotaExceeded, slots())
        default:
            // Four cases stay here on purpose, and each would need a wording
            // decision no head has made:
            //
            // * `.maxRetriesExceeded` and `.streamingInterrupted` — the
            //   underlying cause (network, rate limit, provider error) is gone
            //   by the time either is thrown, so any specific code would be a
            //   guess.
            // * `.busy` — the app is mid-transcription. ENGINE_UNAVAILABLE's
            //   hint says "pick a different engine", which is wrong advice.
            // * `.localRuntimeUnavailable` — names the local LLM used for
            //   POST-processing, not a transcription engine, and its own doc
            //   comment says the raw `reason` is for logs and must not be
            //   shown. ENGINE_UNAVAILABLE would put the wrong noun on the one
            //   route (`/post-process`) that produces it.
            //
            // The generic row passes `localizedDescription` through verbatim,
            // which is exactly what this arm always did.
            return (.transcriptionFailed, slots(detail: error.localizedDescription))
        }
    }

    /// The shared params record, with every slot defaulted to absent.
    ///
    /// The generated initializer takes all seven, so calling it inline at
    /// twenty sites would bury the two or three values that matter in five
    /// `nil`s.
    private static func slots(
        provider: String? = nil,
        detail: String? = nil,
        model: String? = nil,
        limitBytes: UInt64? = nil,
        httpStatus: UInt16? = nil,
        retryAfterSeconds: UInt32? = nil,
        hint: String? = nil
    ) -> HwLocalApiTranscriptionFailureParams {
        HwLocalApiTranscriptionFailureParams(
            provider: provider,
            detail: detail,
            model: model,
            limitBytes: limitBytes,
            httpStatus: httpStatus,
            retryAfterSeconds: retryAfterSeconds,
            hint: hint
        )
    }
}
