//
//  DeepgramProvider.swift
//  hyperwhisper
//
//  Adapter skeleton for Deepgram STT.
//
//  Wave 3 / M3-B.2: the URL + query construction (model, smart_format,
//  mip_opt_out, language vs detect_language, and the model/language-gated
//  keyterm/keywords vocabulary params) and the JSON parse now run through the
//  Rust shared core (`deepgramBuild/ParseTranscribeResponse`). The body is a raw
//  binary `FileStream` (no multipart) — the executor's `.fileStream` branch
//  streams the audio file with the request `Content-Type`. The core bakes the
//  `Authorization: Token` header, resolves the model (alias migration of removed
//  IDs → nova-3-general), and parses `results.channels[0].alternatives[0].transcript`
//  with NoSpeech-on-empty. This file keeps the platform-owned shell: key config,
//  URLSession, preflight, retry, logging, health.
//
//  AUDIO MIME PARITY: Deepgram natively fell back to `application/octet-stream`
//  (not the resolver's default `audio/mp4`). We pass `audioMime` explicitly with
//  that fallback so the core (and the streamed request Content-Type) matches.
//

import Foundation

class DeepgramProvider: TranscriptionProvider {
    private var apiKey: String = ""

    /// Shared session with 180s resource timeout (matches Windows)
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 180
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Deepgram" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Deepgram API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        let suffix = String(trimmed.suffix(4))
        AppLogger.network.debug("Deepgram API key configured · nonEmpty=\(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private)")
        self.apiKey = trimmed
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard !apiKey.isEmpty else {
            AppLogger.network.error("Deepgram transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: "Deepgram")
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Deepgram transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Deepgram transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileSize = try audioURL.fileSize()
        AppLogger.transcription.debug("Deepgram audio file size · sizeKB=\(fileSize / 1024, privacy: .public)")
        let maxSize = CloudProvider.deepgram.maxFileSizeBytes
        if fileSize > maxSize {
            AppLogger.network.error("Deepgram transcription aborted · reason=File too large · bytes=\(fileSize, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(fileSize: fileSize, limit: maxSize, providerName: "Deepgram")
        }

        // Model: pass the mode's selection raw (or "" → core defaults to
        // nova-3-general). The core owns the removed-alias migration, so we do
        // NOT pre-resolve via CloudTranscriptionModels here. The core also owns
        // the model/language vocabulary gating (keyterm vs keywords vs none).
        let model = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""

        // Preserve the native `application/octet-stream` fallback (Deepgram never
        // used the resolver's audio/mp4 default).
        let contentType = AudioMimeTypeResolver.infer(for: audioURL, fallback: "application/octet-stream")

        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: model
        )

        let request: HttpRequest
        do {
            request = try deepgramBuildTranscribeRequest(params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: "Deepgram")
        }

        // NOTE: do not log request.url — Deepgram carries the user's vocabulary
        // terms as `keywords=` query parameters, which would leak into public logs.
        AppLogger.network.info("Deepgram transcription request · model=\(model.isEmpty ? "<default>" : model, privacy: .public) · language=\(language ?? "auto", privacy: .public)")

        // Execute via the shared executor + core retry loop. The core's parse fn
        // classifies non-2xx; surface the real error on give-up.
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: "Deepgram") {
                _ = try deepgramParseTranscribeResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }

        // Parse the success response via the core. An empty / missing transcript
        // (blank audio) maps to NoSpeech inside the core.
        let transcript: HwTranscript
        do {
            transcript = try deepgramParseTranscribeResponse(resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: "Deepgram")
        }

        AppLogger.network.info("Deepgram transcript parsed · chars=\(transcript.text.count, privacy: .public)")
        return transcript.text
    }
}

// MARK: - Health Checks

extension DeepgramProvider {
    /// Perform a basic GET request to verify the API key and connectivity.
    func healthCheck(apiKey: String) async -> ProviderHealth {
        guard !apiKey.isEmpty else { return .unknown }
        guard let url = URL(string: "https://api.deepgram.com/v1/projects") else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Token \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Deepgram health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("Deepgram health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Deepgram health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Deepgram health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Deepgram health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }
}
