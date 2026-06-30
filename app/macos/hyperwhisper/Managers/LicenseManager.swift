//
//  LicenseManager.swift
//  hyperwhisper
//
//  LICENSE MANAGER
//  Coordinates license operations and manages UI state.
//
//  COMPONENTS:
//  - LicenseNetworkService: API calls and local storage
//  - LicenseUsageTracker: Trial limits (daily time, model downloads)
//
//  USAGE:
//  - Injected as @EnvironmentObject throughout the app
//  - SettingsView for license UI
//  - HyperWhisperCloudProvider for transcription identifiers
//

import Foundation
import SwiftUI
import Combine

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

    // MARK: - Usage Tracker Properties (delegated)

    /// Daily transcription usage in seconds
    var dailyUsageSeconds: Int { usageTracker.dailyUsageSeconds }

    /// Number of models downloaded by the user
    var modelsDownloaded: Int { usageTracker.modelsDownloaded }

    /// Whether the daily limit has been reached
    var isDailyLimitReached: Bool { usageTracker.isDailyLimitReached }

    /// Whether the model download limit has been reached
    var isModelLimitReached: Bool { usageTracker.isModelLimitReached }

    /// Trial daily transcription limit (for UI display)
    var trialDailyTranscriptionLimit: Int { usageTracker.trialDailyTranscriptionLimit }

    /// Trial model download limit (for UI display)
    var trialModelDownloadLimit: Int { usageTracker.trialModelDownloadLimit }

    /// Formatted license status description with dynamic model limit
    ///
    /// For trial status, this returns a localized string with the current model limit.
    /// For other statuses, it returns the standard description from the enum.
    ///
    /// This ensures the UI always shows the correct model limit from the usage tracker
    /// instead of a hardcoded value.
    var licenseStatusDescription: String {
        if licenseStatus == .trial {
            return String(format: "license.status.trial.description".localized, trialModelDownloadLimit)
        }
        return licenseStatus.description
    }

    // MARK: - Components

    /// Shared key-value store backing the Rust license core. Created ONCE here
    /// and passed to both the network service and the usage tracker so the whole
    /// subsystem shares a single instance — and a single one-shot Core Data →
    /// UserDefaults usage seed (run in `RustLicenseStore.init`, before any usage
    /// call). This is load-bearing for backward compatibility.
    private let store = RustLicenseStore()

    /// Network service for license API calls
    private let networkService: LicenseNetworkService

    /// Usage tracker for trial limits
    let usageTracker: LicenseUsageTracker

    // MARK: - Initialization

    init() {
        networkService = LicenseNetworkService(store: store)
        usageTracker = LicenseUsageTracker(store: store)

        // CRITICAL: Forward objectWillChange from usageTracker to LicenseManager
        // This ensures SwiftUI views observing LicenseManager (@EnvironmentObject)
        // receive updates when usage tracker properties change (dailyUsageSeconds,
        // modelsDownloaded, isDailyLimitReached, isModelLimitReached).
        //
        // Without this forwarding:
        // - Views wouldn't update when usage limits change
        // - Trial limit banners wouldn't appear when limits reached
        // - Usage counters in Settings wouldn't update in real-time
        //
        // The cancellable is stored to keep the subscription alive for the
        // lifetime of the LicenseManager instance.
        usageTracker.objectWillChange.sink { [weak self] _ in
            self?.objectWillChange.send()
        }.store(in: &cancellables)

        // Load stored license and remote config on initialization
        Task {
            await loadStoredLicense()
            await usageTracker.refreshUsageStats()
            await loadRemoteConfig()
        }
    }

    /// Cancellables for subscriptions (e.g., usageTracker objectWillChange)
    private var cancellables = Set<AnyCancellable>()

    // MARK: - License Operations

    /// Activates a license key by validating it with the backend.
    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        isValidating = true
        lastError = nil
        defer { isValidating = false }

        let result = await networkService.activateLicense(licenseKey)
        await processValidationResult(result)
        return result
    }

    /// Deactivates the license locally (clears UserDefaults).
    func deactivateLicense() async -> Bool {
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
    func validateLicense(_ licenseKey: String) async -> LicenseValidationResult {
        isValidating = true
        lastError = nil
        defer { isValidating = false }

        let result = await networkService.validateLicense(licenseKey)
        await processValidationResult(result)

        return result
    }

    /// Loads stored license from UserDefaults, revalidates if cache expired (24h).
    func loadStoredLicense() async {
        guard let storedKey = networkService.getStoredLicenseKey() else {
            licenseStatus = .trial
            usageTracker.updateLicenseStatus(.trial)
            return
        }

        if networkService.shouldRevalidateLicense() {
            _ = await validateLicense(storedKey)
        } else if let cachedStatus = networkService.getCachedLicenseStatus() {
            licenseStatus = cachedStatus
            usageTracker.updateLicenseStatus(cachedStatus)
        }
    }

    /// Clears stored license and resets to trial mode.
    func clearLicense() {
        networkService.clearStoredLicense()
        licenseStatus = .trial
        customerEmail = nil
        customerName = nil
        lastError = nil
        usageTracker.updateLicenseStatus(.trial)
        NotificationCenter.default.post(name: .licenseStatusChanged, object: nil)
    }

    // MARK: - Remote Config

    /// Loads remote trial config: applies cached values immediately, then fetches fresh values.
    /// Non-blocking — uses defaults if no cache and no network.
    private func loadRemoteConfig() async {
        // Apply cached override immediately (if fresh, per the core's server-driven
        // TTL: stored Cache-Control max-age, default 6h, clamped to 24h).
        // The cached read goes through the Rust core's store, which reads the
        // legacy un-prefixed ConfigService keys via the alias layer until the
        // first core write self-heals them to the prefixed keys.
        if let cached = licenseRemoteOverrideIfFresh(
            store: store,
            nowUnixSecs: RustLicenseTime.nowUTC()
        ) {
            usageTracker.updateTrialLimits(
                dailySeconds: Int(cached.dailySeconds),
                modelLimit: Int(cached.modelDownloads)
            )
            return // Cache is fresh, no need to fetch
        }

        // Cache expired or missing — fetch from server (native GET kept).
        // `updateTrialLimits` persists the result back through the core's store.
        if let fetched = await ConfigService.shared.fetchConfig() {
            usageTracker.updateTrialLimits(
                dailySeconds: fetched.trialDailyLimitSeconds,
                modelLimit: fetched.trialModelDownloadLimit,
                maxAgeSecs: fetched.maxAgeSeconds,
                isLiveFetch: true
            )
        }
        // On failure, hardcoded defaults remain in effect
    }

    // MARK: - Private

    /// Updates UI state from validation result and posts notification.
    private func processValidationResult(_ result: LicenseValidationResult) {
        licenseStatus = result.status
        customerEmail = result.customerEmail
        customerName = result.customerName
        if !result.isValid { lastError = result.errorMessage }
        usageTracker.updateLicenseStatus(result.status)
        NotificationCenter.default.post(name: .licenseStatusChanged, object: nil)
    }

    // MARK: - Usage Tracking (delegated to LicenseUsageTracker)

    /// Checks if user can start recording based on daily limit
    func canStartRecording() -> Bool {
        return usageTracker.canStartRecording()
    }

    /// Records transcription time and updates usage
    func recordTranscriptionTime(_ seconds: Int) async {
        await usageTracker.recordTranscriptionTime(seconds)
    }

    /// Gets remaining daily transcription time in seconds
    func getRemainingDailyTime() -> Int {
        return usageTracker.getRemainingDailyTime()
    }

    /// Checks if user can download another model
    func canDownloadModel() -> Bool {
        return usageTracker.canDownloadModel()
    }

    /// Increments the model download count
    func incrementModelDownloadCount() async {
        await usageTracker.incrementModelDownloadCount()
    }

    /// Gets remaining model downloads
    func getRemainingModelDownloads() -> Int {
        return usageTracker.getRemainingModelDownloads()
    }

    /// Refreshes usage statistics from Core Data
    func refreshUsageStats() async {
        await usageTracker.refreshUsageStats()
    }

    // MARK: - Customer Portal

    /// Opens the user portal in browser for managing billing and credits
    func openCustomerPortal() {
        if let url = URL(string: "\(NetworkConfig.baseURL)/user") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Opens the purchase page
    func openPurchasePage() {
        if let url = URL(string: "\(NetworkConfig.baseURL)/checkout") {
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
