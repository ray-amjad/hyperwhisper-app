//
//  HyperWhisperCloudProvider.swift
//  hyperwhisper
//
//  HYPERWHISPER CLOUD TRANSCRIPTION PROVIDER (v2 - Streaming)
//  Built-in cloud transcription using Cloudflare Workers edge network.
//
//  KEY FEATURES:
//  - No API key required (uses device_id or license_key for identification)
//  - Credit-based usage ($0.50 for trial, $5.00 for licensed users)
//  - Edge-based processing for low latency
//  - Automatic credit deduction after transcription
//  - Returns updated credit balance in response
//
//  ARCHITECTURE (v2 STREAMING):
//  - POST /transcribe: Raw binary audio streaming (no multipart buffering)
//    → Eliminates ~34MB memory overhead for large files
//    → Query params: license_key/device_id, language, mode, initial_prompt
//    → Headers: Content-Type: audio/*, Content-Length: required
//    → Response: { text, language, duration, cost, metadata }
//
//  - POST /post-process: Standalone text correction (separate from transcription)
//    → JSON body: { text, prompt, license_key/device_id }
//    → Response: { corrected, cost }
//
//  WHY STREAMING?
//  - Cloudflare Workers have 128MB memory limit
//  - Old multipart approach: ~34MB for 17MB file (2x overhead)
//  - New streaming approach: ~0MB (pipes through without buffering)
//  - Now supports files up to 2GB (Deepgram's limit)
//
//  CUSTOM VOCABULARY (KEYTERM):
//  - Max 100 terms supported by Deepgram
//  - Terms are sent as comma-separated string via initial_prompt query param
//  - Backend converts to Deepgram format with boost intensifiers (term:1.5)
//  - KEYTERM only works with explicit language (not auto-detect)
//
//  CREDIT MODEL:
//  - 1 credit = $0.001 USD (precise charging based on actual API costs)
//  - Typical cost: ~4.3 credits per audio minute (based on Deepgram pricing)
//  - Trial users: 150 credits (~24 minutes)
//  - Licensed users: Credits purchased via Polar meters
//
//  CLIENT RETRY STRATEGY:
//  - 4 attempts with exponential backoff (1s, 2s, 4s delays)
//  - Only retries on network/server errors, not validation errors
//

import Foundation

/// HyperWhisper Cloud transcription provider (v2 - Streaming)
/// Uses Cloudflare Workers for edge-based speech-to-text processing
class HyperWhisperCloudProvider: TranscriptionProvider {

    // MARK: - Properties

    /// License manager for getting device ID / license key
    private let licenseManager: LicenseManager

    /// Credit manager for balance tracking
    private let creditManager: HyperWhisperCloudManager

    /// Settings manager for prompt configuration
    private weak var settingsManager: SettingsManager?

    /// Always available (no API key required)
    var isAvailable: Bool { true }

    /// Provider name for display
    var name: String { "HyperWhisper Cloud" }

    /// AI-ENHANCED TEXT FROM SERVER:
    /// HyperWhisper Cloud performs AI post-processing on the server (typo correction, formatting, etc.)
    /// and returns the AI-enhanced version via /post-process endpoint.
    /// TranscriptionPipeline uses this to skip client-side AI processing.
    /// Reset to nil at the start of each transcription request.
    private(set) var aiEnhancedText: String?

    /// DETECTED LANGUAGE FROM SERVER:
    /// The backend reports the language it detected (e.g. "en", "de") in the
    /// /transcribe response. The pipeline reads this right after `transcribe(...)`
    /// to gate language-sensitive post steps (filler-word removal) on the
    /// detected language rather than the requested "auto". Reset at the start of
    /// each request.
    private(set) var detectedLanguage: String?

    /// Pre-captured application context from the pipeline.
    /// Set by TranscriptionPipeline before calling transcribe() so that server-side
    /// post-processing sees the user's actual foreground app (not HyperWhisper's recording dialog).
    var applicationContext: ApplicationContext?

    /// Shared URLSession reused across every transcribe() call AND across the
    /// sibling HW-Cloud-routed providers (`AzureMAIProvider`, `GoogleChirpProvider`).
    /// Owned by `HyperWhisperRoutedTranscription` so all three providers coalesce
    /// HTTP/2 connections to `transcribe.hyperwhisper.com` — meaning a warmup
    /// fired before a recording primes the same connection the subsequent upload
    /// uses, regardless of which STT engine the backend dispatches to.
    /// Never call `invalidate` on this; `reset` is gated by
    /// `performDnsRecoveryReset` for one-shot DNS-flush recovery only.
    private var session: URLSession { HyperWhisperRoutedTranscription.sharedSession }

    // MARK: - Connection Pre-Warm

    private var lastWarmupAt: Date?
    private static let warmupMinInterval: TimeInterval = 60

    /// Fires a HEAD /warmup to pre-establish the TLS/HTTP2 connection to Fly.
    /// Call on hotkey-down / recording-start paths when cloud is the active provider.
    /// Fire-and-forget — callers must not await this. Uses the same `session` as
    /// `transcribe(...)` so the pooled connection is reused for the subsequent POST.
    func prewarmConnection() {
        if let last = lastWarmupAt, Date().timeIntervalSince(last) < Self.warmupMinInterval {
            return
        }
        sendWarmup()
    }

