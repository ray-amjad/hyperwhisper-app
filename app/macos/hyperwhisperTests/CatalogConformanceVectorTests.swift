//
//  CatalogConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/catalog-vectors.json` against the Swift UniFFI
//  binding. Issue #280 deleted the native catalog decoders, so there is exactly
//  one implementation of the polymorphic decoding, the vendor grouping and the
//  picker-language folding; these vectors prove macOS reads that one
//  implementation's answer unchanged. Rust and C# run the same file:
//
//    shared-core-rs/crates/hw-core/tests/catalog_vectors.rs
//    app/shared-dotnet/HyperWhisper.CatalogConformance.Tests/Program.cs
//
//  Before #280 the three stacks had already drifted: `deepgramNova3` offered
//  `gu`/`th`/`zh` on Windows and not here.
//
//  Regenerate the vectors from Rust after an intended catalog change:
//    cd shared-core-rs && cargo test -p hw-core --test catalog_vectors -- --ignored regenerate
//

import Foundation
import Testing
@testable import HyperWhisper

struct CatalogConformanceVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let cloudSttEntries: [EntryVector]
        let vendorGroups: [VendorGroupVector]
        let pickerLanguageCodes: [PickerVector]
        let modelsEntries: [ModelsEntryVector]
    }

    struct EntryVector: Decodable {
        let id: String
        let displayName: String
        let displayModel: String?
        let vendor: String
        let vendorDisplayName: String?
        let vendorLabel: String
        let sttProvider: String?
        let cloudTierEligible: Bool?
        let byokEligible: Bool?
        let cloudTierAccuracy: String?
        let cloudTierCreditsPerMinute: Double?
        let wordTimestamps: Bool
        let diarization: Bool
        let streaming: Bool
        /// "yes" / "no" / "unverified", or nil when the row carries no
        /// `customVocabulary` block at all.
        let customVocabularySupported: String?
        let customVocabularyFieldName: String?
        let languagesCount: Int?
        let languagesAutoDetect: Bool?
        let languagesCodeFormat: String?
        let languagesHasCodes: Bool
        let languagesRawCodeCount: Int
        let maxFileSizeMb: Double?
        let maxDurationMinutes: Int?
        let acceptedFormats: [String]
        let previewStatus: Bool?
        let migrateFrom: [String]
        let legacyCloudProviderAliases: [String]
        let defaultModelId: String?
        let models: [ModelVector]
    }

    struct ModelVector: Decodable {
        let id: String
        let displayName: String
        let creditsPerMinute: Double?
        let isDefault: Bool?
        let previewStatus: Bool?
        let supportsCustomVocabulary: Bool?
        let streaming: Bool?
    }

    struct VendorGroupVector: Decodable {
        let vendorKey: String
        let displayName: String
        let entryIds: [String]
        let models: [String]
    }

    struct PickerVector: Decodable {
        let id: String
        let codes: [String]?
    }

    struct ModelsEntryVector: Decodable {
        let provider: String
        let id: String
        let kind: String
        let supportsCustomVocabulary: Bool
        let availableViaHyperWhisperCloud: Bool
        let supportsAllLanguages: Bool
        let languageCodes: [String]
    }

    /// The vectors are repo data shared by three stacks, not a bundled app
    /// resource, so read them from the source tree the way
    /// `CloudSttTierParityTests` reads the catalog.
    private static func vectors() throws -> Document {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/catalog-vectors.json")
        return try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    private static func vocabName(_ supported: VocabSupport?) -> String? {
        switch supported {
        case .yes: return "yes"
        case .no: return "no"
        case .unverified: return "unverified"
        case nil: return nil
        }
    }

    // MARK: - Tests

    @Test("Polymorphic catalog decoding matches the shared vectors")
    func cloudSttEntriesMatchTheVectors() throws {
        let expected = try Self.vectors().cloudSttEntries
        let actual = cloudSttEntries()
        #expect(actual.count == expected.count, "cloud-STT provider count changed")

        for (want, got) in zip(expected, actual) {
            #expect(got.id == want.id, "entry order changed")
            let id = want.id
            #expect(got.displayName == want.displayName, "\(id).displayName")
            #expect(got.displayModel == want.displayModel, "\(id).displayModel")
            #expect(got.vendor == want.vendor, "\(id).vendor")
            #expect(got.vendorDisplayName == want.vendorDisplayName, "\(id).vendorDisplayName")
            #expect(got.vendorLabel == want.vendorLabel, "\(id).vendorLabel")
            #expect(got.sttProvider == want.sttProvider, "\(id).sttProvider")

            #expect(got.access?.cloudTierEligible == want.cloudTierEligible, "\(id).access.cloudTierEligible")
            #expect(got.access?.byokEligible == want.byokEligible, "\(id).access.byokEligible")
            #expect(got.cloudTier?.accuracy == want.cloudTierAccuracy, "\(id).cloudTier.accuracy")
            #expect(got.cloudTier?.creditsPerMinute == want.cloudTierCreditsPerMinute, "\(id).cloudTier.credits")

            #expect(got.features.wordTimestamps == want.wordTimestamps, "\(id).features.wordTimestamps")
            #expect(got.features.diarization == want.diarization, "\(id).features.diarization")
            #expect(got.features.streaming == want.streaming, "\(id).features.streaming")

            // The tri-state the bool-or-string field decodes to.
            #expect(
                Self.vocabName(got.customVocabulary?.supported) == want.customVocabularySupported,
                "\(id).customVocabulary.supported"
            )
            #expect(
                got.customVocabulary?.fieldName == want.customVocabularyFieldName,
                "\(id).customVocabulary.fieldName"
            )

            #expect(got.languages.count.map { Int($0) } == want.languagesCount, "\(id).languages.count")
            #expect(got.languages.autoDetect == want.languagesAutoDetect, "\(id).languages.autoDetect")
            #expect(got.languages.codeFormat == want.languagesCodeFormat, "\(id).languages.codeFormat")
            #expect(got.languages.hasCodes == want.languagesHasCodes, "\(id).languages.hasCodes")

            let rawCodes = cloudSttLanguageCodes(id: id)
            #expect((rawCodes?.count ?? 0) == want.languagesRawCodeCount, "\(id) raw language code count")
            #expect(
                (rawCodes != nil) == want.languagesHasCodes,
                "\(id): hasCodes must agree with whether the code accessor returns a list"
            )

            #expect(got.maxFileSizeMb == want.maxFileSizeMb, "\(id).maxFileSizeMb")
            #expect(got.maxDurationMinutes.map { Int($0) } == want.maxDurationMinutes, "\(id).maxDurationMinutes")
            #expect(got.acceptedFormats == want.acceptedFormats, "\(id).acceptedFormats")
            #expect(got.previewStatus == want.previewStatus, "\(id).previewStatus")
            #expect(got.migrateFrom == want.migrateFrom, "\(id).migrateFrom")
            #expect(
                got.legacyCloudProviderAliases == want.legacyCloudProviderAliases,
                "\(id).legacyCloudProviderAliases"
            )

            // "" is a real default model id (Grok's single implicit model); it
            // must not collapse into nil, which is what an unknown id returns.
            #expect(cloudSttDefaultModelId(id: id) == want.defaultModelId, "\(id).defaultModelId")

            #expect(got.models.count == want.models.count, "\(id).models count")
            for (wantModel, gotModel) in zip(want.models, got.models) {
                let label = "\(id)/\(wantModel.id)"
                #expect(gotModel.id == wantModel.id, "\(label).id (order)")
                #expect(gotModel.displayName == wantModel.displayName, "\(label).displayName")
                #expect(gotModel.creditsPerMinute == wantModel.creditsPerMinute, "\(label).creditsPerMinute")
                #expect(gotModel.isDefault == wantModel.isDefault, "\(label).isDefault")
                #expect(gotModel.previewStatus == wantModel.previewStatus, "\(label).previewStatus")
                #expect(
                    gotModel.supportsCustomVocabulary == wantModel.supportsCustomVocabulary,
                    "\(label).supportsCustomVocabulary"
                )
                #expect(gotModel.streaming == wantModel.streaming, "\(label).streaming")
            }
        }
    }

    @Test("Vendor grouping matches the shared vectors")
    func vendorGroupsMatchTheVectors() throws {
        let expected = try Self.vectors().vendorGroups
        let actual = cloudSttCloudTierVendorGroups()
        // Order IS the Provider dropdown's order, so it is part of the contract.
        #expect(actual.count == expected.count, "vendor group count changed")

        for (want, got) in zip(expected, actual) {
            #expect(got.vendorKey == want.vendorKey, "vendor group order changed")
            #expect(got.displayName == want.displayName, "\(want.vendorKey).displayName")
            #expect(got.entries.map(\.id) == want.entryIds, "\(want.vendorKey).entries")
            #expect(
                got.models.map { "\($0.entry.id)/\($0.model.id)" } == want.models,
                "\(want.vendorKey).models — each model must stay tagged with the tier that routes it"
            )
            // The same group must be reachable by its key and by any of its tiers.
            #expect(
                cloudSttVendorGroupForVendorKey(vendorKey: want.vendorKey)?.vendorKey == want.vendorKey,
                "\(want.vendorKey): lookup by vendor key"
            )
            for entryId in want.entryIds {
                #expect(
                    CloudSTTCatalog.shared.vendorGroup(forEntryId: entryId)?.vendorKey == want.vendorKey,
                    "\(want.vendorKey): lookup by member tier \(entryId)"
                )
            }
        }

        #expect(
            CloudSTTCatalog.shared.vendorGroup(forEntryId: "noSuchTier") == nil,
            "an unknown tier id must not resolve to a vendor group"
        )
    }

    @Test("Picker-language folding matches the shared vectors")
    func pickerLanguageCodesMatchTheVectors() throws {
        for want in try Self.vectors().pickerLanguageCodes {
            let got = CloudSTTCatalog.shared.pickerLanguageCodes(forEntryId: want.id)
            guard let expected = want.codes else {
                #expect(
                    got == nil,
                    "\(want.id): an unverified language set must fold to nil so the picker keeps its full list"
                )
                continue
            }
            #expect(got == expected, "\(want.id): picker language fold")
        }

        #expect(
            CloudSTTCatalog.shared.pickerLanguageCodes(forEntryId: "noSuchProvider") == nil,
            "an unknown provider must fold to nil"
        )
    }

    @Test("models-catalog lookups match the shared vectors")
    func modelsEntriesMatchTheVectors() throws {
        let expected = try Self.vectors().modelsEntries
        let actual = SharedModelsCatalog.allEntries()
        #expect(actual.count == expected.count, "models-catalog row count changed")

        for (want, got) in zip(expected, actual) {
            let label = "\(want.provider)/\(want.kind)/\(want.id)"
            #expect(got.provider == want.provider, "\(label).provider (order)")
            #expect(got.id == want.id, "\(label).id")
            #expect(got.kind == want.kind, "\(label).kind")
            #expect(got.supportsCustomVocabulary == want.supportsCustomVocabulary, "\(label).supportsCustomVocabulary")
            #expect(
                got.availableViaHyperWhisperCloud == want.availableViaHyperWhisperCloud,
                "\(label).availableViaHyperWhisperCloud"
            )

            // Resolved support, not the raw column: this pins the wildcard
            // fallback and the "uncatalogued ⇒ every language" rule too.
            let kind: SharedModelsCatalog.Kind = got.kind == "text" ? .text : .voice
            let support = SharedModelsCatalog.languageSupport(provider: got.provider, kind: kind, id: got.id)
            #expect(support.supportsAll == want.supportsAllLanguages, "\(label).supportsAllLanguages")
            #expect(support.codes == Set(want.languageCodes), "\(label).languageCodes")
        }
    }

    @Test("The vectors cover every polymorphic branch")
    func vectorsCoverEveryBranch() throws {
        let doc = try Self.vectors()
        #expect(doc.cloudSttEntries.count >= 10, "expected the full provider list in the vectors")
        #expect(
            doc.cloudSttEntries.contains { !$0.languagesHasCodes },
            "no vector exercises the \"unverified\" languages.codes branch"
        )
        #expect(
            doc.cloudSttEntries.contains { $0.languagesHasCodes },
            "no vector exercises the enumerated languages.codes branch"
        )
        #expect(
            doc.cloudSttEntries.contains { $0.maxFileSizeMb == nil },
            "no vector exercises the \"unverified\" maxFileSizeMb branch"
        )
        #expect(
            doc.cloudSttEntries.contains { $0.maxDurationMinutes != nil },
            "no vector exercises the numeric maxDurationMinutes branch"
        )
        #expect(
            doc.vendorGroups.contains { $0.entryIds.count > 1 },
            "no company owns two tiers, so the vendor merge is untested"
        )
    }
}
