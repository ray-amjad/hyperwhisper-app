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
//    connection refused) with a short bounded backoff before giving up — a real
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

import Foundation

/// Handles license validation network operations.
/// Stateless service - returns LicenseValidationResult for LicenseManager to process.
/// Falls back to cached status on network errors (7-day grace period).
///
/// M3-C: the validate/cache/grace LOGIC now lives in the Rust shared core
/// (`hw-license`). This service keeps the macOS-owned I/O — URLSession config,
/// `performWithRetry(.cloud / .licenseLaunchValidation)`, and Sentry — and
/// delegates request building, response parsing, and persistence to the core
/// over a shared `RustLicenseStore`.
class LicenseNetworkService {

    // MARK: - UserDefaults Keys

    /// Keys for storing license information in UserDefaults.
    /// Canonical source of truth for these keys — referenced by `BackupManager`
    /// so backup export/import use the same key the license is actually stored under.
    ///
    /// M3-C: these still match the Rust core's `com.hyperwhisper.license.*` keys
    /// 1:1, so the core reads/writes the exact same UserDefaults entries. Kept as
    /// constants for `BackupManager` (which references `DefaultsKey.licenseKey`).
    enum DefaultsKey {
        static let licenseKey = "com.hyperwhisper.license.key"
        static let customerId = "com.hyperwhisper.license.customerId"
        static let lastValidation = "com.hyperwhisper.license.lastValidation"
        static let cachedStatus = "com.hyperwhisper.license.cachedStatus"
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
        return await validateLicense(licenseKey)
    }

    // MARK: - License Deactivation

    /// Deactivates the license locally by clearing UserDefaults.
    func deactivateLicense() async -> (success: Bool, error: String?) {
        clearStoredLicense()
        AppLogger.network.info("License deactivated locally")
        return (true, nil)
    }

    // MARK: - License Validation

    /// Validates a license key with the backend and tracks device usage.
    /// Falls back to cached status if network fails (within 7-day grace period).
    ///
    /// - Parameter isLaunchValidation: `true` for the silent, background
    ///   revalidation `LicenseManager.loadStoredLicense()` fires on every app
    ///   launch when the cache is stale — uses the tighter `.licenseLaunchValidation`
    ///   retry budget AND the shorter `licenseLaunchValidationTimeout` per-request
    ///   timeout instead of `.cloud` / `licenseValidationTimeout`, so a flaky
    ///   network at launch (wake from sleep, captive portal, DNS not up yet)
    ///   self-heals in a few seconds rather than leaving a paying user looking
    ///   unlicensed for ~30s. Explicit, user-triggered activation (the default,
    ///   `false`) keeps the `.cloud` budget and full request timeout, matching
    ///   the wait a user already expects after tapping "Activate". HYPERWHISPER-F4.
    func validateLicense(_ licenseKey: String, isLaunchValidation: Bool = false) async -> LicenseValidationResult {
        let trimmedKey = licenseKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedKey.isEmpty else {
            AppLogger.network.warning("License validation rejected: empty license key")
            // Core owns the empty-key outcome ("License key cannot be empty").
            return Self.adapt(licenseEmptyKeyOutcome())
        }

        // Generate device identifier for tracking (kept native).
        // This is used by the backend for fair usage policy monitoring.
        let deviceId = DeviceIdentifierGenerator.generate()
        let deviceName = ProcessInfo.processInfo.hostName

        // Core builds the POST body (fixed field order, JSON-escaped, trimmed key).
        let coreRequest = licenseBuildValidateRequest(
            licenseKey: trimmedKey,
            deviceId: deviceId,
            deviceName: deviceName
        )

        // Create the URLRequest shell natively (timeout, headers), then attach the
        // core-built body. `createRequest` defaults to POST + JSON content type,
        // matching the core's request.
        //
        // Both the per-request timeout AND the retry preset are selected off the
        // same `isLaunchValidation` bool via `Self.requestPolicy(isLaunchValidation:)`
        // — a single lookup instead of two independent ternaries, so the two
        // values (which must stay paired: a short timeout with a short retry
        // budget, a long timeout with the long one) can't drift out of sync in a
        // future edit. Launch-time validation uses the much shorter
        // `licenseLaunchValidationTimeout` so a hung request doesn't eat the
        // whole per-attempt budget — otherwise 3 attempts at the full 10s
        // `licenseValidationTimeout` would still cost ~30s worst case, defeating
        // the point of the tighter `.licenseLaunchValidation` retry preset.
        // HYPERWHISPER-F4.
        let policy = Self.requestPolicy(isLaunchValidation: isLaunchValidation)
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
        request.setValue(coreRequest.contentType, forHTTPHeaderField: "Content-Type")
        request.httpBody = coreRequest.body

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
                    if AppLogger.isErrorLoggingEnabled {
                        let err = NSError(
                            domain: "LicenseHTTP",
                            code: httpResponse.statusCode,
                            userInfo: [NSLocalizedDescriptionKey: outcome.errorMessage ?? "Server error"]
                        )
                        SentryService.capture(
                            error: err,
                            message: "License validation server error",
                            extras: ["endpoint": NetworkConfig.licenseValidateEndpoint, "status": httpResponse.statusCode],
                            tags: ["component": "license"]
                        )
                    }
                    return Self.adapt(outcome)
                }

