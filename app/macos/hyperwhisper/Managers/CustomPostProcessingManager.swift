//
//  CustomPostProcessingManager.swift
//  hyperwhisper
//
//  CUSTOM POST-PROCESSING ENDPOINT MANAGER
//  Manages user-configured OpenAI-compatible API endpoints for post-processing.
//
//  This manager handles:
//  - CRUD operations for custom endpoints (stored in UserDefaults as JSON)
//  - API key storage via Keychain
//  - Endpoint testing with a simple "Hello World" request
//  - Integration with the post-processing provider system
//
//  Architecture:
//  - Endpoints stored as JSON array in UserDefaults (simple, no Core Data migration needed)
//  - API keys stored in Keychain via KeychainManager (secure)
//  - Published properties for SwiftUI reactivity
//  - MainActor for thread safety
//

import Foundation
import Combine
import os

// MARK: - Test Result

/// Result of testing a custom endpoint
enum CustomEndpointTestResult {
    case success(responsePreview: String)
    case failure(error: String)
}

// MARK: - Manager

/// Manages custom OpenAI-compatible endpoints for post-processing
///
/// USAGE:
/// - Inject as @EnvironmentObject in SwiftUI views
/// - Use `endpoints` property to display list of configured endpoints
/// - Call CRUD methods to add/update/delete endpoints
/// - Call `testEndpoint(id:)` to verify an endpoint works
@MainActor
class CustomPostProcessingManager: ObservableObject {
    // MARK: - Published State

    /// All configured custom endpoints
    @Published private(set) var endpoints: [CustomPostProcessingEndpoint] = []

    /// Currently testing endpoint IDs
    @Published private(set) var testingEndpoints: Set<UUID> = []

    /// Error message for display (nil if no error)
    @Published var errorMessage: String?

