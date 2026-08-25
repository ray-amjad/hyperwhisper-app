//
//  CloudSttTierParityTests.swift
//  hyperwhisperTests
//
//  Guards the one seam where `shared-app-classification/cloud-stt-catalog.json`
//  stops being data-only: the Provider dropdown is built straight from the
//  catalog's `cloudTierEligible` entries, but the selection is PERSISTED and
//  routed through the hand-written `CloudAccuracyTier` enum. A catalog-only edge
//  (a 12th cloud-tier entry with no matching enum case) would therefore ship a
//  selectable row that silently resolves to `fromStorageValue`'s Deepgram
//  default — wrong X-STT-Provider, wrong credits, wrong vocabulary support.
//
//  The Windows counterpart lives in HyperWhisper.SmokeTests/Program.cs
//  ("Every cloudTierEligible catalog id has a CloudAccuracyTier case").
//

import Foundation
import Testing
@testable import HyperWhisper

struct CloudSttTierParityTests {

    /// The catalog as the shared Rust core reads it. This used to decode the
    /// repo's JSON file directly through a second, macOS-only decoder — that
    /// decoder is gone (issue #280), and the core `include_str!`s the same file
    /// a catalog edit touches, so this still asserts against the source of
    /// truth and not against a stale bundled copy.
    private func repoCatalog() -> CloudSTTCatalog { CloudSTTCatalog.shared }

    @Test("Every cloudTierEligible catalog id has a CloudAccuracyTier case")
    func everyCloudTierIdHasAnEnumCase() throws {
        let entries = repoCatalog().cloudTierEntries
        #expect(!entries.isEmpty, "catalog exposed no cloudTierEligible providers")

        for entry in entries {
            #expect(
                CloudAccuracyTier(rawValue: entry.id) != nil,
                """
                catalog id '\(entry.id)' has no CloudAccuracyTier case, so its Provider row \
                would be visible but unselectable on macOS and would route as Deepgram on \
                Windows. Add the case to ModeModels.swift (and Models/CloudAccuracyTier.cs) \
                in the same change as the catalog entry.
                """
            )
        }
    }

    @Test("Every CloudAccuracyTier case resolves to a cloud-tier catalog entry")
    func everyEnumCaseHasACatalogEntry() throws {
        let catalog = repoCatalog()
        let ids = Set(catalog.cloudTierEntries.map(\.id))

        for tier in CloudAccuracyTier.allCases {
            #expect(
                ids.contains(tier.rawValue),
                """
                CloudAccuracyTier.\(tier.rawValue) has no cloudTierEligible catalog entry, so \
                the tier can be persisted but never offered — and its credits, models and \
                vocabulary flags all resolve to nothing.
                """
            )
        }
    }

    /// Grok's API takes no `model` parameter, so its single registry entry is
    /// stored under the empty id. The Model row is now a one-item dropdown like
    /// every other provider's, so that id has to resolve through the
    /// provider-scoped lookup or the name, the description and the mode card all
    /// render blank. The Windows counterpart lives in HyperWhisper.SmokeTests
    /// ("Grok's empty model id resolves through a provider-scoped lookup").
    @Test("Grok's empty model id resolves through a provider-scoped lookup")
    func grokEmptyModelIdResolves() {
        let grok = CloudTranscriptionModels.model(withId: "", provider: .grok)
        #expect(grok != nil, "the Grok Model row would render blank")
        #expect(grok?.displayName == "Grok Speech-to-Text")
        #expect(
            CloudTranscriptionModels.displayName(for: "", provider: .grok) == "Grok Speech-to-Text")

        // Unscoped, "" stays ambiguous: any provider left without a model would
        // otherwise resolve to Grok.
        #expect(CloudTranscriptionModels.model(withId: "") == nil)
    }

    @Test("Retired cloud model ids resolve to selectable canonical models")
    func retiredCloudModelIdsResolve() {
        let cases: [(String, CloudProvider, String)] = [
            ("slam-1", .assemblyAI, "universal-3-5-pro"),
            ("universal-3-pro", .assemblyAI, "universal-3-5-pro"),
            ("universal-3-pro-medical", .assemblyAI, "universal-3-5-pro-medical"),
            ("stt-async-v4", .soniox, "stt-async-v5"),
            ("gemini-3.1-flash-lite-preview", .gemini, "gemini-3.1-flash-lite"),
        ]

        for (oldId, provider, replacement) in cases {
            #expect(CloudTranscriptionModels.resolveModelAlias(oldId, provider: provider) == replacement)
            #expect(CloudTranscriptionModels.model(withId: replacement, provider: provider) != nil)
        }
    }

    @Test("Retired cloud models are not selectable")
    func retiredCloudModelsAreNotSelectable() {
        let retired = Set([
            "universal-3-pro", "universal-3-pro-medical",
            "stt-async-v4", "gemini-3.1-flash-lite-preview",
        ])
        #expect(CloudTranscriptionModels.availableModels.allSatisfy { !retired.contains($0.id) })
    }
}
