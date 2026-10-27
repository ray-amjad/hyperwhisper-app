//
//  BackupManager+Inspect.swift
//  hyperwhisper
//
//  Inspect: what a chosen backup file holds, the vocabulary merge preview,
//  and the user-facing decode error text.
//  Split out of BackupManager.swift (#732); same class, same behaviour.
//

import Foundation
import SwiftUI
import AppKit

extension BackupManager {

    /// Pre-parses a chosen backup file to report which sections it contains, so the import UI can
    /// offer granular, auto-detected options (absent sections are disabled). Sets `lastError` and
    /// returns nil if the file can't be read or isn't a recognized backup.
    func inspectBackupFile(at url: URL) -> BackupContents? {
        guard let data = try? Data(contentsOf: url) else {
            lastError = NSLocalizedString("settings.backup.import.error.read", value: "Failed to read backup file", comment: "")
            return nil
        }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            lastError = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
            return nil
        }

        // Universal v2 file (cross-platform) — macOS supports only its vocabulary section.
        if let schemaVersion = obj["schemaVersion"] as? Int {
            if schemaVersion > UniversalVocabBackup.universalSchemaVersion {
                lastError = NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: "")
                return nil
            }
            let vocabArray = obj["vocabulary"] as? [[String: Any]]
            return BackupContents(
                format: .universalVocab,
                hasSettings: false,
                hasModes: false,
                hasVocabulary: vocabArray != nil,
                vocabularyCount: vocabArray?.count ?? 0,
                hasAPIKeys: false,
                hasLicense: false,
                appVersion: obj["appVersion"] as? String
            )
        }

        // Legacy macOS v1 file.
        if let version = obj["version"] as? Int {
            if version > BackupData.currentVersion {
                lastError = NSLocalizedString("settings.backup.import.error.version", value: "Backup file is from a newer version", comment: "")
                return nil
            }
            let vocabArray = obj["vocabulary"] as? [[String: Any]]
            let apiKeys = obj["apiKeys"] as? [String: Any]
            let license = obj["licenseKey"] as? String
            return BackupContents(
                format: .legacyV1,
                hasSettings: obj["settings"] is [String: Any],
                hasModes: obj["modes"] is [Any],
                hasVocabulary: vocabArray != nil,
                vocabularyCount: vocabArray?.count ?? 0,
                hasAPIKeys: !(apiKeys?.isEmpty ?? true),
                hasLicense: !(license?.isEmpty ?? true),
                appVersion: obj["appVersion"] as? String
            )
        }

        lastError = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
        return nil
    }

    /// Computes how many vocabulary words in a backup file are new vs. already present (matched by
    /// word, case-insensitive), to show a pre-merge summary before applying a vocabulary import.
    /// Returns nil if the file can't be parsed.
    func vocabularyMergePreview(at url: URL) -> (newCount: Int, conflictCount: Int)? {
        guard let data = try? Data(contentsOf: url),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let vocabArray = obj["vocabulary"] as? [[String: Any]] else {
            return nil
        }

        // Snapshot existing words once (lowercased, trimmed) for an O(n) comparison.
        let existing = Set(
            PersistenceController.shared.fetchAllVocabularyItems().compactMap {
                $0.word?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            }
        )

        var newCount = 0
        var conflictCount = 0
        var seenInFile = Set<String>()
        for entry in vocabArray {
            guard let word = entry["word"] as? String else { continue }
            let key = word.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            guard !key.isEmpty, seenInFile.insert(key).inserted else { continue }
            if existing.contains(key) {
                conflictCount += 1
            } else {
                newCount += 1
            }
        }
        return (newCount, conflictCount)
    }

    /// Builds a user-facing decode failure message.
    ///
    /// Keeps the existing localized "Invalid backup file format" prefix (so all
    /// translations still apply) and appends a concise diagnostic — including the
    /// failing field path for `DecodingError`s — so the user has a recovery path
    /// instead of an opaque error.
    nonisolated static func decodeErrorMessage(for error: Error) -> String {
        let prefix = NSLocalizedString("settings.backup.import.error.decode", value: "Invalid backup file format", comment: "")
        guard let detail = decodeErrorDetail(for: error) else { return prefix }
        return "\(prefix)\n\(detail)"
    }

    /// Renders a `DecodingError` into a short, human-readable detail string with
    /// the failing coding path. Returns nil for non-decoding errors.
    nonisolated static func decodeErrorDetail(for error: Error) -> String? {
        guard let decodingError = error as? DecodingError else { return nil }

        func pathString(_ context: DecodingError.Context) -> String {
            context.codingPath.map(\.stringValue).joined(separator: ".")
        }

        switch decodingError {
        case let .keyNotFound(key, context):
            let path = pathString(context)
            let field = path.isEmpty ? key.stringValue : "\(path).\(key.stringValue)"
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.keyNotFound", value: "Missing required field: %@", comment: "Backup import decode error; %@ is the field path"),
                field
            )
        case let .typeMismatch(_, context):
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.typeMismatch", value: "Wrong type for field: %@", comment: "Backup import decode error; %@ is the field path"),
                pathString(context)
            )
        case let .valueNotFound(_, context):
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.valueNotFound", value: "Missing value for field: %@", comment: "Backup import decode error; %@ is the field path"),
                pathString(context)
            )
        case let .dataCorrupted(context):
            let path = pathString(context)
            if path.isEmpty {
                return String(
                    format: NSLocalizedString("settings.backup.import.error.decode.corrupted", value: "Corrupted data: %@", comment: "Backup import decode error; %@ is the underlying reason"),
                    context.debugDescription
                )
            }
            return String(
                format: NSLocalizedString("settings.backup.import.error.decode.corruptedField", value: "Corrupted value for field: %@", comment: "Backup import decode error; %@ is the field path"),
                path
            )
        @unknown default:
            return nil
        }
    }
}
