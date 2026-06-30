//
//  VocabularyProcessor.swift
//  hyperwhisper
//
//  VOCABULARY PROCESSOR
//  This class handles custom vocabulary replacements after transcription.
//
//  Key Features:
//  - Vocabulary replacements (e.g., "ETA" → "estimated time of arrival")
//
//  Architecture Notes:
//  - Extracted from TranscriptionPipeline to separate concerns
//  - Only handles vocabulary replacements (punctuation/capitalization/profanity are handled by AI prompts)
//  - Uses regex for vocabulary replacements to ensure word boundaries
//

import Foundation

/// Handles custom vocabulary replacements for transcribed text
class VocabularyProcessor {

    // MARK: - Shared Replacement Helper

    /// Apply a single hardened, word-boundary-anchored vocabulary replacement.
    ///
    /// This is the canonical per-word logic shared by the batch
    /// (`applyVocabularyReplacements`) and streaming
    /// (`RecordingTranscriptionFlow.applyStreamingVocabulary`) paths so they
    /// behave identically:
    /// - both `word` and `replacement` are trimmed, and an empty trimmed word or
    ///   empty trimmed replacement is a no-op (an empty `word` would build the
    ///   pattern "\b\b", which matches at every word boundary and injects the
    ///   replacement throughout the transcript; trimming the replacement keeps
    ///   the batch and streaming callers identical — e.g. " Katherine " inserts
    ///   "Katherine", not " Katherine " with stray spaces),
    /// - the word is `escapedPattern`-quoted and wrapped in `\b…\b` so only
    ///   standalone occurrences match (no substring mangling),
    /// - the replacement is `escapedTemplate`-quoted so "$1"/"$&"/"\" are treated
    ///   as literal text rather than regex template references,
    /// - matching is case-insensitive (mirrors the batch matcher; deliberately
    ///   NOT diacritic-insensitive so streaming and batch stay consistent).
    /// Now a thin shim over the shared Rust core (`hw-text`,
    /// `applyHardenedReplacement`) so macOS and Windows apply vocabulary
    /// identically. Normalizes the transcript and search word to NFC first
    /// because regex matching is code-unit based and does not treat canonically
    /// equivalent accented text as equal. Module-qualified to defeat
    /// member-shadowing of the same-named global binding func.
    static func applyHardenedReplacement(to text: String, word: String, replacement: String) -> String {
        HyperWhisper.applyHardenedReplacement(
            text: text.precomposedStringWithCanonicalMapping,
            word: word.precomposedStringWithCanonicalMapping,
            replacement: replacement
        )
    }

    // MARK: - Public Methods

    /// Apply custom vocabulary replacements to transcribed text
    ///
    /// This method processes vocabulary items that have replacement values.
    /// Items without replacements are handled by Whisper's prompt mechanism.
    ///
    /// - Parameters:
    ///   - text: Raw transcription text
    ///   - mode: Transcription mode (currently unused, kept for API compatibility)
    /// - Returns: Text with vocabulary replacements applied
    func applyVocabularyReplacements(_ text: String, mode: Mode?) -> String {
        var processed = text

        // STEP 1: VOCABULARY REPLACEMENT PHASE
        // Fetch vocabulary from Core Data
        // Only processes vocabulary items that have a replacement value
        // Items without replacements are already handled by Whisper's prompt mechanism
        let vocabulary = PersistenceController.shared.fetchAllVocabularyItems()

        for vocabItem in vocabulary {
            if let word = vocabItem.word,
               !word.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
               let replacement = vocabItem.replacement,
               !replacement.isEmpty {
                // Hardened per-word replacement (trim + \b…\b boundaries +
                // escapedPattern/escapedTemplate, case-insensitive). The guard
                // against empty/whitespace-only words — which would otherwise
                // build "\b\b" / "\b \b" and corrupt the whole transcript — lives
                // inside the shared helper, mirroring the trim-then-check guard
                // used on the add/import paths. Legacy, CloudKit-synced, or
                // migrated rows may still carry such values even though the UI no
                // longer persists them.
                let before = processed
                processed = Self.applyHardenedReplacement(to: processed, word: word, replacement: replacement)

                // Log replacements for debugging
                if processed != before {
                    AppLogger.transcription.debug("Applied vocabulary replacement: \(word) → \(replacement)")
                }
            }
        }

        // Trim whitespace and return final result
        return processed.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
