//
//  AssemblyAIProvider.swift
//  hyperwhisper
//
//  Adapter for AssemblyAI STT (upload → create → poll pipeline).
//
//  Wave 3 / M3-B.3: URL / header / JSON body construction and response parsing
//  now run through the Rust shared core's per-step builders/parsers
//  (`assemblyaiBuild/ParseUploadRequest`, `…CreateRequest`, `…PollRequest`).
//  This file keeps only the platform-owned shell: API-key configuration, the
//  shared URLSession, offline / file-existence / file-size preflight, the
//  executor + core retry loop for the non-poll steps, the BESPOKE Swift poll
//  loop (Swift owns the wall-clock deadline + sleep interval + cancellation +
//  transient-poll tolerance), and logging.
//
//  The core owns model defaulting (empty → universal-2), legacy alias
//  resolution (`universal`→`universal-2`, `slam-1`→`universal-3-pro`), the
//  `-medical`→`domain: medical-v1` split, language detection, the
//  `keyterms_prompt` build (shared sanitize/dedup, ≤6-word/cap-by-model), and the
//  poll-completion `{text}` parse + NoSpeech-on-empty.
//

import Foundation
import OSLog

class AssemblyAIProvider: TranscriptionProvider {
    private var apiKey: String = ""
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AssemblyAIProvider")