                // Core parses the 200 body and maps it to a status/outcome.
                let outcome = licenseParseValidateResponse(body: data)

                // Persist the key + cache the result through the core's store.
                // The core stores the key only on a valid verdict and updates the
                // (global, key-less) validation cache only when the attempted key
                // is the stored key — a rejected replacement key must not
                // overwrite the stored key's cached status with Invalid, which
                // would lock out a valid user for up to 24h.
                licensePersistValidationVerdict(
                    store: store,
                    status: outcome.status,
                    attemptedKey: trimmedKey,
                    nowUnixSecs: RustLicenseTime.nowUTC()
                )
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
                // safety net. Worth its own distinct signal: unlike the 200-path
                // "License validation server error" above (a real, non-2xx server
                // verdict), this is a pure connectivity failure with nothing to
                // fall back on. Tagged distinctly so it doesn't get lumped in with
                // either of those.
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
    func clearStoredLicense() {
        licenseClearStoredLicense(store: store)
    }

    // MARK: - Per-call-site request policy

    /// The per-request timeout and retry budget to use for a `validateLicense`
    /// call — paired together so callers only make ONE `isLaunchValidation`
    /// decision instead of two independent ternaries (one for the timeout, one
    /// for the retry preset) that have to be kept in sync by hand. HYPERWHISPER-F4
    /// (review round 2).
    struct RequestPolicy {
        let requestTimeout: TimeInterval
        let retryConfig: RetryConfiguration
    }

    /// Single source of truth for `isLaunchValidation`'s two derived settings.
    /// Launch-time (silent, background) revalidation gets both the shorter
    /// `licenseLaunchValidationTimeout` AND the tighter `.licenseLaunchValidation`
    /// retry budget; explicit, user-triggered activation gets the normal
    /// `licenseValidationTimeout` AND `.cloud` budget. These two values are
    /// deliberately paired (a short per-request timeout is only useful alongside
    /// a short retry budget, and vice versa) — computing them together here
    /// means a future change to one can't accidentally leave the other on the
    /// wrong preset.
    static func requestPolicy(isLaunchValidation: Bool) -> RequestPolicy {
        isLaunchValidation
            ? RequestPolicy(
                requestTimeout: NetworkConfig.licenseLaunchValidationTimeout,
                retryConfig: .licenseLaunchValidation
            )
            : RequestPolicy(
                requestTimeout: NetworkConfig.licenseValidationTimeout,
                retryConfig: .cloud
            )
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

    /// Maps the app's `LicenseStatus` to the core's `HwLicenseStatus`.
    static func toCore(_ status: LicenseStatus) -> HwLicenseStatus {
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
