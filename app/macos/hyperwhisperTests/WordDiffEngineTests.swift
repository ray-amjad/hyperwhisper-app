//
//  WordDiffEngineTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct WordDiffEngineTests {
    @Test func detectsMergedNameCorrection() {
        let substitutions = WordDiffEngine.findSingleWordSubstitutions(
            original: "Let's email kath mandu about the trip",
            edited: "Let's email Kathmandu about the trip"
        )

        #expect(substitutions == [
            WordDiffSubstitution(original: "kath", replacement: "Kathmandu"),
            WordDiffSubstitution(original: "mandu", replacement: "Kathmandu")
        ])
    }

    @Test func detectsSplitPlaceCorrection() {
        let substitutions = WordDiffEngine.findSingleWordSubstitutions(
            original: "I moved to new yourk last year",
            edited: "I moved to New York last year"
        )

        #expect(substitutions == [
            WordDiffSubstitution(original: "yourk", replacement: "York")
        ])
    }

    @Test func skipsCaseOnlyChanges() {
        let substitutions = WordDiffEngine.findSingleWordSubstitutions(
            original: "sql works well",
            edited: "SQL works well"
        )

        #expect(substitutions.isEmpty)
    }

    @Test func trimsPunctuationAroundTokens() {
        let substitutions = WordDiffEngine.findSingleWordSubstitutions(
            original: "Ask kath, please.",
            edited: "Ask Kathmandu, please."
        )

        #expect(substitutions == [
            WordDiffSubstitution(original: "kath", replacement: "Kathmandu")
        ])
    }

    @Test func handlesMixedEditsAroundStableAnchors() {
        let substitutions = WordDiffEngine.findSingleWordSubstitutions(
            original: "Please call john about acmee tomorrow",
            edited: "Please call Jon about Acme tomorrow"
        )

        #expect(substitutions == [
            WordDiffSubstitution(original: "john", replacement: "Jon"),
            WordDiffSubstitution(original: "acmee", replacement: "Acme")
        ])
    }
}
