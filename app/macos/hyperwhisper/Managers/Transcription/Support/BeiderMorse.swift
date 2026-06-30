//
//  BeiderMorse.swift
//  hyperwhisper
//
//  Swift wrapper around the HyperWhisper shared Rust core (hw-core) for
//  Beider-Morse phonetic encoding. Used by PhoneticVocabularyMatcher to match
//  misrecognized words to user vocabulary.
//
//  Backed by `phoneticEncode(word:)` from the UniFFI-generated
//  `hyperwhisper_core.swift` binding (shared-core-rs/crates/hw-phonetic),
//  which replaced the old hand-written `bm_encode`/`bm_free` C ABI.
//

import Foundation

/// Swift wrapper for the Beider-Morse phonetic encoding algorithm.
/// Encodes words into phonetic representations for sound-based comparison.
enum BeiderMorse {

    /// Encode a word into its Beider-Morse phonetic representations.
    /// Returns an array of phonetic codes (the algorithm may produce multiple alternatives).
    /// Returns an empty array for empty input.
    static func encode(_ word: String) -> [String] {
        guard !word.isEmpty else { return [] }
        return phoneticEncode(word: word)
    }
}
