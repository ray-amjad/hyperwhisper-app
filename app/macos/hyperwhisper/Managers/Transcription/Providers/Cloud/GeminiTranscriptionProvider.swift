//
//  GeminiTranscriptionProvider.swift
//  hyperwhisper
//
//  GEMINI TRANSCRIPTION PROVIDER
//  Adapter for Google Gemini generateContent + Files API for speech-to-text.
//
//  Wave 3 / M3-B.3: URL / header / body construction and response parsing now
//  run through the Rust shared core's per-step builders/parsers
//  (`geminiBuild/ParseUploadStartRequest`, `…UploadBytesRequest`,
//  `…PollRequest`, `…GenerateRequest`, `…DeleteRequest`, `geminiBuildPrompt`).
//
//  DIVERGENCE FROM NATIVE: the inline-base64 transport is GONE. All audio now
//  flows through the Files API (audio bytes can't cross the FFI boundary, so the
//  core only implements the Files-API path — documented divergence, identical
//  transcript). The `<20 MB → inline` branch, `transcribeInline`,
//  `buildInlineRequestBody`, and the base64-size helpers have been deleted.
//
//  API WORKFLOW (Files API only):
//    POST /upload/v1beta/files?key=... to start a resumable upload session
//      → parse the `X-Goog-Upload-URL` RESPONSE HEADER (the core parse fn reads it)
//    POST raw audio bytes to that upload URL → GeminiFile
//    GET /v1beta/{file.name}?key=... until the file becomes ACTIVE (poll loop)
//    POST /v1beta/models/{model}:generateContent?key=... with file_data → transcript
//    DELETE /v1beta/{file.name}?key=... for best-effort cleanup (fire-and-forget)
//
//  The core owns model defaulting (empty → gemini-2.5-flash), the prompt build
//  (`geminiBuildPrompt`: base instruction + language hint + vocabulary +
//  `params.prompt` custom prompt), and the generate `{text}` parse +
//  NoSpeech-on-empty.
//

import Foundation

class GeminiTranscriptionProvider: TranscriptionProvider {
    private enum Constants {
        /// File-active poll interval / cap. Identical to native.
        static let filePollIntervalNanoseconds: UInt64 = 300_000_000
        static let maxFilePollAttempts = 500
    }

    private var apiKey: String = ""

    var isAvailable: Bool { !apiKey.isEmpty }
    var name: String { "Gemini" }

    func configure(apiKey: String) {
        let trimmed = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != apiKey {
            AppLogger.network.debug("Gemini API key trimmed · originalLength=\(apiKey.count, privacy: .public) · trimmedLength=\(trimmed.count, privacy: .public)")
        }
        let suffix = String(trimmed.suffix(4))
        AppLogger.network.debug("Gemini API key configured · nonEmpty=\(!trimmed.isEmpty, privacy: .public) · suffix=\(suffix, privacy: .private)")
        self.apiKey = trimmed
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        guard !apiKey.isEmpty else {
            AppLogger.network.error("Gemini transcription aborted · reason=Missing API key")
            throw TranscriptionError.apiKeyMissing(provider: "Gemini")
        }
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("Gemini transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: nil)
        }
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("Gemini transcription aborted · reason=Audio file missing · path=\(audioURL.path, privacy: .private)")
            throw TranscriptionError.audioFileNotFound
        }

        let fileSizeBytes = try audioURL.fileSize()
        AppLogger.transcription.debug("Gemini audio file size · sizeKB=\(fileSizeBytes / 1024, privacy: .public)")

