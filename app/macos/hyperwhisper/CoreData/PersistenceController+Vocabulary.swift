//
//  PersistenceController+Vocabulary.swift
//  hyperwhisper
//
//  Vocabulary reads and writes.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    // MARK: - RecordingSession Operations
    //
    // NOTE: Session create/update/delete live on RecordingSessionManager +
    // performWriteRequiringSave (serial background writer context), NOT here.
    // A pre-refactor synchronous pair (`createRecordingSession(deviceId:...)` /
    // `updateRecordingSessionWithResult`) used to live in this section — it
    // inserted + saved a RecordingSession directly on `container.viewContext`
    // (main thread, no `perform`/`performAndWait`), which reproduced Sentry
    // HYPERWHISPER-SH / HYPERWHISPER-R0 ("DB on Main Thread at Recording
    // Start"). It had no remaining callers after the async rewrite, so it was
    // deleted rather than fixed in place — do not reintroduce a synchronous
    // viewContext insert/save for RecordingSession here.

    // MARK: - Vocabulary Operations

    /// Splits a vocabulary add-field value into one word per non-empty line,
    /// each trimmed (issue #1475). A pasted list becomes one word per line
    /// instead of one word with newlines inside it. Single-line input comes
    /// back as one trimmed word; blank input comes back empty.
    nonisolated static func vocabularyWordLines(from text: String) -> [String] {
        text.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }

    /// Adds a new vocabulary item to Core Data
    /// - Parameters:
    ///   - word: The word/phrase to recognize
    ///   - replacement: Optional replacement text
    /// - Returns: True if added successfully, false if duplicate
    func addVocabularyItem(
        word: String,
        replacement: String?,
        excludingId: UUID? = nil,
        source: String? = "manual"
    ) -> Bool {
        let context = container.viewContext

        // Normalize input (trim whitespace)
        let normalizedWord = word.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalizedReplacement = replacement?.trimmingCharacters(in: .whitespacesAndNewlines)

        // Reject empty/whitespace-only words. An empty word would later compile
        // to the regex "\b\b" in VocabularyProcessor, matching every word
        // boundary and corrupting the entire transcript.
        guard !normalizedWord.isEmpty else {
            return false
        }

        // A word is one line (issue #1475). Callers split a multi-line value
        // with `vocabularyWordLines(from:)` first. The replacement may keep
        // its line breaks: a multi-line snippet is allowed.
        guard normalizedWord.rangeOfCharacter(from: .newlines) == nil else {
            return false
        }

        // Check for duplicate (exclude the item being edited)
        if vocabularyItemExists(word: normalizedWord, excludingId: excludingId) {
            return false
        }
        
        // Create new vocabulary item
        let vocabItem = Vocabulary(context: context)
        vocabItem.id = UUID()
        vocabItem.word = normalizedWord
        vocabItem.replacement = normalizedReplacement?.isEmpty == true ? nil : normalizedReplacement
        vocabItem.setValue(source, forKey: "source")
        vocabItem.createdDate = Date()
        
        // Set sort order (add to end)
        let maxSortOrder = fetchAllVocabularyItems().map { $0.sortOrder }.max() ?? 0
        vocabItem.sortOrder = maxSortOrder + 1
        
        save()
        return true
    }
    
    /// Checks if a vocabulary item with the given word already exists
    /// - Parameter word: The word to check
    /// - Returns: True if exists, false otherwise
    func vocabularyItemExists(word: String, excludingId: UUID? = nil) -> Bool {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        let trimmedWord = word.trimmingCharacters(in: .whitespacesAndNewlines)

        if let excludingId = excludingId {
            request.predicate = NSCompoundPredicate(andPredicateWithSubpredicates: [
                NSPredicate(format: "word ==[c] %@", trimmedWord),
                NSPredicate(format: "id != %@", excludingId as CVarArg)
            ])
        } else {
            request.predicate = NSPredicate(format: "word ==[c] %@", trimmedWord)
        }
        request.fetchLimit = 1

        do {
            let count = try container.viewContext.count(for: request)
            return count > 0
        } catch {
            AppLogger.coreData.error("Failed to check vocabulary existence: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to check vocabulary existence", tags: ["component": "PersistenceController", "operation": "vocabularyItemExists"])
            return false
        }
    }
    
    /// Deletes a vocabulary item
    /// - Parameter item: The vocabulary item to delete
    func deleteVocabularyItem(_ item: Vocabulary) {
        let context = container.viewContext
        context.delete(item)
        save()
    }
    
    /// Deletes a vocabulary item by ID
    /// - Parameter id: The ID of the vocabulary item to delete
    func deleteVocabularyItem(byId id: UUID) {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        request.predicate = NSPredicate(format: "id == %@", id as CVarArg)
        request.fetchLimit = 1
        
        do {
            if let item = try container.viewContext.fetch(request).first {
                deleteVocabularyItem(item)
            }
        } catch {
            AppLogger.logCoreData(.delete(entity: "Vocabulary"), error: error)
            SentryService.capture(error: error, message: "Failed to delete vocabulary item", tags: ["component": "PersistenceController", "operation": "deleteVocabularyItem"])
        }
    }
    
    /// Fetch all vocabulary entries on a background context to avoid blocking the main thread.
    /// Returns thread-safe value-type snapshots instead of managed objects, so callers can
    /// hold them in callbacks that fire on arbitrary threads.
    /// Same rationale as `fetchModeSnapshotInBackground` (DB on Main Thread during Recording Start).
    func fetchVocabularyEntriesInBackground() async -> [VocabularyEntrySnapshot] {
        let context = container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
            request.sortDescriptors = [NSSortDescriptor(keyPath: \Vocabulary.word, ascending: true)]
            do {
                return try context.fetch(request).compactMap { item in
                    guard let word = item.word else { return nil }
                    return VocabularyEntrySnapshot(word: word, replacement: item.replacement)
                }
            } catch {
                AppLogger.coreData.error("Failed to fetch vocabulary entries: \(error, privacy: .public)")
                SentryService.capture(error: error, message: "Failed to fetch vocabulary entries", tags: ["component": "PersistenceController", "operation": "fetchVocabularyEntriesInBackground"])
                return []
            }
        }
    }

    /// Fetches all vocabulary items sorted by word
    /// - Returns: Array of vocabulary items
    func fetchAllVocabularyItems() -> [Vocabulary] {
        let request: NSFetchRequest<Vocabulary> = Vocabulary.fetchRequest()
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Vocabulary.word, ascending: true)]
        
        do {
            return try container.viewContext.fetch(request)
        } catch {
            AppLogger.coreData.error("Failed to fetch vocabulary: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to fetch vocabulary", tags: ["component": "PersistenceController", "operation": "fetchAllVocabularyItems"])
            return []
        }
    }
}
