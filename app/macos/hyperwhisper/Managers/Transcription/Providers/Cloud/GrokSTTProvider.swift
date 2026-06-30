//
//  GrokSTTProvider.swift
//  hyperwhisper
//
//  Adapter for xAI Grok speech-to-text API (batch HTTP).
//
//  Wave 3 / M3-B.2: the multipart request build and the `{text}` parse now run
//  through the Rust shared core (`grokBuild/ParseTranscribeResponse`). The core
//  bakes the `Authorization: Bearer` header, the conditional `language` +
//  `format=true` fields (coupled, gated on xAI's supported-formatting set with
//  the `tl`→`fil` alias), the `file` part, and the NoSpeech-on-empty parse. Grok
//  STT has no model parameter and no custom-vocabulary support — both are owned
//  (dropped) by the core. This file keeps the platform-owned shell: key config,
//  the long-timeout URLSession, preflight, retry, logging, health.
//

import Foundation
import OSLog

final class GrokSTTProvider: TranscriptionProvider {
    private enum Constants {
        static let maxUploadBytes: Int64 = 500 * 1024 * 1024 // 500 MB per docs

        // Matches the Windows GrokSttService ceiling so a 500 MB upload on a
        // slow connection has enough headroom to finish. The old 120 s cap
        // made anything beyond ~30 MB fail on typical home uplinks.
        static let resourceTimeout: TimeInterval = 30 * 60
    }

    private var apiKey: String = ""
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "GrokSTTProvider")

    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = Constants.resourceTimeout
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Grok" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Grok API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        self.apiKey = trimmed

        let suffix = String(trimmed.suffix(4))
        logger.debug("🔑 Grok API key configured (non-empty: \(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private))")
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard !apiKey.isEmpty else {
            AppLogger.network.error("Grok transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: name)
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Grok transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Grok transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileBytes = try audioURL.fileSize()
        AppLogger.transcription.debug("Grok audio size · bytes=\(fileBytes, privacy: .public)")
        if fileBytes > Constants.maxUploadBytes {
            AppLogger.network.error("Grok transcription aborted · reason=File too large · bytes=\(fileBytes, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(
                fileSize: fileBytes,
                limit: Constants.maxUploadBytes,
                providerName: name
            )
        }

        if !vocabulary.isEmpty {
            AppLogger.network.info("Grok STT does not support custom vocabulary · \(vocabulary.count, privacy: .public) term(s) will be ignored")
        }

        AppLogger.network.info("Grok transcription started · file=\(audioURL.lastPathComponent, privacy: .public) · language=\(language ?? "auto", privacy: .public)")

        // Grok has no model param and no vocab support — both owned (dropped) by
        // the core. Pass the natively-resolved mime (mp4/mkv overrides) explicitly.
        let contentType = mimeType(for: audioURL)
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: [],
            apiKey: apiKey
        )

        let providerName = name
        let request: HttpRequest
        do {
            request = try grokBuildTranscribeRequest(params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerName)
        }

        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: providerName) {
                _ = try grokParseTranscribeResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }

        let transcript: HwTranscript
        do {
            transcript = try grokParseTranscribeResponse(resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerName)
        }

        AppLogger.network.info("Grok transcription completed · chars=\(transcript.text.count, privacy: .public)")
        return transcript.text
    }
}

// MARK: - Private helpers

private extension GrokSTTProvider {
    func mimeType(for url: URL) -> String {
        let overrides = [
            "mp4": "video/mp4",
            "mkv": "video/x-matroska"
        ]
        return AudioMimeTypeResolver.infer(for: url, fallback: "application/octet-stream", overrides: overrides)
    }
}
