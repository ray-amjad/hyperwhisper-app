//
//  ElevenLabsProvider.swift
//  hyperwhisper
//
//  Adapter for ElevenLabs Scribe speech-to-text API.
//
//  Wave 3 / M3-B.2: the multipart request build and the JSON response parse now
//  run through the Rust shared core (`elevenlabsBuild/ParseTranscribeResponse`).
//  The core bakes the `xi-api-key` auth header, the `model_id` /
//  `tag_audio_events` / `language_code` fields, the capped repeated `keyterms`
//  fields (Scribe v2 only — 100 terms, ≤50 chars each), and the multi-shape
//  `text` / `transcripts` / `words` parse + NoSpeech-on-empty. This file keeps
//  the platform-owned shell: key config, URLSession, preflight, retry, logging,
//  and the STT-scope health probe.
//

import Foundation
import OSLog

final class ElevenLabsProvider: TranscriptionProvider {
    private enum Constants {
        static let maxUploadBytes: Int64 = 3 * 1024 * 1024 * 1024 // 3 GB limit per docs
    }

    private var apiKey: String = ""
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ElevenLabsProvider")

    /// Shared session with 120s timeout
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 120
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "ElevenLabs" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("ElevenLabs API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        self.apiKey = trimmed

        let suffix = String(trimmed.suffix(4))
        logger.debug("🔑 ElevenLabs API key configured (non-empty: \(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private))")
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard !apiKey.isEmpty else {
            AppLogger.network.error("ElevenLabs transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: name)
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("ElevenLabs transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("ElevenLabs transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileBytes = try audioURL.fileSize()
        AppLogger.transcription.debug("ElevenLabs audio size · bytes=\(fileBytes, privacy: .public)")
        if fileBytes > Constants.maxUploadBytes {
            AppLogger.network.error("ElevenLabs transcription aborted · reason=File too large · bytes=\(fileBytes, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(
                fileSize: fileBytes,
                limit: Constants.maxUploadBytes,
                providerName: name
            )
        }

        // Model: pass the mode's selection, or "" so the core applies its default
        // (scribe_v2 — the only model the core emits keyterms for). Legacy IDs are
        // resolved here because the Rust core's `elevenlabs` provider — unlike
        // `assemblyai`, which owns its own `resolve_model_alias` — passes
        // `model_id` straight through unresolved; without this the redirect only
        // takes effect for Swift-side lookups (e.g. `model(withId:)`), not the
        // actual wire request.
        let rawModelId = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        let modelId = rawModelId.isEmpty ? "" : CloudTranscriptionModels.resolveModelAlias(rawModelId, provider: .elevenLabs)
        AppLogger.network.info("ElevenLabs transcription started · model=\(modelId.isEmpty ? "<default>" : modelId, privacy: .public) · file=\(audioURL.lastPathComponent, privacy: .public)")

        // Pass the natively-resolved mime (mp4/mov overrides preserved) explicitly
        // so the core's file part Content-Type matches the old native value. Pass
        // the RAW vocabulary terms — the core caps/filters keyterms (Scribe v2).
        let contentType = mimeType(for: audioURL)
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: modelId,
            // Direct-vendor request: the core cannot attach X-Latency-Opt-Out to
            // one by construction. Pass the user's real choice anyway so this site
            // stays correct if it is ever routed.
            shareAnonymousSpeedData: !LatencyOptOut.isEnabled
        )

        let providerName = name
        let request: HttpRequest
        do {
            request = try elevenlabsBuildTranscribeRequest(params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerName)
        }

        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: providerName) {
                _ = try elevenlabsParseTranscribeResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }

        let transcript: HwTranscript
        do {
            transcript = try elevenlabsParseTranscribeResponse(resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerName)
        }

        AppLogger.network.info("ElevenLabs transcription completed · chars=\(transcript.text.count, privacy: .public)")
        return transcript.text
    }
}

// MARK: - Private helpers

private extension ElevenLabsProvider {
    func mimeType(for url: URL) -> String {
        let overrides = [
            "mp4": "video/mp4",
            "mov": "video/quicktime"
        ]
        return AudioMimeTypeResolver.infer(for: url, fallback: "application/octet-stream", overrides: overrides)
    }
}