    /// Bypasses the 60s warmup debounce. Used by the foreground keepalive
    /// ticker, which fires on its own ~45s cadence and would otherwise be
    /// absorbed into the debounce and throttled back to 60s — defeating the
    /// purpose of ticking faster than `URLSession`'s pool-idle window.
    func prewarmConnectionForced() {
        sendWarmup()
    }

    private func sendWarmup() {
        lastWarmupAt = Date()

        guard let url = URL(string: NetworkConfig.hyperwhisperCloudURL + "/warmup") else { return }
        var req = URLRequest(url: url)
        req.httpMethod = "HEAD"
        req.timeoutInterval = 5

        session.dataTask(with: req) { [weak self] _, response, error in
            if let error = error {
                let nsError = error as NSError
                // Failed attempt shouldn't burn the debounce window — clear so the next hotkey retries.
                Task { @MainActor in self?.lastWarmupAt = nil }
                AppLogger.network.debug("Cloud warmup failed · \(error.localizedDescription, privacy: .public)")
                if Self.isDnsError(nsError) {
                    Task { @MainActor in self?.performDnsRecoveryReset() }
                }
                return
            }
            if let http = response as? HTTPURLResponse {
                let region = http.value(forHTTPHeaderField: "fly-region") ?? "?"
                AppLogger.network.debug("Cloud warmup ok · status=\(http.statusCode, privacy: .public) · region=\(region, privacy: .public)")
            }
        }.resume()
    }

    private var lastDnsResetAt: Date = .distantPast
    private static let minDnsResetInterval: TimeInterval = 60

    /// Coarse cross-call gate so the keepalive ticker flapping against a
    /// dead DNS entry can't repeatedly call `session.reset` — each reset
    /// also cancels in-flight tasks on the same `URLSession`, so an
    /// ungated warmup failure could kill an active transcribe upload.
    /// Per-retry-sequence flags (`didResetThisSequence`) still guard
    /// within a single transcribe; this gate layers on top of those.
    /// @MainActor for serialized access to `lastDnsResetAt` (matches the
    /// existing `lastWarmupAt` pattern in this file).
    @MainActor
    private func performDnsRecoveryReset() {
        if Date().timeIntervalSince(lastDnsResetAt) < Self.minDnsResetInterval { return }
        lastDnsResetAt = Date()
        session.reset { }
        AppLogger.network.debug("DNS recovery: session reset")
    }

    /// True for errors that look like a stale/poisoned DNS cache — typical after
    /// a network flip (captive portal, VPN toggle, tether swap). Used to gate a
    /// one-shot URLSession pool flush in the warmup callback and transcribe retry.
    private static func isDnsError(_ e: NSError) -> Bool {
        guard e.domain == NSURLErrorDomain else { return false }
        switch e.code {
        case NSURLErrorDNSLookupFailed,
             NSURLErrorCannotFindHost,
             NSURLErrorCannotConnectToHost:
            return true
        default:
            return false
        }
    }

    // MARK: - Retry Configuration

    /// Maximum number of retry attempts for network failures
    private static let maxRetryAttempts = 4

    /// Initial delay between retries (doubles each attempt)
    private static let initialRetryDelaySeconds: Double = 1.0

    // MARK: - Initialization

    init(licenseManager: LicenseManager, creditManager: HyperWhisperCloudManager, settingsManager: SettingsManager?) {
        self.licenseManager = licenseManager
        self.creditManager = creditManager
        self.settingsManager = settingsManager
    }

    // MARK: - TranscriptionProvider Protocol

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        AppLogger.network.info("HyperWhisper Cloud transcription started (streaming v2) · file=\(audioURL.lastPathComponent, privacy: .public)")

        // Reset cached corrected text and detected language at the start of each request
        aiEnhancedText = nil
        detectedLanguage = nil

