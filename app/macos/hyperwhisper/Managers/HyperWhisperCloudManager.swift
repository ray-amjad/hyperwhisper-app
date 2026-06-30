//
//  HyperWhisperCloudManager.swift
//  hyperwhisper
//
//  HYPERWHISPER CLOUD MANAGER
//  Manages credit balance fetching and caching for HyperWhisper Cloud service.
//
//  RESPONSIBILITIES:
//  - Fetch credit balance from Cloudflare Worker
//  - Cache balance locally to reduce API calls
//  - Provide UI-friendly credit information
//  - Handle errors and network failures gracefully
//
//  USAGE:
//  - Called by settings UI to display credit balance
//  - Called by HyperWhisperCloudProvider before transcription
//  - Automatically refreshes stale cache
//

import Foundation
import SwiftUI

/// Manager for HyperWhisper Cloud credit operations
@MainActor
class HyperWhisperCloudManager: ObservableObject {

    // MARK: - Published Properties

    /// Current credit balance (nil if not yet fetched)
    @Published var credits: HyperWhisperCloudCredits?

    /// Whether a credit fetch is in progress
    @Published var isFetchingCredits: Bool = false

    /// Last error encountered during credit fetch
    @Published var lastError: String?

    // MARK: - Private Properties

    /// License manager for getting device ID / license key
    private let licenseManager: LicenseManager

    /// URLSession for API calls
    private let session: URLSession

    /// Cache timestamp to avoid excessive API calls
    private var lastFetchTime: Date?

    /// Cache duration (60 seconds)
    private let cacheDuration: TimeInterval = 60

    /// Observer token for license status changes
    private var licenseStatusObserver: NSObjectProtocol?

    // MARK: - Initialization

    init(licenseManager: LicenseManager) {
        self.licenseManager = licenseManager

        // Configure URLSession with short timeout for credit checks
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 10.0
        config.timeoutIntervalForResource = 15.0
        self.session = URLSession(configuration: config)

        // OBSERVER SETUP: Listen for license status changes
        // When the user activates or deactivates a license, we need to invalidate our credit cache
        // because the identifier changes (license_key ↔ device_id), which means different credit pools
        //
        // Flow:
        // 1. User deactivates license in LicenseSettingsSection
        // 2. LicenseManager.clearLicense() posts .licenseStatusChanged notification
        // 3. This observer receives the notification
        // 4. We invalidate the cache by setting lastFetchTime = nil
        // 5. Next credit fetch will query the server with the new identifier (device_id)
        // 6. UI shows correct trial credits instead of stale licensed credits
        setupLicenseObserver()
    }

    /// Sets up observer for license status changes
    /// This ensures credit cache is invalidated when license status changes
    private func setupLicenseObserver() {
        licenseStatusObserver = NotificationCenter.default.addObserver(
            forName: .licenseStatusChanged,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self = self else { return }

            // CACHE INVALIDATION: License status changed, so identifier changed
            // We must invalidate our cached credits because they're tied to the old identifier
            //
            // Run on MainActor since HyperWhisperCloudManager is @MainActor
            // and we're modifying @Published properties
            Task { @MainActor in
                self.invalidateCache()

                // Clear the credits display immediately to avoid showing stale data
                // This provides instant feedback that the license status change affected credits
                self.credits = nil

                AppLogger.network.info("HyperWhisper Cloud cache invalidated due to license status change")
            }
        }
    }

    deinit {
        // Clean up observer when manager is deallocated
        if let licenseStatusObserver {
            NotificationCenter.default.removeObserver(licenseStatusObserver)
        }
    }

    // MARK: - Public Methods

