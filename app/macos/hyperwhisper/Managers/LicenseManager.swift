//
//  LicenseManager.swift
//  hyperwhisper
//
//  LICENSE MANAGER
//  Coordinates license operations and manages UI state.
//
//  The license key is the HyperWhisper Cloud "wallet": `licenseStatus == .active`
//  selects the Cloud transcription identifier (license key vs device id). Local,
//  on-device transcription and model downloads are unconditionally free and
//  unlimited (open source) — there is no local trial gate.
//
//  COMPONENTS:
//  - LicenseNetworkService: API calls and local storage
//
//  USAGE:
//  - Injected as @EnvironmentObject throughout the app
//  - SettingsView for license UI
//  - HyperWhisperCloudProvider for transcription identifiers
//

import Foundation
import SwiftUI

/// Orchestrates license operations and maintains UI state.
/// @MainActor because it updates @Published properties for SwiftUI.
@MainActor
class LicenseManager: ObservableObject {

    // MARK: - Published Properties (for UI binding)

    /// Current license status
    @Published var licenseStatus: LicenseStatus = .trial

    /// Whether license validation is in progress
    @Published var isValidating: Bool = false

    /// Error message from last validation attempt
    @Published var lastError: String?

    /// Customer email associated with the license
    @Published var customerEmail: String?

    /// Customer name associated with the license
    @Published var customerName: String?

    /// Whether deactivation is in progress
    @Published var isDeactivating: Bool = false

    // MARK: - Components

    /// Shared key-value store backing the Rust license core. Created ONCE here
    /// and passed to both the network service and the usage tracker so the whole
    /// subsystem shares a single instance — and a single one-shot Core Data →
    /// UserDefaults usage seed (run in `RustLicenseStore.init`, before any usage
    /// call). This is load-bearing for backward compatibility.
    private let store = RustLicenseStore()

    /// Network service for license API calls
    private let networkService: LicenseNetworkService

    /// One-shot background retry, scheduled only when launch-time validation
    /// fell back to a cached verdict because of a genuine network failure (see
    /// `loadStoredLicense()`). Held so a second `loadStoredLicense()` call
    /// (unlikely in practice — `LicenseManager` is a long-lived singleton — but
    /// possible in tests) cancels any still-pending retry rather than stacking
    /// them. HYPERWHISPER-F4 (review round 2).
    private var networkFailureRetryTask: Task<Void, Never>?
    private var networkFailureRetryID: UUID?

    // MARK: - Initialization

    init() {
        networkService = LicenseNetworkService(store: store)

        // Load stored license on initialization.
        Task {
            await loadStoredLicense()
        }
    }

    // MARK: - License Operations

    /// Activates a license key by validating it with the backend.
    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        cancelNetworkFailureRetry()
        isValidating = true
        lastError = nil
        defer { isValidating = false }