        // Build TranscribeParams. Pass the RAW model id (empty → core default
        // gemini-2.5-flash), sanitized vocabulary boost terms, and the per-mode custom
        // Gemini prompt as `params.prompt` — the core's `geminiBuildPrompt`
        // assembles the final prompt (base instruction + language hint +
        // vocabulary + custom prompt).
        let modelToSend = (mode?.cloudTranscriptionModel?.isEmpty == false)
            ? (mode?.cloudTranscriptionModel ?? "")
            : ""
        let mimeType = AudioMimeTypeResolver.infer(for: audioURL, fallback: "audio/wav")
        let customPrompt = mode?.geminiCustomPrompt?.trimmingCharacters(in: .whitespacesAndNewlines)
        let promptForCore: String? = (customPrompt?.isEmpty == false) ? customPrompt : nil
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: mimeType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: modelToSend,
            prompt: promptForCore
        )

        let session = URLSession(configuration: Self.makeSessionConfiguration())

        AppLogger.transcription.debug("Gemini using Files API transport · file=\(audioURL.lastPathComponent, privacy: .public) · mimeType=\(mimeType, privacy: .public)")

        var uploadedFileName: String?
        do {
            // 1) Start resumable upload → upload URL (from X-Goog-Upload-URL header).
            // 2) Upload the raw bytes → GeminiFile.
            let uploadedFile = try await uploadAudioFile(params: params, fileSizeBytes: fileSizeBytes, session: session)
            uploadedFileName = uploadedFile.name

            // 3) Poll until the file is ACTIVE (bespoke Swift loop).
            let activeFile = try await waitForFileToBecomeActive(params: params, uploadedFile: uploadedFile, session: session)

            // 4) Generate the transcript.
            let transcript = try await generateTranscript(params: params, file: activeFile, session: session)

            // 5) Best-effort cleanup (fire-and-forget; matches native await-detached).
            await performDeleteCleanup(params: params, fileName: uploadedFileName)
            return transcript
        } catch {
            await performDeleteCleanup(params: params, fileName: uploadedFileName)
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

    /// Steps 1+2: start the resumable upload, then upload the raw audio bytes.
    /// Each is single-shot via the shared executor + core retry loop.
    ///
    /// The upload-start RESPONSE carries `X-Goog-Upload-URL` as a header; the
    /// core's `geminiParseUploadStartResponse` reads it (the executor passes
    /// response headers through). The bytes upload streams the file from disk
    /// (`Body.fileStream`) so audio never crosses the FFI boundary.
    private func uploadAudioFile(params: TranscribeParams, fileSizeBytes: Int64, session: URLSession) async throws -> GeminiFile {
        AppLogger.network.info("Gemini upload starting")

        // Step 1: start resumable upload → upload URL (from response header).
        // The core's upload-start builder intentionally delegates the
        // `X-Goog-Upload-Header-Content-Length` header to the platform (it can't
        // stat the file across FFI), so append it here from the size we already
        // stat'd. Mutate a fresh copy per attempt so the retry loop re-applies it.
        let startResponse = try await RustRetry.perform(
            session: session,
            buildRequest: {
                var request = try geminiBuildUploadStartRequest(params: params)
                request.headers.append(Header(name: "X-Goog-Upload-Header-Content-Length", value: String(fileSizeBytes)))
                return request
            },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try geminiParseUploadStartResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        let uploadURL: String
        do {
            uploadURL = try geminiParseUploadStartResponse(resp: startResponse)
        } catch {
            throw mapError(error)
        }

        // Step 2: upload the raw bytes to the returned URL → GeminiFile.
        let bytesResponse = try await RustRetry.perform(
            session: session,
            buildRequest: { try geminiBuildUploadBytesRequest(params: params, uploadUrl: uploadURL) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try geminiParseUploadBytesResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        let uploadedFile: GeminiFile
        do {
            uploadedFile = try geminiParseUploadBytesResponse(resp: bytesResponse)
        } catch {
            throw mapError(error)
        }

        AppLogger.network.info("Gemini upload completed · name=\(uploadedFile.name ?? "unknown", privacy: .private) · state=\(uploadedFile.state ?? "unknown", privacy: .public)")
        return uploadedFile
    }

    /// Step 3: BESPOKE file-active poll loop. Swift owns the sleep interval, the
    /// attempt cap, and cancellation. Each iteration builds via
    /// `geminiBuildPollRequest`, executes a SINGLE request (NOT via RustRetry),
    /// and parses via `geminiParsePollResponse` → switch the outcome:
    ///   - `.pending`  → sleep + continue
    ///   - `.active`   → return the active `GeminiFile`
    /// A FAILED file state makes the core parser throw, which propagates out.
    private func waitForFileToBecomeActive(params: TranscribeParams, uploadedFile: GeminiFile, session: URLSession) async throws -> GeminiFile {
        // If the upload response already reports ACTIVE, short-circuit (native did).
        if uploadedFile.state?.uppercased() == "ACTIVE" {
            return uploadedFile
        }

        guard let fileName = uploadedFile.name else {
            AppLogger.network.error("Gemini file polling failed · reason=Missing file name")
            throw TranscriptionError.invalidRequest
        }

        for attempt in 1...Constants.maxFilePollAttempts {
            try Task.checkCancellation()

            AppLogger.network.debug("Gemini file polling · name=\(fileName, privacy: .private) · attempt=\(attempt, privacy: .public)")
            try await Task.sleep(nanoseconds: Constants.filePollIntervalNanoseconds)

            let request = try geminiBuildPollRequest(params: params, name: fileName)
            let response = try await RustHTTPExecutor.execute(request, session: session)

            // Non-2xx on a poll: let the core classify the body.
            if !(200...299).contains(Int(response.status)) {
                do {
                    _ = try geminiParsePollResponse(resp: response)
                } catch let err as HwTranscriptionError {
                    throw RustCoreMapping.mapTranscriptionError(err, providerName: "Gemini")
                }
            }

            let outcome: GeminiFilePollOutcome
            do {
                outcome = try geminiParsePollResponse(resp: response)
            } catch let err as HwTranscriptionError {
                throw RustCoreMapping.mapTranscriptionError(err, providerName: "Gemini")
            }
            switch outcome {
            case let .active(file):
                return file
            case .pending:
                continue
            }
        }

        AppLogger.network.error("Gemini file polling timed out")
        throw TranscriptionError.timeout(operation: "Gemini file processing")
    }

    /// Step 4: generate the transcript. Single-shot via executor + core retry.
    /// The core's generate parse owns the `{text}` extraction + NoSpeech-on-empty.
    private func generateTranscript(params: TranscribeParams, file: GeminiFile, session: URLSession) async throws -> String {
        AppLogger.network.info("Gemini Files API transcription request · model=\(params.model.isEmpty ? "<default>" : params.model, privacy: .public)")

        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { try geminiBuildGenerateRequest(params: params, file: file) },
            parseError: RustCoreMapping.parseErrorClosure(providerName: name) {
                _ = try geminiParseGenerateResponse(resp: $0)
            }
        )
        if Task.isCancelled { throw CancellationError() }
        do {
            let transcript = try geminiParseGenerateResponse(resp: response)
            AppLogger.network.info("Gemini transcript parsed · chars=\(transcript.text.count, privacy: .public)")
            return transcript.text
        } catch {
            throw mapError(error)
        }
    }

    /// Step 5: best-effort delete cleanup. Fire-and-forget on a detached task
    /// (awaited, matching the native semantics); request built by the core.
    private func performDeleteCleanup(params: TranscribeParams, fileName: String?) async {
        guard let fileName, !fileName.isEmpty, !apiKey.isEmpty else { return }

        await Task.detached(priority: .utility) {
            let session = URLSession(configuration: Self.makeSessionConfiguration())
            do {
                let request = try geminiBuildDeleteRequest(params: params, name: fileName)
                let response = try await RustHTTPExecutor.execute(request, session: session)
                let status = Int(response.status)
                if (200...299).contains(status) || status == 404 {
                    AppLogger.network.debug("Gemini cleanup complete · name=\(fileName, privacy: .private)")
                } else {
                    AppLogger.network.warning("Gemini cleanup failed · name=\(fileName, privacy: .private) · status=\(status, privacy: .public)")
                }
            } catch {
                AppLogger.network.warning("Gemini cleanup failed · name=\(fileName, privacy: .private) · error=\(error.localizedDescription, privacy: .public)")
            }
        }.value
    }

    private static func makeSessionConfiguration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 120
        config.timeoutIntervalForResource = 900
        config.waitsForConnectivity = false
        return config
    }
}

// MARK: - Health Checks

extension GeminiTranscriptionProvider {
    /// Perform a basic GET request to verify the API key and connectivity.
    /// Uses the same /v1beta/models endpoint as the post-processing health check.
    func healthCheck(apiKey: String) async -> ProviderHealth {
        guard !apiKey.isEmpty else { return .unknown }
        guard var components = URLComponents(string: "https://generativelanguage.googleapis.com/v1beta/models") else { return .unknown }
        components.queryItems = [URLQueryItem(name: "key", value: apiKey)]
        guard let url = components.url else { return .unknown }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let session = URLSession(configuration: .ephemeral)
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                AppLogger.network.error("Gemini transcription health check missing HTTPURLResponse")
                return .unreachable
            }
            switch http.statusCode {
            case 200..<300:
                return .healthy
            case 400, 401, 403:
                AppLogger.network.error("Gemini transcription health check unauthorized · status=\(http.statusCode, privacy: .public)")
                return .unauthorized
            default:
                AppLogger.network.error("Gemini transcription health check failed · status=\(http.statusCode, privacy: .public)")
                return .unreachable
            }
        } catch {
            if let urlError = error as? URLError {
                AppLogger.network.error("Gemini transcription health check network error · code=\(urlError.code.rawValue, privacy: .public)")
            } else {
                AppLogger.network.error("Gemini transcription health check error · message=\(error.localizedDescription, privacy: .public)")
            }
            return .unreachable
        }
    }
}
