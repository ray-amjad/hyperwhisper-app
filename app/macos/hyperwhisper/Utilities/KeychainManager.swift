//
//  KeychainManager.swift
//  hyperwhisper
//
//  KEYCHAIN MANAGER
//  Provides secure storage for API keys using the macOS Keychain.
//  This replaces the insecure @AppStorage approach that stored keys in UserDefaults.
//
//  Key Features:
//  - Encrypted storage using macOS Keychain Services
//  - Service-based key organization for different API providers
//  - Automatic migration from UserDefaults to Keychain
//  - Error handling with descriptive messages
//  - Thread-safe operations
//
//  Security Benefits:
//  - Keys are encrypted at rest by macOS
//  - Protected by user's login keychain
//  - Not accessible via file system browsing
//  - Requires explicit keychain access permission
//  - Can be synced securely via iCloud Keychain if enabled
//
//  Architecture:
//  - Each API key is stored as a separate keychain item
//  - Uses kSecClassGenericPassword for storage
//  - Service identifier pattern: "com.hyperwhisper.apikeys.<provider>"
//  - Account identifier is the provider name for easy identification

import Foundation
import Security

/// Manages secure storage of API keys in the macOS Keychain
/// Thread-safe and can be called from any queue
class KeychainManager {
    
    // MARK: - Singleton
    
    /// Shared instance for app-wide access
    static let shared = KeychainManager()
    
    // MARK: - Constants
    
    /// Base service identifier for all API keys
    #if DEBUG
    private let baseService = "com.hyperwhisper.apikeys.dev"
    #else
    private let baseService = "com.hyperwhisper.apikeys"
    #endif
    
    /// Migration flag key in UserDefaults
    private let migrationKey = "apiKeysMigratedToKeychain"
    
    // MARK: - API Key Types
    
    /// Enumeration of all supported API key types
    enum APIKeyType: String, CaseIterable {
        case openAI = "openai"
        case groq = "groq"
        case anthropic = "anthropic"
        case gemini = "gemini"
        case deepgram = "deepgram"
        case assemblyAI = "assemblyai"
        case elevenLabs = "elevenlabs"
        case mistral = "mistral"
        case soniox = "soniox"
        case cerebras = "cerebras"
        case grok = "grok"

        /// User-friendly display name
        var displayName: String {
            switch self {
            case .openAI: return "OpenAI"
            case .groq: return "Groq"
            case .anthropic: return "Anthropic"
            case .gemini: return "Google Gemini"
            case .deepgram: return "Deepgram"
            case .assemblyAI: return "AssemblyAI"
            case .elevenLabs: return "ElevenLabs"
            case .mistral: return "Mistral"
            case .soniox: return "Soniox"
            case .cerebras: return "Cerebras"
            case .grok: return "Grok"
            }
        }

        /// Legacy UserDefaults key for migration
        var legacyKey: String {
            switch self {
            case .openAI: return "openAIAPIKey"
            case .groq: return "groqAPIKey"
            case .anthropic: return "anthropicAPIKey"
            case .gemini: return "geminiAPIKey"
            case .deepgram: return "deepgramAPIKey"
            case .assemblyAI: return "assemblyAIAPIKey"
            case .elevenLabs: return "elevenLabsAPIKey"
            case .mistral: return "mistralAPIKey"
            case .soniox: return "sonioxAPIKey"
            case .cerebras: return "cerebrasAPIKey"
            case .grok: return "grokAPIKey"
            }
        }
    }
    
    // MARK: - Keychain Errors
    
    /// Custom errors for keychain operations
    enum KeychainError: LocalizedError {
        case unhandledError(status: OSStatus)
        case noPassword
        case unexpectedPasswordData
        case migrationFailed(provider: String)
        
