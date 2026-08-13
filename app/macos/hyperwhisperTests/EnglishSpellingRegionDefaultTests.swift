//
//  EnglishSpellingRegionDefaultTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

struct EnglishSpellingRegionDefaultTests {

    // MARK: - Region table

    @Test func britishRegionsMapToBritish() {
        #expect(EnglishSpelling.forRegion("GB") == .british)
        #expect(EnglishSpelling.forRegion("IE") == .british)
        #expect(EnglishSpelling.forRegion("ZA") == .british)
        #expect(EnglishSpelling.forRegion("IN") == .british)
        #expect(EnglishSpelling.forRegion("SG") == .british)
        // New Zealand has no variant of its own; British is the closest of the four.
        #expect(EnglishSpelling.forRegion("NZ") == .british)
    }

    @Test func australiaAndItsTerritoriesMapToAustralian() {
        #expect(EnglishSpelling.forRegion("AU") == .australian)
        #expect(EnglishSpelling.forRegion("NF") == .australian)
    }

    @Test func canadaMapsToCanadian() {
        #expect(EnglishSpelling.forRegion("CA") == .canadian)
    }

    @Test func americanIsTheFallback() {
        #expect(EnglishSpelling.forRegion("US") == .american)
        // An unlisted region keeps the value the app used before this table.
        #expect(EnglishSpelling.forRegion("JP") == .american)
        #expect(EnglishSpelling.forRegion("PH") == .american)
        #expect(EnglishSpelling.forRegion("ZZ") == .american)
        #expect(EnglishSpelling.forRegion(nil) == .american)
        #expect(EnglishSpelling.forRegion("") == .american)
        #expect(EnglishSpelling.forRegion("   ") == .american)
    }

    @Test func codeCaseAndPaddingDoNotChangeTheResult() {
        #expect(EnglishSpelling.forRegion("gb") == .british)
        #expect(EnglishSpelling.forRegion(" au ") == .australian)
        #expect(EnglishSpelling.forRegion("\nca\n") == .canadian)
    }

    // MARK: - No region belongs to two lists

    @Test func regionSetsDoNotOverlap() {
        // A code that resolves to one variant must not resolve to another; a
        // duplicate between the tables would make the result order-dependent.
        let sample = ["GB", "IE", "NZ", "AU", "NF", "CA", "US", "IN", "ZA"]
        var seen: [String: EnglishSpelling] = [:]
        for code in sample {
            let resolved = EnglishSpelling.forRegion(code)
            #expect(seen[code] == nil || seen[code] == resolved)
            seen[code] = resolved
        }
        #expect(seen["CA"] == .canadian)
        #expect(seen["AU"] == .australian)
        #expect(seen["GB"] == .british)
    }

    // MARK: - Live system region

    @Test func currentRegionResolvesToAKnownVariant() {
        let current = EnglishSpelling.defaultForCurrentRegion
        #expect(EnglishSpelling.allCases.contains(current))
        // The picker builds from allCases, so the seeded value is always selectable.
        #expect(EnglishSpelling(rawValue: current.rawValue) == current)
    }
}
