//
//  CustomPostProcessingEndpoint.swift
//  hyperwhisper
//
//  CUSTOM POST-PROCESSING ENDPOINT MODEL
//  Represents a user-configured OpenAI-compatible API endpoint for post-processing.
//
//  This model allows users to add their own cloud-based LLM endpoints (like Ollama,
//  LM Studio, or any OpenAI-compatible API) for text post-processing.
//
//  Key Features:
//  - Each endpoint is a single URL + model combination
//  - Users can duplicate endpoints to use multiple models from the same server
//  - API keys are stored separately in Keychain for security
//  - Tracks test status to show users if the endpoint is working
//

import Foundation

/// Represents a custom OpenAI-compatible endpoint for post-processing
///
/// ARCHITECTURE NOTES:
/// - Stored as JSON array in UserDefaults (not Core Data) for simplicity
/// - API keys are NOT stored here - they go in Keychain via KeychainManager
/// - The `id` is used to link to the corresponding Keychain entry
/// - Test status is persisted to show users last known state in UI
struct CustomPostProcessingEndpoint: Codable, Identifiable, Equatable {
    // MARK: - Properties

    /// Unique identifier for this endpoint configuration
    /// Used to link API keys in Keychain and to reference in Mode settings
    let id: UUID

    /// User-defined display name for this endpoint
    /// Example: "My Ollama Server", "LM Studio Local", "OpenRouter GPT-4"
    var name: String

    /// Full URL to the OpenAI-compatible chat completions endpoint
    /// Example: "http://localhost:11434/v1/chat/completions"
    /// Example: "https://api.openrouter.ai/v1/chat/completions"
    var endpointURL: String

    /// Model identifier to use with this endpoint
    /// Example: "llama3.2", "gpt-4", "mistral-7b-instruct"
    var modelName: String

    /// When this endpoint configuration was created
    let createdAt: Date

    /// When the endpoint was last tested (nil if never tested)
    var lastTestedAt: Date?

    /// Result of the last test (nil if never tested)
    /// true = test succeeded, false = test failed
    var lastTestSuccess: Bool?

    // MARK: - Computed Properties

    /// Provider string used in Mode settings storage
    /// Format: "custom:<uuid>" to distinguish from built-in providers
    var providerString: String {
        "custom:\(id.uuidString)"
    }

    /// Shortened URL for display in UI (removes protocol, truncates long paths)
    var displayURL: String {
        var display = endpointURL
            .replacingOccurrences(of: "https://", with: "")
            .replacingOccurrences(of: "http://", with: "")

        // Truncate if too long
        if display.count > 40 {
            display = String(display.prefix(37)) + "..."
        }
        return display
    }

    /// Whether the endpoint has been tested and passed
    var isVerified: Bool {
        lastTestSuccess == true
    }

    // MARK: - Initialization

    /// Create a new custom endpoint configuration
    /// - Parameters:
    ///   - name: Display name for the endpoint
    ///   - endpointURL: Full URL to the chat completions endpoint
    ///   - modelName: Model identifier to use
    init(name: String, endpointURL: String, modelName: String) {
        self.id = UUID()
        self.name = name
        self.endpointURL = endpointURL
        self.modelName = modelName
        self.createdAt = Date()
        self.lastTestedAt = nil
        self.lastTestSuccess = nil
    }

    // MARK: - Mutating Methods

    /// Update the test status after running a test
    /// - Parameter success: Whether the test succeeded
    mutating func updateTestStatus(success: Bool) {
        self.lastTestedAt = Date()
        self.lastTestSuccess = success
    }

    /// Clear the test status (e.g., when endpoint URL changes)
    mutating func clearTestStatus() {
        self.lastTestedAt = nil
        self.lastTestSuccess = nil
    }
}

// MARK: - Parsing Helpers

extension CustomPostProcessingEndpoint {
    /// Parse a provider string to extract the custom endpoint UUID
    /// - Parameter providerString: The provider string (e.g., "custom:uuid-here")
    /// - Returns: The UUID if this is a custom provider string, nil otherwise
    static func parseCustomProviderString(_ providerString: String) -> UUID? {
        guard providerString.hasPrefix("custom:") else { return nil }
        let uuidString = String(providerString.dropFirst(7)) // "custom:" is 7 chars
        return UUID(uuidString: uuidString)
    }

    /// Check if a provider string represents a custom endpoint
    /// - Parameter providerString: The provider string to check
    /// - Returns: true if this is a custom provider string
    static func isCustomProviderString(_ providerString: String) -> Bool {
        providerString.hasPrefix("custom:")
    }
}

// MARK: - Validation

extension CustomPostProcessingEndpoint {
    /// Validation errors for endpoint configuration
    enum ValidationError: LocalizedError {
        case emptyName
        case emptyURL
        case invalidURL
        case emptyModelName

        var errorDescription: String? {
            switch self {
            case .emptyName:
                return "Name is required"
            case .emptyURL:
                return "Endpoint URL is required"
            case .invalidURL:
                return "Invalid URL format"
            case .emptyModelName:
                return "Model name is required"
            }
        }
    }

    /// Validate the endpoint configuration
    /// - Throws: ValidationError if configuration is invalid
    func validate() throws {
        // VALIDATION STEP 1: Check name
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ValidationError.emptyName
        }

        // VALIDATION STEP 2: Check URL is not empty
        let trimmedURL = endpointURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedURL.isEmpty else {
            throw ValidationError.emptyURL
        }

        // VALIDATION STEP 3: Check URL is valid
        guard URL(string: trimmedURL) != nil else {
            throw ValidationError.invalidURL
        }

        // VALIDATION STEP 4: Check model name
        guard !modelName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ValidationError.emptyModelName
        }
    }

    /// Check if the endpoint configuration is valid
    var isValid: Bool {
        do {
            try validate()
            return true
        } catch {
            return false
        }
    }
}
