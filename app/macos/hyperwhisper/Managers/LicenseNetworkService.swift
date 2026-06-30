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

import Foundation

/// Handles license validation network operations.
/// Stateless service - returns LicenseValidationResult for LicenseManager to process.
/// Falls back to cached status on network errors (7-day grace period).
///
/// M3-C: the validate/cache/grace LOGIC now lives in the Rust shared core
/// (`hw-license`). This service keeps the macOS-owned I/O — URLSession config,
/// `performWithRetry(.cloud)`, and Sentry — and delegates request building,
/// response parsing, and persistence to the core over a shared `RustLicenseStore`.
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
    func validateLicense(_ licenseKey: String) async -> LicenseValidationResult {
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
        guard var request = NetworkConfig.createRequest(
            for: NetworkConfig.licenseValidateEndpoint,
            timeout: NetworkConfig.licenseValidationTimeout
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
            return try await performWithRetry(config: .cloud) { [self] _ in
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
                if outcome.isValid {
                    licenseStoreLicenseKey(store: store, key: trimmedKey)
                }
                licenseUpdateValidationCache(
                    store: store,
                    status: outcome.status,
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
            let outcome = licenseOfflineFallbackOutcome(
                store: store,
                nowUnixSecs: RustLicenseTime.nowUTC()
            )
            AppLogger.network.info(
                "License validation offline · status=\(Self.adapt(outcome.status).rawValue)"
            )
            return Self.adapt(outcome)
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
    static func adapt(_ outcome: ValidationOutcome) -> LicenseValidationResult {
        LicenseValidationResult(
            isValid: outcome.isValid,
            status: adapt(outcome.status),
            customerId: outcome.customerId,
            customerEmail: outcome.customerEmail,
            customerName: nil,
            errorMessage: outcome.errorMessage
        )
    }
}