        var errorDescription: String? {
            switch self {
            case .unhandledError(let status):
                return "Keychain error: \(status). \(SecCopyErrorMessageString(status, nil) as String? ?? "Unknown error")"
            case .noPassword:
                return "No API key found in keychain"
            case .unexpectedPasswordData:
                return "API key data format is invalid"
            case .migrationFailed(let provider):
                return "Failed to migrate \(provider) API key to keychain"
            }
        }
    }
    
    // MARK: - Private Init
    
    private init() {
        // Private init for singleton pattern
    }
    
    // MARK: - Public Methods
    
    /// Save an API key to the keychain
    /// - Parameters:
    ///   - key: The API key to save
    ///   - type: The type of API key
    /// - Throws: KeychainError if the operation fails
    func saveAPIKey(_ key: String, for type: APIKeyType) throws {
        // SAVE OPERATION - STEP 1: Prepare the keychain query
        // We use the service + account pattern to uniquely identify each API key
        // This allows us to store multiple keys under the same service
        let service = "\(baseService).\(type.rawValue)"
        let account = type.rawValue  // Use stable identifier, not display name
        
        // Convert the key string to Data for storage
        guard let data = key.data(using: .utf8) else {
            throw KeychainError.unexpectedPasswordData
        }
        
        // STEP 2: Try to update existing item first
        // This is more efficient than delete + add for existing keys
        let updateQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        
        let updateAttributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,  // More secure - prevents backup/sync
            kSecAttrSynchronizable as String: false  // Don't sync to iCloud by default for security
        ]
        
        var status = SecItemUpdate(updateQuery as CFDictionary, updateAttributes as CFDictionary)
        
        // STEP 3: If item doesn't exist, add it
        if status == errSecItemNotFound {
            var addQuery = updateQuery
            addQuery[kSecValueData as String] = data
            addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly  // More secure
            addQuery[kSecAttrSynchronizable as String] = false
            addQuery[kSecAttrLabel as String] = "\(type.displayName) API Key"  // Display name for UI
            addQuery[kSecAttrDescription as String] = "API key for \(type.displayName) service"
            
            status = SecItemAdd(addQuery as CFDictionary, nil)
        }
        
        // STEP 4: Handle any errors
        guard status == errSecSuccess else {
            throw KeychainError.unhandledError(status: status)
        }
        
