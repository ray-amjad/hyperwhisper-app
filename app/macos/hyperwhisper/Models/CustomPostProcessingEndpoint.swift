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
        guard let uuidString = llmParseCustomProviderString(providerString: providerString) else { return nil }
        return UUID(uuidString: uuidString)
    }

    /// Check if a provider string represents a custom endpoint
    /// - Parameter providerString: The provider string to check
    /// - Returns: true if this is a custom provider string
    static func isCustomProviderString(_ providerString: String) -> Bool {
        llmIsCustomProviderString(providerString: providerString)
    }
}

// MARK: - Validation

extension CustomPostProcessingEndpoint {
    /// Validation errors for endpoint configuration
    enum ValidationError: LocalizedError {
        case emptyName
        /// A rule break reported by the shared core, already worded for the user.
        case rejected(String)

        var errorDescription: String? {
            switch self {
            case .emptyName:
                return "Name is required"
            case let .rejected(message):
                return message
            }
        }
    }

    /// Validate the endpoint configuration
    ///
    /// The URL and model rules come from the shared core (issue #282). This used
    /// to accept any string `URL(string:)` accepted — which includes a bare
    /// `"localhost:11434"` with no scheme — while the Windows app demanded an
    /// absolute URI and the .NET runtime added scheme, userinfo, fragment and
    /// length rules on top. Four answers to one question, and a backup carried
    /// endpoints between them.
    ///
    /// Saving is STRICT. An endpoint already on disk is judged leniently by
    /// `llmValidateExistingCustomEndpoint`, so a tightened rule repairs it
    /// instead of deleting it.
    ///
    /// - Throws: ValidationError if configuration is invalid
    func validate() throws {
        // VALIDATION STEP 1: Check name
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ValidationError.emptyName
        }

        // VALIDATION STEP 2: One rule for the URL and the model.
        let verdict = llmNormalizeCustomEndpoint(raw: endpointURL, model: modelName, mode: .strict)
        guard verdict.status == .valid else {
            throw ValidationError.rejected(verdict.message ?? "Invalid URL format")
        }
    }
}
