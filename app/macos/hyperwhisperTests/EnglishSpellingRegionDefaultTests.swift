//
//  EnglishSpellingRegionDefaultTests.swift
//  hyperwhisperTests
//
//  The ISO 3166-1 region table now lives in the shared Rust core, so these
//  tests are no longer checking a Swift table: they run the real FFI and prove
//  that the core's answers still land on the four cases this app can store.
//  That makes them the macOS half of the cross-platform parity check — Windows
//  (SmokeTests) and the portable head (ModeDefaults.Tests) assert the same
//  region codes against the same table.
//

import Foundation
import Testing
@testable import HyperWhisper

struct EnglishSpellingRegionDefaultTests {

    // MARK: - Region table, through the shim the app actually calls

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
        // Trimming and case folding are the core's, not this file's.
        #expect(EnglishSpelling.forRegion("gb") == .british)
        #expect(EnglishSpelling.forRegion(" au ") == .australian)
        #expect(EnglishSpelling.forRegion("\nca\n") == .canadian)
    }

    // MARK: - The core's own answer, before the raw-value round-trip

    @Test func theCoreResolvesTheSameRegionsWithoutTheShim() {
        // If the shim ever started swallowing a variant, the assertions above
        // would still pass by falling to .american. These pin the FFI directly.
        #expect(englishSpellingForRegion(region: "GB") == .british)
        #expect(englishSpellingForRegion(region: "AU") == .australian)
        #expect(englishSpellingForRegion(region: "CA") == .canadian)
        #expect(englishSpellingForRegion(region: "US") == .american)
        #expect(englishSpellingForRegion(region: "ZZ") == .american)
        #expect(englishSpellingForRegion(region: nil) == .american)
    }

    // MARK: - The four-case / five-case seam

    @Test func everySeedableVariantSurvivesTheRawValueRoundTrip() {
        // `forRegion` goes HwEnglishSpelling -> raw token -> EnglishSpelling.
        // The two enums are declared in different languages, so nothing but a
        // test stops their raw tokens drifting apart.
        let pairs: [(HwEnglishSpelling, EnglishSpelling)] = [
            (.american, .american),
            (.british, .british),
            (.australian, .australian),
            (.canadian, .canadian)
        ]
        for (core, stored) in pairs {
            #expect(englishSpellingRawValue(spelling: core) == stored.rawValue)
            #expect(EnglishSpelling(rawValue: englishSpellingRawValue(spelling: core)) == stored)
        }
        // A fifth storable variant would need a row above before it could seed.
        #expect(EnglishSpelling.allCases.count == pairs.count)
    }

    @Test func seedingNeverAsksForTheNoSpellingState() {
        // HwEnglishSpelling.none is "emit no spelling instruction at all" — the
        // live meaning of a mode whose englishSpelling was never chosen. It is
        // NOT american, it has no EnglishSpelling case, and a seeding call must
        // never produce it. That is what makes forRegion's `?? .american` arm
        // unreachable rather than a silent fallback.
        #expect(englishSpellingRawValue(spelling: HwEnglishSpelling.none).isEmpty)
        #expect(EnglishSpelling(rawValue: "") == nil)

        for code in ["GB", "AU", "CA", "US", "JP", "ZZ", "", "   ", "gb"] {
            #expect(englishSpellingForRegion(region: code) != HwEnglishSpelling.none)
        }
        #expect(englishSpellingForRegion(region: nil) != HwEnglishSpelling.none)
    }

    // MARK: - Live system region

    @Test func currentRegionResolvesToAKnownVariant() {
        let current = EnglishSpelling.defaultForCurrentRegion
        #expect(EnglishSpelling.allCases.contains(current))
        // The picker builds from allCases, so the seeded value is always selectable.
        #expect(EnglishSpelling(rawValue: current.rawValue) == current)
    }
}
