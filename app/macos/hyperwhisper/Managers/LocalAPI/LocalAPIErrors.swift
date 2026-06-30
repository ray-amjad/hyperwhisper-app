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
    static func mapTranscriptionError(_ error: Error) -> (LocalAPIErrorCode, String, String?) {
        if let txError = error as? TranscriptionError {
            switch txError {
            case .modelNotDownloaded:
                return (.modelNotInstalled, "Required model is not installed.", "Install the model from the app's Library, or pick a different engine/model.")
            case .modelProtected:
                return (.modelNotInstalled, "Required model is locked.", nil)
            case .apiKeyMissing(let provider):
                let prov = provider ?? "this provider"
                return (.missingAPIKey, "API key for \(prov) is missing.", "Add the API key in Settings → API Keys.")
            case .unauthorized(let provider):
                let prov = provider ?? "this provider"
                return (.missingAPIKey, "API key for \(prov) is invalid or expired.", "Update the API key in Settings → API Keys.")
            case .audioFileNotFound:
                return (.fileNotFound, "Audio file not found.", "Pass an absolute path the running app can read.")
            case .invalidAudioFormat, .audioConversionFailed:
                return (.audioDecodeFailed, "Could not decode the audio file.", "Use a supported format (wav, m4a, mp3, flac).")
            case .audioFileTooLarge(_, let limit, let providerName):
                return (.invalidRequest, "Audio file exceeds \(providerName) limit (\(limit) bytes).", nil)
            case .rateLimited:
                return (.rateLimited, "Provider rate-limited the request.", "Try again in a moment.")
            case .timeout:
                return (.timeout, "Transcription timed out.", nil)
            case .providerNotAvailable(let provider, let reason):
                let prov = provider ?? "engine"
                let r = reason ?? "Not available"
                return (.engineUnavailable, "\(prov) is unavailable: \(r)", nil)
            case .transientNetwork(let details):
                return (.engineUnavailable, "Network error: \(details ?? "transient failure")", "Check connectivity and retry.")
            case .invalidResponse(let details):
                return (.transcriptionFailed, "Provider returned an invalid response: \(details ?? "")", nil)
            case .invalidRequest:
                return (.invalidRequest, "Invalid request.", nil)
            case .serverError(let statusCode, let message):
                return (.transcriptionFailed, "Provider error (HTTP \(statusCode)): \(message)", nil)
            case .noSpeechDetected:
                return (.transcriptionFailed, "No speech detected in audio.", nil)
            default:
                return (.transcriptionFailed, error.localizedDescription, nil)
            }
        }
        return (.transcriptionFailed, error.localizedDescription, nil)
    }
}