        // Verify audio file exists
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("HyperWhisper Cloud transcription aborted · reason=Audio file missing")
            throw TranscriptionError.audioFileNotFound
        }

        // Check network connectivity
        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("HyperWhisper Cloud transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: "No internet connection")
        }

        // Get identifier (license key or device ID)
        let (identifier, isLicensed) = await licenseManager.getTranscriptionIdentifier()

        AppLogger.network.debug("HyperWhisper Cloud identifier · licensed=\(isLicensed, privacy: .public) · hash=\(identifier.prefix(8), privacy: .private)")

        // Determine post-processing configuration based on mode settings
        let postProcessingMode = mode.flatMap { PostProcessingMode(rawValue: $0.postProcessingMode) } ?? .cloud
        let selectedPostProcessingProvider = mode?.postProcessingProvider.flatMap { PostProcessingProvider(rawValue: $0) }
            ?? postProcessingMode.defaultProvider ?? .hyperwhisper
        let postProcessingEnabled = postProcessingMode != .off && selectedPostProcessingProvider == .hyperwhisper
        var postProcessingPrompt: String? = nil
        if postProcessingEnabled, let mode {
            postProcessingPrompt = await buildPostProcessingPrompt(for: mode, vocabulary: vocabulary)
        }

        // Resolve the accuracy tier early so we can gate the send path on the
        // catalog's `customVocabulary.supported` flag (Chirp 3 / Grok modes
        // currently get `initial_prompt` silently dropped server-side).
        let accuracyTier = CloudAccuracyTier.fromStorageValue(mode?.cloudAccuracyTier)
        // X-STT-Model: the selected model within the provider/tier. Empty →
        // omit so the backend applies the provider default. Trim defensively.
        // cloudTranscriptionModel is shared with the BYOK path (Core Data
        // default "whisper-1"), so a HyperWhisper Cloud mode can carry a model
        // that doesn't belong to its tier. Resolve it against the tier's catalog
        // models (mirrors ModeEditorView.onAppear) so we never send a mismatched
        // X-STT-Model the backend 400s on; an unknown model falls back to the
        // tier default ("" → header omitted).
        let rawModelId = (mode?.cloudTranscriptionModel ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let tierModelIds = accuracyTier.models.map { $0.id }
        let selectedModelId = tierModelIds.contains(rawModelId) ? rawModelId : accuracyTier.defaultModelId
        // X-STT-Domain: "medical" (assemblyAI) or nil.
        let trimmedDomain = mode?.cloudTranscriptionDomain?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let transcriptionDomain: String? = (trimmedDomain?.isEmpty == false) ? trimmedDomain : nil
        let candidatePrompt = buildInitialTranscriptionPrompt(vocabulary: vocabulary)
        let initialPrompt: String?
        // Vocabulary support is now model-specific (e.g. Deepgram nova-3 supports
        // keyterm but the catalog flags per-model). Gate on the selected model.
        if candidatePrompt != nil && !accuracyTier.supportsCustomVocabulary(forModelId: selectedModelId) {
            AppLogger.network.info("HyperWhisper Cloud dropping initial_prompt · tier=\(accuracyTier.rawValue, privacy: .public) · model=\(selectedModelId, privacy: .public) reason=catalog_unsupported")
            initialPrompt = nil
        } else {
            initialPrompt = candidatePrompt
        }

        AppLogger.network.debug("HyperWhisper Cloud post-processing config · enabled=\(postProcessingEnabled, privacy: .public) · provider=\(selectedPostProcessingProvider.displayName, privacy: .public) · hasPrompt=\(postProcessingPrompt != nil, privacy: .public)")
        AppLogger.network.debug("HyperWhisper Cloud initial prompt · hasCustomVocabulary=\(initialPrompt != nil, privacy: .public)")

        // =====================================================================
        // STEP 1: Resolve audio file size without loading the full payload into memory.
        // =====================================================================
        // File-existence/size preflight stays native (the core never touches
        // disk). resolveUploadFileSize also guards against a 0-byte race.
        let fileSizeBytes = try await resolveUploadFileSize(for: audioURL)
        let contentType = AudioMimeTypeResolver.infer(for: audioURL)

        AppLogger.transcription.debug(
            "HyperWhisper Cloud audio payload prepared · sizeKB=\(fileSizeBytes / 1024, privacy: .public) · contentType=\(contentType, privacy: .public) · strategy=file_upload"
        )

        // =====================================================================
        // STEP 2: Build the transcription request via the Rust shared core.
        // =====================================================================
        // The core (`hyperwhisperCloudBuildTranscribeRequest`) builds the URL +
        // query (license_key/device_id, language, initial_prompt), the
        // Content-Type, the routed X-STT-* headers, and the @raw raw-stream body.
        //
        // The native X-STT-Provider/-Model/-Domain (derived from the accuracy
        // tier) are passed as the core's routed_* fields so the builder bakes
        // the same headers. Vocabulary gating (catalog `supportsCustomVocabulary`)
        // is decided here in native code: when dropped we pass an empty term list.
        let vocabTermsForCore: [String] = (initialPrompt != nil)
            ? RustCoreMapping.boostVocabularyTerms(from: vocabulary)
            : []
        let trimmedModelId = selectedModelId.isEmpty ? nil : selectedModelId

        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: vocabTermsForCore,
            baseURL: NetworkConfig.hyperwhisperCloudURL,
            licenseKey: isLicensed ? identifier : nil,
            deviceID: isLicensed ? nil : identifier,
            routedProvider: accuracyTier.sttProvider,
            routedModel: trimmedModelId,
            routedDomain: transcriptionDomain
        )

        let baseRequest: HttpRequest
        do {
            baseRequest = try hyperwhisperCloudBuildTranscribeRequest(params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: "HyperWhisper Cloud")
        }

        // The core does not add the platform `mode` query param or `User-Agent`
        // header (both are HW-Cloud-specific, not part of the shared contract).
        // Inject them natively while preserving everything the core built.
        var request = baseRequest
        if let mode, let modeId = mode.id {
            request.url = Self.appendingModeQuery(to: request.url, modeId: modeId.uuidString)
        }
        let userAgent = "HyperWhisper/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")"
        request.headers.append(Header(name: "User-Agent", value: userAgent))

        AppLogger.network.info("HyperWhisper Cloud streaming request · sttProvider=\(accuracyTier.sttProvider, privacy: .public) · fileSizeKB=\(fileSizeBytes / 1024, privacy: .public) · contentType=\(contentType, privacy: .public) · licensed=\(isLicensed, privacy: .public)")

        // =====================================================================
        // STEP 3: Perform request via the shared executor + core retry loop.
        // =====================================================================
        // Cancellation, file streaming, and DNS recovery stay native (in the
        // executor / session). The core decides retries via nextRetry(...).
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: { resp in
                // Map the core's classified error, enriching the HW-Cloud 402
                // credit context from the response body (remaining/required).
                do {
                    _ = try hyperwhisperCloudParseTranscribeResponse(resp: resp)
                    // 2xx never reaches parseError; a non-error parse is unexpected.
                    return TranscriptionError.invalidResponse(details: "unexpected non-error response")
                } catch let err as HwTranscriptionError {
                    let creditDenial = Self.creditDenialContext(from: resp)
                    if resp.status == 402, let invalidMessage = creditDenial.invalidExhaustedBalanceMessage {
                        return TranscriptionError.invalidResponse(details: invalidMessage)
                    }
                    let (tooBigBytes, tooBigLimit) = RustCoreMapping.fileTooLargeContext(from: resp)
                    return RustCoreMapping.mapTranscriptionError(
                        err,
                        providerName: "HyperWhisper Cloud",
                        insufficientCredits: (resp.status == 402),
                        creditsRemaining: creditDenial.remaining,
                        creditsRequired: creditDenial.required,
                        fileTooLargeBytes: tooBigBytes,
                        fileTooLargeLimit: tooBigLimit
                    )
                } catch {
                    return TranscriptionError.invalidResponse(details: error.localizedDescription)
                }
            },
            onTransportError: { [weak self] urlError in
                // One-shot DNS-cache flush on a network flip — restores the
                // recovery the deleted native `performRequestWithRetry` did. The
                // existing `performDnsRecoveryReset` is @MainActor and applies its
                // own coarse cross-call gate on top of RustRetry's one-shot gate.
                if Self.isDnsError(urlError as NSError) {
                    await self?.performDnsRecoveryReset()
                }
            }
        )
        try throwIfCancelled()

        // Parse the success response via the core.
        let transcript: HwTranscript
        do {
            transcript = try hyperwhisperCloudParseTranscribeResponse(resp: response)
        } catch let err as HwTranscriptionError {
            // 200-but-no-speech surfaces here as a NoSpeech error. The server still
            // received and processed (and may have charged for) the request, so the
            // cached credit balance is now stale — invalidate it on this path too,
            // mirroring the success path below (which is otherwise skipped by this
            // re-throw). `defer` can't be used here because invalidateCache() awaits.
            await creditManager.invalidateCache()
            throw RustCoreMapping.mapTranscriptionError(err, providerName: "HyperWhisper Cloud")
        }

        // Capture the server-detected language so the pipeline can gate
        // language-sensitive post steps. The core's Transcript does not carry
        // the detected language; read it from the response body natively.
        if let lang = Self.detectedLanguage(from: response) {
            detectedLanguage = lang
        }

        // Surface the credit balance for the cache/UI from the response headers.
        if let remaining = hyperwhisperCloudParseCreditsRemaining(resp: response) {
            AppLogger.network.info("HyperWhisper Cloud credits remaining · remaining=\(remaining, privacy: .public)")
        }
        if let used = hyperwhisperCloudParseCreditsUsed(resp: response) {
            AppLogger.network.info("HyperWhisper Cloud credits used · used=\(used, privacy: .public)")
        }

        // Invalidate the credit cache so the next balance fetch is fresh after
        // this transcription charged the account (HyperWhisperCloudManager
        // exposes only invalidateCache(), no value-setter).
        await creditManager.invalidateCache()

        // =====================================================================
        // STEP 4: Optionally call /post-process for AI correction
        // =====================================================================
        if postProcessingEnabled, let prompt = postProcessingPrompt, !transcript.text.isEmpty {
            AppLogger.network.info("HyperWhisper Cloud calling /post-process endpoint")

            do {
                try throwIfCancelled()

                let corrected = try await performPostProcess(
                    session: session,
                    text: transcript.text,
                    prompt: prompt,
                    identifier: identifier,
                    isLicensed: isLicensed,
                    mode: mode
                )

                try throwIfCancelled()

                if !corrected.isEmpty {
                    // Post-processed output should be <<CLEANED>>-wrapped. Prefer
                    // strict extraction; when the model didn't wrap, fall back to
                    // the lenient strip (which itself guards against a prompt/OCR
                    // leak) before skipping — mirrors the AIPostProcessor pattern so
                    // a valid-but-unwrapped correction isn't silently dropped after
                    // the credit was already charged.
                    let trimmedCorrected = corrected.trimmingCharacters(in: .whitespacesAndNewlines)
                    var cleanedCorrected = TranscriptionTextProcessing.extractCleanedFromWrapped(trimmedCorrected)
                    if cleanedCorrected.isEmpty {
                        cleanedCorrected = TranscriptionTextProcessing.stripWrapperMarkers(trimmedCorrected)
                    }
                    if !cleanedCorrected.isEmpty {
                        aiEnhancedText = cleanedCorrected
                        AppLogger.network.debug("HyperWhisper Cloud stored corrected text · chars=\(cleanedCorrected.count, privacy: .public)")
                    }
                }
            } catch is CancellationError {
                AppLogger.network.info("HyperWhisper Cloud post-processing cancelled")
                throw CancellationError()
            } catch {
                // Post-processing failed, but we still have the raw transcription
                AppLogger.network.warning("HyperWhisper Cloud post-processing failed · error=\(error.localizedDescription, privacy: .public)")

                // Log to Sentry so we get alerted about real post-processing failures
                SentryService.capture(
                    error: error,
                    message: "HyperWhisper Cloud post-processing failed",
                    tags: ["component": "post_process", "provider": "hyperwhisper_cloud"],
                    fingerprint: ["post-process-failure", error.localizedDescription]
                )

                // Continue without AI enhancement
            }
        }

        try throwIfCancelled()

        // Return the raw transcription text — may not be wrapped, so use the
        // lenient passthrough (strict extraction would wipe an unwrapped result).
        let cleanedText = TranscriptionTextProcessing.stripWrapperMarkers(transcript.text)
        let hasCorrection = aiEnhancedText != nil
        AppLogger.network.info("HyperWhisper Cloud transcription completed · chars=\(cleanedText.count, privacy: .public) · hasServerCorrection=\(hasCorrection, privacy: .public)")

        return cleanedText
    }

    // MARK: - Rust Core Helpers (mode query, credit context, detected language)

    /// Append the HW-Cloud-specific `mode` query param to a core-built URL. The
    /// shared contract has no `mode` field, so this stays native. Appends with
    /// the correct `?`/`&` separator and percent-encodes the value.
    private static func appendingModeQuery(to urlString: String, modeId: String) -> String {
        let separator = urlString.contains("?") ? "&" : "?"
        let encoded = modeId.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? modeId
        return "\(urlString)\(separator)mode=\(encoded)"
    }

    /// Read the HW-Cloud 402 credit context from an error response body.
    private static func creditDenialContext(from response: HttpResponse) -> (remaining: Int, required: Int, invalidExhaustedBalanceMessage: String?) {
        guard let json = try? JSONSerialization.jsonObject(with: response.body) as? [String: Any] else {
            return (0, 0, nil)
        }
        let serverMessage = json["message"] as? String ?? json["error"] as? String
        let denial = HyperWhisperCloudCreditDenial(errorJson: json, message: serverMessage)
        return (
            denial.remainingForTranscriptionError,
            denial.requiredForTranscriptionError,
            denial.invalidExhaustedBalanceMessage
        )
    }

    /// Read the server-detected language from a success response body. The
    /// core's `HwTranscript` doesn't carry it, so parse it natively. Empty → nil.
    private static func detectedLanguage(from response: HttpResponse) -> String? {
        guard let json = try? JSONSerialization.jsonObject(with: response.body) as? [String: Any],
              let lang = (json["language"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !lang.isEmpty else {
            return nil
        }
        return lang
    }

    // MARK: - Post-Process Failure Diagnostics

    /// Emits a detailed diagnostics line for a failed request. Meant for the -1200 / TLS-handshake
    /// black-box — walks the NSError chain, pulls CFStream SSL subcode + SecCopyErrorMessageString,
    /// derives which phase of the transaction failed from partial URLSessionTaskMetrics, and logs
    /// path metadata (proxy/Private Relay, local/remote addr, negotiated TLS version/cipher).
    private func logFailureDiagnostics(error: Error, metricsDelegate: TaskMetricsDelegate, attempt: Int, attemptElapsedMs: Int) {
        // 1. Full NSError chain
        var chain: [String] = []
        var current: NSError? = error as NSError
        var depth = 0
        while let e = current, depth < 8 {
            chain.append("\(e.domain):\(e.code)")
            current = e.userInfo[NSUnderlyingErrorKey] as? NSError
            depth += 1
        }
        let errorChain = chain.joined(separator: "->")

        // 2. CFStream SSL subcode
        let nsError = error as NSError
        let cfStreamDomain = (nsError.userInfo["_kCFStreamErrorDomainKey"] as? Int) ?? -1
        let cfStreamCode = (nsError.userInfo["_kCFStreamErrorCodeKey"] as? Int) ?? 0
        var sslMessage = "n/a"
        if cfStreamDomain == 3 && cfStreamCode != 0 {
            if let msg = SecCopyErrorMessageString(OSStatus(cfStreamCode), nil) as String? {
                sslMessage = msg
            }
        }

        // 3. Failure phase from partial URLSessionTaskMetrics
        var phase = "unknown"
        var isProxy = false
        var localAddr = "n/a"
        var remoteAddr = "n/a"
        var dnsProto = "n/a"
        var tlsProto = "n/a"
        var tlsCipher: UInt16 = 0
        var netProto = "n/a"

        if let metrics = metricsDelegate.metrics, let tx = metrics.transactionMetrics.last {
            if tx.domainLookupStartDate != nil && tx.domainLookupEndDate == nil {
                phase = "dns"
            } else if tx.connectStartDate != nil && tx.connectEndDate == nil {
                phase = "tcp-connect"
            } else if tx.secureConnectionStartDate != nil && tx.secureConnectionEndDate == nil {
                phase = "tls-handshake"
            } else if tx.requestStartDate != nil && tx.requestEndDate == nil {
                phase = "upload"
            } else if tx.requestEndDate != nil && tx.responseStartDate == nil {
                phase = "server-wait"
            } else if tx.responseStartDate != nil && tx.responseEndDate == nil {
                phase = "post-response"
            } else if tx.fetchStartDate != nil && tx.domainLookupStartDate == nil && tx.connectStartDate == nil {
                phase = "pre-dns"
            }

            isProxy = tx.isProxyConnection
            localAddr = tx.localAddress ?? "n/a"
            remoteAddr = tx.remoteAddress ?? "n/a"
            dnsProto = String(describing: tx.domainResolutionProtocol)
            if let v = tx.negotiatedTLSProtocolVersion {
                tlsProto = String(format: "0x%04x", v.rawValue)
            }
            if let c = tx.negotiatedTLSCipherSuite {
                tlsCipher = c.rawValue
            }
            netProto = tx.networkProtocolName ?? "n/a"
        }

        AppLogger.network.error(
            "HyperWhisper Cloud failure diagnostics · attempt=\(attempt, privacy: .public) · attemptElapsedMs=\(attemptElapsedMs, privacy: .public) · phase=\(phase, privacy: .public) · errorChain=\(errorChain, privacy: .public) · cfStreamDomain=\(cfStreamDomain, privacy: .public) · cfStreamCode=\(cfStreamCode, privacy: .public) · sslMessage=\(sslMessage, privacy: .public) · isProxy=\(isProxy, privacy: .public) · localAddr=\(localAddr, privacy: .public) · remoteAddr=\(remoteAddr, privacy: .public) · dnsProto=\(dnsProto, privacy: .public) · tlsProto=\(tlsProto, privacy: .public) · tlsCipher=\(tlsCipher, privacy: .public) · netProto=\(netProto, privacy: .public)"
        )
    }

    // MARK: - Post-Processing Request

    /// Calls the /post-process endpoint for AI text correction
    private func performPostProcess(
        session: URLSession,
        text: String,
        prompt: String,
        identifier: String,
        isLicensed: Bool,
        mode: Mode? = nil
    ) async throws -> String {
        guard let url = URL(string: NetworkConfig.hyperwhisperCloudURL + NetworkConfig.hyperwhisperCloudPostProcessEndpoint) else {
            throw TranscriptionError.serverError(statusCode: 0, message: "Invalid post-process URL")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("HyperWhisper/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")",
                       forHTTPHeaderField: "User-Agent")

        let cloudPPModel = CloudPostProcessingModel.fromStorageValue(mode?.cloudPostProcessingModel)
        if let llmHeader = cloudPPModel.llmProviderHeader {
            request.setValue(llmHeader, forHTTPHeaderField: "X-LLM-Provider")
        }
        if let llmModelHeader = cloudPPModel.llmModelHeader {
            request.setValue(llmModelHeader, forHTTPHeaderField: "X-LLM-Model")
        }

        // Build JSON body
        var body: [String: Any] = [
            "text": text,
            "prompt": prompt
        ]

        if isLicensed {
            body["license_key"] = identifier
        } else {
            body["device_id"] = identifier
        }

        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        // Perform request with retry
        var lastError: Error?
        for attempt in 1...Self.maxRetryAttempts {
            try throwIfCancelled()

            AppLogger.network.info(
                "HyperWhisper Cloud post-process attempt started · attempt=\(attempt, privacy: .public)/\(Self.maxRetryAttempts, privacy: .public)"
            )
            let attemptStart = Date()
            let metricsDelegate = TaskMetricsDelegate()
            do {
                let (data, response) = try await session.data(for: request, delegate: metricsDelegate)
                try throwIfCancelled()

                guard let httpResponse = response as? HTTPURLResponse else {
                    throw TranscriptionError.invalidResponse(details: "Invalid server response")
                }

                if httpResponse.statusCode != 200 {
                    try handleHTTPError(statusCode: httpResponse.statusCode, data: data, httpResponse: httpResponse)
                }

                guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let corrected = json["corrected"] as? String else {
                    throw TranscriptionError.serverError(statusCode: httpResponse.statusCode, message: "Failed to parse post-process response")
                }

                return corrected
            } catch is CancellationError {
                AppLogger.network.info("HyperWhisper Cloud post-process cancelled · attempt=\(attempt, privacy: .public)")
                throw CancellationError()
            } catch let error as TranscriptionError {
                switch error {
                case .insufficientCredits, .unauthorized, .rateLimited:
                    throw error
                default:
                    lastError = error
                }
            } catch {
                if isCancellationError(error) {
                    AppLogger.network.info("HyperWhisper Cloud post-process cancelled · attempt=\(attempt, privacy: .public)")
                    throw CancellationError()
                }

                let attemptElapsedMs = Int(Date().timeIntervalSince(attemptStart) * 1000)
                logFailureDiagnostics(error: error, metricsDelegate: metricsDelegate, attempt: attempt, attemptElapsedMs: attemptElapsedMs)
                lastError = error
            }

            if attempt < Self.maxRetryAttempts {
                let delay = Self.initialRetryDelaySeconds * pow(2.0, Double(attempt - 1))
                AppLogger.network.warning("HyperWhisper Cloud post-process retry · attempt=\(attempt)/\(Self.maxRetryAttempts) · delaySeconds=\(delay, privacy: .public)")
                try throwIfCancelled()
                try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
        }

        throw lastError ?? TranscriptionError.transientNetwork(details: "Post-process request failed")
    }

    private func isCancellationError(_ error: Error) -> Bool {
        if error is CancellationError {
            return true
        }

        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
            return true
        }

        return Task.isCancelled
    }

    private func throwIfCancelled(_ error: Error? = nil) throws {
        if let error, isCancellationError(error) {
            throw CancellationError()
        }

        if Task.isCancelled {
            throw CancellationError()
        }
    }

    // MARK: - Error Handling

    /// Handles HTTP error responses and throws appropriate TranscriptionError
    private func handleHTTPError(statusCode: Int, data: Data, httpResponse: HTTPURLResponse) throws {
        let responseString = String(data: data, encoding: .utf8) ?? "No response body"
        let headerDump = httpResponse.allHeaderFields
            .map { "\($0.key): \($0.value)" }
            .joined(separator: ", ")
        AppLogger.network.error("HyperWhisper Cloud error · status=\(statusCode, privacy: .public) · headers=\(headerDump, privacy: .public)")

        // Try to parse structured error from JSON response
        if let errorJson = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let errorMessage = errorJson["message"] as? String ?? errorJson["error"] as? String {

            let serverContext = errorJson["context"] as? [String: Any]
            let contextDump = serverContext?.map { "\($0.key): \($0.value)" }.joined(separator: ", ") ?? "none"
            AppLogger.network.error("HyperWhisper Cloud API error · status=\(statusCode, privacy: .public) · message=\(errorMessage, privacy: .public) · context=\(contextDump, privacy: .public)")

            switch statusCode {
            case 402:
                let denial = HyperWhisperCloudCreditDenial(errorJson: errorJson, message: errorMessage)
                if let invalidMessage = denial.invalidExhaustedBalanceMessage {
                    throw TranscriptionError.invalidResponse(details: invalidMessage)
                }
                throw TranscriptionError.insufficientCredits(
                    remaining: denial.remainingForTranscriptionError,
                    required: denial.requiredForTranscriptionError
                )
            case 429:
                let retryAfter = httpResponse.value(forHTTPHeaderField: "Retry-After").flatMap { Int($0) }
                throw TranscriptionError.rateLimited(retryAfter: retryAfter)
            case 401, 403:
                throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
            case 400:
                throw TranscriptionError.serverError(statusCode: statusCode, message: errorMessage)
            case 500...599:
                throw TranscriptionError.serverError(statusCode: statusCode, message: errorMessage)
            default:
                throw TranscriptionError.serverError(statusCode: statusCode, message: errorMessage)
            }
        }

        // Generic error handling
        let preview = responseString.prefix(200)
        AppLogger.network.error("HyperWhisper Cloud HTTP error (no JSON) · status=\(statusCode, privacy: .public) · bodyPreview=\(preview, privacy: .private)")

        switch statusCode {
        case 429:
            let retryAfter = httpResponse.value(forHTTPHeaderField: "Retry-After").flatMap { Int($0) }
            throw TranscriptionError.rateLimited(retryAfter: retryAfter)
        case 402:
            throw TranscriptionError.insufficientCredits(remaining: 0, required: 0)
        case 401, 403:
            throw TranscriptionError.unauthorized(provider: "HyperWhisper Cloud")
        case 408, 504:
            throw TranscriptionError.timeout(operation: "transcription")
        case 500...599:
            throw TranscriptionError.serverError(statusCode: statusCode, message: "HyperWhisper Cloud server error")
        case 400:
            throw TranscriptionError.serverError(statusCode: statusCode, message: "Invalid request")
        default:
            throw TranscriptionError.invalidResponse(details: "HTTP \(statusCode)")
        }
    }

    // MARK: - Prompt Building

    /// Build the system prompt for server-side post-processing using existing PromptBuilder logic.
    /// Uses pre-captured applicationContext from the pipeline when available, falling back to
    /// fresh gathering if nil (which will see HyperWhisper's own window).
    private func buildPostProcessingPrompt(for mode: Mode, vocabulary: [Vocabulary]) async -> String {
        return await MainActor.run {
            let appContext = self.applicationContext ?? ApplicationContextGatherer.shared.gatherContext()
            AppLogger.transcription.info("=== APPLICATION CONTEXT (HyperWhisper Cloud) ===")
            AppLogger.transcription.info("Source: \(self.applicationContext != nil ? "pre-captured" : "fresh gather", privacy: .public)")
            AppLogger.transcription.info("Active App: \(appContext.appName, privacy: .public)")
            AppLogger.transcription.info("Bundle ID: \(appContext.bundleId, privacy: .public)")
            AppLogger.transcription.info("Category: \(appContext.category, privacy: .public)")
            AppLogger.transcription.info("Text Input Format: \(appContext.textInputFormat, privacy: .public)")
            AppLogger.transcription.info("Browser Tab Title: \(appContext.browserTabTitle ?? "None", privacy: .public)")
            AppLogger.transcription.info("=== END CONTEXT ===")

            // Concatenate static prompt + dynamic info for HW Cloud backend
            let systemPrompt = PromptBuilder.systemPrompt(
                for: mode,
                applicationContext: appContext
            )
            let systemInfo = PromptBuilder.systemInfo(
                for: mode,
                vocabulary: vocabulary,
                applicationContext: appContext
            )
            return systemPrompt + "\n\n" + systemInfo
        }
    }

    /// DEEPGRAM VOCABULARY LIMIT:
    /// Deepgram supports a maximum of 100 terms per request for vocabulary boosting.
    /// Terms beyond this limit are silently dropped by the API.
    private static let maxKeywords = 100

    /// Builds the vocabulary terms for Deepgram keyterm prompting
    /// Returns a comma-separated list of terms (max 100)
    ///
    /// KEYTERM PROMPTING (Nova-3):
    /// This function extracts vocabulary terms from Core Data entities.
    /// Terms are sent as comma-separated string via initial_prompt query parameter.
    ///
    /// The backend uses Deepgram's 'keyterm' parameter:
    ///   - Works with BOTH monolingual and multilingual transcription
    ///   - Up to 90% improvement in Keyword Recall Rate (KRR)
    ///   - Plain strings without intensifiers (not term:1.5 format)
    ///   - Max 500 tokens, recommended 20-50 terms for best results
    ///
    /// Reference: https://developers.deepgram.com/docs/keyterm
    ///
    /// NOTE: Only the first 100 unique terms are included due to Deepgram's limit.
    /// The VocabularyView should also enforce this limit to provide better UX.
    private func buildInitialTranscriptionPrompt(vocabulary: [Vocabulary]) -> String? {
        // Route through the shared sanitizer so every vocabulary egress path
        // applies identical filtering, sanitization, and dedup (#769).
        let sanitizedEntries = RustCoreMapping.boostVocabularyTerms(from: vocabulary)
        let entries = Array(sanitizedEntries.prefix(Self.maxKeywords))
        if sanitizedEntries.count > Self.maxKeywords {
            AppLogger.transcription.warning("Custom vocabulary truncated to \(Self.maxKeywords) items (Deepgram limit)")
        }

        guard !entries.isEmpty else {
            return nil
        }

        // Return comma-separated list for Deepgram keywords
        // The backend will convert to "term:intensifier" format
        return entries.joined(separator: ",")
    }

    private func resolveUploadFileSize(for audioURL: URL) async throws -> Int64 {
        var lastError: Error?

        for attempt in 1...5 {
            do {
                let attributes = try FileManager.default.attributesOfItem(atPath: audioURL.path)
                if let size = attributes[.size] as? Int64, size > 0 {
                    if attempt > 1 {
                        AppLogger.network.debug("HyperWhisper Cloud file size resolved after retry · attempt=\(attempt, privacy: .public) · sizeKB=\(size / 1024, privacy: .public)")
                    }
                    return size
                }
                lastError = TranscriptionError.audioFileNotFound
            } catch {
                lastError = error
            }

            let delayMs = min(80, 12 * (1 << (attempt - 1)))
            AppLogger.network.debug("HyperWhisper Cloud retrying file size lookup · attempt=\(attempt, privacy: .public) · delayMs=\(delayMs, privacy: .public)")
            try? await Task.sleep(nanoseconds: UInt64(delayMs) * 1_000_000)
        }

        throw lastError ?? TranscriptionError.audioFileNotFound
    }
}

// MARK: - URLSession Task Metrics Delegate

/// Lightweight delegate that captures URLSessionTaskMetrics for network timing diagnostics.
/// Passed as the per-request delegate to `session.upload(for:fromFile:delegate:)`.
private final class TaskMetricsDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    /// Collected after the task finishes; read by the caller to log timing breakdown.
    private(set) var metrics: URLSessionTaskMetrics?

    func urlSession(_ session: URLSession, task: URLSessionTask, didFinishCollecting metrics: URLSessionTaskMetrics) {
        self.metrics = metrics
    }
}
