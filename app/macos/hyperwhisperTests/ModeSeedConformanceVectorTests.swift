//
//  ModeSeedConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/mode-seed-vectors.json` through the macOS seed.
//
//  Issue #285 replaced three first-run seeders with one: macOS wrote a single
//  mode named "Default" (`language "en"`, provider `"hyperwhisper"`, a hardcoded
//  `claudeHaiku`), Windows and the Linux/portable head wrote six named "Hyper",
//  "Voice to Text", "Message", "Mail", "Note", "Meeting" off a hand-rolled
//  catalog parse. There is now exactly one definition, in `hw-catalog`, and
//  these vectors are how the three heads prove they read it identically. Rust
//  and C# run the same file:
//
//    shared-core-rs/crates/hw-core/tests/mode_seed_vectors.rs
//    app/shared-dotnet/HyperWhisper.ModeDefaults.Tests/Program.cs
//
//  The assertions go through `SeededModeValues`, not through the raw binding,
//  because that is what `PersistenceController.initializeDefaultModes()`
//  actually writes — and because it is where the one non-obvious mapping lives:
//  the shared vocabulary's `providerType` is macOS' `mode.model`. A vector row
//  is a `providerType` and the macOS side is a `model`; if that ever silently
//  became an identity mapping onto a `providerType` column macOS does not have,
//  these tests are what notices.
//
//  Only `englishSpelling` varies by region. Every other field is identical in
//  all eleven rows, which is the point — `regionOnlyMovesEnglishSpelling` below
//  asserts that shape rather than trusting the file.
//
//  Regenerate the vectors from Rust after an intended policy change:
//    cd shared-core-rs && cargo test -p hw-core --test mode_seed_vectors -- --ignored regenerate
//

import Foundation
import Testing

@testable import HyperWhisper

