//
//  SonioxProvider.swift
//  hyperwhisper
//
//  Async/file transcription via Soniox Files + Transcriptions APIs.
//
//  Wave 3 / M3-B.3: URL / header / body construction and response parsing now
//  run through the Rust shared core's per-step builders/parsers
//  (`sonioxBuild/ParseUploadRequest`, `…CreateRequest`, `…StatusRequest`,
//  `…TranscriptRequest`, `…DeleteTranscriptionRequest`, `…DeleteFileRequest`).
//  This file keeps only the platform-owned shell: API-key configuration, the
//  shared URLSession, offline / file-existence / file-size preflight, the
//  executor + core retry loop for the non-poll steps, the BESPOKE Swift status
//  poll loop (Swift owns the deadline + sleep interval + cancellation +
//  transient tolerance), the fire-and-forget cleanup deletes, and logging.
//
//  The core owns model defaulting (empty → stt-async-v5), the `context`
//  vocabulary build, the `language_hints` gate, the `status == "error"` →
//  QuotaExceeded/BadRequest throw (incl. balance/funds/autopay/quota/limit
//  keyword mapping), and the transcript `{text}` parse + NoSpeech-on-empty.
//

import Foundation

class SonioxProvider: TranscriptionProvider {
    private enum Constants {
        /// Base URL retained ONLY for the out-of-scope health check (the
        /// transcription steps now build URLs in the Rust core).
        static let apiBaseURL = "https://api.soniox.com/v1"
        static let maxPollAttempts = 180
        /// Total wall-clock budget for the status-poll loop, decoupled from the
        /// attempt cap so tuning one doesn't silently change the other.
        static let maxPollDurationSeconds: TimeInterval = 180
        static let pollIntervalNanoseconds: UInt64 = 1_000_000_000
    }

