//
//  PersistenceController+VocabularyTransfer.swift
//  hyperwhisper
//
//  The store side of vocabulary import/export (issue #2409). Parsing and
//  writing the files is `VocabularyTransfer`; this merges parsed rows into
//  Core Data and reads the list back out for export.
//

import CoreData
import Foundation

/// What an import did, for the Vocabulary page's result line.
struct VocabularyImportSummary: Equatable, Sendable {
    /// New terms appended to the list.
    let added: Int
    /// Existing terms whose replacement the file changed.
    let updated: Int
    /// Words dropped for being longer than `vocabularyMaxWordChars`.
    let skipped: Int
}

extension PersistenceController {

    /// The longest vocabulary word an import accepts, in UTF-16 units. The
    /// same limit as .NET's `VocabularyWords.MaxWordChars` (#2390).
    nonisolated static let vocabularyMaxWordChars = 1000

    /// Merges parsed file rows into the vocabulary and saves once.
    ///
    /// - A new word (case-insensitive, trimmed) is appended at the end, in file
    ///   order, with the next `sortOrder` — `addVocabularyItem`'s "add to end".
    /// - A word already in the list keeps its spelling and position; it takes
    ///   the file's replacement only when the file gives a non-empty one, so a
    ///   words-only TXT never wipes a replacement.
    /// - A word repeated in the file keeps the first occurrence's position; a
    ///   later non-empty replacement for it wins (last non-empty wins).
    ///
    /// Nothing is written when the save fails: the staged changes are rolled
    /// back and the error is thrown.
    @MainActor
    func importVocabularyEntries(
        _ parsed: VocabularyTransferParseResult,
        source: String = "manual"
    ) throws -> VocabularyImportSummary {
        let context = container.viewContext

        var byKey: [String: Vocabulary] = [:]
        var maxSortOrder: Int16 = 0
        for item in fetchAllVocabularyItems() {
            maxSortOrder = max(maxSortOrder, item.sortOrder)
            guard let word = item.word else { continue }
            let key = Self.vocabularyMergeKey(word)
            // Keep the first row a CloudKit duplicate pair offers; the page's
            // dedup pass collapses the pair separately.
            if byKey[key] == nil { byKey[key] = item }
        }

        var added = 0
        var updatedIDs = Set<NSManagedObjectID>()
        var insertedIDs = Set<ObjectIdentifier>()

        for entry in parsed.entries {
            let word = entry.word.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !word.isEmpty, word.rangeOfCharacter(from: .newlines) == nil else { continue }
            let trimmedReplacement = entry.replacement?.trimmingCharacters(in: .whitespacesAndNewlines)
            let replacement = (trimmedReplacement?.isEmpty ?? true) ? nil : trimmedReplacement

            let key = Self.vocabularyMergeKey(word)
            if let existing = byKey[key] {
                if let replacement, existing.replacement != replacement {
                    existing.replacement = replacement
                    if !insertedIDs.contains(ObjectIdentifier(existing)) {
                        updatedIDs.insert(existing.objectID)
                    }
                }
                continue
            }

            let item = Vocabulary(context: context)
            item.id = UUID()
            item.word = word
            item.replacement = replacement
            item.setValue(source, forKey: "source")
            item.createdDate = Date()
            // Int16: clamp at .max (as the backup import does) rather than trap.
            maxSortOrder = maxSortOrder == .max ? .max : maxSortOrder + 1
            item.sortOrder = maxSortOrder
            byKey[key] = item
            insertedIDs.insert(ObjectIdentifier(item))
            added += 1
        }

        if context.hasChanges {
            do {
                try context.save()
            } catch {
                context.rollback()
                AppLogger.coreData.error("Vocabulary import save failed: \(error, privacy: .public)")
                throw error
            }
        }

        AppLogger.coreData.info("Vocabulary import: \(added) added, \(updatedIDs.count) updated, \(parsed.skipped) skipped")
        return VocabularyImportSummary(added: added, updated: updatedIDs.count, skipped: parsed.skipped)
    }

    /// The vocabulary in the Vocabulary page's display order (sorted by word,
    /// the page's `@FetchRequest` sort), as rows an export writes.
    @MainActor
    func vocabularyEntriesForExport() -> [VocabularyTransferEntry] {
        fetchAllVocabularyItems().compactMap { item in
            guard let word = item.word, !word.isEmpty else { return nil }
            let replacement = (item.replacement?.isEmpty ?? true) ? nil : item.replacement
            return VocabularyTransferEntry(word: word, replacement: replacement)
        }
    }

    /// Case-insensitive identity of a word, the same folding the page's
    /// CloudKit dedup pass uses (`İ` folds to `I`; accents stay distinct).
    nonisolated static func vocabularyMergeKey(_ word: String) -> String {
        word.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "\u{0130}", with: "I")
            .folding(options: [.caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))
    }
}
