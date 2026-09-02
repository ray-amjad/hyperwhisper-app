//
//  MetaMuseProvider.swift
//  hyperwhisper
//
//  Direct, batch-only Meta Muse transcription. Swift owns secure-key lookup,
//  artifact validation, transport, retry, and cancellation. The Rust shared
//  core exclusively owns Meta's multipart wire format and response parser.
//

import Foundation

final class MetaMuseProvider: TranscriptionProvider {
    static let modelID = "muse-voice-transcribe-1.0"
    static let maxUploadBytes: Int64 = 32 * 1024 * 1024
    /// Bounded source envelope for containers that can shrink during WAV normalization.
    static let maxSourceBytes: Int64 = 64 * 1024 * 1024
    static let maxDurationSeconds: Double = 10 * 60

    private var apiKey = ""
    private let execute: RustRetry.Executor
    private let isOnline: () -> Bool
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 120
        configuration.timeoutIntervalForResource = 600
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Meta Muse" }

    init(
        execute: @escaping RustRetry.Executor = { request, session in
            try await RustHTTPExecutor.execute(request, session: session)
        },
        isOnline: @escaping () -> Bool = { NetworkStatus.shared.isOnline }
    ) {
        self.execute = execute
        self.isOnline = isOnline
    }

    func configure(apiKey: String) {
        // Keep no derived key material in logs. The key exists only in this
        // provider instance and the Keychain-backed settings manager.
        self.apiKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        AppLogger.network.debug("Meta Muse API key configured · nonEmpty=\(!self.apiKey.isEmpty, privacy: .public)")
    }

    func transcribe(
        audioURL: URL,
        language: String?,
        mode: Mode?,
        vocabulary: [Vocabulary]
    ) async throws -> String {
        guard !apiKey.isEmpty else {
            throw TranscriptionError.apiKeyMissing(provider: name)
        }
        guard isOnline() else {
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            throw TranscriptionError.audioFileNotFound
        }

        let sourceIsCanonical = (try? MetaMuseWAVInspector.inspect(url: audioURL)) != nil
        if !sourceIsCanonical {
            let sourceBytes = try audioURL.fileSize()
            guard sourceBytes <= Self.maxSourceBytes else {
                throw TranscriptionError.audioFileTooLarge(
                    fileSize: sourceBytes,
                    limit: Self.maxSourceBytes,
                    providerName: name
                )
            }
        }

        return try await CloudAudioFormatRecovery.withMuseTransportNormalization(
            sourceURL: audioURL,
            isCanonicalMuseWAV: { _ in sourceIsCanonical },
            reencode: CloudAudioFormatRecovery.reencodeToWAV
        ) { [self] uploadURL in
            try MetaMuseWAVInspector.validateForUpload(url: uploadURL)
            return try await transcribeCanonicalWAV(
                uploadURL,
                language: language,
                mode: mode,
                vocabulary: vocabulary
            )
        }
    }

    private func transcribeCanonicalWAV(
        _ audioURL: URL,
        language: String?,
        mode: Mode?,
        vocabulary: [Vocabulary]
    ) async throws -> String {
        // Re-open and validate immediately before request construction. The body
        // remains a disk-backed FileRef through UniFFI and is streamed per retry.
        try MetaMuseWAVInspector.validateForUpload(url: audioURL)
        try Task.checkCancellation()

        let selectedModel = mode?.cloudTranscriptionModel?.trimmingCharacters(in: .whitespacesAndNewlines)
        let model = selectedModel?.isEmpty == false ? selectedModel! : Self.modelID
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: CloudAudioFormatRecovery.wavContentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: model,
            shareAnonymousSpeedData: !LatencyOptOut.isEnabled
        )

        let request: HttpRequest
        do {
            request = try metaBuildTranscribeRequest(params: params)
        } catch let error as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(error, providerName: name)
        }

        let providerName = name
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: RustCoreMapping.parseErrorClosure(providerName: providerName) {
                _ = try metaParseTranscribeResponse(resp: $0)
            },
            execute: execute
        )
        try Task.checkCancellation()

        do {
            return try metaParseTranscribeResponse(resp: response).text
        } catch let error as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(error, providerName: providerName)
        }
    }
}

