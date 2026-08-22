//
//  LocalProviderVocabularyTests.swift
//  hyperwhisperTests
//
//  Locks the substring vocabulary rules the on-device providers use, now that
//  the four private copies live in one place. These rules are deliberately
//  different from `VocabularyProcessor.applyHardenedReplacement` — unanchored
//  and diacritic-insensitive — so a later "cleanup" that points the local
//  providers at the hardened helper fails here instead of silently changing
//  what users see.
//

import Testing
@testable import HyperWhisper

struct LocalProviderVocabularyTests {
    @Test func replacesCaseInsensitively() {
        let result = VocabularyProcessor.applySubstringReplacement(
            to: "we ship on FRIDAY",
            word: "friday",
            replacement: "Friday"
        )

        #expect(result == "we ship on Friday")
    }

    @Test func replacesDiacriticInsensitively() {
        let result = VocabularyProcessor.applySubstringReplacement(
            to: "meet Renee at noon",
            word: "renée",
            replacement: "Renée"
        )

        #expect(result == "meet Renée at noon")
    }

    @Test func matchesInsideAWordBecauseThereIsNoBoundaryAnchor() {
        // No `\b…\b` here, unlike applyHardenedReplacement. This is the
        // documented behaviour of the local-provider pass, not an oversight.
        let result = VocabularyProcessor.applySubstringReplacement(
            to: "cats and catalogues",
            word: "cat",
            replacement: "dog"
        )

        #expect(result == "dogs and dogalogues")
    }

    @Test func trimsBothWordAndReplacement() {
        let result = VocabularyProcessor.applySubstringReplacement(
            to: "call kate now",
            word: "  kate  ",
            replacement: "  Katherine  "
        )

        #expect(result == "call Katherine now")
    }

    @Test func anEmptyWordIsANoOp() {
        let result = VocabularyProcessor.applySubstringReplacement(
            to: "unchanged text",
            word: "   ",
            replacement: "boom"
        )

        #expect(result == "unchanged text")
    }

    @Test func anEmptyReplacementIsANoOp() {
        // The old copies skipped these entries, so a vocabulary row with a
        // blank replacement must never delete the word from the transcript.
        let result = VocabularyProcessor.applySubstringReplacement(
            to: "keep the word",
            word: "word",
            replacement: "  "
        )

        #expect(result == "keep the word")
    }
}