struct ModeSeedConformanceVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let seeds: [SeedVector]
    }

    /// One row of the vector file. Field names are the SHARED vocabulary, so
    /// `providerType` here is deliberately not called `model`.
    struct SeedVector: Decodable {
        /// `nil` is a vector, not a gap: "the host reported no region".
        let region: String?
        let id: String
        let name: String
        let preset: String
        let language: String
        let providerType: String
        let cloudProvider: String
        let cloudAccuracyTier: String
        let cloudTranscriptionModel: String
        let postProcessingMode: Int
        let postProcessingProvider: String
        let cloudPostProcessingModel: String
        let englishSpelling: String
        let punctuation: Bool
        let capitalization: Bool
        let profanityFilter: Bool
        let customInstructions: String
        let isDefault: Bool
        let isSystemProvided: Bool
        let sortOrder: Int
    }

    // MARK: - Loading

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/mode-seed-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    // MARK: - The vectors

    @Test("every seed vector matches what macOS would write")
    func seedMatchesVectors() {
        #expect(!document.seeds.isEmpty, "the vector file decoded to no rows")

        for vector in document.seeds {
            let label = vector.region.map { "region \"\($0)\"" } ?? "region nil"
            let seeded = SeededModeValues.forRegion(vector.region)

            #expect(seeded.id.uuidString.lowercased() == vector.id.lowercased(), "\(label): id")
            #expect(seeded.name == vector.name, "\(label): name")
            #expect(seeded.preset == vector.preset, "\(label): preset")
            #expect(seeded.language == vector.language, "\(label): language")
            // The mapping the plan calls C7: shared `providerType` → macOS `model`.
            #expect(seeded.model == vector.providerType, "\(label): providerType → model")
            #expect(seeded.cloudProvider == vector.cloudProvider, "\(label): cloudProvider")
            #expect(
                seeded.cloudAccuracyTier == vector.cloudAccuracyTier, "\(label): cloudAccuracyTier")
            #expect(
                seeded.cloudTranscriptionModel == vector.cloudTranscriptionModel,
                "\(label): cloudTranscriptionModel")
            #expect(
                Int(seeded.postProcessingMode) == vector.postProcessingMode,
                "\(label): postProcessingMode")
            #expect(
                seeded.postProcessingProvider == vector.postProcessingProvider,
                "\(label): postProcessingProvider")
            #expect(
                seeded.cloudPostProcessingModel == vector.cloudPostProcessingModel,
                "\(label): cloudPostProcessingModel")
            #expect(seeded.englishSpelling == vector.englishSpelling, "\(label): englishSpelling")
            #expect(seeded.punctuation == vector.punctuation, "\(label): punctuation")
            #expect(seeded.capitalization == vector.capitalization, "\(label): capitalization")
            #expect(seeded.profanityFilter == vector.profanityFilter, "\(label): profanityFilter")
            #expect(
                seeded.customInstructions == vector.customInstructions,
                "\(label): customInstructions")
            #expect(seeded.isDefault == vector.isDefault, "\(label): isDefault")
            #expect(seeded.isSystemProvided == vector.isSystemProvided, "\(label): isSystemProvided")
            #expect(Int(seeded.sortOrder) == vector.sortOrder, "\(label): sortOrder")
        }
    }

    /// The whole reason the seed takes a region: it may move the spelling
    /// variant and NOTHING else. A change that makes another field
    /// region-dependent is a product decision, and this is where it surfaces.
    @Test("the region moves englishSpelling and nothing else")
    func regionOnlyMovesEnglishSpelling() throws {
        let reference = try #require(document.seeds.first)
        for vector in document.seeds {
            let label = vector.region.map { "region \"\($0)\"" } ?? "region nil"
            #expect(vector.id == reference.id, "\(label): id moved with the region")
            #expect(vector.name == reference.name, "\(label): name moved with the region")
            #expect(vector.preset == reference.preset, "\(label): preset moved with the region")
            #expect(vector.language == reference.language, "\(label): language moved with the region")
            #expect(
                vector.providerType == reference.providerType,
                "\(label): providerType moved with the region")
            #expect(
                vector.cloudProvider == reference.cloudProvider,
                "\(label): cloudProvider moved with the region")
            #expect(
                vector.cloudAccuracyTier == reference.cloudAccuracyTier,
                "\(label): cloudAccuracyTier moved with the region")
            #expect(
                vector.cloudTranscriptionModel == reference.cloudTranscriptionModel,
                "\(label): cloudTranscriptionModel moved with the region")
            #expect(
                vector.postProcessingMode == reference.postProcessingMode,
                "\(label): postProcessingMode moved with the region")
            #expect(
                vector.postProcessingProvider == reference.postProcessingProvider,
                "\(label): postProcessingProvider moved with the region")
            #expect(
                vector.cloudPostProcessingModel == reference.cloudPostProcessingModel,
                "\(label): cloudPostProcessingModel moved with the region")
            #expect(
                vector.customInstructions == reference.customInstructions,
                "\(label): customInstructions moved with the region")
            #expect(vector.sortOrder == reference.sortOrder, "\(label): sortOrder moved with the region")
        }
    }

    /// The four spelling variants macOS can store. The core carries a fifth
    /// `.none` token meaning "emit no spelling instruction at all", and a seed
    /// that landed on it would ship a mode with no spelling rule — so the
    /// vectors have to keep proving it never does.
    @Test("every vector's spelling is one macOS can store, and never the empty token")
    func everySpellingIsStorable() {
        for vector in document.seeds {
            let label = vector.region.map { "region \"\($0)\"" } ?? "region nil"
            #expect(!vector.englishSpelling.isEmpty, "\(label): seeded the empty spelling token")
            #expect(
                EnglishSpelling(rawValue: vector.englishSpelling) != nil,
                "\(label): \"\(vector.englishSpelling)\" is not a macOS EnglishSpelling case")
        }
    }

    /// The macOS-only halves of the contract the shared vectors cannot express:
    /// the seeded strings have to survive macOS' own parsers. Both fall back
    /// silently rather than failing, which is exactly why they need pinning —
    /// `PostProcessingProvider` returning nil makes post-processing a no-op, and
    /// `CloudPostProcessingModel.fromStorageValue` falls back to **Grok**.
    @Test("the seeded tokens round-trip through the macOS parsers")
    func seededTokensParseOnMacOS() {
        for vector in document.seeds {
            let label = vector.region.map { "region \"\($0)\"" } ?? "region nil"

            #expect(
                PostProcessingProvider(rawValue: vector.postProcessingProvider) == .hyperwhisper,
                "\(label): \"\(vector.postProcessingProvider)\" does not read as HyperWhisper Cloud")

            let model = CloudPostProcessingModel.fromStorageValue(vector.cloudPostProcessingModel)
            #expect(model.engineId == "anthropic", "\(label): post-processing engine")
            #expect(model.modelId == "claude-haiku-4-5", "\(label): post-processing model")
            #expect(
                model.storageValue == vector.cloudPostProcessingModel,
                "\(label): the stored form is not the canonical one")

            #expect(
                CloudAccuracyTier.fromStorageValue(vector.cloudAccuracyTier) == .elevenLabsScribeV2,
                "\(label): accuracy tier")
            #expect(
                CloudAccuracyTier.elevenLabsScribeV2.defaultModelId == vector.cloudTranscriptionModel,
                "\(label): the tier's catalog default no longer matches the seeded model")
        }
    }

    // MARK: - The compile-time link in the chain

    /// ⚠️ Constructs `ModeSeed` with its FULL memberwise initialiser on purpose.
    /// Do not simplify this to `modeSeedDefault(region:)`.
    ///
    /// Nothing else on the macOS side fails when a field is added to the shared
    /// `ModeSeed`. `SeededModeValues.init(seed:)` copies the fields it names and
    /// ignores the rest, `applySeededValues` writes the columns it names, and
    /// the vector comparison compares only the fields both sides already have —
    /// so a new field would be written by no head, the macOS row would inherit
    /// the hostile Core Data default the no-optional-fields design exists to
    /// prevent, and CI would stay green.
    ///
    /// UniFFI regenerates this initialiser from the Rust record, so adding a
    /// field adds a parameter and this call stops compiling. Swift has no
    /// exhaustive struct destructure, so a call site with every argument spelled
    /// out is the closest equivalent. The Rust and C# links are the exhaustive
    /// destructures in `hw-core`'s `From<hw_catalog::ModeSeed>` and
    /// `mode_seed_vectors.rs`, and the positional deconstruction in
    /// `SharedCoreBridge.ModeSeedDefault`.
    ///
    /// The assertions are the second half: every value handed in must come back
    /// out of `SeededModeValues`, so a field that IS added but is dropped by the
    /// translation layer fails here rather than silently.
    @Test("every ModeSeed field is carried into SeededModeValues")
    func theSeedRecordIsConstructedFieldByField() {
        let probe = ModeSeed(
            id: "00000000-0000-0000-0000-0000000000ff",
            name: "probe-name",
            preset: "probe-preset",
            language: "probe-language",
            providerType: "probe-provider-type",
            cloudProvider: "probe-cloud-provider",
            cloudAccuracyTier: "probe-accuracy-tier",
            cloudTranscriptionModel: "probe-transcription-model",
            postProcessingMode: 7,
            postProcessingProvider: "probe-pp-provider",
            cloudPostProcessingModel: "probe-engine:probe-model",
            englishSpelling: "probe-spelling",
            punctuation: false,
            capitalization: false,
            profanityFilter: true,
            customInstructions: "probe-instructions",
            isDefault: false,
            isSystemProvided: false,
            sortOrder: 9)

        let values = SeededModeValues(seed: probe)

        #expect(values.id.uuidString.lowercased() == probe.id.lowercased())
        #expect(values.name == probe.name)
        #expect(values.preset == probe.preset)
        #expect(values.language == probe.language)
        // The one non-identity mapping: `providerType` → macOS' `model` column.
        #expect(values.model == probe.providerType)
        #expect(values.cloudProvider == probe.cloudProvider)
        #expect(values.cloudAccuracyTier == probe.cloudAccuracyTier)
        #expect(values.cloudTranscriptionModel == probe.cloudTranscriptionModel)
        #expect(Int32(values.postProcessingMode) == probe.postProcessingMode)
        #expect(values.postProcessingProvider == probe.postProcessingProvider)
        #expect(values.cloudPostProcessingModel == probe.cloudPostProcessingModel)
        #expect(values.englishSpelling == probe.englishSpelling)
        #expect(values.punctuation == probe.punctuation)
        #expect(values.capitalization == probe.capitalization)
        #expect(values.profanityFilter == probe.profanityFilter)
        #expect(values.customInstructions == probe.customInstructions)
        #expect(values.isDefault == probe.isDefault)
        #expect(values.isSystemProvided == probe.isSystemProvided)
        #expect(Int32(values.sortOrder) == probe.sortOrder)
    }
}
