//
//  HyperWhisperRoutedTranscription.swift
//  hyperwhisper
//
//  Shared transcription helper for providers that route through the Fly
//  transcribe service but pin a specific upstream via the `X-STT-Provider`
//  header. Used by AzureMAIProvider and GoogleChirpProvider — both expose
//  upstream models via HyperWhisper Cloud and never request BYOK in v1.
//
//  Wave 3 / M3-B: the URL / header / vocabulary construction and the
//  request/response handling now run through the Rust shared core's per-provider
//  builders (`azureMaiBuildTranscribeRequest` / `googleChirpBuildTranscribeRequest`
//  and their parsers), which bake the `X-STT-Provider` (and pass-through
//  `X-STT-Model` / `X-STT-Domain`) headers and encode the `@raw` raw-stream body.
//  This file keeps only the platform-owned I/O shell: the shared `URLSession`,
//  the executor + core retry loop, file preflight, cancellation, and the
//  HW-Cloud-specific `mode` / `User-Agent` additions.
//

import Foundation

enum HyperWhisperRoutedTranscription {

    /// One `URLSession` shared by every provider that terminates at the Fly
    /// transcribe backend (`HyperWhisperCloudProvider`, `AzureMAIProvider`,
    /// `GoogleChirpProvider`). Coalescing onto a single session lets URLSession
    /// reuse HTTP/2 connections to `transcribe.hyperwhisper.com` across
    /// providers — so the connection a hotkey-down warmup primes is the same
    /// one the upload actually uses, regardless of which STT engine is
    /// dispatched server-side.
    ///
    /// NOTE: `HyperWhisperCloudProvider.performDnsRecoveryReset()` calls
    /// `sharedSession.reset(...)` on DNS-shaped failures. After this change
    /// that reset drops in-flight uploads from AzureMAI and GoogleChirp too —
    /// the correct behavior on a network flip since all three hit the same host.
    static let sharedSession: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 120
        config.timeoutIntervalForResource = 300
        config.httpMaximumConnectionsPerHost = 4
        config.waitsForConnectivity = false
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: config)
    }()

    static func run(
        session: URLSession,
        providerHeader: String,
        providerDisplayName: String,
        audioURL: URL,
        language: String?,
        mode: Mode?,
        vocabulary: [Vocabulary],
        licenseManager: LicenseManager,
        creditManager: HyperWhisperCloudManager
    ) async throws -> String {
        AppLogger.network.info("HW-routed transcription started · provider=\(providerHeader, privacy: .public) · file=\(audioURL.lastPathComponent, privacy: .public)")

        // File-existence preflight stays native (the core never touches disk).
        guard FileManager.default.fileExists(atPath: audioURL.path) else {
            AppLogger.network.error("HW-routed transcription aborted · reason=Audio file missing")
            throw TranscriptionError.audioFileNotFound
        }

        guard NetworkStatus.shared.isOnline else {
            AppLogger.network.error("HW-routed transcription aborted · reason=Offline")
            throw TranscriptionError.transientNetwork(details: "No internet connection")
        }

        let attributes = try FileManager.default.attributesOfItem(atPath: audioURL.path)
        guard let fileSize = attributes[.size] as? Int64, fileSize > 0 else {
            throw TranscriptionError.audioFileNotFound
        }

        let (identifier, isLicensed) = await licenseManager.getTranscriptionIdentifier()
        let contentType = AudioMimeTypeResolver.infer(for: audioURL)

        // Build the routed request via the shared core. The core bakes the
        // X-STT-Provider header (forced to the provider's value), the query
        // (license_key/device_id, language, initial_prompt), the Content-Type,
        // and the @raw raw-stream body. We pass the raw vocabulary term list —
        // the core builds the CSV (trim + drop-empty, no lowercase/dedup).
        let params = RustCoreMapping.transcribeParams(
            audioPath: audioURL.path,
            audioMime: contentType,
            language: language,
            vocabulary: RustCoreMapping.boostVocabularyTerms(from: vocabulary),
            baseURL: NetworkConfig.hyperwhisperCloudURL,
            licenseKey: isLicensed ? identifier : nil,
            deviceID: isLicensed ? nil : identifier,
            routedProvider: providerHeader
        )

        let baseRequest: HttpRequest
        do {
            baseRequest = try buildRoutedRequest(providerHeader: providerHeader, params: params)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerDisplayName)
        }

        // The core does not add the platform `mode` query param or `User-Agent`
        // header (HW-Cloud-specific, not part of the shared contract).
        var request = baseRequest
        if let mode, let modeId = mode.id {
            let separator = request.url.contains("?") ? "&" : "?"
            let encoded = modeId.uuidString.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? modeId.uuidString
            request.url = "\(request.url)\(separator)mode=\(encoded)"
        }
        let userAgent = "HyperWhisper/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")"
        request.headers.append(Header(name: "User-Agent", value: userAgent))

        AppLogger.network.info("HW-routed transcription request · sttProvider=\(providerHeader, privacy: .public) · fileSizeKB=\(fileSize / 1024, privacy: .public) · licensed=\(isLicensed, privacy: .public)")

        // Perform via the shared executor + core retry loop.
        let response = try await RustRetry.perform(
            session: session,
            buildRequest: { request },
            parseError: { resp in
                do {
                    _ = try parseRoutedResponse(providerHeader: providerHeader, resp: resp)
                    return TranscriptionError.invalidResponse(details: "unexpected non-error response")
                } catch let err as HwTranscriptionError {
                    let creditDenial = Self.creditDenialContext(from: resp)
                    if resp.status == 402, let invalidMessage = creditDenial.invalidExhaustedBalanceMessage {
                        return TranscriptionError.invalidResponse(details: invalidMessage)
                    }
                    let (tooBigBytes, tooBigLimit) = RustCoreMapping.fileTooLargeContext(from: resp)
                    return RustCoreMapping.mapTranscriptionError(
                        err,
                        providerName: providerDisplayName,
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
            onTransportError: { urlError in
                // One-shot DNS-cache flush on a network flip (VPN/captive-portal/
                // tether swap). All three Fly providers share `session`, so a
                // reset re-resolves the host for the next attempt. Gated to one
                // flush per sequence by RustRetry; we additionally gate on the
                // DNS-shaped error codes so a generic blip doesn't reset the pool.
                if Self.isDnsError(urlError) {
                    await Self.recoverDns(session: session)
                }
            }
        )

        if Task.isCancelled { throw CancellationError() }

        let transcript: HwTranscript
        do {
            transcript = try parseRoutedResponse(providerHeader: providerHeader, resp: response)
        } catch let err as HwTranscriptionError {
            throw RustCoreMapping.mapTranscriptionError(err, providerName: providerDisplayName)
        }

        await creditManager.invalidateCache()

        AppLogger.network.info("HW-routed transcription completed · provider=\(providerHeader, privacy: .public) · chars=\(transcript.text.count, privacy: .public) · creditsUsed=\(transcript.cost ?? 0, privacy: .public)")

        // Raw routed transcription text — may not be wrapped, so use the lenient
        // passthrough (strict extraction would wipe an unwrapped transcript).
        return TranscriptionTextProcessing.stripWrapperMarkers(transcript.text)
    }

    // MARK: - Per-provider core dispatch

    /// Route to the correct core builder by the pinned `X-STT-Provider` value.
    /// Both builders force their own provider header; any other value falls back
    /// to the base HyperWhisper Cloud builder (defensive — callers only pass the
    /// two routed values today).
    private static func buildRoutedRequest(providerHeader: String, params: TranscribeParams) throws -> HttpRequest {
        switch providerHeader {
        case "azure-mai":
            return try azureMaiBuildTranscribeRequest(params: params)
        case "google-chirp":
            return try googleChirpBuildTranscribeRequest(params: params)
        default:
            return try hyperwhisperCloudBuildTranscribeRequest(params: params)
        }
    }

    /// Route to the correct core parser by the pinned `X-STT-Provider` value.
    /// (All three share the same routed response contract, so this is purely for
    /// symmetry / future divergence.)
    private static func parseRoutedResponse(providerHeader: String, resp: HttpResponse) throws -> HwTranscript {
        switch providerHeader {
        case "azure-mai":
            return try azureMaiParseTranscribeResponse(resp: resp)
        case "google-chirp":
            return try googleChirpParseTranscribeResponse(resp: resp)
        default:
            return try hyperwhisperCloudParseTranscribeResponse(resp: resp)
        }
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

    // MARK: - DNS recovery (network-flip resilience)

    /// True for errors that look like a stale/poisoned DNS cache — typical after
    /// a network flip (captive portal, VPN toggle, tether swap). Mirrors
    /// `HyperWhisperCloudProvider.isDnsError`.
    private static func isDnsError(_ error: URLError) -> Bool {
        switch error.code {
        case .dnsLookupFailed, .cannotFindHost, .cannotConnectToHost:
            return true
        default:
            return false
        }
    }

    /// One-shot URLSession pool flush so the next attempt re-resolves the host.
    /// `reset` also cancels in-flight tasks on the shared session — acceptable on
    /// a network flip since all three Fly providers hit the same host.
    private static func recoverDns(session: URLSession) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            session.reset { continuation.resume() }
        }
        AppLogger.network.debug("HW-routed DNS recovery: session reset")
    }
}
