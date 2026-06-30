//
//  MistralProvider.swift
//  hyperwhisper
//
//  MISTRAL PROVIDER
//  Adapter for Mistral's Voxtral speech-to-text transcription API.
//
//  API SPECIFICATION:
//  - Endpoint: https://api.mistral.ai/v1/audio/transcriptions
//  - Auth: x-api-key header (NOT Bearer token)
//  - Request: Multipart form-data with 'model' and 'file' fields
//  - Response: { "text": "..." }
//
//  SUPPORTED MODELS:
//  - voxtral-mini-latest: State-of-the-art transcription model ($0.002/min)
//
//  LIMITATIONS:
//  - No custom vocabulary/prompt support
//  - language and timestamp_granularities cannot be used together
//
//  Wave 3 / M3-B.2: the multipart request build and the `{text}` parse now run
//  through the Rust shared core (`mistralBuild/ParseTranscribeResponse`). The
//  core bakes the `x-api-key` auth header, the `model` / `language` fields (no
//  vocabulary — Mistral doesn't support it), and the NoSpeech-on-empty parse.
//  This file keeps the platform-owned shell: key config, URLSession, preflight,
//  retry, logging, health.
//

import Foundation

class MistralProvider: TranscriptionProvider {
    private var apiKey: String = ""

    /// Shared session with 120s timeout
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 120
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Mistral" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Mistral API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        let suffix = String(trimmed.suffix(4))
        AppLogger.network.debug("Mistral API key configured · nonEmpty=\(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private)")
        self.apiKey = trimmed
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        // STEP 1: Validate preconditions (stays native)
        guard !apiKey.isEmpty else {
            AppLogger.network.error("Mistral transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: "Mistral")
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Mistral transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Mistral transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileSize = try audioURL.fileSize()
        AppLogger.transcription.debug("Mistral audio file size · sizeKB=\(fileSize / 1024, privacy: .public)")
        let maxSize = CloudProvider.mistral.maxFileSizeBytes
        if fileSize > maxSize {
            AppLogger.network.error("Mistral transcription aborted · reason=File too large · bytes=\(fileSize, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(fileSize: fileSize, limit: maxSize, providerName: "Mistral")
        }

        // STEP 2: Build TranscribeParams. Model: mode selection or "" (core
        // defaults to voxtral-mini-latest). Mistral has no vocabulary support —
        // the core ignores the vocab list, so pass an empty list. Pass the
        // natively-resolved mime (fallback audio/wav) explicitly.
        let model = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        let contentType = AudioMimeTypeResolver.infer(for: audioURL, fallback: "audio/wav")
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: [],
            apiKey: apiKey,
            model: model
        )

        let request: HttpRequest
        do {
            request = try mistralBuildTranscribeRequest(params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: "Mistral")
        }

        AppLogger.network.info("Mistral transcription request · model=\(model.isEmpty ? "<default>" : model, privacy: .public) · language=\(language ?? "auto", privacy: .public)")

        // STEP 3: Execute via the shared executor + core retry loop.
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: "Mistral") {
                _ = try mistralParseTranscribeResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }

        // STEP 4: Parse the success response via the core (empty → NoSpeech).
        let transcript: HwTranscript
        do {
            transcript = try mistralParseTranscribeResponse(resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: "Mistral")
        }

        AppLogger.network.info("Mistral transcript parsed · chars=\(transcript.text.count, privacy: .public)")
        return transcript.text
    }
}

// MARK: - Health Checks

extension MistralProvider {
    /// Perform a basic GET request to verify the API key and connectivity.
    /// Note: Mistral uses Bearer token for /v1/models endpoint (different from transcription x-api-key)
    func healthCheck(apiKey: String) async -> ProviderHealth {
        guard !apiKey.isEmpty else { return .unknown }
        guard let url = URL(string: "https://api.mistral.ai/v1/models") else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        // Health check uses Bearer token format (different from transcription's x-api-key)
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Mistral health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("Mistral health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Mistral health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Mistral health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Mistral health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }
}