    /// Fetches credit balance from HyperWhisper Cloud
    /// Uses cache if recent fetch exists (within cacheDuration)
    ///
    /// - Parameter forceRefresh: If true, bypass cache and fetch fresh data
    /// - Returns: Credit balance or throws error
    func fetchCredits(forceRefresh: Bool = false) async throws -> HyperWhisperCloudCredits {
        // Check cache first
        if !forceRefresh,
           let cachedCredits = credits,
           let lastFetch = lastFetchTime,
           Date().timeIntervalSince(lastFetch) < cacheDuration {
            AppLogger.network.debug("Using cached HyperWhisper Cloud credits")
            return cachedCredits
        }

        // Update UI state
        isFetchingCredits = true
        lastError = nil

        defer {
            isFetchingCredits = false
        }

        do {
            // Get identifier from license manager
            let (identifier, _) = licenseManager.getTranscriptionIdentifier()

            // Build request URL with optional force_refresh parameter
            // When forceRefresh is true, the backend will bypass its cache and fetch fresh data
            var urlString = "\(NetworkConfig.hyperwhisperCloudURL)\(NetworkConfig.hyperwhisperCloudCreditsEndpoint)?identifier=\(identifier)"
            if forceRefresh {
                urlString += "&force_refresh=true"
            }
            guard let url = URL(string: urlString) else {
                throw HyperWhisperCloudError.invalidResponse("Invalid URL")
            }

            var request = URLRequest(url: url)
            request.httpMethod = "GET"
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue("HyperWhisper/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")",
                           forHTTPHeaderField: "User-Agent")

            // Do NOT log the full URL: for licensed users the `identifier` query param is the
            // raw license key (a paid-service bearer credential), and `.public` os_log entries
            // persist to disk / sysdiagnose. Log host + forceRefresh only.
            let logHost = url.host() ?? "?"
            AppLogger.network.info("Fetching HyperWhisper Cloud credits · host=\(logHost, privacy: .public) · forceRefresh=\(forceRefresh, privacy: .public)")

            // Perform request
            let (data, response) = try await session.data(for: request)

            // Check HTTP status
            guard let httpResponse = response as? HTTPURLResponse else {
                throw HyperWhisperCloudError.invalidResponse("Invalid response")
            }

            guard httpResponse.statusCode == 200 else {
                // Try to parse error message
                if let errorJson = try? JSONDecoder().decode([String: String].self, from: data),
                   let errorMessage = errorJson["message"] ?? errorJson["error"] {
                    throw HyperWhisperCloudError.serverError(errorMessage)
                }
                throw HyperWhisperCloudError.serverError("HTTP \(httpResponse.statusCode)")
            }

            // Decode response
            let decoder = JSONDecoder()
            let fetchedCredits = try decoder.decode(HyperWhisperCloudCredits.self, from: data)

            // Update cache
            self.credits = fetchedCredits
            self.lastFetchTime = Date()

            AppLogger.network.info("HyperWhisper Cloud credits fetched · remaining=\(fetchedCredits.creditsRemaining, privacy: .public) · minutes=\(fetchedCredits.minutesRemaining, privacy: .public)")

            return fetchedCredits

        } catch let error as HyperWhisperCloudError {
            lastError = error.localizedDescription
            throw error
        } catch {
            let errorMessage = "Failed to fetch credits: \(error.localizedDescription)"
            lastError = errorMessage
            AppLogger.network.error("HyperWhisper Cloud credit fetch failed · error=\(error.localizedDescription, privacy: .public)")
            if error is URLError {
                throw HyperWhisperCloudError.transientNetwork(error.localizedDescription)
            }
            if error is DecodingError {
                throw HyperWhisperCloudError.invalidResponse(error.localizedDescription)
            }
            throw HyperWhisperCloudError.transientNetwork(error.localizedDescription)
        }
    }

    /// Refreshes credit balance (bypasses cache)
    func refreshCredits() async {
        do {
            _ = try await fetchCredits(forceRefresh: true)
        } catch {
            // Error already logged and stored in lastError
        }
    }

    /// Checks if user has sufficient credits for estimated audio duration
    ///
    /// - Parameter estimatedMinutes: Estimated audio duration in minutes
    /// - Returns: True if sufficient credits, false otherwise
    func hasSufficientCredits(for estimatedMinutes: Int) -> Bool {
        guard let credits = credits else {
            // If we don't have credit info yet, assume sufficient
            // The actual check will happen on the server
            return true
        }

        let requiredCredits = Double(estimatedMinutes) * 10.0
        return credits.creditsRemaining >= requiredCredits
    }

    /// Invalidates the credit cache (forces next fetch to retrieve fresh data)
    func invalidateCache() {
        lastFetchTime = nil
        AppLogger.network.debug("HyperWhisper Cloud credit cache invalidated")
    }

    /// Returns formatted credit balance for UI display
    var formattedBalance: String {
        guard let credits = credits else {
            return "Loading..."
        }
        return credits.formattedBalance
    }
}
