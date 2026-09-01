//
//  LicenseNetworkService.swift
//  hyperwhisper
//
//  LICENSE NETWORK SERVICE
//  Handles license validation and local storage.
//
//  FLOW:
//  - activateLicense() → validates license and tracks device
//  - deactivateLicense() → clears local UserDefaults (no network call)
//  - validateLicense() → POST /api/license/validate with device_id
//
//  CACHING:
//  - 24-hour validation cache
//  - 7-day offline grace period
//
//  NETWORK RESILIENCE (HYPERWHISPER-F4):
//  - validateLicense() retries a NETWORK failure (timeout/no connection/DNS/
//    connection refused) with a short bounded backoff before giving up — a
//    non-2xx server verdict is never retried (see licenseHttpErrorOutcome).
//  - The launch-time revalidation (LicenseManager.loadStoredLicense(), passing
//    isLaunchValidation: true) uses both a tighter retry budget AND a much
//    shorter per-request timeout than explicit, user-triggered activation
//    (`.licenseLaunchValidation` + `licenseLaunchValidationTimeout` vs `.cloud`
//    + `licenseValidationTimeout`) so a flaky network at launch self-heals in a
//    few seconds instead of leaving a paying user looking unlicensed for the
//    ~30s `.cloud` budget.
//  - If retries are exhausted, we fall back to the last cached SERVER verdict
//    (never a fabricated one) for the key on file, within its 7-day grace, and
//    let the app proceed — no hard error shown to the user. Only the "no usable
//    cache" case gets a distinct diagnostic signal.
//
//  ERROR REPORTING (HYPERWHISPER-SP / HYPERWHISPER-FM):
//  - An ordinary "this license isn't entitled" reply is logged, not captured to
//    Sentry — see `licenseVerdictReason` for the rule and why.
//

import Foundation

protocol LicenseNetworkServing {
    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult
    func deactivateLicense() async -> (success: Bool, error: String?)
    /// Protocol requirements cannot carry default arguments, so the launch-retry
    /// parameters are spelled out here; callers that don't care pass `false`/`nil`.
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult
    func shouldRevalidateLicense() -> Bool
    func getCachedLicenseStatus() -> LicenseStatus?
    func getStoredLicenseKey() -> String?
    func clearStoredLicense() -> Bool
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool
}

/// Handles license validation network operations.
/// Stateless service - returns LicenseValidationResult for LicenseManager to process.
/// Falls back to cached status on network errors (7-day grace period).
///
/// M3-C: the validate/cache/grace LOGIC now lives in the Rust shared core
/// (`hw-license`). This service keeps the macOS-owned I/O — URLSession config,
/// `performWithRetry(.cloud / .licenseLaunchValidation)`, and Sentry — and
/// delegates request building, response parsing, and persistence to the core
/// over a shared `RustLicenseStore`.
class LicenseNetworkService: LicenseNetworkServing {

    enum ValidationMode {
        case stateful
        case probe

        var includesDeviceTracking: Bool { self == .stateful }
        var persistsResult: Bool { self == .stateful }
        var allowsOfflineFallback: Bool { self == .stateful }
    }

    private struct ProbeRequest: Encodable {
        let licenseKey: String
        let probeOnly = true

        enum CodingKeys: String, CodingKey {
            case licenseKey = "license_key"
            case probeOnly = "probe_only"
        }
    }

    // MARK: - Properties

    /// Shared key-value store backing the Rust license core. Injected so the
    /// whole license subsystem (network, usage, manager) shares one instance and
    /// one one-shot seed.
    private let store: RustLicenseStore