    private var apiKey: String = ""

    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 180
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Soniox" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Soniox API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        let suffix = String(trimmed.suffix(4))
        AppLogger.network.debug("Soniox API key configured · nonEmpty=\(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private)")
        self.apiKey = trimmed
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard !apiKey.isEmpty else {
            AppLogger.network.error("Soniox transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: "Soniox")
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Soniox transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Soniox transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileSize = try audioURL.fileSize()
        AppLogger.transcription.debug("Soniox audio file size · sizeKB=\(fileSize / 1024, privacy: .public)")
        let maxSize = CloudProvider.soniox.maxFileSizeBytes
        if fileSize > maxSize {
            AppLogger.network.error("Soniox transcription aborted · reason=File too large · bytes=\(fileSize, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(fileSize: fileSize, limit: maxSize, providerName: "Soniox")
        }

        // Build TranscribeParams. Pass the RAW model id (empty → core default
        // stt-async-v5) and sanitized vocabulary boost terms — the core's create builder
        // owns model defaulting, the `context` vocabulary join, and the
        // `language_hints` gate. The previous catalog-default lookup is no longer
        // needed (the core's default tracks the catalog default).
        let modelToSend = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        let contentType = AudioMimeTypeResolver.infer(for: audioURL, fallback: "application/octet-stream")
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: modelToSend
        )

        AppLogger.network.info("Soniox transcription started · file=\(audioURL.lastPathComponent, privacy: .public) · language=\(language ?? "auto", privacy: .public)")

        var fileId: String?
        var transcriptionId: String?
        do {
            let uploadedFileId = try await uploadFile(params: params)
            fileId = uploadedFileId
            let createdId = try await createTranscription(params: params, fileId: uploadedFileId)
            transcriptionId = createdId

            try await waitForCompletion(params: params, id: createdId)
            // The core's transcript parse owns NoSpeech-on-empty (throws), so we
            // do not re-check for empty text here.
            let text = try await fetchTranscript(params: params, id: createdId)

            Task {
                await cleanupTranscription(params: params, id: createdId)
                await deleteFile(params: params, id: uploadedFileId)
            }

            AppLogger.network.info("Soniox transcript parsed · chars=\(text.count, privacy: .public)")
            return text
        } catch {
            if let transcriptionId {
                Task { await self.cleanupTranscription(params: params, id: transcriptionId) }
            }
            if let fileId {
                Task { await self.deleteFile(params: params, id: fileId) }
            }
            throw error
        }
    }

    // MARK: - Private (Rust-core-driven steps)

    private func mapError(_ error: Error) -> Error {
        if let hwErr = error as? HwTranscriptionError {
            return RustCoreMapping.mapTranscriptionError(hwErr, providerName: name)
        }
        return error
    }

    /// Step 1: upload audio (multipart file part). Single-shot via executor +
    /// core retry.
    private func uploadFile(params: TranscribeParams) async throws -> String {
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { try sonioxBuildUploadRequest(params: params) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try sonioxParseUploadResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        do {
            return try sonioxParseUploadResponse(resp: response)
        } catch {
            throw mapError(error)
        }
    }

    /// Step 2: create the transcription job. Single-shot via executor + core retry.
    private func createTranscription(params: TranscribeParams, fileId: String) async throws -> String {
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { try sonioxBuildCreateRequest(params: params, fileId: fileId) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try sonioxParseCreateResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        do {
            return try sonioxParseCreateResponse(resp: response)
        } catch {
            throw mapError(error)
        }
    }

    /// Step 3: BESPOKE status poll loop. Swift owns the deadline, sleep interval,
    /// cancellation, and transient (429/5xx) tolerance. Each iteration builds via
    /// `sonioxBuildStatusRequest`, executes a SINGLE request (NOT via RustRetry),
    /// and parses via `sonioxParseStatusResponse` → switch the outcome:
    ///   - `.pending`   → sleep + continue
    ///   - `.completed` → return (the transcript is fetched in step 4)
    /// A `status == "error"` body makes the core parser THROW
    /// (QuotaExceeded/BadRequest, incl. balance/funds/autopay/quota/limit keyword
    /// mapping); that propagates out of the loop (no native double-handling).
    private func waitForCompletion(params: TranscribeParams, id: String) async throws {
        // Bound total wall-clock independently of attempt count so a stream of
        // large `Retry-After` headers can't make the loop run far past its
        // ~180s budget. The attempt cap remains a secondary guard.
        let pollDeadline = Date().addingTimeInterval(Constants.maxPollDurationSeconds)
        for attempt in 0..<Constants.maxPollAttempts {
            try Task.checkCancellation()
            if Date() >= pollDeadline {
                AppLogger.network.error("Soniox polling exceeded total deadline · id=\(id, privacy: .private)")
                throw TranscriptionError.transientNetwork(details: nil)
            }

            do {
                let request = try sonioxBuildStatusRequest(params: params, transcriptionId: id)
                let response = try await RustHTTPExecutor.execute(request, session: session)

                let status = Int(response.status)
                switch status {
                case 200...299:
                    // The core classifies pending vs completed (and THROWS on a
                    // `status == "error"` body).
                    let outcome: SonioxPollStatus
                    do {
                        outcome = try sonioxParseStatusResponse(resp: response)
                    } catch let err as HwTranscriptionError {
                        throw RustCoreMapping.mapTranscriptionError(err, providerName: "Soniox")
                    }
                    switch outcome {
                    case .completed:
                        return
                    case .pending:
                        break
                    }
                case 429, 500, 502, 503, 504:
                    // Transient errors on a status poll are non-fatal: the
                    // server-side job is still processing. Honor Retry-After and
                    // keep polling, clamped to the poll cap.
                    let retryAfter = Self.retryAfterSeconds(from: response)
                    AppLogger.network.warning("Soniox poll transient (non-fatal) · attempt=\(attempt + 1, privacy: .public) · status=\(status, privacy: .public) · retryAfter=\(retryAfter.map(String.init) ?? "nil", privacy: .public)")
                    if let retryAfter, retryAfter > 1 {
                        let sleepSeconds = min(retryAfter, RetryConfiguration.maxPollRetryAfterSeconds)
                        try await Task.sleep(nanoseconds: UInt64(sleepSeconds) * 1_000_000_000)
                    }
                default:
                    // Other non-2xx on a poll: let the core classify the body.
                    do {
                        _ = try sonioxParseStatusResponse(resp: response)
                        throw TranscriptionError.invalidResponse(details: nil)
                    } catch let err as HwTranscriptionError {
                        throw RustCoreMapping.mapTranscriptionError(err, providerName: "Soniox")
                    }
                }
            } catch let error as TranscriptionError {
                throw error
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                AppLogger.network.warning("Soniox poll network error (non-fatal) · attempt=\(attempt + 1, privacy: .public) · error=\(error.localizedDescription, privacy: .public)")
            }

            try await Task.sleep(nanoseconds: Constants.pollIntervalNanoseconds)
        }

        AppLogger.network.error("Soniox polling timed out · id=\(id, privacy: .private)")
        throw TranscriptionError.transientNetwork(details: nil)
    }

    /// Step 4: fetch the transcript. Single-shot via executor + core retry. The
    /// core's transcript parse owns the `{text}` extraction + NoSpeech-on-empty.
    private func fetchTranscript(params: TranscribeParams, id: String) async throws -> String {
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { try sonioxBuildTranscriptRequest(params: params, transcriptionId: id) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try sonioxParseTranscriptResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        do {
            return try sonioxParseTranscriptResponse(resp: response).text
        } catch {
            throw mapError(error)
        }
    }

    // MARK: - Cleanup (fire-and-forget; request built by the core)

    private func cleanupTranscription(params: TranscribeParams, id: String) async {
        let request = sonioxBuildDeleteTranscriptionRequest(params: params, transcriptionId: id)
        do {
            let response = try await RustHTTPExecutor.execute(request, session: session)
            if !(200...299).contains(Int(response.status)) {
                AppLogger.network.warning("Soniox cleanup failed · id=\(id, privacy: .private) · status=\(response.status, privacy: .public)")
            }
        } catch {
            AppLogger.network.warning("Soniox cleanup request failed · id=\(id, privacy: .private) · error=\(error.localizedDescription, privacy: .public)")
        }
    }

    private func deleteFile(params: TranscribeParams, id: String) async {
        let request = sonioxBuildDeleteFileRequest(params: params, fileId: id)
        do {
            let response = try await RustHTTPExecutor.execute(request, session: session)
            if !(200...299).contains(Int(response.status)) {
                AppLogger.network.warning("Soniox file cleanup failed · id=\(id, privacy: .private) · status=\(response.status, privacy: .public)")
            }
        } catch {
            AppLogger.network.warning("Soniox file cleanup request failed · id=\(id, privacy: .private) · error=\(error.localizedDescription, privacy: .public)")
        }
    }

    /// Parse the integer `Retry-After` header from a core `HttpResponse`
    /// (case-insensitive). Used only by the bespoke poll loop.
    private static func retryAfterSeconds(from response: HttpResponse) -> Int? {
        guard let value = response.headers.first(where: {
            $0.name.caseInsensitiveCompare("Retry-After") == .orderedSame
        })?.value else { return nil }
        return Int(value.trimmingCharacters(in: .whitespaces))
    }
}

extension SonioxProvider {
    func healthCheck(apiKey: String) async -> ProviderHealth {
        guard !apiKey.isEmpty else { return .unknown }
        guard let url = URL(string: "\(Constants.apiBaseURL)/models") else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Soniox health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("Soniox health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Soniox health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Soniox health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Soniox health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }
}
