//
//  LanguageConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/language-vectors.json` through `LanguageData`.
//  Issue #285 deleted the 126-row table, the BCP-47 canonicalizer and the alias
//  map from `LanguageData.swift`, and the 102-row `LanguageInfo.cs` from
//  Windows; there is now exactly one language catalog, in `hw-catalog`. These
//  vectors prove the macOS facade reads that one catalog's answer unchanged —
//  they go through `LanguageData`, not through the raw binding, because the
//  facade is what every picker and every stored Mode actually calls. Rust and
//  C# run the same file:
//
//    shared-core-rs/crates/hw-core/tests/language_vectors.rs
//    app/shared-dotnet/HyperWhisper.LanguageConformance.Tests/Program.cs
//
//  `catalog` carries a `decision` field: `both` is a row the two native lists
//  agreed on, `macos` is one of the 24 region and script variants the Windows
//  picker GAINS, and `renamed` is `zh-TW`, which read "Chinese (Traditional)"
//  — the same words as `zh-Hant` — and now reads "Chinese (Traditional,
//  Taiwan)".
//
//  A `null` `displayName` in `lookupCases` is not a gap in the vectors: it
//  means the catalog does not know the code and the host localizes it with its
//  own system database. That fallback (`Locale.localizedString`) is the one
//  piece of this that stays native, so its exact text is Foundation's and the
//  vectors only pin that a name comes out at all.
//
//  Regenerate the vectors from Rust after an intended policy change:
//    cd shared-core-rs && cargo test -p hw-core --test language_vectors -- --ignored regenerate
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LanguageConformanceVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let popularCodes: [String]
        let catalog: [CatalogVector]
        let canonicalCases: [CanonicalVector]
        let lookupCases: [LookupVector]
        let scalarCases: [ScalarVector]
    }

    struct CatalogVector: Decodable {
        let code: String
        let displayName: String
        let decision: String
    }

    struct CanonicalVector: Decodable {
        let name: String
        let input: String
        let canonical: String
    }

    struct LookupVector: Decodable {
        let name: String
        let input: String
        let code: String
        /// `nil` when the catalog does not know the code.
        let displayName: String?
    }

    struct ScalarVector: Decodable {
        let name: String
        /// `nil` is the vector: a missing code, not an empty one.
        let input: String?
        let normalized: String
        let canonicalCode: String
        let isEnglish: Bool
    }

    // MARK: - Loading

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/language-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    // MARK: - The vectors

    /// Order is the assertion, not just membership: `allLanguages` is what the
    /// pickers render top to bottom.
    @Test("the picker catalog matches the shared vectors, in order")
    func catalogMatchesVectors() {
        let actual = LanguageData.allLanguages
        #expect(actual.count == document.catalog.count, "catalog row count")
        for (got, want) in zip(actual, document.catalog) {
            #expect(got.code == want.code, "\(want.code): code")
            #expect(got.displayName == want.displayName, "\(want.code): display name")
        }
    }

    /// `canonicalLanguageCode` is the only public canonicalizer left — the
    /// private `canonicalize` went to Rust with the table. It differs from the
    /// core's `canonicalize` in exactly one documented way: it is the tag to
    /// *persist*, so a missing or empty code becomes `en` rather than `auto`.
    /// `scalarCases` pins that rule separately; here the empty inputs are
    /// checked against it instead of against `canonical`.
    @Test("canonicalization matches the shared vectors")
    func canonicalizationMatchesVectors() {
        for vector in document.canonicalCases {
            let storable = vector.input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            let expected = storable ? "en" : vector.canonical
            #expect(
                LanguageData.canonicalLanguageCode(vector.input) == expected, "\(vector.name)")

            // The resolve path has to canonicalize identically. Only rows the
            // catalog knows go through it: `languages(for:)` deliberately fires
            // an `assertionFailure` for an uncatalogued code, which would trap
            // a Debug test run.
            if LanguageData.info(for: vector.input) != nil {
                #expect(
                    LanguageData.languages(for: [vector.input]).first?.code == vector.canonical,
                    "\(vector.name): via languages(for:)")
            }
        }
    }

    @Test("lookups match the shared vectors")
    func lookupsMatchVectors() {
        for vector in document.lookupCases {
            guard let displayName = vector.displayName else {
                // The catalog does not know this code. `languages(for:)` would
                // assert, so check the two members that carry the same answer
                // without one: the canonical form still comes back, the lookup
                // is a miss, and Foundation names it. The exact text is
                // Foundation's, not ours.
                #expect(
                    LanguageData.canonicalLanguageCode(vector.input) == vector.code,
                    "\(vector.name): canonical form")
                #expect(LanguageData.info(for: vector.input) == nil, "\(vector.name): not in catalog")
                #expect(
                    !LanguageData.displayName(for: vector.input).isEmpty,
                    "\(vector.name): native fallback produced no name")
                continue
            }

            let resolved = LanguageData.languages(for: [vector.input], context: vector.name)
            #expect(resolved.count == 1, "\(vector.name): row count")
            #expect(resolved.first?.code == vector.code, "\(vector.name): code")
            #expect(resolved.first?.displayName == displayName, "\(vector.name): display name")
            #expect(LanguageData.displayName(for: vector.input) == displayName, "\(vector.name): displayName(for:)")
        }
    }

    @Test("the scalar helpers match the shared vectors")
    func scalarsMatchVectors() {
        for vector in document.scalarCases {
            #expect(
                LanguageData.normalizeLanguageCode(vector.input) == vector.normalized,
                "\(vector.name): normalizeLanguageCode")
            #expect(
                LanguageData.canonicalLanguageCode(vector.input) == vector.canonicalCode,
                "\(vector.name): canonicalLanguageCode")
            #expect(
                LanguageData.isEnglish(vector.input) == vector.isEnglish,
                "\(vector.name): isEnglish")
        }
    }

    @Test("the popular codes match the shared vectors")
    func popularCodesMatchVectors() {
        #expect(LanguageData.popularLanguageCodes == document.popularCodes)
    }

    /// The rename this change makes. Both rows said "Chinese (Traditional)", so
    /// the picker showed the same words twice and the user could not tell which
    /// one they were choosing.
    @Test("the two Traditional Chinese rows now read differently")
    func traditionalChineseRowsReadDifferently() {
        #expect(LanguageData.displayName(for: "zh-TW") == "Chinese (Traditional, Taiwan)")
        #expect(LanguageData.displayName(for: "zh-Hant") == "Chinese (Traditional)")
    }

    /// `whisperUniversalCodes` is the one list `LanguageData` still derives
    /// itself, by subtracting a native set of region and script variants from
    /// the shared catalog. It is not language data — it is which slice of the
    /// catalog the Whisper-family providers advertise, and it also defines
    /// `LibraryLanguageFilter.allCodes`, the "covers every language" reference.
    /// That subtraction has to keep reproducing the row set both native lists
    /// carried before #285, which is exactly the vectors' non-`macos` rows.
    @Test("the Whisper universal set is the catalog minus the variant rows")
    func whisperUniversalSetMatchesTheSharedDecisions() {
        let expected = document.catalog.filter { $0.decision != "macos" }.map { $0.code }
        #expect(LanguageData.whisperUniversalCodes == expected)
    }
}
