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
    /// (internal so `KeychainManagerSharedPathTests` can pin it)
    #if DEBUG
    static let baseServiceName = "com.hyperwhisper.apikeys.dev"
    #else
    static let baseServiceName = "com.hyperwhisper.apikeys"
    #endif

    private let baseService = KeychainManager.baseServiceName
    
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
        /// Gemini 3.5 Transcribe (`/v1beta/interactions`). A SEPARATE slot from
        /// `.gemini`: same vendor, different API and different eligibility, so
        /// the two keys must not overwrite each other.
        case geminiTranscribe = "geminitranscribe"
        /// Isolated Meta Model API key slot. This value is never stored in defaults.
        case meta = "MetaApiKey"

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
            case .geminiTranscribe: return "Gemini 3.5 Transcribe"
            case .meta: return "Meta"
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
            // Never lived in UserDefaults (the provider postdates the Keychain
            // migration); the arm exists so the switch stays exhaustive.
            case .geminiTranscribe: return "geminiTranscribeAPIKey"
            // Meta postdates the defaults migration and has never had a defaults slot.
            case .meta: return "metaAPIKey"
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
    
    // MARK: - Shared Keychain Item Sequence

    /// Identity and Finder-visible metadata of one keychain item.
    ///
    /// `service` + `account` locate the item; `label` and `description` are
    /// written only when the item is first added. Changing any of these
    /// strings moves (or orphans) the user's saved keys, which is why
    /// `KeychainManagerSharedPathTests` pins every one of them.
    struct ItemIdentity: Equatable {
        let service: String
        let account: String
        let label: String
        let description: String
    }

    /// Identity of a built-in BYOK key slot.
    static func identity(for type: APIKeyType, baseService: String) -> ItemIdentity {
        ItemIdentity(
            service: "\(baseService).\(type.rawValue)",
            account: type.rawValue,  // Use stable identifier, not display name
            label: "\(type.displayName) API Key",  // Display name for UI
            description: "API key for \(type.displayName) service"
        )
    }

    /// Identity of a custom post-processing endpoint's key.
    static func identity(forCustomEndpoint endpointId: UUID, baseService: String) -> ItemIdentity {
        ItemIdentity(
            service: "\(baseService).custom.\(endpointId.uuidString)",
            account: "custom_endpoint",
            label: "Custom Endpoint API Key",
            description: "API key for custom post-processing endpoint"
        )
    }

    /// The query that locates an item (used for update and delete).
    static func itemQuery(service: String, account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
    }

    /// Attributes written over an existing item by the update step.
    static func updateAttributes(data: Data) -> [String: Any] {
        [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,  // More secure - prevents backup/sync
            kSecAttrSynchronizable as String: false  // Don't sync to iCloud by default for security
        ]
    }

    /// The full dictionary used by the add step when no item exists yet.
    static func addQuery(identity: ItemIdentity, data: Data) -> [String: Any] {
        var query = itemQuery(service: identity.service, account: identity.account)
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly  // More secure
        query[kSecAttrSynchronizable as String] = false
        query[kSecAttrLabel as String] = identity.label
        query[kSecAttrDescription as String] = identity.description
        return query
    }

    /// The query used to read an item's data.
    static func readQuery(service: String, account: String) -> [String: Any] {
        var query = itemQuery(service: service, account: account)
        query[kSecReturnData as String] = true  // Return the actual data
        query[kSecMatchLimit as String] = kSecMatchLimitOne  // Only return one item
        return query
    }

    /// Update the item if it exists, otherwise add it.
    /// - Returns: The status of the update, or of the add when the update found no item.
    private func upsertItem(_ identity: ItemIdentity, data: Data) -> OSStatus {
        // Try to update existing item first.
        // This is more efficient than delete + add for existing keys
        let updateQuery = Self.itemQuery(service: identity.service, account: identity.account)
        let attributes = Self.updateAttributes(data: data)

        var status = SecItemUpdate(updateQuery as CFDictionary, attributes as CFDictionary)

        // If item doesn't exist, add it
        if status == errSecItemNotFound {
            let addQuery = Self.addQuery(identity: identity, data: data)
            status = SecItemAdd(addQuery as CFDictionary, nil)
        }
        return status
    }

    /// Read an item's data.
    /// - Returns: The status of the search, and the item's data when it was found.
    private func readItem(_ identity: ItemIdentity) -> (status: OSStatus, data: Data?) {
        let query = Self.readQuery(service: identity.service, account: identity.account)
        var dataTypeRef: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &dataTypeRef)
        return (status, dataTypeRef as? Data)
    }

    /// Delete an item. The caller decides which statuses are errors.
    private func deleteItem(_ identity: ItemIdentity) -> OSStatus {
        let query = Self.itemQuery(service: identity.service, account: identity.account)
        return SecItemDelete(query as CFDictionary)
    }

    /// Save a UTF-8 string, mapping failures to `KeychainError`.
    private func saveString(_ value: String, as identity: ItemIdentity) throws {
        // Convert the key string to Data for storage
        guard let data = value.data(using: .utf8) else {
            throw KeychainError.unexpectedPasswordData
        }

        let status = upsertItem(identity, data: data)
        guard status == errSecSuccess else {
            throw KeychainError.unhandledError(status: status)
        }
    }

    /// Read a UTF-8 string.
    /// - Returns: The string, or an empty string when no item exists, its data is unreadable, or the read fails.
    ///   Any other status is handed to `onReadError` so each caller keeps its own log line.
    private func readString(_ identity: ItemIdentity, onReadError: (OSStatus) -> Void) -> String {
        let result = readItem(identity)

        if result.status == errSecSuccess {
            if let data = result.data,
               let key = String(data: data, encoding: .utf8) {
                return key
            }
        } else if result.status == errSecItemNotFound {
            // This is not an error - the key simply hasn't been set yet
            return ""
        } else {
            // Log other errors but return empty string to maintain compatibility
            onReadError(result.status)
        }

        return ""
    }

    /// Delete an item, treating "not found" as success.
    /// - Returns: True if an item was deleted, false if there was none.
    private func deleteString(_ identity: ItemIdentity) throws -> Bool {
        let status = deleteItem(identity)

        // errSecItemNotFound is not an error for delete operations
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unhandledError(status: status)
        }
        return status == errSecSuccess
    }

    // MARK: - Public Methods

    /// Save an API key to the keychain
    /// - Parameters:
    ///   - key: The API key to save
    ///   - type: The type of API key
    /// - Throws: KeychainError if the operation fails
    func saveAPIKey(_ key: String, for type: APIKeyType) throws {
        try saveString(key, as: Self.identity(for: type, baseService: baseService))
        AppLogger.settings.info("Successfully saved \(type.displayName, privacy: .public) API key to keychain")
    }

    /// Retrieve an API key from the keychain
    /// - Parameter type: The type of API key to retrieve
    /// - Returns: The API key string, or empty string if not found
    func getAPIKey(for type: APIKeyType) -> String {
        readString(Self.identity(for: type, baseService: baseService)) { status in
            AppLogger.settings.error("Keychain read error for \(type.displayName, privacy: .public): \(status, privacy: .public)")
        }
    }

    /// Delete an API key from the keychain
    /// - Parameter type: The type of API key to delete
    /// - Throws: KeychainError if the operation fails
    func deleteAPIKey(for type: APIKeyType) throws {
        if try deleteString(Self.identity(for: type, baseService: baseService)) {
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
        try saveString(key, as: Self.identity(forCustomEndpoint: endpointId, baseService: baseService))
        AppLogger.settings.info("Successfully saved custom endpoint API key to keychain")
    }

    /// Retrieve an API key for a custom endpoint
    /// - Parameter endpointId: UUID of the custom endpoint
    /// - Returns: The API key string, or empty string if not found
    func getCustomEndpointAPIKey(for endpointId: UUID) -> String {
        readString(Self.identity(forCustomEndpoint: endpointId, baseService: baseService)) { status in
            AppLogger.settings.error("Keychain read error for custom endpoint: \(status, privacy: .public)")
        }
    }

    /// Delete an API key for a custom endpoint
    /// - Parameter endpointId: UUID of the custom endpoint
    /// - Throws: KeychainError if the operation fails
    func deleteCustomEndpointAPIKey(for endpointId: UUID) throws {
        if try deleteString(Self.identity(forCustomEndpoint: endpointId, baseService: baseService)) {
            AppLogger.settings.info("Successfully deleted custom endpoint API key from keychain")
        }
    }

}