    /// URLSession for API calls with timeout configuration
    private let session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = NetworkConfig.licenseValidationTimeout
        return URLSession(configuration: config)
    }()

    init(store: RustLicenseStore) {
        self.store = store
    }

    // MARK: - License Activation

    /// Activates a license key by validating it and tracking the device.
    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        return await performValidation(licenseKey, mode: .stateful)
    }

    // MARK: - License Deactivation

    /// Deactivates the license locally by deleting the secure record.
    func deactivateLicense() async -> (success: Bool, error: String?) {
        guard clearStoredLicense() else {
            return (false, "Could not securely remove the license")
        }
        AppLogger.network.info("License deactivated locally")
        return (true, nil)
    }

    // MARK: - License Validation

    /// Validates a license key with the backend and tracks device usage.
    /// Falls back to cached status if network fails (within 7-day grace period).
    ///
    /// - Parameter isLaunchValidation: `true` for the silent, background
    ///   revalidation `LicenseManager.loadStoredLicense()` fires on every app
    ///   launch when the cache is stale. When a cached verdict is still within
    ///   its offline grace period, the call uses the tighter
    ///   `.licenseLaunchValidation` retry budget and shorter per-request timeout.
    ///   With no safe cached fallback, it retains the normal `.cloud` budget so
    ///   a slow-but-live network is not prematurely presented as an invalid
    ///   license. Explicit activation also keeps the normal budget.
    /// - Parameter expectedStoredLicenseKey: When non-nil, the parsed server
    ///   verdict is persisted only if this is still the stored key. The delayed
    ///   launch retry supplies its scheduled key so an activation, deactivation,
    ///   or restore that lands while the request is in flight wins the race and
    ///   cannot be undone by the stale response.
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool = false,
        expectedStoredLicenseKey: String? = nil
    ) async -> LicenseValidationResult {
        return await performValidation(
            licenseKey,
            mode: .stateful,
            isLaunchValidation: isLaunchValidation,
            expectedStoredLicenseKey: expectedStoredLicenseKey
        )
    }

    /// Checks a key without activating it on this Mac.
    ///
    /// Probe requests omit device identity, do not persist the key or validation
    /// cache, and never substitute an existing key's offline cached result.
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult {
        return await performValidation(licenseKey, mode: .probe)
    }

    /// - Parameter isLaunchValidation / expectedStoredLicenseKey: only meaningful
    ///   in `.stateful` mode; a `.probe` never persists, so it never consults them.
    private func performValidation(
        _ licenseKey: String,
        mode: ValidationMode,
        isLaunchValidation: Bool = false,
        expectedStoredLicenseKey: String? = nil
    ) async -> LicenseValidationResult {
        let trimmedKey = licenseKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedKey.isEmpty else {
            AppLogger.network.warning("License validation rejected: empty license key")
            // Core owns the empty-key outcome ("License key cannot be empty").
            return Self.adapt(licenseEmptyKeyOutcome())
        }

        let requestBody: Data
        let contentType: String
        if mode.includesDeviceTracking {
            // Stateful validation identifies this Mac so the backend can enforce
            // its fair-usage device policy.
            let coreRequest = licenseBuildValidateRequest(
                licenseKey: trimmedKey,
                deviceId: DeviceIdentifierGenerator.generate(),
                // `ProcessInfo.processInfo.hostName` used to be here. It routes
                // through `-[NSHost name]` → a blocking `.local` mDNS resolve
                // (issue #313), and this call site is on the cooperative pool
                // during launch validation. `DeviceName` reads the friendly
                // computer name locally instead: "Ray's MacBook Pro" rather than
                // "Rays-MacBook-Pro.local". The field is a display label in the
                // licence portal's device list — device identity is carried by
                // `device_id` — so the friendlier spelling is also the better one.
                deviceName: DeviceName.current
            )
            requestBody = coreRequest.body
            contentType = coreRequest.contentType
        } else {
            // The backend treats device_id as optional and only records a device
            // validation when it is present. Keep onboarding's Test action a
            // lookup-only request.
            guard let body = Self.makeProbeRequestBody(licenseKey: trimmedKey) else {
                return LicenseValidationResult(
                    isValid: false,
                    status: .invalid,
                    customerId: nil,
                    customerEmail: nil,
                    customerName: nil,
                    errorMessage: "Invalid request configuration"
                )
            }
            requestBody = body
            contentType = "application/json"
        }

        // Create the URLRequest shell natively (timeout, headers), then attach the
        // core-built body. `createRequest` defaults to POST + JSON content type,
        // matching the core's request.
        //
        // A tight launch budget is safe only when a cached server verdict is
        // available for this same key within the offline grace period. Without
        // that safety net, keep the normal request/retry budget so a slow VPN or
        // weak network still gets the same chance to validate as an explicit
        // activation instead of being surfaced as an invalid license.
        let nowUnixSecs = RustLicenseTime.nowUTC()
        let hasCachedVerdict = licenseStoredLicenseKey(store: store) == trimmedKey
            && licenseCachedStatusWithinGrace(store: store, nowUnixSecs: nowUnixSecs) != nil
        let policy = Self.requestPolicy(
            isLaunchValidation: isLaunchValidation,
            hasCachedVerdict: hasCachedVerdict
        )
        guard var request = NetworkConfig.createRequest(
            for: NetworkConfig.licenseValidateEndpoint,
            timeout: policy.requestTimeout
        ) else {
            return LicenseValidationResult(
                isValid: false,
                status: .invalid,
                customerId: nil,
                customerEmail: nil,
                customerName: nil,
                errorMessage: "Invalid request configuration"
            )
        }
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        request.httpBody = requestBody

        do {
            return try await performWithRetry(config: policy.retryConfig) { [self] _ in
                let (data, response) = try await session.data(for: request)

                guard let httpResponse = response as? HTTPURLResponse else {
                    throw URLError(.badServerResponse)
                }

                // Retry transient server errors and rate limits; surface other non-200s as terminal.
                if (500...599).contains(httpResponse.statusCode) || httpResponse.statusCode == 429 {
                    throw URLError(URLError.Code(rawValue: httpResponse.statusCode))
                }

                if httpResponse.statusCode != 200 {
                    // Core extracts the server `error` field (or a generic message).
                    let outcome = licenseHttpErrorOutcome(
                        statusCode: UInt16(httpResponse.statusCode),
                        body: data
                    )
                    // The server's own classification of this rejection, or nil
                    // if it did not state one. Decoded once and used for BOTH
                    // the verdict decision and the diagnostic that follows, so
                    // the two can never disagree about what the server said.
                    let verdictReason = Self.licenseVerdictReason(
                        statusCode: httpResponse.statusCode,
                        body: data
                    )
                    let reasonForDiagnostics = verdictReason ?? Self.unstatedReason

                    if verdictReason == Self.notEntitledReason {
                        // The core already extracted the server's `error` field into
                        // `outcome.errorMessage` — don't re-decode, and never log the
                        // license key itself. `.public` on every interpolation: an
                        // os.Logger redacts by default, which would throw away the
                        // only record of a rejection we deliberately don't capture.
                        let serverMessage = outcome.errorMessage ?? "no message"
                        AppLogger.network.warning(
                            "License validation rejected by server · status=\(httpResponse.statusCode, privacy: .public) · reason=\(reasonForDiagnostics, privacy: .public) · message=\(serverMessage, privacy: .public)"
                        )
                    } else if AppLogger.isErrorLoggingEnabled {
                        let err = NSError(
                            domain: "LicenseHTTP",
                            code: httpResponse.statusCode,
                            userInfo: [NSLocalizedDescriptionKey: outcome.errorMessage ?? "Server error"]
                        )
                        // `license_reason` as a TAG, not just an extra: everything
                        // that reaches here shares one issue title, so without it
                        // `lookup_failed` (a real backend incident), `bad_request`
                        // (a client bug), an unrecognised reason and an unstated
                        // one are one undifferentiated pile. As a tag it is
                        // searchable and chartable, which is what triage needs.
                        // Grouping is deliberately NOT changed — no fingerprint
                        // override — so existing HYPERWHISPER-SP / -FM history
                        // stays intact.
                        SentryService.capture(
                            error: err,
                            message: "License validation server error",
                            extras: [
                                "endpoint": NetworkConfig.licenseValidateEndpoint,
                                "status": httpResponse.statusCode,
                                "license_reason": reasonForDiagnostics
                            ],
                            tags: [
                                "component": "license",
                                "license_reason": reasonForDiagnostics
                            ]
                        )
                    }
                    return Self.adapt(outcome)
                }

                // Core parses the 200 body and maps it to a status/outcome.
                let outcome = licenseParseValidateResponse(body: data)

                // Persist the key + cache the result through the core's store —
                // but only for explicit activation or background revalidation,
                // never for an onboarding probe.
                //
                // The delayed launch retry additionally supplies the key that
                // was current when it was scheduled. Check that precondition
                // immediately before the side-effecting FFI call, on MainActor,
                // so it is serialized with LicenseManager activation/deactivation:
                // if one of those operations wins while this request is in
                // flight, a stale Active response cannot restore the old key.
                //
                // The core still owns entitlement semantics: an Active verdict
                // stores the key, while a rejected replacement cannot overwrite
                // the current key's global cache.
                if mode.persistsResult {
                    let persistence = await MainActor.run {
                        Self.persistValidationVerdictIfCurrent(
                            store: store,
                            status: outcome.status,
                            attemptedKey: trimmedKey,
                            expectedStoredLicenseKey: expectedStoredLicenseKey,
                            nowUnixSecs: RustLicenseTime.nowUTC(),
                            isCancelled: withUnsafeCurrentTask { $0?.isCancelled ?? false }
                        )
                    }
                    switch persistence {
                    case .persisted:
                        break
                    case .staleOrCancelled:
                        throw CancellationError()
                    case .storageFailed:
                        return LicenseValidationResult(
                            isValid: false,
                            status: .invalid,
                            customerId: nil,
                            customerEmail: nil,
                            customerName: nil,
                            errorMessage: "Could not securely save the license"
                        )
                    }
                }
                AppLogger.network.info("License validation · status=\(Self.adapt(outcome.status).rawValue)")

                return Self.adapt(outcome)
            }
        } catch is CancellationError {
            return LicenseValidationResult(
                isValid: false,
                status: .invalid,
                customerId: nil,
                customerEmail: nil,
                customerName: nil,
                errorMessage: "Validation cancelled"
            )
        } catch {
            guard mode.allowsOfflineFallback else {
                return LicenseValidationResult(
                    isValid: false,
                    status: .invalid,
                    customerId: nil,
                    customerEmail: nil,
                    customerName: nil,
                    errorMessage: "Unable to verify license while offline"
                )
            }

            // Retries exhausted. The cached offline-grace status is only valid for
            // the SAME license key it was cached under. If the user is validating a
            // DIFFERENT key (or there is no stored key yet), reporting the cached
            // verdict would wrongly mark an unverified key as Active/offline — so
            // only honor the offline fallback when the submitted key matches the
            // stored one. (G2 parity with Windows LicenseNetworkService.)
            if licenseStoredLicenseKey(store: store) != trimmedKey {
                AppLogger.network.info(
                    "License validation offline · submitted key differs from stored — not honoring cached verdict"
                )
                return LicenseValidationResult(
                    isValid: false,
                    status: .invalid,
                    customerId: nil,
                    customerEmail: nil,
                    customerName: nil,
                    errorMessage: "Unable to verify license while offline"
                )
            }

            // Core decides the offline fallback (cached status within the 7-day
            // grace, else Invalid) for the key currently on file.
            let nowUnixSecs = RustLicenseTime.nowUTC()
            let outcome = licenseOfflineFallbackOutcome(
                store: store,
                nowUnixSecs: nowUnixSecs
            )

            // Whether there is a genuinely cached SERVER verdict at all — distinct
            // from `outcome.isValid`, which only means "the cached verdict is
            // currently Active." A cached Expired/Invalid verdict within the 7-day
            // grace is still a real cache hit (the core's `licenseOfflineFallbackOutcome`
            // returns it as-is); it's just not an active entitlement. Gating the
            // Sentry signal below on `outcome.isValid` would misfire it for every
            // offline Expired/Invalid user even though the cache did its job.
            //
            // NOTE: this is a second, separate FFI read of the same underlying
            // cache that `licenseOfflineFallbackOutcome` above just read — in
            // principle a concurrent `clearStoredLicense()` landing between the
            // two calls could make them disagree (e.g. `outcome` reflects the
            // pre-clear cache while `hasCachedVerdict` reflects the post-clear
            // empty state), which would only mislabel the diagnostic below, not
            // the `outcome` actually returned to the caller. Narrow and
            // theoretical (deactivation racing an in-flight offline validation),
            // not worth a Swift-side workaround (duplicating the core's
            // is-valid/customer-id derivation here to collapse this to one call
            // would re-introduce cache logic on the client, which is exactly what
            // the Rust core migration was meant to centralize). A real fix is a
            // single combined Rust core accessor (e.g. returning the outcome
            // alongside a `hadCache` flag from one read) — tracked as a follow-up,
            // out of scope for this Swift-only PR. HYPERWHISPER-F4 (review round 2).
            let hasCachedVerdict = licenseCachedStatusWithinGrace(
                store: store,
                nowUnixSecs: nowUnixSecs
            ) != nil

            // Distinguishes a genuine connectivity failure (can't reach the
            // network at all) from repeated 5xx/429 responses that exhausted
            // retries (the server WAS reached, it just kept erroring) — used to
            // split both the Sentry signal below and the "should we retry again
            // soon" decision surfaced to `LicenseManager`. HYPERWHISPER-F4
            // (review round 2).
            let isNetworkFailure = Self.isNetworkFailure(error)

            if hasCachedVerdict {
                // Retries exhausted, but we have a last-known-good server verdict
                // for this exact key within its 7-day grace — serve it and move on
                // (whatever that verdict is — Active, Expired, or Invalid). This is
                // the resilience behavior we WANT (HYPERWHISPER-F4): no hard error,
                // no Sentry noise, the app just proceeds on the cached verdict as
                // if the network blip never happened.
                AppLogger.network.info(
                    "License validation offline · serving cached verdict status=\(Self.adapt(outcome.status).rawValue) after retries exhausted (launch=\(isLaunchValidation), networkFailure=\(isNetworkFailure))"
                )
            } else {
                // No usable cached fallback (never validated yet, or the 7-day
                // grace has elapsed) — the user is genuinely stuck offline with no
                // safety net. Worth its own distinct signal: unlike the non-200
                // "License validation server error" above, this is a pure
                // connectivity failure with nothing to fall back on. Tagged
                // distinctly so it doesn't get lumped in with that one.
                //
                // `failure_kind` further splits this signal by `isNetworkFailure`:
                // a genuine connectivity failure (dead network, DNS, etc.) is a
                // very different operational condition from repeated 5xx/429
                // responses exhausting retries (the server IS reachable, it's
                // unhealthy) — conflating them into one tag would make this signal
                // much less actionable. HYPERWHISPER-F4.
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.capture(
                        error: error,
                        message: isNetworkFailure
                            ? "License validation offline — no cached fallback available (network unreachable)"
                            : "License validation offline — no cached fallback available (server error exhausted retries)",
                        extras: [
                            "endpoint": NetworkConfig.licenseValidateEndpoint,
                            "isLaunchValidation": isLaunchValidation,
                        ],
                        tags: [
                            "component": "license",
                            "reason": "offline_no_cache",
                            "failure_kind": isNetworkFailure ? "network" : "server_error",
                        ]
                    )
                }
                AppLogger.network.warning(
                    "License validation offline · no cached fallback available (launch=\(isLaunchValidation), networkFailure=\(isNetworkFailure))"
                )
            }

            // Surface "this specific result was served from a cached verdict (or
            // Invalid) because we couldn't reach the network" so a launch-time
            // caller (`LicenseManager.loadStoredLicense()`) can schedule a prompt
            // background retry instead of waiting out the full 24h cache TTL / 7-
            // day grace — a merely-slow-but-live network shouldn't ride a stale
            // cached verdict for up to a week. Deliberately NOT set for the
            // "submitted key differs from stored" early-return above (that path
            // never applies to launch validation, which always revalidates the
            // stored key) or for exhausted-5xx/429 fallbacks (a real server
            // response, not a connectivity problem — retrying again in a minute
            // is unlikely to help). HYPERWHISPER-F4 (review round 2).
            return Self.adapt(outcome, networkFailureFallback: isNetworkFailure)
        }
    }

    static func makeProbeRequestBody(licenseKey: String) -> Data? {
        try? JSONEncoder().encode(ProbeRequest(licenseKey: licenseKey))
    }

    // MARK: - Cache Management (delegated to the Rust core)

    /// Checks if license should be revalidated (>24h since last validation, or no
    /// cached timestamp). Delegates to the core's `licenseShouldRevalidate`.
    func shouldRevalidateLicense() -> Bool {
        // Cache TTL is a pure duration delta → plain UTC.
        return licenseShouldRevalidate(store: store, nowUnixSecs: RustLicenseTime.nowUTC())
    }

    /// Gets the cached license status if within the 7-day offline grace period.
    /// Delegates to the core's `licenseCachedStatusWithinGrace`.
    func getCachedLicenseStatus() -> LicenseStatus? {
        guard let hwStatus = licenseCachedStatusWithinGrace(
            store: store,
            nowUnixSecs: RustLicenseTime.nowUTC()
        ) else {
            return nil
        }
        return Self.adapt(hwStatus)
    }

    /// Gets stored license key (nil for empty/whitespace). Delegates to the core.
    func getStoredLicenseKey() -> String? {
        return licenseStoredLicenseKey(store: store)
    }

    // MARK: - License Data Management

    /// Clears all stored license data (key, customerId, lastValidation, status).
    /// Delegates to the core's `licenseClearStoredLicense` (which leaves the
    /// remote-override config untouched).
    @discardableResult
    func clearStoredLicense() -> Bool {
        store.performLicenseTransaction {
            licenseClearStoredLicense(store: store)
        }
    }

    /// Atomically installs a backup key without carrying over the prior key's
    /// cached status, customer, or timestamp.
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool {
        store.replaceLicenseKeyForImport(licenseKey)
    }

    // MARK: - Per-call-site request policy

    /// The per-request timeout and retry budget to use for a `validateLicense`
    /// call. The tight launch policy is selected only when there is a usable
    /// cached verdict to serve if the live refresh exhausts that short budget.
    struct RequestPolicy {
        let requestTimeout: TimeInterval
        let retryConfig: RetryConfiguration
    }

    /// Single source of truth for the paired timeout and retry settings.
    /// Launch-time revalidation with a usable cached verdict gets the short
    /// policy. Explicit activation and launch validation without a safe cached
    /// fallback retain the normal policy.
    static func requestPolicy(
        isLaunchValidation: Bool,
        hasCachedVerdict: Bool
    ) -> RequestPolicy {
        isLaunchValidation && hasCachedVerdict
            ? RequestPolicy(
                requestTimeout: NetworkConfig.licenseLaunchValidationTimeout,
                retryConfig: .licenseLaunchValidation
            )
            : RequestPolicy(
                requestTimeout: NetworkConfig.licenseValidationTimeout,
                retryConfig: .cloud
            )
    }

    // MARK: - Validation persistence

    /// Persists a server verdict only while the caller's validation is still
    /// current. Main-actor isolation makes the expected-key check and the
    /// side-effecting Rust FFI call one serialized unit relative to
    /// LicenseManager activation/deactivation.
    ///
    /// A nil `expectedStoredLicenseKey` is the explicit-activation contract:
    /// an Active verdict may replace the currently stored key. The delayed
    /// launch retry passes a non-nil expected key and therefore cannot restore
    /// a key that was cleared or replaced while its request was in flight.
    enum ValidationPersistenceResult: Equatable {
        case persisted
        case staleOrCancelled
        case storageFailed
    }

    @MainActor
    static func persistValidationVerdictIfCurrent(
        store: KeyValueStore,
        status: HwLicenseStatus,
        attemptedKey: String,
        expectedStoredLicenseKey: String?,
        nowUnixSecs: Int64,
        isCancelled: Bool
    ) -> ValidationPersistenceResult {
        guard !isCancelled else { return .staleOrCancelled }

        let persist = {
            licensePersistValidationVerdict(
                store: store,
                status: status,
                attemptedKey: attemptedKey,
                nowUnixSecs: nowUnixSecs
            )
        }
        if let secureStore = store as? RustLicenseStore {
            var isCurrent = true
            let didCommit = secureStore.performLicenseTransaction {
                if let expectedStoredLicenseKey,
                   licenseStoredLicenseKey(store: secureStore)
                    != expectedStoredLicenseKey.trimmingCharacters(in: .whitespacesAndNewlines) {
                    isCurrent = false
                    return
                }
                persist()
            }
            guard didCommit else { return .storageFailed }
            return isCurrent ? .persisted : .staleOrCancelled
        }

        if let expectedStoredLicenseKey,
           licenseStoredLicenseKey(store: store) != expectedStoredLicenseKey.trimmingCharacters(
               in: .whitespacesAndNewlines
           ) {
            return .staleOrCancelled
        }
        persist()
        return .persisted
    }

    // MARK: - Error classification

    /// Whether `error` represents a network-connectivity failure (timeout, no
    /// connection, DNS lookup failure, connection refused, dropped connection,
    /// etc.) as opposed to a real server-issued verdict.
    ///
    /// Real non-2xx verdicts never reach this classifier — the request closure
    /// above resolves them to a `ValidationOutcome` directly instead of throwing
    /// (see `licenseHttpErrorOutcome`). The 500/599 and 429 statuses ARE thrown
    /// (as a same-domain `URLError`) so `performWithRetry` treats a transient
    /// server hiccup as retryable, but their raw status codes don't match any
    /// case here — this classifier answers "was this specifically a connectivity
    /// problem," which is used only to enrich the offline diagnostic signal above,
    /// not to gate retry/fallback eligibility. HYPERWHISPER-F4.
    ///
    /// Reuses `TranscriptionPipeline.transientURLErrorCodes` (the same
    /// connectivity-code set already used for transcription's Sentry-capture
    /// classification) rather than maintaining an independent copy of the
    /// NSURLErrorDomain code list — see that property's doc comment.
    static func isNetworkFailure(_ error: Error) -> Bool {
        let nsError = error as NSError
        guard nsError.domain == NSURLErrorDomain else { return false }
        return TranscriptionPipeline.transientURLErrorCodes.contains(URLError.Code(rawValue: nsError.code))
    }

    /// The one field of the backend's invalid-license reply that this classifier
    /// reads. Everything else in that body (`valid`, `error`) is the core's job.
    ///
    /// Plain lowercase key, deliberately: this file's only snake_case is on the
    /// REQUEST side (`ProbeRequest.CodingKeys`), so no `convertFromSnakeCase`
    /// here. Optional so a body that omits `reason` decodes to nil (and is then
    /// treated as unstated below) rather than throwing ambiguously.
    private struct LicenseVerdictBody: Decodable {
        let reason: String?
    }

    /// The one `reason` value that means "ordinary verdict — log it, don't
    /// report it". Named rather than spelled out at each use so the predicate,
    /// the call site's branch and its log line cannot drift apart.
    static let notEntitledReason = "not_entitled"

    /// Stand-in used in diagnostics when the server stated no usable reason, so
    /// "the backend didn't classify this" is searchable in Sentry instead of
    /// being an absent tag.
    static let unstatedReason = "unstated"

    /// The backend's machine-readable classification of a non-200 invalid-license
    /// reply — `not_entitled`, `lookup_failed`, `bad_request`, or whatever else
    /// it may add later — returned verbatim. Nil when the status is not 400, the
    /// body does not decode, or it states no non-empty `reason`.
    ///
    /// The full rationale for the field, and what each value means, lives on
    /// `LicenseInvalidReason` in nextjs/src/lib/license-validation-probe.ts. The
    /// short version: the response SHAPE cannot decide this, because the backend
    /// answers 400 with the same `{"valid":false,"error":"…"}` for an ordinary
    /// lapsed license, for a key that doesn't exist, and for genuine
    /// infrastructure faults. Only the server knows which branch it took, so it
    /// says so.
    ///
    /// Callers treat exactly one value — `notEntitledReason` — as an ordinary
    /// verdict to log rather than capture (HYPERWHISPER-SP / HYPERWHISPER-FM).
    /// A lapsed subscription, and now also a mistyped or non-existent key, hit
    /// that on every launch-time revalidation; neither is a server fault.
    ///
    /// Everything else is still captured, and the value returned here is
    /// attached to the Sentry event so the cases stay separable: a different
    /// status, `lookup_failed`, `bad_request`, an unrecognised reason, or a body
    /// with no `reason` at all (an older backend, an HTML captive-portal page,
    /// an empty body). Nil-means-report is the safe default, and it is also
    /// accurate — one backend serves every client, and every invalid reply from
    /// it is serialized through `invalidLicenseResponse`, so `reason` is present
    /// on all of them from the moment it deploys.
    ///
    /// Known, accepted gap: a bad migration that flipped every license to
    /// not-granted would be reported as `not_entitled` and stay silent here.
    /// That belongs in backend metrics, not in a client-side Sentry event.
    ///
    /// This changes ONLY whether a Sentry event is sent, and what it is tagged
    /// with. The verdict returned to callers is unaffected: the core's outcome
    /// is still `.invalid`, with the same user-facing message.
    static func licenseVerdictReason(statusCode: Int, body: Data) -> String? {
        guard statusCode == 400 else { return nil }
        guard let decoded = try? JSONDecoder().decode(LicenseVerdictBody.self, from: body) else {
            return nil
        }
        guard let reason = decoded.reason, !reason.isEmpty else { return nil }
        return reason
    }

    // MARK: - ValidationOutcome → app-type adapters

    /// Maps the core's `HwLicenseStatus` to the app's `LicenseStatus` (raw
    /// strings match: Trial/Active/Expired/Invalid).
    static func adapt(_ status: HwLicenseStatus) -> LicenseStatus {
        switch status {
        case .trial: return .trial
        case .active: return .active
        case .expired: return .expired
        case .invalid: return .invalid
        }
    }

    /// Adapts the core's `ValidationOutcome` to the app's `LicenseValidationResult`.
    /// Note: the core does not surface `customerName`; it is always nil here
    /// (matches the prior native behavior, which also never populated it).
    ///
    /// - Parameter networkFailureFallback: `true` only for the offline-fallback
    ///   catch-branch case where retries were exhausted due to a genuine
    ///   connectivity failure (see `isNetworkFailure`) — signals to
    ///   `LicenseManager` that this result rode a cached/Invalid verdict because
    ///   the network couldn't be reached, not because the server issued it.
    ///   Defaults to `false` for every other call site (200-path success, empty
    ///   key, terminal HTTP error, cancellation). HYPERWHISPER-F4 (review round 2).
    static func adapt(_ outcome: ValidationOutcome, networkFailureFallback: Bool = false) -> LicenseValidationResult {
        LicenseValidationResult(
            isValid: outcome.isValid,
            status: adapt(outcome.status),
            customerId: outcome.customerId,
            customerEmail: outcome.customerEmail,
            customerName: nil,
            errorMessage: outcome.errorMessage,
            networkFailureFallback: networkFailureFallback
        )
    }
}
