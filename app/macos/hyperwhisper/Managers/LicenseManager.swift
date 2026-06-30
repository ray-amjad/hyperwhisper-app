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

    /// Formatted license status description (Cloud license state).
    var licenseStatusDescription: String {
        licenseStatus.description
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
            return
        }

        if networkService.shouldRevalidateLicense() {
            _ = await validateLicense(storedKey)
        } else if let cachedStatus = networkService.getCachedLicenseStatus() {
            licenseStatus = cachedStatus
        }
    }

    /// Clears stored license and resets to the unlicensed (trial) state.
    func clearLicense() {
        networkService.clearStoredLicense()
        licenseStatus = .trial
        customerEmail = nil
        customerName = nil
        lastError = nil
        NotificationCenter.default.post(name: .licenseStatusChanged, object: nil)
    }

    // MARK: - Private

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
