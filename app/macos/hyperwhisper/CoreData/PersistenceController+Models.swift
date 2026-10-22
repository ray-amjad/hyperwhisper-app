//
//  PersistenceController+Models.swift
//  hyperwhisper
//
//  Value types and the mode-name policy the controller hands out.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

// MARK: - Mode Name Policy

enum ModeNamePolicy {
    static func normalized(_ name: String) -> String? {
        let canonical = name.precomposedStringWithCanonicalMapping
        let characters = Array(canonical)
        guard let first = characters.firstIndex(where: hasMeaningfulScalar),
              let last = characters.lastIndex(where: hasMeaningfulScalar) else {
            return nil
        }

        return String(characters[first...last]).precomposedStringWithCanonicalMapping
    }

    static func storageName(_ proposed: String, replacing existing: String?) -> String {
        normalized(proposed) ?? existing.flatMap { normalized($0) } ?? "Untitled"
    }

    static func comparisonKey(_ name: String) -> String? {
        normalized(name)?.folding(
            options: [.caseInsensitive],
            locale: Locale(identifier: "en_US_POSIX")
        )
    }

    private static func hasMeaningfulScalar(_ character: Character) -> Bool {
        character.unicodeScalars.contains(where: isMeaningful)
    }

    private static func isMeaningful(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.properties.generalCategory {
        case .control, .format, .spaceSeparator, .lineSeparator, .paragraphSeparator,
             .nonspacingMark, .spacingMark, .enclosingMark, .unassigned:
            return false
        default:
            return true
        }
    }
}

// MARK: - Mode Snapshot

/// Thread-safe, value-type copy of the Mode properties needed for background
/// validation during recording start. Core Data managed objects are tied to
/// their context's queue, so this snapshot allows safe access from any thread.
struct ModeSnapshot: Sendable {
    let id: UUID
    let name: String
    let model: String
    /// The Mode's transcription language, exactly as stored — including the
    /// literal `"auto"`. Kept raw so the snapshot stays a faithful copy;
    /// `ModeSnapshot.effectiveLanguage(_:)` below is what maps `"auto"` onto
    /// `nil` for the providers.
    /// Added for #318: the language is a model-preparation input (it selects the
    /// English-optimized whisper weights), so anything keyed on this snapshot
    /// has to be able to see it change.
    let language: String?
    let cloudProvider: String
    let rawCloudProvider: String?
    let postProcessingMode: Int16
    let postProcessingProvider: String
    let rawPostProcessingProvider: String?
    let languageModel: String?
    let enableScreenOCR: Bool
    let sortOrder: Int16

    init(_ mode: Mode) {
        self.id = mode.id ?? UUID()
        self.name = mode.name ?? "Default"
        self.model = mode.model ?? "base"
        self.language = mode.language
        self.cloudProvider = CloudProvider.effectiveModeStorageValue(mode.cloudProvider)
        self.rawCloudProvider = mode.cloudProvider
        self.postProcessingMode = mode.postProcessingMode
        self.postProcessingProvider = mode.postProcessingProvider ?? "hyperwhisper"
        self.rawPostProcessingProvider = mode.postProcessingProvider
        self.languageModel = mode.languageModel
        self.enableScreenOCR = mode.enableScreenOCR
        self.sortOrder = mode.sortOrder
    }

    /// The language value that actually reaches a transcription provider:
    /// lowercased, with the sentinel `"auto"` collapsed onto `nil` so the
    /// provider auto-detects.
    ///
    /// The ONE copy of this rule (#318). It used to be written out four times —
    /// `TranscriptionModelManager.extractLanguage(from:)`,
    /// `TranscriptionProviderRouter.extractLanguage(from:)`, the `languageArg`
    /// closure in `TranscriptionPipeline.transcribeWithDetails`, and
    /// `ASRPreparationKey.init` — and the last of those is a cache KEY over the
    /// first: if one copy were ever hardened (trim whitespace, treat `""` as
    /// unset) and the key's were not, a Mode whose language is `" auto"` would
    /// get a changed effective language while the key compared equal,
    /// `removeDuplicates()` would swallow the emission and the model would never
    /// re-prepare. That is #318 again, on the language axis. Harden it here, and
    /// the key moves with the providers by construction.
    ///
    /// Lives on `ModeSnapshot` because the key reads a snapshot while the other
    /// three read a `Mode`; a `String?` in and a `String?` out is the only shape
    /// all four can share.
    static func effectiveLanguage(_ raw: String?) -> String? {
        guard let lowered = raw?.lowercased() else { return nil }
        return lowered == "auto" ? nil : lowered
    }
}

// MARK: - Vocabulary Entry Snapshot

/// Thread-safe, value-type copy of a Vocabulary entry for use on the recording
/// hot path. Core Data managed objects are tied to their context's queue, so
/// this snapshot allows safe access from streaming callbacks on any thread.
struct VocabularyEntrySnapshot: Sendable {
    let word: String
    let replacement: String?
}

// MARK: - Auto-Delete Transaction Snapshot

/// Plain values produced by the serial writer after an auto-delete transaction
/// commits. No managed object or context crosses the writer queue boundary.
struct AutoDeleteTransactionSnapshot: Sendable {
    let audioPaths: [String]
    let transcriptsDeleted: Int
    let hasMore: Bool
    /// The deleted rows, whose running retries must stop (#1743).
    var transcriptIDs: [UUID] = []
}
