//
//  LocalAPIAuth.swift
//  hyperwhisper
//
//  Bearer-token authorization for the Local API. Tokens are 32 random
//  bytes encoded as base64-url (43 ASCII chars, no padding). Stored in
//  the macOS Keychain so they survive app reinstalls but not user
//  deletion; mirrored into local-api.json (chmod 600) so MCP wrappers
//  and CLI scripts on the same machine can read them.
//

import Foundation
import Security
import FlyingFox

enum LocalAPIAuth {

    private static let keychainService = "com.hyperwhisper.app.localapi"
    private static let keychainAccount = "bearer_token"

    /// Load the existing token, or generate + store a fresh one if none is
    /// present. Returns the token on every code path; failure to persist
    /// (Keychain ACL denial etc.) is logged but does not block the server —
    /// we still want the API up, just with a token that resets on next launch.
    @discardableResult
    static func loadOrCreateToken() -> String {
        if let existing = readToken(), !existing.isEmpty {
            return existing
        }
        let fresh = generateToken()
        do {
            try writeToken(fresh)
        } catch {
            AppLogger.settings.error("LocalAPI auth: failed to persist token · \(error.localizedDescription, privacy: .public)")
        }
        return fresh
    }

    /// Wipe the stored token and generate a new one. Used by Settings →
    /// "Regenerate token". The caller is responsible for restarting the
    /// server so the new token gets written into local-api.json.
    @discardableResult
    static func regenerateToken() -> String {
        deleteToken()
        return loadOrCreateToken()
    }

    /// Remove the stored token entirely — used by tests / a future
    /// "reset all" affordance.
    static func deleteToken() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainAccount
        ]
        SecItemDelete(query as CFDictionary)
    }

    // MARK: - Header check

    /// Return true iff `request` carries `Authorization: Bearer <expected>`.
    ///
    /// THE COMPARE LIVES IN RUST (issue #289). All three platforms did this
    /// differently — this file hand-rolled an XOR loop that returned early on
    /// a length mismatch, so the length of the stored token leaked through
    /// timing even though the loop that followed did not. `hw_localapi`
    /// hashes both sides to a fixed 32 bytes first, so the compare runs the
    /// same number of iterations for every request whatever the caller sent.
    static func authorize(_ request: HTTPRequest, expected: String) -> Bool {
        // FlyingFox surfaces headers via the request's headers map. We look
        // up case-insensitively since RFC 7230 §3.2 makes header names CI.
        let header = headerValue(request: request, name: "Authorization")
            ?? headerValue(request: request, name: "authorization")
        return localApiAuthorize(authorizationHeader: header, expectedToken: expected)
    }

    private static func headerValue(request: HTTPRequest, name: String) -> String? {
        // FlyingFox HTTPRequest exposes `headers: [HTTPHeader: String]`.
        // `HTTPHeader` is a CustomStringConvertible wrapper around a
        // case-insensitive name; constructing it from `name` is the safest
        // cross-version lookup.
        let key = HTTPHeader(name)
        return request.headers[key]
    }

    // MARK: - Token primitives

    private static func readToken() -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainAccount,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var ref: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &ref)
        if status == errSecSuccess, let data = ref as? Data, let s = String(data: data, encoding: .utf8) {
            return s
        }
        return nil
    }

    private static func writeToken(_ token: String) throws {
        guard let data = token.data(using: .utf8) else {
            throw NSError(domain: "LocalAPIAuth", code: -1, userInfo: [NSLocalizedDescriptionKey: "Token not UTF-8 encodable"])
        }

        // Try update first; if not present, add.
        let baseQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainAccount
        ]
        let updateAttributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecAttrSynchronizable as String: false
        ]
        var status = SecItemUpdate(baseQuery as CFDictionary, updateAttributes as CFDictionary)
        if status == errSecItemNotFound {
            var addQuery = baseQuery
            addQuery[kSecValueData as String] = data
            addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            addQuery[kSecAttrSynchronizable as String] = false
            addQuery[kSecAttrLabel as String] = "HyperWhisper Local API token"
            status = SecItemAdd(addQuery as CFDictionary, nil)
        }
        if status != errSecSuccess {
            throw NSError(domain: "LocalAPIAuth", code: Int(status), userInfo: [NSLocalizedDescriptionKey: "Keychain status \(status)"])
        }
    }

    /// 32 random bytes → base64-url, the encoding all three platforms wrote
    /// out separately.
    ///
    /// THE ENTROPY STAYS HERE AND THE ENCODING MOVED (issue #289). The shared
    /// core builds with `panic = "abort"`, so a random-number failure inside
    /// Rust would abort the whole app; `SecRandomCopyBytes` failing is
    /// something this function can handle, and does — the UUID fallback below
    /// is the pre-existing behaviour and is why `rand` is not a dependency of
    /// `hw-localapi`.
    static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: Int(localApiTokenEntropyBytes()))
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        if status != errSecSuccess {
            // Defensive fallback: UUID-based (less ideal but never blocking).
            return UUID().uuidString.replacingOccurrences(of: "-", with: "") + UUID().uuidString.replacingOccurrences(of: "-", with: "")
        }
        do {
            return try localApiGenerateToken(entropy: Data(bytes))
        } catch {
            AppLogger.settings.error("LocalAPI auth: token encoding failed · \(error.localizedDescription, privacy: .public)")
            return UUID().uuidString.replacingOccurrences(of: "-", with: "") + UUID().uuidString.replacingOccurrences(of: "-", with: "")
        }
    }
}
