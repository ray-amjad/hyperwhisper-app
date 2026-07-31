//
//  ParakeetModelIdentifierTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

@MainActor
struct ParakeetModelIdentifierTests {

    @Test func missingAndBlankSelectionModelIdsDefaultToCanonicalV3() {
        #expect(
            ParakeetModelManager.Constants.modelIdForSelection(nil)
                == ParakeetModelManager.Constants.v3ModelId
        )
        #expect(
            ParakeetModelManager.Constants.modelIdForSelection("  ")
                == ParakeetModelManager.Constants.v3ModelId
        )
    }

    @Test func legacyAliasesNormalizeToInstalledModelIdentifiers() {
        #expect(
            ParakeetModelManager.Constants.canonicalModelId(
                for: "parakeet-tdt-v3-multilingual"
            ) == ParakeetModelManager.Constants.v3ModelId
        )
    }

    @Test func canonicalIdentifiersRemainStable() {
        #expect(
            ParakeetModelManager.Constants.canonicalModelId(
                for: ParakeetModelManager.Constants.v2ModelId
            ) == ParakeetModelManager.Constants.v2ModelId
        )
        #expect(
            ParakeetModelManager.Constants.canonicalModelId(
                for: ParakeetModelManager.Constants.v3ModelId
            ) == ParakeetModelManager.Constants.v3ModelId
        )
    }

    @Test func unknownAndTypoIdentifiersAreNotCoercedToInstalledModels() {
        #expect(
            ParakeetModelManager.Constants.canonicalModelId(
                for: "parakeet-tdt-v2-english"
            ) == nil
        )
        #expect(
            ParakeetModelManager.Constants.canonicalModelId(
                for: "parakeet-tdt-v4-multilingual"
            ) == nil
        )
        #expect(
            ParakeetModelManager.Constants.canonicalModelId(
                for: "typo"
            ) == nil
        )
        #expect(
            ParakeetModelManager.Constants.modelIdForSelection("typo") == "typo"
        )
    }
}
