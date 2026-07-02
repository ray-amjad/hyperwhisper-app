//
//  RustRetry.swift
//  hyperwhisper
//
//  Retry wrapper + boundary mappers for the Rust shared core (Wave 3 / M3-B).
//
//  The retry policy is owned by the core: `nextRetry(attempt:status:body:retryAfter:)`
//  classifies the (status, body) and returns a `RetryDecision` — `.retry(delayMs:)`
//  or `.giveUp`. This wrapper drives a single request through that decision loop
//  via `RustHTTPExecutor`, keeping ALL I/O, cancellation, and `Retry-After`
//  header parsing on the platform side.
//
//  Behavioral note (flagged in the PR): this unifies the previously-divergent
//  per-provider retry loops onto the core's `nextRetry`. `nextRetry` is 1-based
//  on the attempt that just FAILED and gives up at attempt >= MAX_ATTEMPTS (8),
//  with exponential backoff (1s, 2s, 4s, … 64s), honoring `Retry-After` clamped
//  to 10s. The core is RNG-free, so a small randomized jitter (0–30%) is added
//  platform-side at the sleep point (see `sleep(_:)`) to avoid a thundering herd.
//

import Foundation

enum RustRetry {

    /// Drive `buildRequest()`'s output through the executor + core retry loop.
    ///
    /// - On a 2xx response, returns the captured `HttpResponse`.
    /// - On a non-2xx, parses `Retry-After` natively, asks the core
    ///   `nextRetry(...)`, and either sleeps `delayMs` and retries or gives up.
    /// - On a `URLError` with no HTTP response (network blip), treats it as a
    ///   retryable 503-equivalent (`nextRetry(attempt, 503, "", nil)`).
    /// - On cancellation, throws `CancellationError`.
    /// - On give-up, throws the core-mapped Swift `TranscriptionError` derived
    ///   from the last status/body (via `parseError`), so callers surface the
    ///   real failure rather than a generic one.
    ///
    /// `buildRequest` is a closure so the same `HttpRequest` is re-issued each
    /// attempt (the body is a file ref, so re-streaming is cheap and correct).
    ///
    /// `onTransportError` is an OPTIONAL one-shot recovery hook invoked in the
    /// transport-error path (a `URLError` with no HTTP response) BEFORE the next
    /// retry sleeps. It is fired at most once per `perform(...)` call (mirroring
    /// the original `performRequestWithRetry`'s `didResetThisSequence` gate) so a
    /// flapping network can't thrash the pool. HyperWhisper Cloud / routed pass a
    /// DNS-shaped session-reset closure here so a network flip
    /// (VPN/captive-portal/tether swap) re-resolves instead of burning all
    /// attempts against a poisoned connection pool. Default `nil` = no-op, so
    /// other callers are unaffected.
    static func perform(
        session: URLSession,
        buildRequest: () throws -> HttpRequest,
        parseError: (HttpResponse) -> TranscriptionError,
        onTransportError: ((URLError) async -> Void)? = nil
    ) async throws -> HttpResponse {
        var attempt: UInt32 = 0
        // One-shot-per-sequence gate for the recovery hook (matches the original
        // native `didResetThisSequence` semantics).
        var didRecoverThisSequence = false

        while true {
            try Task.checkCancellation()
            attempt += 1

            let request = try buildRequest()

            // Perform one attempt. A thrown error here is either cancellation or
            // a URLSession transport error (no HTTP response).
            let response: HttpResponse
            do {
                response = try await RustHTTPExecutor.execute(request, session: session)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                let nsError = error as NSError
                if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
                    throw CancellationError()
                }

                // No HTTP response — treat as a retryable 503-equivalent.
                let decision = nextRetry(attempt: attempt, status: 503, body: "", retryAfter: nil)
                switch decision {
                case let .retry(delayMs):
                    // One-shot transport-error recovery (e.g. DNS-cache flush on a
                    // network flip) BEFORE the retry sleeps. Gated to once per
                    // sequence so a flapping network can't thrash the pool.
                    if !didRecoverThisSequence, let hook = onTransportError, let urlError = error as? URLError {
                        didRecoverThisSequence = true
                        await hook(urlError)
                    }
                    try await sleep(delayMs)
                    continue
                case .giveUp:
                    throw TranscriptionError.transientNetwork(details: error.localizedDescription)
                }
            }

            // 2xx → success.
            if (200...299).contains(Int(response.status)) {
                return response
            }

            // Non-2xx → consult the core retry decision.
            let bodyText = String(data: response.body, encoding: .utf8) ?? ""
            let retryAfter = parseRetryAfterHeader(response)

            let decision = nextRetry(
                attempt: attempt,
                status: response.status,
                body: bodyText,
                // Clamp at the conversion: `parseRetryAfterHeader` uses `Int(...)`
                // and so accepts negatives; `UInt64(-1)` would TRAP. A negative
                // Retry-After is meaningless, so floor it at 0. (The `Int?` value
                // is still passed to `enrichRateLimited` below for user messaging.)
                retryAfter: retryAfter.map { UInt64(max(0, $0)) }
            )

            switch decision {
            case let .retry(delayMs):
                try await sleep(delayMs)
                continue
            case .giveUp:
                // The core's RateLimited carries no Retry-After (it doesn't read
                // the header); enrich the give-up error with the value we parsed
                // here so the "try again in N seconds" UI is preserved.
                throw enrichRateLimited(parseError(response), retryAfter: retryAfter)
            }
        }
    }

    /// When `error` is a `.rateLimited` with a nil `retryAfter`, fill in the
    /// `Retry-After` value parsed from the response header. Otherwise pass the
    /// error through unchanged.
    private static func enrichRateLimited(_ error: TranscriptionError, retryAfter: Int?) -> TranscriptionError {
        guard let retryAfter, case .rateLimited(let existing) = error, existing == nil else {
            return error
        }
        return .rateLimited(retryAfter: retryAfter)
    }

    /// Parse the integer `Retry-After` header from a binding `HttpResponse`,
    /// reading the header list the core captured (case-insensitive).
    private static func parseRetryAfterHeader(_ response: HttpResponse) -> Int? {
        guard let value = response.headers.first(where: {
            $0.name.caseInsensitiveCompare("Retry-After") == .orderedSame
        })?.value else { return nil }
        return Int(value.trimmingCharacters(in: .whitespaces))
    }

    private static func sleep(_ delayMs: UInt64) async throws {
        // Add randomized jitter (0–30%) on top of the core's deterministic
        // backoff so concurrent clients don't all retry in lockstep (thundering
        // herd). The core forbids RNG, so the jitter lives here — restoring the
        // pre-migration `.transcription` preset's `0...0.3` jitterRange.
        let jitterFactor = 1.0 + Double.random(in: 0...0.3)
        let nanos = UInt64(Double(delayMs) * jitterFactor * 1_000_000)
        try await Task.sleep(nanoseconds: nanos)
    }
}