        let result = await networkService.activateLicense(licenseKey)
        await processValidationResult(result)
        return result
    }

    /// Deactivates the license locally (clears UserDefaults).
    func deactivateLicense() async -> Bool {
        cancelNetworkFailureRetry()
        isDeactivating = true
        lastError = nil
        defer { isDeactivating = false }

        let (success, error) = await networkService.deactivateLicense()
        if success {
            await clearLicense()
        } else {
            lastError = error
        }
        return success
    }

    /// Validates a license key with the backend.
    /// - Parameter isLaunchValidation: forwarded to `LicenseNetworkService` — `true`
    ///   only for the silent background revalidation `loadStoredLicense()` fires at
    ///   launch, selecting its tighter retry budget. See HYPERWHISPER-F4.
    func validateLicense(_ licenseKey: String, isLaunchValidation: Bool = false) async -> LicenseValidationResult {
        if !isLaunchValidation {
            cancelNetworkFailureRetry()
        }
        isValidating = true
        lastError = nil
        defer { isValidating = false }

        let result = await networkService.validateLicense(licenseKey, isLaunchValidation: isLaunchValidation)
        await processValidationResult(result)

        return result
    }

    /// Loads stored license from UserDefaults, revalidates if cache expired (24h).
    ///
    /// The revalidation call is tagged `isLaunchValidation: true` so a stale
    /// network at launch (wake-from-sleep, captive portal, DNS not up yet) gets a
    /// short bounded retry and — if that's still not enough — falls back to the
    /// last cached server verdict instead of leaving `licenseStatus` stuck at its
    /// default `.trial` for the whole `.cloud` retry budget. HYPERWHISPER-F4.
    ///
    /// If that fallback happened specifically because the network couldn't be
    /// reached (`result.networkFailureFallback`), a short background retry is
    /// scheduled — see `scheduleRetrySoonAfterNetworkFallback`. HYPERWHISPER-F4
    /// (review round 2).
    func loadStoredLicense() async {
        guard let storedKey = networkService.getStoredLicenseKey() else {
            licenseStatus = .trial
            return
        }

        if networkService.shouldRevalidateLicense() {
            // Publish the still-usable cached verdict before awaiting the live
            // refresh. Otherwise the manager remains at its default `.trial`
            // throughout the request/retry budget even though a server-issued
            // verdict is available within the offline grace period.
            if let cachedStatus = networkService.getCachedLicenseStatus() {
                licenseStatus = cachedStatus
            }

            let result = await validateLicense(storedKey, isLaunchValidation: true)
            if result.networkFailureFallback {
                scheduleRetrySoonAfterNetworkFallback(licenseKey: storedKey)
            }
        } else if let cachedStatus = networkService.getCachedLicenseStatus() {
            licenseStatus = cachedStatus
        }
    }

    /// Schedules ONE short, background revalidation after launch-time
    /// validation fell back to a cached (or Invalid) verdict due to a genuine
    /// network failure, rather than waiting out the full 24h cache TTL / 7-day
    /// offline grace before trying the real server again.
    ///
    /// A merely-slow-but-live network (weak wifi, VPN overhead) can trip the
    /// deliberately tight launch-time timeout/retry budget
    /// (`NetworkConfig.licenseLaunchValidationTimeout` +
    /// `.licenseLaunchValidation`) without the user actually being offline —
    /// that budget is tuned for a fast self-heal, not for correctly
    /// distinguishing "dead" from "slow." Without this follow-up, a
    /// legitimately-licensed user who hit that misclassification would silently
    /// ride the cached fallback for up to a week. By the time this fires, a
    /// merely-slow connection has almost certainly stabilized, so this quietly
    /// re-confirms against the real server. Deliberately a simple one-shot
    /// delayed `Task` rather than wiring up `NetworkStatus`/reachability
    /// observation — a single retry in the background costs nothing extra if
    /// the network happens to still be down (it just repeats the same
    /// fallback), and avoids the added complexity of debouncing a
    /// possibly-flapping reachability signal for what is already a rare edge
    /// case. HYPERWHISPER-F4 (review round 2).
    private func scheduleRetrySoonAfterNetworkFallback(licenseKey: String) {
        cancelNetworkFailureRetry()
        let retryID = UUID()
        networkFailureRetryID = retryID
        networkFailureRetryTask = Task { [weak self] in
            defer {
                if let self, self.networkFailureRetryID == retryID {
                    self.networkFailureRetryTask = nil
                    self.networkFailureRetryID = nil
                }
            }

            let delayNanoseconds = UInt64(NetworkConfig.licenseLaunchValidationRetrySoonDelay * 1_000_000_000)
            try? await Task.sleep(nanoseconds: delayNanoseconds)
            guard let self,
                  !Task.isCancelled,
                  self.networkFailureRetryID == retryID else {
                return
            }
            guard self.networkService.getStoredLicenseKey() == licenseKey else { return }

            AppLogger.network.info("License validation · retrying soon after launch-time network-failure fallback")
            let result = await self.networkService.validateLicense(
                licenseKey,
                isLaunchValidation: true,
                expectedStoredLicenseKey: licenseKey
            )

            // Activation, deactivation, backup restore, or another validation
            // may have changed the key while the request was in flight. Never
            // let a stale retry overwrite the current key's published state.
            guard !Task.isCancelled,
                  self.networkFailureRetryID == retryID,
                  self.networkService.getStoredLicenseKey() == licenseKey else {
                return
            }
            self.processValidationResult(result)
        }
    }

    /// Clears stored license and resets to the unlicensed (trial) state.
    func clearLicense() {
        cancelNetworkFailureRetry()
        networkService.clearStoredLicense()
        licenseStatus = .trial
        customerEmail = nil
        customerName = nil
        lastError = nil
        NotificationCenter.default.post(name: .licenseStatusChanged, object: nil)
    }

    // MARK: - Private

    private func cancelNetworkFailureRetry() {
        networkFailureRetryID = nil
        networkFailureRetryTask?.cancel()
        networkFailureRetryTask = nil
    }

    /// Updates UI state from validation result and posts notification.
    private func processValidationResult(_ result: LicenseValidationResult) {
        licenseStatus = result.status
        customerEmail = result.customerEmail
        customerName = result.customerName
        if !result.isValid { lastError = result.errorMessage }
        NotificationCenter.default.post(name: .licenseStatusChanged, object: nil)
    }

    // MARK: - Customer Portal

    /// Opens the user portal in browser for managing billing and credits
    func openCustomerPortal() {
        if let url = URL(string: "\(NetworkConfig.baseURL)/user") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Builds the identifier-aware credits purchase URL — `/credits` tagged
    /// with the caller's license key (licensed) or device ID (guest) so the
    /// purchase is attributed to the right wallet. Mirrors Windows
    /// `LicenseManager.GetCreditsPurchaseUrl()`; single source of truth for
    /// the credits URL on macOS.
    func creditsPurchaseURL() -> URL? {
        let (identifier, isLicensed) = getTranscriptionIdentifier()
        var components = URLComponents(string: "\(NetworkConfig.baseURL)/credits")
        components?.queryItems = [
            URLQueryItem(name: isLicensed ? "license_key" : "device_id", value: identifier)
        ]
        return components?.url
    }

    /// Opens the credits purchase page in the browser.
    func openCreditsPurchasePage() {
        if let url = creditsPurchaseURL() {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - HyperWhisper Cloud

    /// Returns license key if active, otherwise device ID for credit tracking.
    ///
    /// **Used By:**
    /// - HyperWhisperCloudProvider: Transcription API authentication
    /// - AIPostProcessor: Post-processing API authentication
    /// - StreamingTranscriptionClient: WebSocket authentication
    /// - HyperWhisperCloudManager: Credit balance fetching
    ///
    /// **Returns:**
    /// - `identifier`: License key (if licensed) or device ID (if trial)
    /// - `isLicensed`: true if user has active license, false if trial
    func getTranscriptionIdentifier() -> (identifier: String, isLicensed: Bool) {
        if licenseStatus == .active,
           let key = networkService.getStoredLicenseKey(),
           !key.isEmpty {
            return (key, true)
        }
        return (DeviceIdentifierGenerator.generate(), false)
    }
}
