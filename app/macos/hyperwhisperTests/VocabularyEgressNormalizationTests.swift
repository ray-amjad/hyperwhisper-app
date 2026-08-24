//
//  VocabularyEgressNormalizationTests.swift
//  hyperwhisperTests
//
//  The vocabulary-egress rule — sanitize, drop empties, de-duplicate
//  case-insensitively, then cap — now lives in the shared Rust core
//  (`hw-net::helpers::keyword_boost_terms`, exported as
//  `normalizeVocabularyTerms`). macOS has two callers, and they deliberately
//  disagree about the cap and the join:
//
//  - `RustCoreMapping.boostVocabularyTerms` (batch egress) — UNCAPPED, no join.
//  - `RecordingTranscriptionFlow.buildVocabularyString` (streaming) — capped at
//    100 (Deepgram's limit), joined with ",".
//
//  Every test below drives one of those two PRODUCTION methods, not the bare
//  FFI, so the replacement-pair filter, the cap, the join and the nil-when-empty
//  answer are all covered: deleting any of them fails a test here. That is the
//  same shape as the Windows smoke test ("StreamingTranscriptionSessionFactory
//  .BuildVocabulary normalizes through the shared core"), which drives the real
//  `BuildVocabulary`. Both run the real FFI underneath, so together with
//  HyperWhisper.SharedCore.Tests they are a cross-platform parity check.
//
//  `boostVocabularyTerms` takes Core Data `Vocabulary` rows, so those tests use
//  an in-memory `PersistenceController` — the same approach as
//  `AutoDeleteCleanupServiceTests`, and the reason this suite is `@MainActor`.
//

import CoreData
import Testing
@testable import HyperWhisper

@MainActor
struct VocabularyEgressNormalizationTests {
    private let messy = [
        "  API  ",
        "api",
        "Rust<script>",
        "multi\n  word",
        "   ",
        "",
    ]

    // MARK: - Streaming egress: RecordingTranscriptionFlow.buildVocabularyString

    @Test func streamingBuilderSanitizesDropsEmptiesAndDedupesCaseInsensitively() {
        let result = RecordingTranscriptionFlow.buildVocabularyString(
            from: messy.map { VocabularyEntrySnapshot(word: $0, replacement: nil) })

        // First-seen casing and order survive; "api" folds into "  API  ".
        #expect(result == "API,Rustscript,multi word")
    }

    /// A replacement pair is a post-transcription find-and-replace correction,
    /// not a recognition hint. Sending its `word` would boost the ASR toward the
    /// exact misspelling the user configured a fix for.
    @Test func streamingBuilderExcludesReplacementPairs() {
        let result = RecordingTranscriptionFlow.buildVocabularyString(from: [
            VocabularyEntrySnapshot(word: "Klawd", replacement: "Claude"),
            VocabularyEntrySnapshot(word: "Kubernetes", replacement: nil),
            // An EMPTY replacement is not a pair — the entry is a plain hint.
            VocabularyEntrySnapshot(word: "UniFFI", replacement: ""),
        ])

        #expect(result == "Kubernetes,UniFFI")
    }

    @Test func streamingBuilderCapsAtOneHundredTermsAndJoinsWithAComma() {
        let entries = (0..<150).map { VocabularyEntrySnapshot(word: "term\($0)", replacement: nil) }

        let result = RecordingTranscriptionFlow.buildVocabularyString(from: entries)
        let terms = (result ?? "").components(separatedBy: ",")

        #expect(terms.count == 100)
        #expect(terms.first == "term0")
        #expect(terms.last == "term99")
        // The join is "," with no space — Deepgram/xAI `keyterm` splitting and
        // the HW Cloud `initial_prompt` both depend on it.
        #expect(result?.hasPrefix("term0,term1,term2,") == true)
    }

    @Test func streamingBuilderReturnsNilWhenNothingSurvives() {
        #expect(RecordingTranscriptionFlow.buildVocabularyString(from: []) == nil)
        // Sanitizing away entirely is also "nothing", not an empty string.
        #expect(RecordingTranscriptionFlow.buildVocabularyString(from: [
            VocabularyEntrySnapshot(word: "<>", replacement: nil),
            VocabularyEntrySnapshot(word: "   ", replacement: nil),
        ]) == nil)
        // Every entry being a replacement pair is also "nothing".
        #expect(RecordingTranscriptionFlow.buildVocabularyString(from: [
            VocabularyEntrySnapshot(word: "Klawd", replacement: "Claude"),
        ]) == nil)
    }

    @Test func streamingBuilderTruncatesALongTermAtEightyCharacters() {
        let result = RecordingTranscriptionFlow.buildVocabularyString(from: [
            VocabularyEntrySnapshot(word: String(repeating: "x", count: 150), replacement: nil),
        ])

        #expect(result?.count == 80)
    }

    // MARK: - Batch egress: RustCoreMapping.boostVocabularyTerms

    @Test func batchBuilderSanitizesDropsEmptiesAndDedupesCaseInsensitively() {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let terms = RustCoreMapping.boostVocabularyTerms(
            from: messy.map { insert(word: $0, replacement: nil, into: context) })

        #expect(terms == ["API", "Rustscript", "multi word"])
    }

    @Test func batchBuilderExcludesReplacementPairs() {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let terms = RustCoreMapping.boostVocabularyTerms(from: [
            insert(word: "Klawd", replacement: "Claude", into: context),
            insert(word: "Kubernetes", replacement: nil, into: context),
            insert(word: "UniFFI", replacement: "", into: context),
        ])

        #expect(terms == ["Kubernetes", "UniFFI"])
    }

    /// The batch path is UNCAPPED on purpose: each provider request builder in
    /// the core applies its own cap afterwards. Capping here would spend the
    /// budget before the provider that owns it ever sees the list.
    @Test func batchBuilderIsUncapped() {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let entries = (0..<150).map { insert(word: "term\($0)", replacement: nil, into: context) }

        let terms = RustCoreMapping.boostVocabularyTerms(from: entries)

        #expect(terms.count == 150)
        #expect(terms.last == "term149")
    }

    @Test func batchBuilderReturnsNoTermsWhenNothingSurvives() {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        #expect(RustCoreMapping.boostVocabularyTerms(from: []).isEmpty)
        #expect(RustCoreMapping.boostVocabularyTerms(from: [
            insert(word: "<>", replacement: nil, into: context),
            insert(word: "Klawd", replacement: "Claude", into: context),
        ]).isEmpty)
    }

    // MARK: - Helpers

    /// An unsaved in-memory `Vocabulary` row. `boostVocabularyTerms` only reads
    /// `word` / `replacement`, so there is nothing to persist.
    private func insert(
        word: String,
        replacement: String?,
        into context: NSManagedObjectContext
    ) -> Vocabulary {
        let item = Vocabulary(context: context)
        item.id = UUID()
        item.word = word
        item.replacement = replacement
        return item
    }
}