    // MARK: - Private

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "CustomPostProcessingManager")

    /// UserDefaults key for storing endpoints
    private let storageKey = "customPostProcessingEndpoints"

    // MARK: - Initialization

    init() {
        loadEndpoints()
        logger.info("CustomPostProcessingManager initialized with \(self.endpoints.count, privacy: .public) endpoints")
    }

    // MARK: - Public API - CRUD

    /// Add a new custom endpoint
    /// - Parameters:
    ///   - name: Display name for the endpoint
    ///   - endpointURL: Full URL to the chat completions endpoint
    ///   - modelName: Model identifier to use
    ///   - apiKey: Optional API key (stored in Keychain)
    /// - Returns: The created endpoint
    /// - Throws: Validation error if configuration is invalid
    @discardableResult
    func addEndpoint(
        name: String,
        endpointURL: String,
        modelName: String,
        apiKey: String? = nil
    ) throws -> CustomPostProcessingEndpoint {
        // ADD ENDPOINT - STEP 1: Create the endpoint
        var endpoint = CustomPostProcessingEndpoint(
            name: name.trimmingCharacters(in: .whitespacesAndNewlines),
            endpointURL: endpointURL.trimmingCharacters(in: .whitespacesAndNewlines),
            modelName: modelName.trimmingCharacters(in: .whitespacesAndNewlines)
        )

        // STEP 2: Validate the endpoint
        try endpoint.validate()

        // STEP 3: Save API key to Keychain if provided
        if let apiKey = apiKey, !apiKey.isEmpty {
            try KeychainManager.shared.saveCustomEndpointAPIKey(apiKey, for: endpoint.id)
        }

        // STEP 4: Add to list and save
        endpoints.append(endpoint)
        saveEndpoints()

        logger.info("Added custom endpoint: \(endpoint.name, privacy: .public)")
        return endpoint
    }

    /// Update an existing custom endpoint
    /// - Parameters:
    ///   - id: ID of the endpoint to update
    ///   - name: New display name (optional)
    ///   - endpointURL: New URL (optional)
    ///   - modelName: New model name (optional)
    ///   - apiKey: New API key (optional, pass empty string to clear)
    /// - Throws: Error if endpoint not found or validation fails
    func updateEndpoint(
        id: UUID,
        name: String? = nil,
        endpointURL: String? = nil,
        modelName: String? = nil,
        apiKey: String? = nil
    ) throws {
        // UPDATE ENDPOINT - STEP 1: Find the endpoint
        guard let index = endpoints.firstIndex(where: { $0.id == id }) else {
            throw CustomEndpointError.endpointNotFound
        }

        // STEP 2: Apply updates
        if let name = name {
            endpoints[index].name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if let endpointURL = endpointURL {
            let newURL = endpointURL.trimmingCharacters(in: .whitespacesAndNewlines)
            if newURL != endpoints[index].endpointURL {
                endpoints[index].endpointURL = newURL
                // Clear test status when URL changes
                endpoints[index].clearTestStatus()
            }
        }
        if let modelName = modelName {
            endpoints[index].modelName = modelName.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        // STEP 3: Validate the updated endpoint
        try endpoints[index].validate()

        // STEP 4: Update API key if provided
        if let apiKey = apiKey {
            if apiKey.isEmpty {
                try? KeychainManager.shared.deleteCustomEndpointAPIKey(for: id)
            } else {
                try KeychainManager.shared.saveCustomEndpointAPIKey(apiKey, for: id)
            }
        }

        // STEP 5: Save changes
        saveEndpoints()

        logger.info("Updated custom endpoint: \(self.endpoints[index].name, privacy: .public)")
    }

    /// Delete a custom endpoint
    /// - Parameter id: ID of the endpoint to delete
    func deleteEndpoint(id: UUID) {
        // DELETE ENDPOINT - STEP 1: Find and remove the endpoint
        guard let index = endpoints.firstIndex(where: { $0.id == id }) else {
            logger.warning("Attempted to delete non-existent endpoint: \(id.uuidString, privacy: .public)")
            return
        }

        let name = endpoints[index].name

        // STEP 2: Remove from list
        endpoints.remove(at: index)

        // STEP 3: Delete API key from Keychain
        try? KeychainManager.shared.deleteCustomEndpointAPIKey(for: id)

        // STEP 4: Save changes
        saveEndpoints()

        logger.info("Deleted custom endpoint: \(name, privacy: .public)")
    }

    /// Duplicate a custom endpoint with smart numbering
    ///
    /// DUPLICATION FLOW:
    /// This creates an exact copy of an existing endpoint with a new UUID and smart numbered name.
    /// The duplicate preserves all settings including test status, since the URL and model are identical.
    ///
    /// SMART NUMBERING:
    /// - "Name" → "Name (copy)"
    /// - "Name (copy)" → "Name (copy 2)"
    /// - "Name (copy 2)" → "Name (copy 3)"
    ///
    /// API KEY HANDLING:
    /// - Securely copies API key from original UUID to new UUID in Keychain
    /// - If API key copy fails (non-critical), clears test status and continues
    /// - User can manually add API key later via Edit button
    ///
    /// - Parameter id: ID of the endpoint to duplicate
    /// - Returns: The newly created endpoint
    /// - Throws: CustomEndpointError.endpointNotFound if original endpoint not found
    @discardableResult
    func duplicateEndpoint(id: UUID) throws -> CustomPostProcessingEndpoint {
        // DUPLICATE ENDPOINT - STEP 1: Find the original endpoint
        guard let index = endpoints.firstIndex(where: { $0.id == id }) else {
            logger.warning("Attempted to duplicate non-existent endpoint: \(id.uuidString, privacy: .public)")
            throw CustomEndpointError.endpointNotFound
        }

        let original = endpoints[index]

        // STEP 2: Generate smart numbered name
        // Uses pattern matching to detect existing copy suffixes
        let newName = generateCopyName(from: original.name)

        // STEP 3: Create new endpoint with copied properties
        // IMPORTANT: New UUID prevents ID conflicts
        // IMPORTANT: Test status is copied to preserve verification state
        var newEndpoint = CustomPostProcessingEndpoint(
            name: newName,
            endpointURL: original.endpointURL,
            modelName: original.modelName
        )

        // COPY TEST STATUS:
        // Preserve verification state since URL and model are identical
        // Users don't need to re-test if original was already verified
        newEndpoint.lastTestedAt = original.lastTestedAt
        newEndpoint.lastTestSuccess = original.lastTestSuccess

        // STEP 4: Copy API key from Keychain
        // SECURITY: Reading from old UUID, writing to new UUID
        // Original key remains untouched and secure
        let apiKey = getAPIKey(for: id)
        if !apiKey.isEmpty {
            do {
                try KeychainManager.shared.saveCustomEndpointAPIKey(apiKey, for: newEndpoint.id)
                logger.info("Successfully copied API key for duplicated endpoint")
            } catch {
                // NON-CRITICAL ERROR: API key copy failed
                // Continue with duplication - user can add key manually via Edit
                logger.warning("Failed to copy API key for duplicated endpoint: \(error.localizedDescription, privacy: .public)")
                // Clear test status since endpoint may not work without API key
                newEndpoint.clearTestStatus()
            }
        }

        // STEP 5: Add to list and save
        endpoints.append(newEndpoint)
        saveEndpoints()

        logger.info("Duplicated custom endpoint: \(original.name, privacy: .public) → \(newName, privacy: .public)")
        return newEndpoint
    }

    /// Get a custom endpoint by ID
    /// - Parameter id: ID of the endpoint
    /// - Returns: The endpoint if found, nil otherwise
    func getEndpoint(id: UUID) -> CustomPostProcessingEndpoint? {
        endpoints.first { $0.id == id }
    }

    /// Get API key for a custom endpoint
    /// - Parameter id: ID of the endpoint
    /// - Returns: The API key if set, empty string otherwise
    func getAPIKey(for id: UUID) -> String {
        KeychainManager.shared.getCustomEndpointAPIKey(for: id)
    }

    /// Check if an endpoint has an API key configured
    /// - Parameter id: ID of the endpoint
    /// - Returns: true if API key is configured
    func hasAPIKey(for id: UUID) -> Bool {
        KeychainManager.shared.hasCustomEndpointAPIKey(for: id)
    }

    // MARK: - Public API - Testing

    /// Test a custom endpoint with a simple "Hello World" request
    /// - Parameter id: ID of the endpoint to test
    /// - Returns: Test result with success/failure details
    func testEndpoint(id: UUID) async -> CustomEndpointTestResult {
        // TEST ENDPOINT - STEP 1: Find the endpoint
        guard let index = endpoints.firstIndex(where: { $0.id == id }) else {
            return .failure(error: "Endpoint not found")
        }

        let endpoint = endpoints[index]

        // STEP 2: Mark as testing
        testingEndpoints.insert(id)
        defer { testingEndpoints.remove(id) }

        // STEP 3: Build the test request
        guard let url = URL(string: endpoint.endpointURL) else {
            updateTestStatus(for: id, success: false)
            return .failure(error: "Invalid URL")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        // STEP 4: Add authentication if API key is set
        let apiKey = getAPIKey(for: id)
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }

        // STEP 5: Build minimal OpenAI-compatible request body
        // TEST REQUEST STRUCTURE:
        // This sends a simple "Say hello" prompt to verify the endpoint works
        // We use minimal tokens to reduce cost/time for testing
        let requestBody: [String: Any] = [
            "model": endpoint.modelName,
            "messages": [
                ["role": "user", "content": "Say hello in one word."]
            ],
            "max_tokens": 10,
            "temperature": 0.0
        ]

        do {
            request.httpBody = try JSONSerialization.data(withJSONObject: requestBody)
        } catch {
            updateTestStatus(for: id, success: false)
            return .failure(error: "Failed to encode request: \(error.localizedDescription)")
        }

        // STEP 6: Send the request
        logger.info("Testing endpoint: \(endpoint.name, privacy: .public) at \(endpoint.displayURL, privacy: .public)")

        do {
            let (data, response) = try await URLSession.shared.data(for: request)

            // STEP 7: Check HTTP response
            guard let httpResponse = response as? HTTPURLResponse else {
                updateTestStatus(for: id, success: false)
                return .failure(error: "Invalid response type")
            }

            // STEP 8: Handle error status codes
            if httpResponse.statusCode != 200 {
                let errorBody = String(data: data, encoding: .utf8) ?? "No error details"
                logger.warning("Endpoint test failed with status \(httpResponse.statusCode, privacy: .public): \(errorBody, privacy: .public)")
                updateTestStatus(for: id, success: false)
                return .failure(error: "HTTP \(httpResponse.statusCode): \(parseErrorMessage(from: data) ?? "Unknown error")")
            }

            // STEP 9: Parse successful response
            // RESPONSE PARSING:
            // OpenAI-compatible endpoints return: { "choices": [{ "message": { "content": "..." } }] }
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let choices = json["choices"] as? [[String: Any]],
                  let firstChoice = choices.first,
                  let message = firstChoice["message"] as? [String: Any],
                  let content = message["content"] as? String else {
                updateTestStatus(for: id, success: false)
                return .failure(error: "Invalid response format - expected OpenAI-compatible response")
            }

            // STEP 10: Success!
            let preview = String(content.prefix(50))
            logger.info("Endpoint test succeeded: \(endpoint.name, privacy: .public) - response: \(preview, privacy: .public)")
            updateTestStatus(for: id, success: true)
            return .success(responsePreview: content)

        } catch let error as URLError {
            logger.error("Endpoint test network error: \(error.localizedDescription, privacy: .public)")
            updateTestStatus(for: id, success: false)
            return .failure(error: "Network error: \(error.localizedDescription)")
        } catch {
            logger.error("Endpoint test error: \(error.localizedDescription, privacy: .public)")
            updateTestStatus(for: id, success: false)
            return .failure(error: error.localizedDescription)
        }
    }

    /// Check if an endpoint is currently being tested
    /// - Parameter id: ID of the endpoint
    /// - Returns: true if the endpoint is being tested
    func isTesting(id: UUID) -> Bool {
        testingEndpoints.contains(id)
    }

    // MARK: - Private Methods

    /// Load endpoints from UserDefaults
    private func loadEndpoints() {
        // LOAD ENDPOINTS - STEP 1: Get JSON data from UserDefaults
        guard let data = UserDefaults.standard.data(forKey: storageKey) else {
            logger.debug("No saved custom endpoints found")
            endpoints = []
            return
        }

        // STEP 2: Decode JSON to endpoint array
        do {
            let decoder = JSONDecoder()
            endpoints = try decoder.decode([CustomPostProcessingEndpoint].self, from: data)
            logger.info("Loaded \(self.endpoints.count, privacy: .public) custom endpoints")
        } catch {
            logger.error("Failed to decode custom endpoints: \(error.localizedDescription, privacy: .public)")
            endpoints = []
        }
    }

    /// Save endpoints to UserDefaults
    private func saveEndpoints() {
        // SAVE ENDPOINTS - STEP 1: Encode endpoints to JSON
        do {
            let encoder = JSONEncoder()
            let data = try encoder.encode(endpoints)

            // STEP 2: Save to UserDefaults
            UserDefaults.standard.set(data, forKey: storageKey)

            logger.debug("Saved \(self.endpoints.count, privacy: .public) custom endpoints")
        } catch {
            logger.error("Failed to encode custom endpoints: \(error.localizedDescription, privacy: .public)")
            errorMessage = "Failed to save endpoints: \(error.localizedDescription)"
        }
    }

    /// Update test status for an endpoint
    private func updateTestStatus(for id: UUID, success: Bool) {
        guard let index = endpoints.firstIndex(where: { $0.id == id }) else { return }
        endpoints[index].updateTestStatus(success: success)
        saveEndpoints()
    }

    /// Parse error message from API error response
    private func parseErrorMessage(from data: Data) -> String? {
        // Try to parse OpenAI-style error response: { "error": { "message": "..." } }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let error = json["error"] as? [String: Any],
              let message = error["message"] as? String else {
            return nil
        }
        return message
    }

    /// Generate smart numbered copy name for duplicating an endpoint
    ///
    /// SMART NUMBERING ALGORITHM:
    /// This uses regex pattern matching to detect existing copy suffixes and increment them intelligently.
    ///
    /// Pattern: `\s\(copy(?:\s(\d+))?\)$`
    /// - Matches " (copy)" or " (copy N)" at the END of the string
    /// - Case sensitive (won't match "(COPY)" or "(Copy)")
    /// - Only matches at end of string (won't match "Name (copy) Extra")
    ///
    /// Examples:
    /// - "Name" → "Name (copy)"
    /// - "Name (copy)" → "Name (copy 2)"
    /// - "Name (copy 2)" → "Name (copy 3)"
    /// - "Name (copy 99)" → "Name (copy 100)"
    ///
    /// Why this works:
    /// - Each iteration produces a unique suffix increment
    /// - No collision checking needed (names are non-unique by design)
    /// - Simple, predictable, and user-friendly
    ///
    /// - Parameter originalName: The name to create a copy of
    /// - Returns: A new name with incremented copy suffix
    private func generateCopyName(from originalName: String) -> String {
        // GENERATE COPY NAME - Regex pattern: matches " (copy)" or " (copy N)" at end of string
        let copyPattern = #/\s\(copy(?:\s(\d+))?\)$/#

        if let match = originalName.firstMatch(of: copyPattern) {
            // Extract base name without copy suffix
            let baseName = String(originalName.prefix(upTo: match.range.lowerBound))

            if let numberCapture = match.1, let number = Int(numberCapture) {
                // "Name (copy 2)" → "Name (copy 3)"
                return "\(baseName) (copy \(number + 1))"
            } else {
                // "Name (copy)" → "Name (copy 2)"
                return "\(baseName) (copy 2)"
            }
        } else {
            // First copy: "Name" → "Name (copy)"
            return "\(originalName) (copy)"
        }
    }
}

// MARK: - Errors

/// Errors that can occur during custom endpoint operations
enum CustomEndpointError: LocalizedError {
    case endpointNotFound
    case invalidConfiguration(String)

    var errorDescription: String? {
        switch self {
        case .endpointNotFound:
            return "Custom endpoint not found"
        case .invalidConfiguration(let message):
            return "Invalid configuration: \(message)"
        }
    }
}

// MARK: - Provider String Helpers

extension CustomPostProcessingManager {
    /// Parse a provider string and return the custom endpoint if it's a custom provider
    /// - Parameter providerString: The provider string from Mode settings
    /// - Returns: The custom endpoint if found, nil if not a custom provider or not found
    func endpointFromProviderString(_ providerString: String) -> CustomPostProcessingEndpoint? {
        guard let endpointId = CustomPostProcessingEndpoint.parseCustomProviderString(providerString) else {
            return nil
        }
        return getEndpoint(id: endpointId)
    }

    /// Check if a provider string represents a valid custom endpoint
    /// - Parameter providerString: The provider string to check
    /// - Returns: true if this is a valid custom endpoint
    func isValidCustomProvider(_ providerString: String) -> Bool {
        guard let endpointId = CustomPostProcessingEndpoint.parseCustomProviderString(providerString) else {
            return false
        }
        return getEndpoint(id: endpointId) != nil
    }
}
