//
//  BackupAPIKeyRestoreTests.swift
//  hyperwhisperTests
//
//  Issue #770: a backup restore whose Keychain writes fail used to report
//  "Import complete" with `apiKeysImported == true`, because both importers
//  discarded the write errors and the flag was read off the backup FILE.
//  These tests drive a real `importSettings(from:options:)` with a writer
//  stub that refuses chosen providers, and assert the result reports the
//  writes, not the file.
//
//  Only the API-keys section is selected, so no test here touches Core Data,
//  the live settings or the license store.
//

import Foundation
import Security
import Testing
@testable import HyperWhisper

/// Records successful writes and throws the Keychain's own error for every
/// provider in `refusing`. Never stores or prints a key value.
private final class BackupKeyWriterStub: BackupAPIKeyWriting {
    let refusing: Set<KeychainManager.APIKeyType>
    private(set) var written: [KeychainManager.APIKeyType] = []

    init(refusing: Set<KeychainManager.APIKeyType>) {
        self.refusing = refusing
    }

    func saveAPIKey(_ key: String, for type: KeychainManager.APIKeyType) throws {
        if refusing.contains(type) {
            throw KeychainManager.KeychainError.unhandledError(status: errSecInteractionNotAllowed)
        }
        written.append(type)
    }
}

@MainActor
@Suite(.serialized)
struct BackupAPIKeyRestoreTests {

    // MARK: - Fixtures

    /// Legacy v1 file carrying ONLY an apiKeys section. Values are obvious
    /// placeholders, never key-shaped.
    private static let legacyV1JSON = """
    {
      "version": 1,
      "exportDate": "2026-10-07T00:00:00Z",
      "appVersion": "1.0",
      "apiKeys": {
        "openai": "placeholder-one",
        "groq": "placeholder-two"
      }
    }
    """

    /// Universal v2 file. An empty `modes` array routes it through the full
    /// v2 importer; the modes section itself is not selected.
    private static let universalV2JSON = """
    {
      "schemaVersion": 2,
      "exportDate": "2026-10-07T00:00:00Z",
      "appVersion": "1.0",
      "platform": "windows",
      "modes": [],
      "apiKeys": {
        "openai": "placeholder-one",
        "groq": "placeholder-two",
        "geminitranscribe": "placeholder-three"
      }
    }
    """

    private static func keysOnlyOptions() -> ImportOptions {
        var options = ImportOptions()
        options.importSettings = false
        options.importModes = false
        options.importVocabulary = false
        options.importAPIKeys = true
        options.importLicenseKey = false
        return options
    }

    private static func runImport(
        _ json: String,
        writer: BackupKeyWriterStub
    ) async throws -> ImportResult {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-770-\(UUID().uuidString).json")
        try json.write(to: url, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: url) }

        // Own instance, never `.shared` — see `BackupManager.init`.
        let backup = BackupManager()
        backup.apiKeyWriter = writer
        return await backup.importSettings(from: url, options: keysOnlyOptions())
    }

    // MARK: - v1 importer

    @Test func v1EveryWriteFailingReportsNoKeysAndNamesEachProvider() async throws {
        let writer = BackupKeyWriterStub(refusing: [.openAI, .groq])

        let result = try await Self.runImport(Self.legacyV1JSON, writer: writer)

        // The import is reported, not aborted.
        #expect(result.success)
        #expect(!result.apiKeysImported)
        #expect(result.apiKeysFailedProviders == [.openAI, .groq])
        #expect(writer.written.isEmpty)

        let message = try #require(result.apiKeysFailureMessage)
        #expect(message.contains(KeychainManager.APIKeyType.openAI.displayName))
        #expect(message.contains(KeychainManager.APIKeyType.groq.displayName))
    }

    @Test func v1OneWriteFailingStillRestoresTheOthers() async throws {
        let writer = BackupKeyWriterStub(refusing: [.openAI])

        let result = try await Self.runImport(Self.legacyV1JSON, writer: writer)

        #expect(result.success)
        #expect(result.apiKeysImported)
        #expect(result.apiKeysFailedProviders == [.openAI])
        #expect(writer.written == [.groq])
    }

    @Test func v1CleanRestoreIsUnchanged() async throws {
        let writer = BackupKeyWriterStub(refusing: [])

        let result = try await Self.runImport(Self.legacyV1JSON, writer: writer)

        #expect(result.success)
        #expect(result.apiKeysImported)
        #expect(result.apiKeysFailedProviders.isEmpty)
        #expect(result.apiKeysFailureMessage == nil)
        #expect(writer.written == [.openAI, .groq])
    }

    // MARK: - v2 (universal) importer

    @Test func v2EveryWriteFailingReportsNoKeysAndNamesEachProvider() async throws {
        let writer = BackupKeyWriterStub(refusing: [.openAI, .groq, .geminiTranscribe])

        let result = try await Self.runImport(Self.universalV2JSON, writer: writer)

        #expect(result.success)
        #expect(!result.apiKeysImported)
        #expect(result.apiKeysFailedProviders == [.openAI, .groq, .geminiTranscribe])
        #expect(writer.written.isEmpty)

        let message = try #require(result.apiKeysFailureMessage)
        #expect(message.contains(KeychainManager.APIKeyType.geminiTranscribe.displayName))
    }

    @Test func v2CleanRestoreIsUnchanged() async throws {
        let writer = BackupKeyWriterStub(refusing: [])

        let result = try await Self.runImport(Self.universalV2JSON, writer: writer)

        #expect(result.success)
        #expect(result.apiKeysImported)
        #expect(result.apiKeysFailedProviders.isEmpty)
        #expect(writer.written == [.openAI, .groq, .geminiTranscribe])
    }

    // MARK: - Pure v1 mapping

    @Test func legacyAssignmentsSkipEmptyKeysAndTheRemovedFireworksSlot() throws {
        let data = Data("""
        {"openai": "placeholder-one", "groq": "", "fireworks": "placeholder-old", "meta": "placeholder-four"}
        """.utf8)
        let keys = try JSONDecoder().decode(BackupAPIKeys.self, from: data)

        let providers = BackupManager.legacyAPIKeyAssignments(from: keys).map { $0.provider }

        #expect(providers == [.openAI, .meta])
    }
}