        AppLogger.settings.info("Successfully saved \(type.displayName, privacy: .public) API key to keychain")
    }
    
    /// Retrieve an API key from the keychain
    /// - Parameter type: The type of API key to retrieve
    /// - Returns: The API key string, or empty string if not found
    func getAPIKey(for type: APIKeyType) -> String {
        // RETRIEVE OPERATION - STEP 1: Build the search query
        // We need to specify what we're looking for and what we want returned
        let service = "\(baseService).\(type.rawValue)"
        let account = type.rawValue  // Use stable identifier
        
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,  // Return the actual data
            kSecMatchLimit as String: kSecMatchLimitOne  // Only return one item
        ]
        
        // STEP 2: Execute the search
        var dataTypeRef: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &dataTypeRef)
        
        // STEP 3: Handle the result
        if status == errSecSuccess {
            if let data = dataTypeRef as? Data,
               let key = String(data: data, encoding: .utf8) {
                return key
            }
        } else if status == errSecItemNotFound {
            // This is not an error - the key simply hasn't been set yet
            return ""
        } else {
            // Log other errors but return empty string to maintain compatibility
            AppLogger.settings.error("Keychain read error for \(type.displayName, privacy: .public): \(status, privacy: .public)")
        }
        
        return ""
    }
    
    /// Delete an API key from the keychain
    /// - Parameter type: The type of API key to delete
    /// - Throws: KeychainError if the operation fails
    func deleteAPIKey(for type: APIKeyType) throws {
        // DELETE OPERATION: Remove the key entirely from the keychain
        let service = "\(baseService).\(type.rawValue)"
        let account = type.rawValue  // Use stable identifier
        
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        
        let status = SecItemDelete(query as CFDictionary)
        
        // errSecItemNotFound is not an error for delete operations
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unhandledError(status: status)
        }
        
        if status == errSecSuccess {
            AppLogger.settings.info("Successfully deleted \(type.displayName, privacy: .public) API key from keychain")
        }
    }
    
    /// Check if an API key exists in the keychain
    /// - Parameter type: The type of API key to check
    /// - Returns: True if the key exists, false otherwise
    func hasAPIKey(for type: APIKeyType) -> Bool {
        return !getAPIKey(for: type).isEmpty
    }
    
    // MARK: - Migration
    
    /// Migrate API keys from UserDefaults to Keychain
    /// - Returns: True if migration was needed and successful, false if already migrated
    @discardableResult
    func migrateFromUserDefaults() -> Bool {
        // MIGRATION FLOW - STEP 1: Check if already migrated
        // We use a flag to avoid attempting migration on every launch
        if UserDefaults.standard.bool(forKey: migrationKey) {
            AppLogger.settings.debug("API keys already migrated to keychain")
            return false
        }
        
        AppLogger.settings.info("Starting API key migration from UserDefaults to Keychain")
        
        // STEP 2: Migrate each API key type
        var migrationSuccess = true
        var migratedCount = 0
        
        for keyType in APIKeyType.allCases {
            // Read from UserDefaults using the legacy key
            if let legacyKey = UserDefaults.standard.string(forKey: keyType.legacyKey),
               !legacyKey.isEmpty {
                do {
                    // STEP 3: Save to keychain
                    try saveAPIKey(legacyKey, for: keyType)
                    
                    // STEP 4: Verify the save was successful
                    let savedKey = getAPIKey(for: keyType)
                    if savedKey == legacyKey {
                        // STEP 5: Remove from UserDefaults only after successful migration
                        UserDefaults.standard.removeObject(forKey: keyType.legacyKey)
                        migratedCount += 1
                        AppLogger.settings.info("Migrated \(keyType.displayName, privacy: .public) API key")
                    } else {
                        AppLogger.settings.error("Failed to verify \(keyType.displayName, privacy: .public) migration")
                        migrationSuccess = false
                    }
                } catch {
                    AppLogger.settings.error("Failed to migrate \(keyType.displayName, privacy: .public): \(error, privacy: .public)")
                    migrationSuccess = false
                }
            }
        }
        
        // STEP 6: Set migration flag if all keys migrated successfully
        if migrationSuccess {
            UserDefaults.standard.set(true, forKey: migrationKey)
            UserDefaults.standard.synchronize()  // Force immediate write
            
            if migratedCount > 0 {
                AppLogger.settings.info("Successfully migrated \(migratedCount, privacy: .public) API key(s) to keychain")
            } else {
                AppLogger.settings.debug("No API keys found to migrate")
            }
        } else {
            AppLogger.settings.warning("Some API keys failed to migrate. Will retry on next launch")
        }
        
        return migrationSuccess && migratedCount > 0
    }
    
    /// Clear the migration flag (useful for testing)
    func resetMigration() {
        UserDefaults.standard.removeObject(forKey: migrationKey)
        AppLogger.settings.debug("Migration flag reset")
    }
    
    /// Delete all API keys from keychain (use with caution)
    func deleteAllAPIKeys() {
        for keyType in APIKeyType.allCases {
            try? deleteAPIKey(for: keyType)
        }
        AppLogger.settings.warning("All API keys deleted from keychain")
    }
    
    // MARK: - Debugging

    /// Get a summary of which API keys are configured (for debugging)
    /// - Returns: Dictionary of key types and their configuration status
    func getConfigurationSummary() -> [String: Bool] {
        var summary: [String: Bool] = [:]
        for keyType in APIKeyType.allCases {
            summary[keyType.displayName] = hasAPIKey(for: keyType)
        }
        return summary
    }

    // MARK: - Custom Endpoint API Keys

    /// CUSTOM ENDPOINT API KEY STORAGE
    /// These methods handle API keys for user-defined OpenAI-compatible endpoints.
    /// Each custom endpoint gets its own Keychain entry identified by UUID.
    ///
    /// Service pattern: "com.hyperwhisper.apikeys.custom.<uuid>"
    /// This ensures keys are isolated per endpoint and can be deleted when endpoint is removed.

    /// Save an API key for a custom endpoint
    /// - Parameters:
    ///   - key: The API key to save
    ///   - endpointId: UUID of the custom endpoint
    /// - Throws: KeychainError if the operation fails
    func saveCustomEndpointAPIKey(_ key: String, for endpointId: UUID) throws {
        // CUSTOM ENDPOINT SAVE - STEP 1: Build service identifier
        // Uses a unique service per endpoint to isolate keys
        let service = "\(baseService).custom.\(endpointId.uuidString)"
        let account = "custom_endpoint"

        // Convert the key string to Data for storage
        guard let data = key.data(using: .utf8) else {
            throw KeychainError.unexpectedPasswordData
        }

        // STEP 2: Try to update existing item first
        let updateQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]

        let updateAttributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecAttrSynchronizable as String: false
        ]

        var status = SecItemUpdate(updateQuery as CFDictionary, updateAttributes as CFDictionary)

        // STEP 3: If item doesn't exist, add it
        if status == errSecItemNotFound {
            var addQuery = updateQuery
            addQuery[kSecValueData as String] = data
            addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            addQuery[kSecAttrSynchronizable as String] = false
            addQuery[kSecAttrLabel as String] = "Custom Endpoint API Key"
            addQuery[kSecAttrDescription as String] = "API key for custom post-processing endpoint"

            status = SecItemAdd(addQuery as CFDictionary, nil)
        }

        // STEP 4: Handle any errors
        guard status == errSecSuccess else {
            throw KeychainError.unhandledError(status: status)
        }

        AppLogger.settings.info("Successfully saved custom endpoint API key to keychain")
    }

    /// Retrieve an API key for a custom endpoint
    /// - Parameter endpointId: UUID of the custom endpoint
    /// - Returns: The API key string, or empty string if not found
    func getCustomEndpointAPIKey(for endpointId: UUID) -> String {
        // CUSTOM ENDPOINT RETRIEVE - STEP 1: Build the search query
        let service = "\(baseService).custom.\(endpointId.uuidString)"
        let account = "custom_endpoint"

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        // STEP 2: Execute the search
        var dataTypeRef: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &dataTypeRef)

        // STEP 3: Handle the result
        if status == errSecSuccess {
            if let data = dataTypeRef as? Data,
               let key = String(data: data, encoding: .utf8) {
                return key
            }
        } else if status == errSecItemNotFound {
            // This is not an error - the key simply hasn't been set yet
            return ""
        } else {
            AppLogger.settings.error("Keychain read error for custom endpoint: \(status, privacy: .public)")
        }

        return ""
    }

    /// Delete an API key for a custom endpoint
    /// - Parameter endpointId: UUID of the custom endpoint
    /// - Throws: KeychainError if the operation fails
    func deleteCustomEndpointAPIKey(for endpointId: UUID) throws {
        // CUSTOM ENDPOINT DELETE: Remove the key entirely from the keychain
        let service = "\(baseService).custom.\(endpointId.uuidString)"
        let account = "custom_endpoint"

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]

        let status = SecItemDelete(query as CFDictionary)

        // errSecItemNotFound is not an error for delete operations
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unhandledError(status: status)
        }

        if status == errSecSuccess {
            AppLogger.settings.info("Successfully deleted custom endpoint API key from keychain")
        }
    }

    /// Check if an API key exists for a custom endpoint
    /// - Parameter endpointId: UUID of the custom endpoint
    /// - Returns: True if the key exists, false otherwise
    func hasCustomEndpointAPIKey(for endpointId: UUID) -> Bool {
        return !getCustomEndpointAPIKey(for: endpointId).isEmpty
    }
}
