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

    /// Decode the repo's catalog file directly (rather than `CloudSTTCatalog.shared`,
    /// which reads the app bundle) so this asserts against the source of truth a
    /// catalog edit actually touches.
    private func repoCatalog() throws -> CloudSTTCatalog {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-app-classification/cloud-stt-catalog.json")
        return try JSONDecoder().decode(CloudSTTCatalog.self, from: Data(contentsOf: url))
    }

    @Test("Every cloudTierEligible catalog id has a CloudAccuracyTier case")
    func everyCloudTierIdHasAnEnumCase() throws {
        let entries = try repoCatalog().cloudTierEntries
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
        let catalog = try repoCatalog()
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
}