// MARK: - Boundary mappers

enum RustCoreMapping {

    /// The standard `parseError` give-up closure for `RustRetry.perform`.
    ///
    /// On a non-2xx response, runs the provider's core parser (which classifies
    /// the body into an `HwTranscriptionError`) and maps the result to a Swift
    /// `TranscriptionError` tagged with `providerName`. A non-throwing parse
    /// (unexpected on a non-2xx) and any non-core error both fall back to
    /// `.invalidResponse`. This is the dedup of the identical inline closures
    /// every provider used to carry; providers that need extra context on the
    /// give-up error (HW Cloud / routed 402-credit + file-size enrichment) keep
    /// their own bespoke closure.
    static func parseErrorClosure(
        providerName: String,
        _ parse: @escaping (HttpResponse) throws -> Void
    ) -> (HttpResponse) -> TranscriptionError {
        { resp in
            do {
                try parse(resp)
                return TranscriptionError.invalidResponse(details: "unexpected non-error response")
            } catch let err as HwTranscriptionError {
                return RustCoreMapping.mapTranscriptionError(err, providerName: providerName)
            } catch {
                return TranscriptionError.invalidResponse(details: error.localizedDescription)
            }
        }
    }

    /// Map a core `HwTranscriptionError` to the app's `TranscriptionError`.
    ///
    /// `providerName` is the display name for messaging; `creditsRemaining` /
    /// `creditsRequired` carry the credit context for `.insufficientCredits`
    /// (HW Cloud quota path), when the platform read them from the response body.
    /// `fileTooLargeBytes` / `fileTooLargeLimit` carry the real audio/limit sizes
    /// for `.audioFileTooLarge` (HW Cloud / routed 413), when the platform parsed
    /// `actual_size_mb` / `max_size_mb` from the response body. Default 0 = absent.
    static func mapTranscriptionError(
        _ error: HwTranscriptionError,
        providerName: String,
        insufficientCredits: Bool = false,
        creditsRemaining: Int = 0,
        creditsRequired: Int = 0,
        fileTooLargeBytes: Int64 = 0,
        fileTooLargeLimit: Int64 = 0
    ) -> TranscriptionError {
        switch error {
        case .Unauthorized:
            return .unauthorized(provider: providerName)
        case .QuotaExceeded:
            // HW Cloud / routed: a 402 is "out of credits". Prefer the richer
            // .insufficientCredits when the caller pulled the credit context.
            if insufficientCredits {
                return .insufficientCredits(remaining: creditsRemaining, required: creditsRequired)
            }
            return .quotaExceeded(provider: providerName, message: nil)
        case .FileTooLarge:
            return .audioFileTooLarge(fileSize: fileTooLargeBytes, limit: fileTooLargeLimit, providerName: providerName)
        case let .RateLimited(retryAfterSecs):
            return .rateLimited(retryAfter: retryAfterSecs.map { Int($0) })
        case let .ProviderUnavailable(status):
            return .serverError(statusCode: Int(status), message: "Provider unavailable")
        case .NoSpeech:
            return .noSpeechDetected
        case let .BadRequest(status, message):
            // 400-class. Surface as a server error carrying the upstream message.
            // Even when the message is empty, preserve the HTTP status (matching
            // Windows `RustCoreMapping.cs`) rather than collapsing to a statusless
            // .invalidRequest — the status is diagnostic.
            if message.isEmpty {
                return .serverError(statusCode: Int(status), message: "Invalid request")
            }
            return .serverError(statusCode: Int(status), message: message)
        case let .Parse(message):
            return .invalidResponse(details: message)
        }
    }

