//
//  PhoneticVocabularyMatcher.swift
//  hyperwhisper
//
//  PHONETIC VOCABULARY MATCHER
//  Uses the Beider-Morse phonetic algorithm to match misrecognized words
//  in Parakeet transcription output to user-defined vocabulary entries.
//
//  This catches phonetically similar errors that exact string matching misses,
//  e.g. "hyper wisper" → "HyperWhisper", "pair a keet" → "Parakeet".
//
//  Based on the approach used by Murmure (github.com/Kieirra/murmure).
//

import Foundation
import os

/// Matches transcription words against user vocabulary using phonetic similarity.
class PhoneticVocabularyMatcher {

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "PhoneticVocabularyMatcher")

    /// A pre-encoded vocabulary entry for phonetic comparison.
    private struct EncodedEntry {
        let originalWord: String
        let phoneticCodes: [String]
    }

    /// Pre-encoded vocabulary for fast matching.
    private var encodedVocabulary: [EncodedEntry] = []

    /// Initialize the matcher with vocabulary items.
    /// Pre-encodes all vocabulary words phonetically for efficient matching.
    ///
    /// - Parameter vocabulary: Vocabulary items to match against.
    ///   Items without a replacement are used as-is (the word itself is the correct spelling).
    ///   Items with a replacement are ignored here (handled by VocabularyProcessor's regex replacement).
    init(vocabulary: [Vocabulary]) {
        for item in vocabulary {
            guard let word = item.word?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !word.isEmpty else { continue }

            // Skip items that have explicit replacements — those are handled by regex replacement
            if let replacement = item.replacement, !replacement.isEmpty {
                continue
            }

            // Skip very short words (≤2 chars) to avoid false positives
            guard word.count > 2 else { continue }

            let codes = BeiderMorse.encode(word)
            guard !codes.isEmpty else { continue }

            encodedVocabulary.append(EncodedEntry(originalWord: word, phoneticCodes: codes))
        }

        if !encodedVocabulary.isEmpty {
            logger.info("Phonetic matcher initialized with \(self.encodedVocabulary.count) vocabulary entries")
        }
    }

    /// Apply phonetic vocabulary matching to transcribed text.
    /// For each word in the transcription, checks if it phonetically matches a vocabulary entry.
    /// If a match is found, replaces the transcribed word with the correct vocabulary spelling.
    ///
    /// - Parameter text: Raw transcription text from Parakeet.
    /// - Returns: Text with phonetically matched vocabulary corrections applied.
    func apply(to text: String) -> String {
        guard !encodedVocabulary.isEmpty else { return text }

        var corrected = text
        let words = text.components(separatedBy: .whitespaces).filter { !$0.isEmpty }

        for word in words {
            // Skip very short words to avoid false positives
            guard word.count > 2 else { continue }

            // Strip trailing punctuation for matching; the replacement preserves the
            // original punctuation via the \b-anchored regex below (it sits outside the
            // word characters), so no manual re-attachment is needed.
            let (cleanWord, _) = stripTrailingPunctuation(word)
            guard !cleanWord.isEmpty else { continue }

            let candidateCodes = BeiderMorse.encode(cleanWord)
            guard !candidateCodes.isEmpty else { continue }

            // Check against each vocabulary entry
            for entry in encodedVocabulary {
                // Skip if the word already matches the vocabulary entry exactly (case-insensitive)
                if cleanWord.caseInsensitiveCompare(entry.originalWord) == .orderedSame {
                    break
                }

                // Check if any phonetic code matches
                let hasMatch = entry.phoneticCodes.contains { dictCode in
                    candidateCodes.contains(dictCode)
                }

                if hasMatch {
                    // Word-boundary anchored, escaped regex replace (matches VocabularyProcessor).
                    // Replace only the cleanWord that actually matched so substrings of other
                    // words are left intact (e.g. "Kat" no longer mangles "category"/"scatter").
                    // The \b sits between the word chars and any trailing punctuation, so the
                    // source punctuation survives untouched.
                    let pattern = "\\b\(NSRegularExpression.escapedPattern(for: cleanWord))\\b"
                    corrected = corrected.replacingOccurrences(
                        of: pattern,
                        with: NSRegularExpression.escapedTemplate(for: entry.originalWord),
                        options: [.regularExpression, .caseInsensitive]
                    )
                    logger.debug("Phonetic match: '\(word, privacy: .public)' → '\(entry.originalWord, privacy: .public)'")
                    break // Use first match
                }
            }
        }

        return corrected
    }

    /// Strip trailing punctuation from a word, returning the clean word and the punctuation.
    private func stripTrailingPunctuation(_ word: String) -> (String, String) {
        var clean = word
        var punctuation = ""
        while let last = clean.last, last.isPunctuation {
            punctuation = String(last) + punctuation
            clean.removeLast()
        }
        return (clean, punctuation)
    }
}
