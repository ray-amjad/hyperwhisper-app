//
//  VocabularyEgressNormalizationTests.swift
//  hyperwhisperTests
//
//  The vocabulary-egress rule — sanitize, drop empties, de-duplicate
//  case-insensitively, then cap — now lives in the shared Rust core
//  (`hw-net::helpers::keyword_boost_terms`, exported as
//  `normalizeVocabularyTerms`). macOS has two callers, and they deliberately
//  disagree about the cap and the join, which is exactly what these lock:
//
//  - `RustCoreMapping.boostVocabularyTerms` (batch egress) — UNCAPPED, no join.
//  - `RecordingTranscriptionFlow.buildVocabularyString` (streaming) — capped at
//    100 (Deepgram's limit), joined with ",".
//
//  These run through the real FFI, so they are the macOS half of a
//  cross-platform parity check: the Windows smoke suite
//  ("StreamingTranscriptionSessionFactory.BuildVocabulary normalizes through the
//  shared core") and HyperWhisper.SharedCore.Tests assert the same rule.
//

import Testing
@testable import HyperWhisper

struct VocabularyEgressNormalizationTests {
    private let messy = [
        "  API  ",
        "api",
        "Rust<script>",
        "multi\n  word",
        "   ",
        "",
    ]

    @Test func sanitizesDropsEmptiesAndDedupesCaseInsensitively() {
        // First-seen casing and order survive; "api" folds into "  API  ".
        #expect(normalizeVocabularyTerms(words: messy, limit: nil)
            == ["API", "Rustscript", "multi word"])
    }

    @Test func capsAtTheCallerSuppliedLimit() {
        #expect(normalizeVocabularyTerms(words: messy, limit: 2) == ["API", "Rustscript"])
        // 0 is `.prefix(0)`, NOT "uncapped".
        #expect(normalizeVocabularyTerms(words: messy, limit: 0).isEmpty)
    }

    @Test func sanitizationTruncatesALongTermAtEightyCharacters() {
        let terms = normalizeVocabularyTerms(words: [String(repeating: "x", count: 150)], limit: nil)
        #expect(terms.count == 1)
        #expect(terms.first?.count == 80)
    }

    @Test func theStreamingBuilderCapIsOneHundredAndItsJoinIsAComma() {
        let words = (0..<150).map { "term\($0)" }
        let terms = normalizeVocabularyTerms(words: words, limit: 100)
        #expect(terms.count == 100)
        #expect(terms.last == "term99")
        #expect(terms.joined(separator: ",").hasPrefix("term0,term1,term2,"))
    }
}