    /// Map the app's `CloudProvider`-style routed header to an `HwProvider`.
    /// Used by routed + (later) health probes.
    static func hwProvider(for sttProviderHeader: String) -> HwProvider {
        switch sttProviderHeader {
        case "azure-mai": return .azureMai
        case "google-chirp": return .googleChirp
        default: return .hyperWhisperCloud
        }
    }

    /// Map the app's full `CloudProvider` enum to an `HwProvider`. Used by the
    /// health probes (`buildHealthRequest`/`parseHealthResponse`).
    ///
    /// The three HW-Cloud-routed providers map onto the core's routed cases
    /// (`hyperwhisper`→`hyperWhisperCloud`, `microsoftAzureSpeech`→`azureMai`,
    /// `googleSpeech`→`googleChirp`); every direct vendor maps 1:1.
    static func hwProvider(for provider: CloudProvider) -> HwProvider {
        switch provider {
        case .hyperwhisper: return .hyperWhisperCloud
        case .openai: return .openai
        case .groq: return .groq
        case .deepgram: return .deepgram
        case .assemblyAI: return .assemblyai
        case .elevenLabs: return .elevenlabs
        case .mistral: return .mistral
        case .soniox: return .soniox
        case .gemini: return .gemini
        case .grok: return .grok
        case .microsoftAzureSpeech: return .azureMai
        case .googleSpeech: return .googleChirp
        }
    }

    /// Build a core `TranscribeParams` from the platform's transcription inputs.
    ///
    /// The core builds the vocabulary CSV itself from `vocabulary` (trim +
    /// drop-empty, no lowercase/dedup) — pass the RAW term list, do NOT
    /// pre-encode. `audioMime` is passed explicitly from
    /// `AudioMimeTypeResolver` rather than letting the core re-resolve.
    static func transcribeParams(
        audioPath: String,
        audioMime: String,
        language: String?,
        vocabulary: [String],
        apiKey: String = "",
        model: String = "",
        prompt: String? = nil,
        baseURL: String? = nil,
        licenseKey: String? = nil,
        deviceID: String? = nil,
        routedProvider: String? = nil,
        routedModel: String? = nil,
        routedDomain: String? = nil
    ) -> TranscribeParams {
        TranscribeParams(
            apiKey: apiKey,
            model: model,
            language: language,
            vocabulary: vocabulary,
            prompt: prompt,
            temperature: nil,
            audioPath: audioPath,
            audioMime: audioMime,
            baseUrl: baseURL,
            licenseKey: licenseKey,
            deviceId: deviceID,
            routedProvider: routedProvider,
            routedModel: routedModel,
            routedDomain: routedDomain
        )
    }

    /// Extract vocabulary boost terms for provider egress.
    ///
    /// Replacement-pair entries are post-transcription substitutions, not
    /// recognition hints. The Rust core applies the same sanitizer/dedup/caps
    /// again while building provider requests, but pre-sanitizing here keeps the
    /// FFI boundary free of replacement-pair and oversized prompt-injection data.
    static func boostVocabularyTerms(from vocabulary: [Vocabulary]) -> [String] {
        var terms: [String] = []
        var seen = Set<String>()
        for item in vocabulary {
            guard item.replacement?.isEmpty != false else { continue }
            guard let raw = item.word else { continue }
            let sanitized = PromptBuilder.sanitizeVocabularyWord(raw)
            guard !sanitized.isEmpty else { continue }
            guard seen.insert(sanitized.lowercased()).inserted else { continue }
            terms.append(sanitized)
        }
        return terms
    }

    /// Parse the HW-Cloud / routed 413 size context (`actual_size_mb` /
    /// `max_size_mb`) from an error response body into bytes, matching the old
    /// native routed `handleHTTPError`. Returns `(0, 0)` when absent.
    static func fileTooLargeContext(from response: HttpResponse) -> (bytes: Int64, limit: Int64) {
        guard let json = try? JSONSerialization.jsonObject(with: response.body) as? [String: Any] else {
            return (0, 0)
        }
        let limit = (json["max_size_mb"] as? Int).map { Int64($0) * 1_048_576 } ?? 0
        let bytes = (json["actual_size_mb"] as? Double).map { Int64($0 * 1_048_576) } ?? 0
        return (bytes, limit)
    }
}
