//
//  NetworkConfig.swift
//  hyperwhisper
//
//  NETWORK CONFIGURATION
//  Central configuration for all network-related settings and API endpoints.
//  This file provides a single source of truth for server URLs and network parameters.
//
//  IMPORTANT: This configuration is critical for the license system.
//  All API calls to the HyperWhisper backend should use these endpoints.
//

import Foundation

/// Network configuration for HyperWhisper API communication
struct NetworkConfig {
    
    // MARK: - Base Configuration
    
    /// Base URL for the HyperWhisper backend server
    /// Automatically switches between development and production environments
    static let baseURL = "https://www.hyperwhisper.com"
    
    /// API version for versioning support
    static let apiVersion = "v1"
    
    // MARK: - License Endpoints

    /// License validation endpoint
    /// POST /api/license/validate
    /// Body: { license_key: string, device_id: string, device_name: string }
    /// Response: { valid: boolean, error?: string }
    ///
    /// Device tracking for fair usage policy happens automatically when
    /// device_id is provided - the backend records the validation.
    static let licenseValidateEndpoint = "/api/license/validate"
    
    /// Endpoint for retrieving customer portal URL
    /// POST /api/license/portal
    /// Body: { customer_id: string }
    /// Response: { portal_url: string }
    static let customerPortalEndpoint = "/api/license/portal"

    // MARK: - HyperWhisper Cloud Endpoints

    /// Base URL for HyperWhisper Cloud transcription service (v2)
    /// Uses Fly.io for global edge-based processing
    static var hyperwhisperCloudURL: String {
        #if DEBUG
        // Use staging Fly.io app for testing
        return "https://transcribe-staging-v2.hyperwhisper.com"
        #else
        // Use production Fly.io app in release builds
        return "https://transcribe-prod-v2.hyperwhisper.com"
        #endif
    }

    /// Endpoint for querying usage/balance
    /// GET /usage?identifier=<device_id_or_license_key>
    /// Response: { credits_remaining, minutes_remaining, is_licensed, is_anonymous, resets_at? }
    static let hyperwhisperCloudCreditsEndpoint = "/usage"

    // MARK: - Streaming Endpoints (New)

    /// Streaming transcription endpoint - zero-buffer audio streaming
    /// POST /transcribe
    /// Headers: Content-Type: audio/*, Content-Length: required
    /// Query params: license_key OR device_id, language, mode, initial_prompt
    /// Body: Raw binary audio (NOT multipart)
    /// Response: { text, language, duration, cost: { usd, credits }, metadata }
    static let hyperwhisperCloudTranscribeEndpoint = "/transcribe"

    /// Standalone post-processing endpoint - text correction without transcription
    /// POST /post-process
    /// Content-Type: application/json
    /// Body: { text, prompt, license_key OR device_id }
    /// Response: { corrected, cost: { usd, credits } }
    static let hyperwhisperCloudPostProcessEndpoint = "/post-process"

    // MARK: - Model Catalog Endpoints

    // REMOVED: modelsEndpoint - No longer needed
    // Models are now downloaded directly from Hugging Face
    // See WhisperModelManager.swift for libwhisper.cpp models
    
    // MARK: - Network Timeouts
    
    /// Request timeout for standard API calls (in seconds)
    static let requestTimeout: TimeInterval = 30
    
    /// Extended timeout for file downloads (in seconds)
    static let downloadTimeout: TimeInterval = 300
    
    /// Timeout for license validation calls (in seconds)
    /// Shorter timeout to prevent blocking UI
    static let licenseValidationTimeout: TimeInterval = 10
    
    // MARK: - Caching and Retry Configuration
    
    /// Duration to cache successful license validation (24 hours)
    /// After this period, the license will be revalidated with the server
    static let validationCacheDuration: TimeInterval = 86400
    
    /// Grace period for offline usage (1 week)
    /// If the server cannot be reached, cached license remains valid for this duration
    static let offlineGracePeriod: TimeInterval = 604800 // 7 days
    
    /// Maximum number of retry attempts for failed requests
    static let maxRetryAttempts = 3
    
    /// Delay between retry attempts (in seconds)
    static let retryDelay: TimeInterval = 2
    
    // MARK: - Helper Methods
    
    /// Constructs a full URL for an API endpoint
    /// - Parameter endpoint: The endpoint path (e.g., "/api/license/validate")
    /// - Returns: Complete URL string
    static func fullURL(for endpoint: String) -> String {
        return "\(baseURL)\(endpoint)"
    }
    
    /// Constructs a URLRequest with common headers
    /// - Parameters:
    ///   - endpoint: The API endpoint
    ///   - method: HTTP method (default: "POST")
    ///   - timeout: Request timeout (uses default if not specified)
    /// - Returns: Configured URLRequest
    static func createRequest(
        for endpoint: String,
        method: String = "POST",
        timeout: TimeInterval? = nil
    ) -> URLRequest? {
        guard let url = URL(string: fullURL(for: endpoint)) else { return nil }

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("HyperWhisper/1.0", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = timeout ?? requestTimeout

        return request
    }

    // MARK: - Environment Detection
    
    /// Determines if the app is running in development mode
    static var isDevelopment: Bool {
        #if DEBUG
        return true
        #else
        return false
        #endif
    }
}
