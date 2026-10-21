//
//  BackupManager+APIKeys.swift
//  hyperwhisper
//
//  API keys: the Keychain writes a restore makes, and the v1 and universal
//  v2 API-key mappings.
//  Split out of BackupManager.swift (#732); same class, same behaviour.
//

import Foundation
import SwiftUI
import AppKit

extension BackupManager {

    /// Imports flat lowercase-provider API keys (the universal/Windows shape) into the Keychain.
    /// Reuses the same per-provider save calls as the v1 `importAPIKeys`; unknown keys are ignored.
    func importUniversalAPIKeys(_ keys: [String: String]) -> APIKeyRestoreOutcome {
        restoreAPIKeys(BackupManager.universalAPIKeyAssignments(from: keys))
    }

    /// Writes each assignment through `apiKeyWriter` and reports what landed.
    /// A failed write is logged and counted, and the loop carries on: a
    /// partial key restore is better than none, so nothing is unwound.
    private func restoreAPIKeys(
        _ assignments: [(provider: KeychainManager.APIKeyType, key: String)]
    ) -> APIKeyRestoreOutcome {
        var outcome = APIKeyRestoreOutcome()
        for (provider, key) in assignments {
            do {
                try apiKeyWriter.saveAPIKey(key, for: provider)
                outcome.anySaved = true
            } catch {
                // Both mappings write each slot at most once, so a provider
                // listed here holds no key from this backup.
                outcome.failed.append(provider)
                // PRIVACY: the provider name and the error only. NEVER the key.
                let providerName = provider.displayName
                AppLogger.settings.error("Backup import: could not save the \(providerName, privacy: .public) API key to the Keychain: \(error.localizedDescription, privacy: .public)")
            }
        }
        return outcome
    }

    // MARK: - Universal (v2) API-key mapping

    /// The `apiKeys` object member the Gemini 3.5 Transcribe key is EXPORTED
    /// under. Lowercase, per the schema's "API keys by lowercase provider name",
    /// and identical to `KeychainManager.APIKeyType.geminiTranscribe.rawValue`.
    ///
    /// A SEPARATE member from `gemini`: same vendor, different API
    /// (`/v1beta/interactions`) and a different keychain slot. Omitting it
    /// restores a Gemini 3.5 Transcribe user with no key at all — and the legacy
    /// `gemini` key restoring fine is exactly what hides that.
    static let geminiTranscribeBackupKey = "geminitranscribe"

    /// NON-canonical spellings the IMPORT path also accepts for that key.
    ///
    /// The camelCase form is accepted because the mode-level `cloudProvider` id
    /// for this provider IS camelCase on Windows and Linux, so a writer
    /// mirroring that spelling is a live risk — and a silently dropped key is
    /// unrecoverable for the user. Never written on export.
    static let geminiTranscribeBackupKeyAliases = ["geminiTranscribe"]

    /// Object-member name → keychain slot, in export order. The single source of
    /// truth for both directions of the universal API-key section.
    ///
    /// `cerebras` and `soniox` have keychain slots but no entry here; that
    /// predates this table and is tracked separately.
    static let universalAPIKeyMembers: [(name: String, provider: KeychainManager.APIKeyType)] = [
        ("openai", .openAI),
        ("groq", .groq),
        // "fireworks" is deliberately absent: the provider was removed, so the
        // member is neither written nor read.
        ("anthropic", .anthropic),
        ("gemini", .gemini),
        ("deepgram", .deepgram),
        ("assemblyai", .assemblyAI),
        ("elevenlabs", .elevenLabs),
        ("mistral", .mistral),
        ("grok", .grok),
        // Literal rather than `geminiTranscribeBackupKey` so this table has no
        // ordering dependency on another static; the test pins the two equal.
        ("geminitranscribe", .geminiTranscribe),
        ("meta", .meta),
    ]

    /// Build the exported `apiKeys` object. `read` returns the stored key for a
    /// slot; empty and nil are both treated as "not configured" and omitted.
    /// Pure, so the mapping is testable without touching the Keychain.
    static func universalAPIKeyMap(
        read: (KeychainManager.APIKeyType) -> String?
    ) -> [String: String] {
        var map: [String: String] = [:]
        for member in universalAPIKeyMembers {
            guard let key = read(member.provider), !key.isEmpty else { continue }
            map[member.name] = key
        }
        return map
    }

    /// The keychain writes an imported `apiKeys` object implies, in order.
    /// Unknown members are ignored, so a backup from a newer client restores the
    /// parts this build understands. Pure, for the same reason as above.
    static func universalAPIKeyAssignments(
        from keys: [String: String]
    ) -> [(provider: KeychainManager.APIKeyType, key: String)] {
        var assignments: [(provider: KeychainManager.APIKeyType, key: String)] = []
        // An alias is read only when the documented member is absent, so each
        // slot gets at most one write and a failed write is reported against
        // the value that was meant to land.
        let canonicalTranscribeKey = keys[geminiTranscribeBackupKey] ?? ""
        if canonicalTranscribeKey.isEmpty {
            for alias in geminiTranscribeBackupKeyAliases {
                guard let key = keys[alias], !key.isEmpty else { continue }
                assignments.append((.geminiTranscribe, key))
                break
            }
        }
        for member in universalAPIKeyMembers {
            guard let key = keys[member.name], !key.isEmpty else { continue }
            assignments.append((member.provider, key))
        }
        return assignments
    }

    /// Imports API keys from backup to Keychain
    /// - Parameter apiKeys: BackupAPIKeys to import
    /// - Returns: what the Keychain writes did
    func importAPIKeys(_ apiKeys: BackupAPIKeys) -> APIKeyRestoreOutcome {
        restoreAPIKeys(Self.legacyAPIKeyAssignments(from: apiKeys))
    }

    /// The keychain writes a v1 `apiKeys` section implies, in order. Empty and
    /// nil keys are skipped. Pure, so the mapping is testable without the Keychain.
    nonisolated static func legacyAPIKeyAssignments(
        from apiKeys: BackupAPIKeys
    ) -> [(provider: KeychainManager.APIKeyType, key: String)] {
        let candidates: [(provider: KeychainManager.APIKeyType, key: String?)] = [
            (.openAI, apiKeys.openai),
            (.groq, apiKeys.groq),
            // Fireworks removed: read-and-ignore apiKeys.fireworks so old backups still decode.
            (.anthropic, apiKeys.anthropic),
            (.gemini, apiKeys.gemini),
            (.deepgram, apiKeys.deepgram),
            (.assemblyAI, apiKeys.assemblyai),
            (.elevenLabs, apiKeys.elevenlabs),
            (.mistral, apiKeys.mistral),
            (.grok, apiKeys.grok),
            (.geminiTranscribe, apiKeys.geminitranscribe),
            (.meta, apiKeys.meta),
        ]
        var assignments: [(provider: KeychainManager.APIKeyType, key: String)] = []
        for candidate in candidates {
            guard let key = candidate.key, !key.isEmpty else { continue }
            assignments.append((candidate.provider, key))
        }
        return assignments
    }
}
