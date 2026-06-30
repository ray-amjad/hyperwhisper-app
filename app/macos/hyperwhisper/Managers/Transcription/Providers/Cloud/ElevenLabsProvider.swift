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
        static let headerAPIKey = "xi-api-key"
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
        // (scribe_v2 — the only model the core emits keyterms for).
        let modelId = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
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
            model: modelId
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

// MARK: - Health Checks

extension ElevenLabsProvider {
    /// Probes the actual scope the app needs (`speech_to_text`) so that
    /// restricted API keys minted with only the STT scope are recognised
    /// as healthy. A short GET to `/v1/models` or `/v1/user` requires
    /// `models_read` / `user_read`, which an STT-only key doesn't have —
    /// those keys would (correctly) work for transcription but get flagged
    /// as unauthorized at the health gate.
    ///
    /// Sends ~0.1 s of inline-generated silence (about 3.2 KB of PCM) to
    /// `/v1/speech-to-text` with `model_id=scribe_v1`. Cheap, no embedded
    /// asset required, exercises the exact endpoint the app calls.
    func healthCheck(apiKey: String) async -> ProviderHealth {
        guard !apiKey.isEmpty else { return .unknown }
        guard let url = URL(string: "https://api.elevenlabs.io/v1/speech-to-text") else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue(apiKey, forHTTPHeaderField: Constants.headerAPIKey)
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let boundary = "Boundary-\(UUID().uuidString)"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var body = Data()
        appendFormField(name: "model_id", value: "scribe_v1", boundary: boundary, body: &body)
        appendFileField(name: "file",
                        fileName: "silence.wav",
                        mimeType: "audio/wav",
                        fileData: Self.tinySilenceWAV,
                        boundary: boundary,
                        body: &body)
        body.append("--\(boundary)--\r\n".data(using: .utf8)!)
        request.httpBody = body

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("ElevenLabs health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                // Genuinely unauthorized: bad key, revoked key, or restricted
                // key missing the `speech_to_text` scope. Either way, the
                // app's STT path won't work, so flag it for the UI.
                AppLogger.network.error("ElevenLabs health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            case 402, 429:
                // Quota / billing — auth succeeded, key is "good", just throttled.
                return .healthy
            default:
                AppLogger.network.error("ElevenLabs health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("ElevenLabs health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("ElevenLabs health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }

    func appendFormField(name: String, value: String, boundary: String, body: inout Data) {
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(name)\"\r\n\r\n".data(using: .utf8)!)
        body.append("\(value)\r\n".data(using: .utf8)!)
    }

    func appendFileField(name: String, fileName: String, mimeType: String, fileData: Data, boundary: String, body: inout Data) {
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(name)\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: \(mimeType)\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n".data(using: .utf8)!)
    }

    /// 0.1 s of 16-bit / 16 kHz / mono PCM silence wrapped in a minimal
    /// RIFF/WAVE container. Built once on first access.
    static let tinySilenceWAV: Data = {
        let sampleRate: UInt32 = 16000
        let numSamples: UInt32 = 1600
        let numChannels: UInt16 = 1
        let bitsPerSample: UInt16 = 16
        let byteRate = sampleRate * UInt32(numChannels) * UInt32(bitsPerSample / 8)
        let blockAlign = numChannels * (bitsPerSample / 8)
        let dataSize = numSamples * UInt32(blockAlign)
        let riffSize = 36 + dataSize

        func le<T: FixedWidthInteger>(_ value: T) -> Data {
            var le = value.littleEndian
            return withUnsafeBytes(of: &le) { Data($0) }
        }

        var w = Data()
        w.append(Data("RIFF".utf8))
        w.append(le(riffSize))
        w.append(Data("WAVE".utf8))
        w.append(Data("fmt ".utf8))
        w.append(le(UInt32(16)))
        w.append(le(UInt16(1)))           // PCM
        w.append(le(numChannels))
        w.append(le(sampleRate))
        w.append(le(byteRate))
        w.append(le(blockAlign))
        w.append(le(bitsPerSample))
        w.append(Data("data".utf8))
        w.append(le(dataSize))
        w.append(Data(count: Int(dataSize)))
        return w
    }()
}
