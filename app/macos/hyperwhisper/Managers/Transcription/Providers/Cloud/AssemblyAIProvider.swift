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
//  The core owns model defaulting (empty → universal-3-5-pro), legacy alias
//  resolution (`universal`→`universal-2`, retired Pro ids→`universal-3-5-pro`), the
//  `-medical`→`domain: medical-v1` split, language detection, the
//  `keyterms_prompt` build (shared sanitize/dedup, ≤6-word/cap-by-model), and the
//  poll-completion `{text}` parse + NoSpeech-on-empty.
//
//  SYNC FAST PATH: clips under `assemblyaiSyncMaxDurationSecs()` (currently
//  120s) try AssemblyAI's sync API (`POST sync.assemblyai.com/v1/transcribe`)
//  first — one blocking request returns the finished transcript in the same
//  response (~134ms p50), no upload/create/poll. Falls back to the pipeline
//  above when the requested model is a medical variant (sync has no
//  medical/domain concept), the exact AVFoundation duration is unavailable,
//  >= the cap, or the sync call itself errors/times out. A `.NoSpeech` result
//  is NOT a fallback trigger — see `tryTranscribeSync`.
//

import AVFoundation
import Foundation
import OSLog

class AssemblyAIProvider: TranscriptionProvider {
    private var apiKey: String = ""
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AssemblyAIProvider")

    /// Shared URLSession for connection reuse across upload and polling steps.
    private lazy var session: URLSession = URLSession(configuration: .default)

