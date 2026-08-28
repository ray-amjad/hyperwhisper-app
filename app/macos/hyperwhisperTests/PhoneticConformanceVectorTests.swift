//
//  PhoneticConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/phonetic-vectors.json` against the Swift UniFFI
//  binding. Issue #283 deleted `PhoneticVocabularyMatcher.swift` and its
//  Windows twin, so there is exactly one phonetic matcher and exactly one
//  diacritic-insensitive substring pass; these vectors prove macOS reads that
//  one implementation's answer unchanged. Rust and C# run the same file:
//
//    shared-core-rs/crates/hw-core/tests/phonetic_vectors.rs
//    app/shared-dotnet/HyperWhisper.PhoneticConformance.Tests/Program.cs
//
//  `matcherCases` carries a `decision` field naming which native matcher each
//  unified answer came from. The rows that say `windows`, `macos`, `neither` or
//  `new` are the behaviour changes: macOS's tokenizer excluded newlines, its
//  `<=2`-character gate counted graphemes, neither matcher NFC-normalized, and
//  the exact-hit short-circuit only protected the first entry on both
//  platforms.
//
//  `substringCases` has no `decision` field: only macOS ever had that pass, so
//  every row is the macOS behaviour and the vectors are what keeps it from
//  drifting now that Windows and Linux run it too.
//
//  Regenerate the vectors from Rust after an intended policy change:
//    cd shared-core-rs && cargo test -p hw-core --test phonetic_vectors -- --ignored regenerate
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct PhoneticConformanceVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let matcherCases: [MatcherVector]
        let substringCases: [SubstringVector]
    }

    struct MatcherVector: Decodable {
        let name: String
        let decision: String
        let was: String
        let text: String
        let entries: [EntryVector]
        let expected: ExpectedVector
    }

    struct SubstringVector: Decodable {
        let name: String
        let text: String
        let entries: [EntryVector]
        let expected: String
    }

    struct EntryVector: Decodable {
        let word: String
        let replacement: String?
    }

    struct ExpectedVector: Decodable {
        let text: String
        let matches: [MatchVector]
        let entryCount: UInt32
    }

    struct MatchVector: Decodable {
        let token: String
        let replacement: String
    }

    // MARK: - Loading

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/phonetic-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    private func entries(_ vectors: [EntryVector]) -> [HwVocabularyEntry] {
        vectors.map { HwVocabularyEntry(word: $0.word, replacement: $0.replacement) }
    }

    // MARK: - The vectors

    @Test("the phonetic matcher matches the shared vectors")
    func matcherMatchesVectors() {
        for vector in document.matcherCases {
            let actual = phoneticApplyVocabulary(text: vector.text, entries: entries(vector.entries))
            #expect(actual.text == vector.expected.text, "\(vector.name): text")
            #expect(actual.entryCount == vector.expected.entryCount, "\(vector.name): entryCount")
            #expect(actual.matches.count == vector.expected.matches.count, "\(vector.name): match count")
            for (got, want) in zip(actual.matches, vector.expected.matches) {
                #expect(got.token == want.token, "\(vector.name): token")
                #expect(got.replacement == want.replacement, "\(vector.name): replacement")
            }
        }
    }

    @Test("the substring pass matches the shared vectors")
    func substringMatchesVectors() {
        for vector in document.substringCases {
            let actual = applySubstringVocabulary(text: vector.text, entries: entries(vector.entries))
            #expect(actual == vector.expected, "\(vector.name)")
        }
    }

    /// The wrappers the on-device providers actually call have to agree with the
    /// raw binding. A future edit that re-adds a native pre-pass — a trim, a
    /// normalization, a filter — in front of one of them breaks here rather than
    /// silently changing what users see.
    @Test("VocabularyProcessor passes the vectors through unchanged")
    func processorAgreesWithTheBinding() {
        // The rows are never saved — both helpers only read `word` and
        // `replacement`. Same in-memory approach as
        // `VocabularyEgressNormalizationTests`, and the reason this suite is
        // `@MainActor`.
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        func rows(_ vectors: [EntryVector]) -> [Vocabulary] {
            vectors.map { entry in
                let row = Vocabulary(context: context)
                row.id = UUID()
                row.word = entry.word
                row.replacement = entry.replacement
                return row
            }
        }

        for vector in document.matcherCases {
            let actual = VocabularyProcessor.applyPhoneticVocabulary(
                to: vector.text, vocabulary: rows(vector.entries))
            #expect(actual == vector.expected.text, "\(vector.name)")
        }

        for vector in document.substringCases {
            let actual = VocabularyProcessor.applySubstringVocabulary(
                to: vector.text, vocabulary: rows(vector.entries))
            #expect(actual == vector.expected, "\(vector.name)")
        }
    }

    /// A decision table is only proof while it still has a row in every bucket
    /// that records a behaviour change.
    @Test("every changed-behaviour bucket still has a row")
    func everyChangedBucketHasARow() {
        for decision in ["windows", "macos", "neither", "new"] {
            #expect(
                document.matcherCases.contains { $0.decision == decision },
                "decision bucket \(decision) lost its last vector")
        }
    }
}
