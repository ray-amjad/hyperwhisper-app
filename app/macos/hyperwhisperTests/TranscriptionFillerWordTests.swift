//
//  TranscriptionFillerWordTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct TranscriptionFillerWordTests {
    @Test func stripsFillersForEnglish() {
        let result = TranscriptionTextProcessing.removeFillerWords(
            "so uh I think we should um go",
            language: "en"
        )

        #expect(result == "so I think we should go")
    }

    @Test func stripsFillersForEnglishRegionalVariants() {
        let result = TranscriptionTextProcessing.removeFillerWords(
            "well er maybe later",
            language: "en-GB"
        )

        #expect(result == "well maybe later")
    }

    @Test func preservesGermanRealWords() {
        // "er" = he, "um" = at — both are real German words, not fillers.
        #expect(
            TranscriptionTextProcessing.removeFillerWords("ich denke er ist groß", language: "de")
                == "ich denke er ist groß"
        )
        #expect(
            TranscriptionTextProcessing.removeFillerWords("Wir treffen uns um drei Uhr", language: "de")
                == "Wir treffen uns um drei Uhr"
        )
    }

    @Test func skipsWhenLanguageIsUnknown() {
        // nil corresponds to "auto" — ambiguous, so we leave the text untouched.
        #expect(
            TranscriptionTextProcessing.removeFillerWords("ich denke er ist groß", language: nil)
                == "ich denke er ist groß"
        )
    }

    @Test func stripsSentenceOpeningFiller() {
        // Filler at the very start of the text — already capitalized next word.
        #expect(
            TranscriptionTextProcessing.removeFillerWords("Uh, I think we should go", language: "en")
                == "I think we should go"
        )
    }

    @Test func recapitalizesAfterStrippingLeadingFiller() {
        // Next word was lowercase because the STT treated the filler as the opener.
        #expect(
            TranscriptionTextProcessing.removeFillerWords("um, the cat sat down", language: "en")
                == "The cat sat down"
        )
        #expect(
            TranscriptionTextProcessing.removeFillerWords("uh the meeting starts soon", language: "en")
                == "The meeting starts soon"
        )
    }

    @Test func stripsFillerFollowedByComma() {
        // Mid-sentence "uh," — comma was previously left dangling.
        #expect(
            TranscriptionTextProcessing.removeFillerWords("so uh, I think we should go", language: "en")
                == "so I think we should go"
        )
        // Filler between two clauses, surrounded by commas.
        #expect(
            TranscriptionTextProcessing.removeFillerWords("I think, uh, we should go", language: "en")
                == "I think, we should go"
        )
    }

    @Test func stripsFillerEndingText() {
        #expect(
            TranscriptionTextProcessing.removeFillerWords("I think we should go uh", language: "en")
                == "I think we should go"
        )
    }
}