/// Bounded RIFF metadata reader for the platform-owned final-artifact gate.
/// It does not parse Meta's request or response contract.
enum MetaMuseWAVInspector {
    struct Metadata: Equatable {
        let sampleRate: UInt32
        let channels: UInt16
        let bitsPerSample: UInt16
        let dataBytes: UInt32

        var durationSeconds: Double {
            Double(dataBytes) / Double(sampleRate * UInt32(channels) * UInt32(bitsPerSample / 8))
        }
    }

    static func inspect(url: URL) throws -> Metadata {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let fileBytes = attributes[.size] as? Int64 else {
            throw TranscriptionError.audioFileNotFound
        }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let header = try handle.read(upToCount: 1024 * 1024) ?? Data()
        guard header.count >= 12,
              String(data: header[0..<4], encoding: .ascii) == "RIFF",
              String(data: header[8..<12], encoding: .ascii) == "WAVE" else {
            throw TranscriptionError.serverError(statusCode: 400, message: "Meta Muse requires a WAV file")
        }

        var cursor = 12
        var format: (UInt16, UInt16, UInt32, UInt16)?
        var dataBytes: UInt32?
        while cursor + 8 <= header.count {
            let id = String(data: header[cursor..<(cursor + 4)], encoding: .ascii)
            let size = readUInt32LE(header, cursor + 4)
            let body = cursor + 8
            if id == "data" {
                // Only the data chunk header must fit in the bounded prefix;
                // audio payload can be up to the 32 MiB transport limit.
                guard Int64(body) + Int64(size) <= fileBytes else {
                    throw TranscriptionError.serverError(
                        statusCode: 400,
                        message: "Meta Muse WAV data is truncated"
                    )
                }
                dataBytes = size
                break
            }
            guard body + Int(size) <= header.count else { break }
            if id == "fmt ", size >= 16 {
                format = (
                    readUInt16LE(header, body),
                    readUInt16LE(header, body + 2),
                    readUInt32LE(header, body + 4),
                    readUInt16LE(header, body + 14)
                )
            }
            cursor = body + Int(size) + Int(size % 2)
        }

        guard let (audioFormat, channels, sampleRate, bitsPerSample) = format,
              let dataBytes,
              audioFormat == 1,
              channels == 1,
              bitsPerSample == 16,
              sampleRate == 16_000 || sampleRate == 24_000 else {
            throw TranscriptionError.serverError(
                statusCode: 400,
                message: "Meta Muse requires mono PCM16 WAV at 16 kHz or 24 kHz"
            )
        }
        return Metadata(
            sampleRate: sampleRate,
            channels: channels,
            bitsPerSample: bitsPerSample,
            dataBytes: dataBytes
        )
    }

    static func validateForUpload(url: URL) throws {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let bytes = attributes[.size] as? Int64 else {
            throw TranscriptionError.audioFileNotFound
        }
        guard bytes <= MetaMuseProvider.maxUploadBytes else {
            throw TranscriptionError.audioFileTooLarge(
                fileSize: bytes,
                limit: MetaMuseProvider.maxUploadBytes,
                providerName: "Meta Muse"
            )
        }
        let metadata = try inspect(url: url)
        guard metadata.durationSeconds <= MetaMuseProvider.maxDurationSeconds else {
            throw TranscriptionError.serverError(
                statusCode: 400,
                message: "Meta Muse audio must be 10 minutes or shorter"
            )
        }
    }

    private static func readUInt16LE(_ data: Data, _ offset: Int) -> UInt16 {
        UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
    }

    private static func readUInt32LE(_ data: Data, _ offset: Int) -> UInt32 {
        UInt32(data[offset])
            | (UInt32(data[offset + 1]) << 8)
            | (UInt32(data[offset + 2]) << 16)
            | (UInt32(data[offset + 3]) << 24)
    }
}
