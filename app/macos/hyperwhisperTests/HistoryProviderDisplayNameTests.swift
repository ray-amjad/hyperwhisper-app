//
//  HistoryProviderDisplayNameTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct HistoryProviderDisplayNameTests {
    @Test("Current SpaceXAI history label keeps its capitalization")
    func currentSpaceXAILabel() {
        #expect(
            HistoryProviderDisplayName.normalize("SpaceXAI (Streaming)")
                == "SpaceXAI (Streaming)")
    }

    @Test("Legacy xAI history label displays as SpaceXAI")
    func legacyXAILabel() {
        #expect(
            HistoryProviderDisplayName.normalize("xAI (Streaming)")
                == "SpaceXAI (Streaming)")
    }
}