    /// Short-timeout session dedicated to the sync fast path — a single
    /// blocking call that must fail fast enough to still fall back to async,
    /// not `session`'s larger multi-step retry budget.
    private lazy var syncSession: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = Self.syncTimeoutSeconds
        config.timeoutIntervalForResource = Self.syncTimeoutSeconds
        return URLSession(configuration: config)
    }()

    /// Sync fast path HTTP call timeout — sourced from the Rust core's shared
    /// `assemblyaiSyncTimeoutMs()` FFI constant (see hw-net's
    /// `assemblyai/sync_flow.rs`) instead of a hardcoded literal, so Swift/C#
    /// can't drift from each other. Tightened from an earlier 40s to a much
    /// smaller value (AssemblyAI's sync p50 is ~134ms) so a stalled sync call
    /// blocks the async fallback for far less time — a sequential
    /// sync-then-async redesign into a concurrent race is out of scope; this
    /// just caps the worst case.
    private static let syncTimeoutSeconds: TimeInterval = TimeInterval(assemblyaiSyncTimeoutMs()) / 1000.0

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
        // NOTE: this ONE `params` value is passed to BOTH the sync builder
        // (`assemblyaiBuildSyncRequest`, below) AND — on fallback — the async
        // builders (`assemblyaiBuild{Upload,Create,Poll}Request`). The Rust
        // core's doc comment on `SYNC_BASE_URL` warns against exactly this
        // when `params.base_url` is set: sync and async point at DIFFERENT
        // hosts (`sync.assemblyai.com` vs `api.assemblyai.com`), so one
        // override can't correctly redirect both. Currently latent — `baseURL`
        // is never passed here — but if a future staging/test override is
        // added to this call site, it must NOT reuse this same `params` value
        // for both builders; build sync and async params separately instead.
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            apiKey: apiKey,
            model: modelToSend,
            // Direct-vendor request: the core cannot attach X-Latency-Opt-Out to
            // one by construction. Pass the user's real choice anyway so this site
            // stays correct if it is ever routed.
            shareAnonymousSpeedData: !LatencyOptOut.isEnabled
        )

        // Sync fast path: try AssemblyAI's one-request sync API for clips under
        // its duration cap before falling back to the async upload/create/poll
        // pipeline below. Uses the EXACT AVFoundation duration (not a byte-size
        // estimate) since the file is already on disk. Medical models are
        // excluded: the sync API has no medical/domain concept and always runs
        // plain universal-3-5-pro, so routing a medical request through sync
        // would silently drop the paid Medical Mode add-on instead of erroring
        // or falling back — matches the cloud TS path's existing exclusion.
        let isMedicalModel = CloudTranscriptionModels.assemblyAIRequestParams(for: modelToSend).medical
        let syncMaxDuration = assemblyaiSyncMaxDurationSecs()
        if !isMedicalModel, let duration = await syncEligibleDuration(for: audioURL), duration < syncMaxDuration {
            AppLogger.network.debug("AssemblyAI duration \(duration, privacy: .public)s < \(syncMaxDuration, privacy: .public)s sync cap — trying sync fast path")
            if let syncText = try await tryTranscribeSync(params: params, durationSeconds: duration) {
                AppLogger.network.info("AssemblyAI sync transcription complete · chars=\(syncText.count, privacy: .public)")
                return syncText
            }
            AppLogger.network.info("AssemblyAI sync fast path unavailable — falling back to async upload/create/poll")
        } else {
            AppLogger.network.debug("AssemblyAI skipping sync fast path (medical=\(isMedicalModel, privacy: .public), duration unknown or >= \(syncMaxDuration, privacy: .public)s cap)")
        }

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
                    throw TranscriptionError.unauthorized(provider: "AssemblyAI", statusCode: status)
                case 429, 500, 502, 503, 504:
                    // Transient errors on a status poll are non-fatal: the
                    // server-side job is still processing. Honor Retry-After and
                    // keep polling, clamped so one oversized header can't blow
                    // past the cap (the total deadline bounds the aggregate wait).
                    let retryAfter = response.retryAfterSeconds
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

    // MARK: - Private (sync fast path)

    /// Exact audio duration via AVFoundation, for the sync-vs-async gate.
    /// Returns `nil` (never throws) on any failure or invalid/indefinite
    /// duration so the caller falls back to the async pipeline exactly like an
    /// unknown duration would — this must never fail the whole transcription.
    /// Mirrors `FileTranscriptionFlow.getAudioDuration`'s NaN/indefinite guards.
    ///
    /// FOLLOW-UP (not done here): `FileTranscriptionFlow` and the recording
    /// lifecycle already compute this duration earlier in the same pipeline,
    /// so re-loading it here via AVFoundation is redundant work — but
    /// `AssemblyAIProvider` is a single app-lifetime instance (constructed
    /// once in `TranscriptionProviderRouter`) reachable CONCURRENTLY from the
    /// main dictation/import flow AND the Local API server's `/transcribe`
    /// endpoint (which calls `provider.transcribe(...)` directly, bypassing
    /// `TranscriptionPipeline`'s task-serialization guard). An instance
    /// property set just before `transcribe(...)` would race exactly like the
    /// Windows `_knownDurationSeconds` bug this PR fixes elsewhere (two
    /// concurrent calls could interleave their sets/reads). The
    /// `TranscriptionProvider` protocol has no per-call context object to
    /// thread a duration through without an API change, so this is left as a
    /// known optimization opportunity rather than risking that same race.
    private func syncEligibleDuration(for url: URL) async -> Double? {
        let asset = AVURLAsset(url: url)
        guard let duration = try? await asset.load(.duration),
              duration.isValid, !duration.flags.contains(.indefinite) else {
            return nil
        }
        let seconds = CMTimeGetSeconds(duration)
        guard seconds.isFinite, seconds >= 0 else { return nil }
        return seconds
    }

    /// Attempt AssemblyAI's sync transcription API (one blocking request — no
    /// upload/create/poll) for a clip already confirmed to be under the sync
    /// duration cap. Returns the transcript text on success, or `nil` to
    /// signal the caller should fall back to the async pipeline (HTTP/transport
    /// error, non-2xx, malformed response, or a sync-specific timeout). Does
    /// NOT go through `RustRetry` — sync is meant to be a single fast call;
    /// retrying a deterministic rejection (e.g. "too long") would just delay
    /// the async fallback.
    ///
    /// Genuine cancellation propagates un-swallowed. A `.NoSpeech` parse
    /// result is a legitimate terminal outcome — mirrors the poll loop by
    /// throwing the mapped `TranscriptionError` instead of falling back.
    private func tryTranscribeSync(params: TranscribeParams, durationSeconds: Double) async throws -> String? {
        let request: HttpRequest
        do {
            request = try assemblyaiBuildSyncRequest(params: params)
        } catch {
            logger.warning("AssemblyAI sync request build failed (non-fatal): \(error.localizedDescription, privacy: .public) — falling back to async")
            return nil
        }

        let response: HttpResponse
        do {
            response = try await RustHTTPExecutor.execute(request, session: syncSession)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            let nsError = error as NSError
            if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
                throw CancellationError()
            }
            // Covers the sync-specific timeout (NSURLErrorTimedOut from
            // `syncSession`'s shorter budget) along with any other transport
            // failure — all non-fatal here, the async pipeline is the recovery.
            logger.warning("AssemblyAI sync transport error (non-fatal, \(durationSeconds, privacy: .public)s clip): \(error.localizedDescription, privacy: .public) — falling back to async")
            return nil
        }

        if Task.isCancelled { throw CancellationError() }

        do {
            let transcript = try assemblyaiParseSyncResponse(resp: response)
            return transcript.text
        } catch let err as HwTranscriptionError {
            if case .NoSpeech = err {
                throw RustCoreMapping.mapTranscriptionError(err, providerName: name)
            }
            logger.warning("AssemblyAI sync parse failed (non-fatal): \(String(describing: err), privacy: .public) — falling back to async")
            return nil
        }
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
