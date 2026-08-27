//
//  GeminiTranscribeProvider.swift
//  hyperwhisper
//
//  GEMINI 3.5 TRANSCRIBE PROVIDER
//  Adapter for Google's dedicated speech API, `POST /v1beta/interactions`.
//
//  API SPECIFICATION:
//  - Endpoint: https://generativelanguage.googleapis.com/v1beta/interactions
//  - Auth: `x-goog-api-key` header (NOT the `?key=` query param `.gemini` uses)
//  - Request: JSON with the audio inline as base64 (`Body.jsonWithBase64File`)
//  - Response: transcript at `steps[0].content[0].text`
//
//  NOT THE SAME PROVIDER AS `GeminiTranscriptionProvider`. Same vendor, but a
//  different API, a different BYOK key slot (`.geminiTranscribe`) and different
//  eligibility. Routing `gemini-3.5-transcribe` through `:generateContent` — the
//  path `GeminiTranscriptionProvider` uses — is accepted, BILLS the audio, and
//  returns empty text with no error. See the module docs on
//  `hw_net::providers::gemini_transcribe` (TRAP 1).
//
//  SINGLE-SHOT, unlike `.gemini`: the endpoint takes the audio inline, so there
//  is no Files-API upload/poll/delete dance. The shape below is the standard
//  Rust-backed single-shot one (Grok / Mistral): build via the core, execute via
//  `RustRetry` + `RustHTTPExecutor`, parse via the core.
//
//  The core owns model defaulting (empty → gemini-3.5-transcribe), the
//  `language_codes` array, the `custom_vocabulary` list and its mutual exclusion
//  with diarization/timestamps (TRAP 2), the `Api-Revision` pin, and the
//  NoSpeech-on-empty parse. The live model (`gemini-3.5-transcribe-live`) is
//  WebSocket-only and the core's REST builder rejects it with a 400 — it is
//  deliberately not offered as a pre-recorded model.
//
//  This file keeps the platform-owned shell: key config, URLSession, preflight,
//  retry, logging.
//

import Foundation

class GeminiTranscribeProvider: TranscriptionProvider {
    private var apiKey: String = ""

    /// The whole request body — JSON plus the base64 audio — is uploaded in one
    /// shot, so the resource budget only has to cover 14 MB of audio (~19 MB
    /// encoded) rather than a multi-GB file.
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 120
        config.timeoutIntervalForResource = 600
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Gemini 3.5 Transcribe" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Gemini 3.5 Transcribe API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        let suffix = String(trimmed.suffix(4))
        AppLogger.network.debug("Gemini 3.5 Transcribe API key configured · nonEmpty=\(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private)")
        self.apiKey = trimmed
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        // STEP 1: Validate preconditions (stays native)
        guard !apiKey.isEmpty else {
            AppLogger.network.error("Gemini 3.5 Transcribe transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: name)
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Gemini 3.5 Transcribe transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Gemini 3.5 Transcribe transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        // The cap is on the RAW bytes: base64 inflates them ~33% and the whole
        // body travels inline, so this guard is what keeps the request under the
        // endpoint's ceiling. Checking it here turns an opaque upstream 400 into
        // the app's own "file too large" message.
        let fileSize = try audioURL.fileSize()
        AppLogger.transcription.debug("Gemini 3.5 Transcribe audio file size · sizeKB=\(fileSize / 1024, privacy: .public)")
        let maxSize = CloudProvider.geminiTranscribe.maxFileSizeBytes
        if fileSize > maxSize {
            AppLogger.network.error("Gemini 3.5 Transcribe transcription aborted · reason=File too large · bytes=\(fileSize, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(fileSize: fileSize, limit: maxSize, providerName: name)
        }

        // STEP 2: Build TranscribeParams. Model: mode selection or "" (core
        // defaults to gemini-3.5-transcribe). Pass the RAW vocabulary terms —
        // the core normalizes them and owns the custom_vocabulary rules. Pass
        // the natively-resolved mime explicitly.
        let model = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        let contentType = AudioMimeTypeResolver.infer(for: audioURL, fallback: "audio/wav")
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: model,
            // Direct-vendor request: the core cannot attach X-Latency-Opt-Out to
            // one by construction. Pass the user's real choice anyway so this site
            // stays correct if it is ever routed.
            shareAnonymousSpeedData: !LatencyOptOut.isEnabled
        )

        let request: HttpRequest
        do {
            request = try geminiTranscribeBuildTranscribeRequest(params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: name)
        }

        AppLogger.network.info("Gemini 3.5 Transcribe request · model=\(model.isEmpty ? "<default>" : model, privacy: .public) · language=\(language ?? "auto", privacy: .public)")

        // STEP 3: Execute via the shared executor + core retry loop. The
        // executor re-encodes the audio from disk on every attempt, so the
        // request is safely re-issuable.
        let providerName = name
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: providerName) {
                _ = try geminiTranscribeParseTranscribeResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }

        // STEP 4: Parse the success response via the core (empty → NoSpeech).
        let transcript: HwTranscript
        do {
            transcript = try geminiTranscribeParseTranscribeResponse(resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerName)
        }

        AppLogger.network.info("Gemini 3.5 Transcribe transcript parsed · chars=\(transcript.text.count, privacy: .public)")
        return transcript.text
    }
}
