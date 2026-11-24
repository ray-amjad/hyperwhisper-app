import Foundation
import Security
import Testing
@testable import HyperWhisper

/// Pins where `KeychainManager` stores API keys (#1115).
///
/// The BYOK path and the custom-endpoint path share one Keychain sequence.
/// These tests fix every identity string and attribute that sequence writes,
/// so a later edit cannot silently move saved keys (which would orphan every
/// key a user has already stored). None of these tests touch the real Keychain.
@Suite("KeychainManager shared Keychain path")
struct KeychainManagerSharedPathTests {
    private static let base = "com.hyperwhisper.apikeys"
    private static let endpoint = UUID(uuidString: "11151115-0000-4000-8000-000000001115")!

    private static func sampleData() -> Data {
        Data(["sample", "value", "1115"].joined(separator: "-").utf8)
    }

    private static func same(_ lhs: [String: Any], _ rhs: [String: Any]) -> Bool {
        (lhs as NSDictionary).isEqual(rhs as NSDictionary)
    }

    @Test("Base service differs between Debug and Release builds")
    func baseServiceName() {
        #if DEBUG
        #expect(KeychainManager.baseServiceName == "com.hyperwhisper.apikeys.dev")
        #else
        #expect(KeychainManager.baseServiceName == "com.hyperwhisper.apikeys")
        #endif
    }

    @Test("Every BYOK slot keeps its service, account, label and description")
    func byokIdentities() {
        let expected: [(KeychainManager.APIKeyType, String, String)] = [
            (.openAI, "openai", "OpenAI"),
            (.groq, "groq", "Groq"),
            (.anthropic, "anthropic", "Anthropic"),
            (.gemini, "gemini", "Google Gemini"),
            (.deepgram, "deepgram", "Deepgram"),
            (.assemblyAI, "assemblyai", "AssemblyAI"),
            (.elevenLabs, "elevenlabs", "ElevenLabs"),
            (.mistral, "mistral", "Mistral"),
            (.soniox, "soniox", "Soniox"),
            (.cerebras, "cerebras", "Cerebras"),
            (.grok, "grok", "Grok"),
            (.geminiTranscribe, "geminitranscribe", "Gemini 3.5 Transcribe"),
            (.meta, "MetaApiKey", "Meta"),
        ]
        #expect(expected.count == KeychainManager.APIKeyType.allCases.count)

        for (slot, account, name) in expected {
            let identity = KeychainManager.identity(for: slot, baseService: Self.base)
            #expect(identity == KeychainManager.ItemIdentity(
                service: "com.hyperwhisper.apikeys.\(account)",
                account: account,
                label: "\(name) API Key",
                description: "API key for \(name) service"
            ))
        }
    }

    @Test("A BYOK identity uses the base service it is given")
    func byokIdentityUsesBaseService() {
        let identity = KeychainManager.identity(for: .openAI, baseService: "com.hyperwhisper.apikeys.dev")
        #expect(identity.service == "com.hyperwhisper.apikeys.dev.openai")
        #expect(identity.account == "openai")
    }

    @Test("A custom endpoint keeps its service, account, label and description")
    func customEndpointIdentity() {
        let identity = KeychainManager.identity(forCustomEndpoint: Self.endpoint, baseService: Self.base)
        #expect(identity == KeychainManager.ItemIdentity(
            service: "com.hyperwhisper.apikeys.custom.11151115-0000-4000-8000-000000001115",
            account: "custom_endpoint",
            label: "Custom Endpoint API Key",
            description: "API key for custom post-processing endpoint"
        ))
    }

    @Test("The locate query is a generic password matched by service and account")
    func itemQuery() {
        let query = KeychainManager.itemQuery(service: "svc.1115", account: "acct")
        #expect(query.count == 3)
        #expect(Self.same(query, [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "svc.1115",
            kSecAttrAccount as String: "acct",
        ]))
    }

    @Test("An update writes the data as this-device-only and never synchronizable")
    func updateAttributes() {
        let sample = Self.sampleData()
        let attributes = KeychainManager.updateAttributes(data: sample)
        #expect(attributes.count == 3)
        #expect(Self.same(attributes, [
            kSecValueData as String: sample,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecAttrSynchronizable as String: false,
        ]))
        #expect(attributes[kSecUseDataProtectionKeychain as String] == nil)
    }

    @Test("A BYOK add carries the exact identity and attributes")
    func byokAddQuery() {
        let sample = Self.sampleData()
        let identity = KeychainManager.identity(for: .anthropic, baseService: Self.base)
        let query = KeychainManager.addQuery(identity: identity, data: sample)
        #expect(query.count == 8)
        #expect(Self.same(query, [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.hyperwhisper.apikeys.anthropic",
            kSecAttrAccount as String: "anthropic",
            kSecValueData as String: sample,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecAttrSynchronizable as String: false,
            kSecAttrLabel as String: "Anthropic API Key",
            kSecAttrDescription as String: "API key for Anthropic service",
        ]))
        #expect(query[kSecUseDataProtectionKeychain as String] == nil)
    }

    @Test("A custom endpoint add carries the exact identity and attributes")
    func customEndpointAddQuery() {
        let sample = Self.sampleData()
        let identity = KeychainManager.identity(forCustomEndpoint: Self.endpoint, baseService: Self.base)
        let query = KeychainManager.addQuery(identity: identity, data: sample)
        #expect(query.count == 8)
        #expect(Self.same(query, [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.hyperwhisper.apikeys.custom.11151115-0000-4000-8000-000000001115",
            kSecAttrAccount as String: "custom_endpoint",
            kSecValueData as String: sample,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecAttrSynchronizable as String: false,
            kSecAttrLabel as String: "Custom Endpoint API Key",
            kSecAttrDescription as String: "API key for custom post-processing endpoint",
        ]))
    }

    @Test("A read asks for one item's data")
    func readQuery() {
        let query = KeychainManager.readQuery(service: "com.hyperwhisper.apikeys.groq", account: "groq")
        #expect(query.count == 5)
        #expect(Self.same(query, [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.hyperwhisper.apikeys.groq",
            kSecAttrAccount as String: "groq",
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]))
    }
}
