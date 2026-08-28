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

    // MARK: - Local Provider Replacement Helper

    /// Apply a single substring vocabulary replacement, the way the on-device
    /// providers do it.
    ///
    /// This is deliberately NOT `applyHardenedReplacement` above. The local
    /// providers (Apple Speech Analyzer, Nemotron, Parakeet, Qwen3-ASR) run an
    /// unanchored, diacritic-insensitive substring pass over their own raw
    /// output before the pipeline's batch pass ever sees it, and each one used
    /// to carry its own private `applyVocabulary` copy. The four copies were
    /// identical, so they are unified here unchanged — the semantics stay:
    /// - both `word` and `replacement` are trimmed, and an empty trimmed word or
    ///   an empty trimmed replacement is a no-op,
    /// - matching is `.caseInsensitive` AND `.diacriticInsensitive`,
    /// - matching is plain substring matching, with no `\b…\b` word boundary.
    ///
    /// Keep this next to `applyHardenedReplacement` so the two rule sets stay
    /// visible side by side rather than hidden in four provider files.
    ///
    /// Now a thin shim over the shared Rust core (`hw-phonetic`,
    /// `applySubstringVocabulary`), so Windows and Linux — which had no
    /// counterpart at all — run the same rules (issue #283). Foundation's
    /// `.diacriticInsensitive` option splices the replacement into the ORIGINAL
    /// text at the original range, so the core does the same via an
    /// NFD-folded-to-original byte-offset map rather than returning a folded
    /// string. The transcript is deliberately NOT normalized here: text outside
    /// a match comes back byte-identical, exactly as Foundation left it.
    static func applySubstringReplacement(to text: String, word: String, replacement: String) -> String {
        HyperWhisper.applySubstringVocabulary(
            text: text,
            entries: [HwVocabularyEntry(word: word, replacement: replacement)]
        )
    }

    /// Apply every entry of `vocabulary` to `text` with the local-provider
    /// substring rules, in list order.
    ///
    /// Entries with a nil `word` or a nil `replacement` are skipped, matching
    /// the `guard let … else { continue }` the provider copies used. One core
    /// call for the whole list.
    static func applySubstringVocabulary(to text: String, vocabulary: [Vocabulary]) -> String {
        HyperWhisper.applySubstringVocabulary(
            text: text,
            entries: vocabulary.map {
                HwVocabularyEntry(word: $0.word ?? "", replacement: $0.replacement)
            }
        )
    }

    // MARK: - Phonetic Matcher

    /// Apply phonetic (Beider-Morse) vocabulary matching to transcribed text.
    ///
    /// Replaces `PhoneticVocabularyMatcher`, which was the same eight-step
    /// program as the Windows `PhoneticVocabularyMatcher.cs` and had drifted
    /// from it (issue #283). The policy now lives in `hw-phonetic` and this is
    /// ONE core call for the whole transcript: the old shape built a matcher per
    /// transcription and encoded one word per call, so a 40-entry vocabulary
    /// over a 300-word transcript crossed the boundary ~340 times.
    ///
    /// The core returns every correction rather than logging any of them, so the
    /// log line below stays here, on `os.Logger`, with its own privacy
    /// annotations.
    ///
    /// Behaviour differences the shared policy settles, all documented in
    /// `shared-conformance/phonetic-vectors.json`: tokens now split on ALL
    /// whitespace (the old `CharacterSet.whitespaces` excluded newlines, so a
    /// multi-line transcript silently lost every correction after line 1), the
    /// `<=2`-character gate counts Unicode scalars rather than graphemes, both
    /// inputs are NFC-normalized, and the exact-hit short-circuit now protects
    /// a word that matches ANY vocabulary entry rather than only the first.
    static func applyPhoneticVocabulary(to text: String, vocabulary: [Vocabulary]) -> String {
        let result = phoneticApplyVocabulary(
            text: text,
            entries: vocabulary.map {
                HwVocabularyEntry(word: $0.word ?? "", replacement: $0.replacement)
            }
        )

        if result.entryCount > 0 {
            AppLogger.transcription.info(
                "Phonetic matcher ran with \(result.entryCount, privacy: .public) vocabulary entries")
        }
        for match in result.matches {
            AppLogger.transcription.debug(
                "Phonetic match: '\(match.token, privacy: .public)' → '\(match.replacement, privacy: .public)'")
        }

        return result.text
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
