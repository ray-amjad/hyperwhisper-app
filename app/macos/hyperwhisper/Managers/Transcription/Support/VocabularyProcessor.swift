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

    // MARK: - Local Engine Correction

    /// Run a local engine's own vocabulary correction over its raw text, once.
    ///
    /// Only an engine that conforms to `LocalVocabularyCorrecting` has one (the
    /// Parakeet family's phonetic pass); every other provider, Whisper and every
    /// cloud provider included, gets `rawText` back unchanged.
    ///
    /// The caller runs this AFTER it has kept `rawText` as the raw transcript
    /// (the History row's `transcribedText`) and BEFORE
    /// `applyVocabularyReplacements`. It used to run inside the engine, so the
    /// raw transcript already held the swaps (issue #1622, as Windows #1596).
    ///
    /// There used to be a second local pass, too: an unanchored,
    /// diacritic-insensitive substring pass (`applySubstringVocabulary`) that
    /// all four on-device providers (Parakeet, Nemotron, Qwen3-ASR, Apple Speech)
    /// ran over their output. It is gone (issue #1622, Ray's decision of
    /// 2026-10-09). It matched inside words ("art" -> "ART" turned "quarterly"
    /// into "quARTerly"), and the pipeline's `\b` pass then applied every
    /// replacement row a second time ("fox" -> "fox terrier" gave
    /// "fox terrier terrier"). Replacement rows are now applied by
    /// `applyVocabularyReplacements` alone, whole words only, as on Whisper.
    static func applyLocalEngineVocabularyCorrection(
        to rawText: String,
        provider: TranscriptionProvider,
        vocabulary: [Vocabulary]
    ) -> String {
        guard let corrector = provider as? LocalVocabularyCorrecting,
              !vocabulary.isEmpty,
              !rawText.isEmpty else {
            return rawText
        }
        let corrected = corrector.applyLocalVocabularyCorrection(to: rawText, vocabulary: vocabulary)
        // A correction must never blank a transcript the engine produced.
        return corrected.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? rawText : corrected
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
        // The count only, never the words: a match says what the user dictated,
        // and `.public` text reaches `log show` and the diagnostics export (#1647).
        if !result.matches.isEmpty {
            AppLogger.transcription.debug(
                "Phonetic match: \(result.matches.count, privacy: .public) token(s) corrected")
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
        // STEP 1: VOCABULARY REPLACEMENT PHASE
        // Fetch vocabulary from Core Data
        // Only processes vocabulary items that have a replacement value
        // Items without replacements are already handled by Whisper's prompt mechanism
        Self.applyVocabularyReplacements(
            text,
            vocabulary: PersistenceController.shared.fetchAllVocabularyItems()
        )
    }

    /// `applyVocabularyReplacements(_:mode:)` over a vocabulary the caller
    /// passes in, so a test can drive the real pass without the shared store.
    static func applyVocabularyReplacements(_ text: String, vocabulary: [Vocabulary]) -> String {
        var processed = text

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