    /// Shared URLSession for connection reuse across upload and polling steps.
    private lazy var session: URLSession = URLSession(configuration: .default)

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "AssemblyAI" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("AssemblyAI API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        self.apiKey = trimmed

        let suffix = String(trimmed.suffix(4))
        logger.debug("🔑 AssemblyAI API key configured (non-empty: \(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private))")
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard !apiKey.isEmpty else {
            AppLogger.network.error("AssemblyAI transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: "AssemblyAI")
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("AssemblyAI transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("AssemblyAI transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileSize = try audioURL.fileSize()
        AppLogger.transcription.debug("AssemblyAI audio file size · sizeKB=\(fileSize / 1024, privacy: .public)")
        let maxSize = CloudProvider.assemblyAI.maxFileSizeBytes
        if fileSize > maxSize {
            AppLogger.network.error("AssemblyAI transcription aborted · reason=File too large · bytes=\(fileSize, privacy: .public)")
            throw TranscriptionError.audioFileTooLarge(fileSize: fileSize, limit: maxSize, providerName: "AssemblyAI")
        }

        AppLogger.network.info("AssemblyAI transcription started · file=\(audioURL.lastPathComponent, privacy: .public) · lang=\(language ?? "auto", privacy: .public)")

        // Build TranscribeParams. Pass the RAW model id (empty → core default
        // universal-2) and the sanitized vocabulary boost terms — the core's create builder
        // owns alias resolution, the `-medical`→domain split, language
        // detection, and the `keyterms_prompt` build (≤6-word/cap-by-model).
        let modelToSend = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        let contentType = AudioMimeTypeResolver.infer(for: audioURL)
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: modelToSend
        )

        // 1) Upload the audio to AssemblyAI to get a temporary URL.
        let uploadURL = try await uploadFile(params: params)
        AppLogger.network.debug("AssemblyAI upload URL received · url=\(uploadURL, privacy: .private)")

        // 2) Create the transcript job → transcript id.
        let transcriptId = try await startTranscript(params: params, audioUrl: uploadURL)
        AppLogger.network.info("AssemblyAI transcript initiated · id=\(transcriptId, privacy: .private)")

        // 3) Poll until completed (bespoke Swift loop).
        let text = try await waitForTranscript(params: params, id: transcriptId)
        AppLogger.network.info("AssemblyAI transcript completed · id=\(transcriptId, privacy: .private) · chars=\(text.count, privacy: .public)")
        return text
    }

    // MARK: - Private (Rust-core-driven steps)

    /// Map a thrown core error to the app `TranscriptionError`.
    private func mapError(_ error: Error) -> Error {
        if let hwErr = error as? HwTranscriptionError {
            return RustCoreMapping.mapTranscriptionError(hwErr, providerName: name)
        }
        return error
    }

    /// Step 1: upload audio (raw octet-stream body). Single-shot via the shared
    /// executor + core retry loop.
    private func uploadFile(params: TranscribeParams) async throws -> String {
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { try assemblyaiBuildUploadRequest(params: params) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try assemblyaiParseUploadResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        do {
            return try assemblyaiParseUploadResponse(resp: response)
        } catch {
            throw mapError(error)
        }
    }

    /// Step 2: create the transcript job. Single-shot via executor + core retry.
    private func startTranscript(params: TranscribeParams, audioUrl: String) async throws -> String {
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { try assemblyaiBuildCreateRequest(params: params, audioUrl: audioUrl) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try assemblyaiParseCreateResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        do {
            return try assemblyaiParseCreateResponse(resp: response)
        } catch {
            throw mapError(error)
        }
    }

    /// Step 3: BESPOKE poll loop. Swift owns the wall-clock deadline, sleep
    /// interval, cancellation, and transient-poll (429/5xx) tolerance. Each
    /// iteration builds via `assemblyaiBuildPollRequest`, executes a SINGLE
    /// request (NOT through RustRetry — poll continuation is a separate concern),
    /// and parses via `assemblyaiParsePollResponse` → switch the outcome:
    ///   - `.pending`  → sleep + continue
    ///   - `.done`     → return the core-parsed transcript text
    /// A `status == "error"` body makes the core parser throw a BadRequest, which
    /// propagates out of the loop.
    private func waitForTranscript(params: TranscribeParams, id: String) async throws -> String {
        // Bound total wall-clock independently of attempt count so a stream of
        // large `Retry-After` headers can't make the loop run far past its
        // documented ~120s budget. The attempt cap remains a secondary guard.
        let pollDeadline = Date().addingTimeInterval(120)
        var attempts = 0
        while attempts < 120 { // ~120s max wait (with 1s sleep)
            try Task.checkCancellation()
            if Date() >= pollDeadline {
                AppLogger.network.error("AssemblyAI polling exceeded total deadline · id=\(id, privacy: .private)")
                throw TranscriptionError.transientNetwork(details: nil)
            }

            AppLogger.network.debug("AssemblyAI polling attempt · id=\(id, privacy: .private) · attempt=\(attempts + 1, privacy: .public)")

            do {
                let request = try assemblyaiBuildPollRequest(params: params, id: id)
                let response = try await RustHTTPExecutor.execute(request, session: session)

                let status = Int(response.status)
                switch status {
                case 200...299:
                    break // parse below
                case 401, 403:
                    AppLogger.network.error("AssemblyAI poll unauthorized · status=\(status, privacy: .public)")
                    throw TranscriptionError.unauthorized(provider: "AssemblyAI")
                case 429, 500, 502, 503, 504:
                    // Transient errors on a status poll are non-fatal: the
                    // server-side job is still processing. Honor Retry-After and
                    // keep polling, clamped so one oversized header can't blow
                    // past the cap (the total deadline bounds the aggregate wait).
                    let retryAfter = Self.retryAfterSeconds(from: response)
                    let sleepSeconds = min(max(1, retryAfter ?? 1), RetryConfiguration.maxPollRetryAfterSeconds)
                    AppLogger.network.warning("AssemblyAI poll transient (non-fatal) · attempt=\(attempts + 1, privacy: .public) · status=\(status, privacy: .public) · retryAfter=\(retryAfter.map(String.init) ?? "nil", privacy: .public) · sleptSeconds=\(sleepSeconds, privacy: .public)")
                    try await Task.sleep(nanoseconds: UInt64(sleepSeconds) * 1_000_000_000)
                    attempts += 1
                    continue
                default:
                    AppLogger.network.error("AssemblyAI poll failed · status=\(status, privacy: .public)")
                    throw TranscriptionError.invalidResponse(details: nil)
                }

                // 2xx → let the core classify pending vs done (and throw on a
                // `status == "error"` body / NoSpeech-on-empty).
                let outcome: AssemblyaiPollOutcome
                do {
                    outcome = try assemblyaiParsePollResponse(resp: response)
                } catch let err as HwTranscriptionError {
                    throw RustCoreMapping.mapTranscriptionError(err, providerName: "AssemblyAI")
                }
                switch outcome {
                case let .done(transcript):
                    AppLogger.network.info("AssemblyAI polling complete · id=\(id, privacy: .private)")
                    return transcript.text
                case .pending:
                    AppLogger.network.debug("AssemblyAI polling pending · id=\(id, privacy: .private)")
                }
            } catch let error as TranscriptionError {
                // Propagate explicit transcription errors immediately.
                throw error
            } catch is CancellationError {
                throw CancellationError()
            } catch let error as URLError where error.code == .cancelled || Task.isCancelled {
                throw CancellationError()
            } catch {
                // Network errors during polling are non-fatal; log and continue.
                logger.warning("AssemblyAI poll network error (non-fatal) · attempt=\(attempts, privacy: .public) · error=\(error.localizedDescription, privacy: .public)")
            }

            try await Task.sleep(nanoseconds: 1_000_000_000)
            attempts += 1
        }
        AppLogger.network.error("AssemblyAI polling timed out · id=\(id, privacy: .private)")
        throw TranscriptionError.transientNetwork(details: nil)
    }

    /// Parse the integer `Retry-After` header from a core `HttpResponse`
    /// (case-insensitive). Used only by the bespoke poll loop, which doesn't go
    /// through RustRetry's header parser.
    private static func retryAfterSeconds(from response: HttpResponse) -> Int? {
        guard let value = response.headers.first(where: {
            $0.name.caseInsensitiveCompare("Retry-After") == .orderedSame
        })?.value else { return nil }
        return Int(value.trimmingCharacters(in: .whitespaces))
    }
}

// MARK: - Health Checks

extension AssemblyAIProvider {
    /// Perform a basic GET request to verify the API key and connectivity.
    func healthCheck(apiKey: String) async -> ProviderHealth {
        guard !apiKey.isEmpty else { return .unknown }
        guard let url = URL(string: "https://api.assemblyai.com/v2/transcript?limit=1") else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue(apiKey, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("AssemblyAI health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 401, 403:
                AppLogger.network.error("AssemblyAI health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("AssemblyAI health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("AssemblyAI health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("AssemblyAI health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }
}
