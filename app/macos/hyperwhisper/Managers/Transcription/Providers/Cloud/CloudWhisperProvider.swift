//
//  CloudWhisperProvider.swift
//  hyperwhisper
//
//  Extracted from TranscriptionPipeline for modularity.
//
//  Wave 3 / M3-B.2: URL / header / multipart body construction and the
//  JSON response parsing now run through the Rust shared core's per-provider
//  builders (`openaiBuild/ParseTranscribeRequest` / `groqBuild/Parse…`). This
//  file keeps only the platform-owned shell: API-key configuration, the custom
//  URLSession, offline / file-existence / file-size preflight, the executor +
//  core retry loop, and logging. The core owns vocabulary encoding (the OpenAI
//  `prompt` CSV), model defaulting, language ISO-639-1 normalization, and the
//  `{text}` parse + NoSpeech-on-empty.
//

import Foundation

/// Cloud Whisper provider supporting multiple cloud services (OpenAI, Groq)
class CloudWhisperProvider: TranscriptionProvider {
    private var apiKey: String?
    private var provider: CloudProvider = .openai

    var isAvailable: Bool { apiKey != nil && !apiKey!.isEmpty }
    var name: String { provider.displayName }

    /// Configure the cloud provider with API key and provider type
    /// - Parameters:
    ///   - apiKey: The API key for the provider
    ///   - provider: The cloud provider to use
    func configure(apiKey: String, provider: CloudProvider = .openai) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Cloud provider API key trimmed · provider=\(provider.displayName, privacy: .public) · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        let suffix = String(trimmed.suffix(4))
        AppLogger.network.debug("Cloud provider API key configured · provider=\(provider.displayName, privacy: .public) · nonEmpty=\(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private)")
        self.apiKey = trimmed
        self.provider = provider
    }

    /// Shared session for OpenAI / Groq transcription. Mobile/cellular-tuned;
    /// fail-fast on offline so the retry loop (not URLSession) owns backoff.
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60.0
        config.timeoutIntervalForResource = 120.0
        config.allowsCellularAccess = true
        config.allowsExpensiveNetworkAccess = true
        config.allowsConstrainedNetworkAccess = true
        config.waitsForConnectivity = false
        config.httpMaximumConnectionsPerHost = 1
        return URLSession(configuration: config)
    }()

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard let apiKey = apiKey, !apiKey.isEmpty else {
            AppLogger.network.error("Cloud transcription aborted · provider=\(self.provider.displayName, privacy: .public) · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: self.provider.displayName)
        }

        // Preflight connectivity check to avoid hanging on offline
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Cloud transcription aborted · provider=\(self.provider.displayName, privacy: .public) · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }

        AppLogger.network.info("Cloud transcription started · provider=\(self.provider.displayName, privacy: .public) · file=\(audioURL.lastPathComponent, privacy: .public)")

        // File-existence preflight stays native (the core never touches disk).
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Cloud transcription aborted · provider=\(self.provider.displayName, privacy: .public) · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        // File size pre-validation stays native.
        let fileSize = try audioURL.fileSize()
        AppLogger.transcription.debug("Cloud transcription file size · provider=\(self.provider.displayName, privacy: .public) · sizeKB=\(fileSize / 1024, privacy: .public)")
        let maxSize = self.provider.maxFileSizeBytes
        if fileSize > maxSize {
            AppLogger.network.error("Cloud transcription aborted · provider=\(self.provider.displayName, privacy: .public) · reason=File too large · bytes=\(fileSize, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(fileSize: fileSize, limit: maxSize, providerName: self.provider.displayName)
        }

        // Reject unsupported providers early (this class only handles OpenAI/Groq).
        let supported: Set<CloudProvider> = [.openai, .groq]
        guard supported.contains(provider) else {
            throw TranscriptionError.providerNotAvailable(provider: provider.displayName, reason: "This provider is not supported for transcription")
        }

        // Build TranscribeParams. Pass the RAW boost-vocabulary terms — the core
        // builds the OpenAI/Groq `prompt` CSV itself (trim + drop-empty, no
        // lowercase/sanitize/dedup). Model: pass the mode's selected model, or ""
        // so the core applies its provider default (whisper-1 / whisper-large-v3-turbo).
        let modelToSend = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        // OpenAI/Groq: the native impl folded mode.customInstructions into the
        // `prompt` field after the vocabulary terms. The core appends
        // `params.prompt` after the vocab CSV, so pass customInstructions there.
        let customInstructions = mode?.customInstructions
        let promptForCore: String? = (customInstructions?.isEmpty == false) ? customInstructions : nil

        let contentType = AudioMimeTypeResolver.infer(for: audioURL)
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: modelToSend,
            prompt: promptForCore
        )

        let activeProvider = provider
        let request: HttpRequest
        do {
            request = try Self.buildRequest(for: activeProvider, params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: activeProvider.displayName)
        }

        AppLogger.network.info("Cloud transcription request · provider=\(activeProvider.displayName, privacy: .public) · model=\(modelToSend.isEmpty ? "<default>" : modelToSend, privacy: .public) · url=\(request.url, privacy: .public)")

        // Perform via the shared executor + core retry loop. The core's parse fn
        // classifies non-2xx (Unauthorized / QuotaExceeded / RateLimited / etc.)
        // — surface that real error on give-up.
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: activeProvider.displayName) {
                _ = try Self.parseResponse(for: activeProvider, resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }

        // Parse the success response via the core (200-but-empty → NoSpeech).
        let transcript: HwTranscript
        do {
            transcript = try Self.parseResponse(for: activeProvider, resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: activeProvider.displayName)
        }

        AppLogger.network.info("Cloud transcription completed · provider=\(activeProvider.displayName, privacy: .public) · chars=\(transcript.text.count, privacy: .public)")
        return transcript.text
    }

    // MARK: - Rust core dispatch

    /// Route to the correct core builder for OpenAI vs Groq.
    private static func buildRequest(for provider: CloudProvider, params: TranscribeParams) throws -> HttpRequest {
        switch provider {
        case .groq:
            return try groqBuildTranscribeRequest(params: params)
        default:
            return try openaiBuildTranscribeRequest(params: params)
        }
    }

    /// Route to the correct core parser for OpenAI vs Groq.
    private static func parseResponse(for provider: CloudProvider, resp: HttpResponse) throws -> HwTranscript {
        switch provider {
        case .groq:
            return try groqParseTranscribeResponse(resp: resp)
        default:
            return try openaiParseTranscribeResponse(resp: resp)
        }
    }
}

// MARK: - Health Checks

extension CloudWhisperProvider {
    /// Lightweight endpoint check for OpenAI/Groq to validate API connectivity.
    func healthCheck(apiKey: String, provider: CloudProvider) async -> ProviderHealth {
        switch provider {
        case .openai:
            return await performModelListHealthCheck(
                apiKey: apiKey,
                provider: provider,
                urlString: "https://api.openai.com/v1/models"
            )
        case .groq:
            return await performModelListHealthCheck(
                apiKey: apiKey,
                provider: provider,
                urlString: "https://api.groq.com/openai/v1/models"
            )
        default:
            return .unknown
        }
    }

    private func performModelListHealthCheck(apiKey: String, provider: CloudProvider, urlString: String) async -> ProviderHealth {
        guard let url = URL(string: urlString) else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Health check missing HTTPURLResponse · provider=\(provider.displayName, privacy: .public)")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("Health check unauthorized · provider=\(provider.displayName, privacy: .public) · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Health check failed · provider=\(provider.displayName, privacy: .public) · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Health check network error · provider=\(provider.displayName, privacy: .public) · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Health check error · provider=\(provider.displayName, privacy: .public) · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }
}
