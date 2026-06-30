//
//  hyperwhisperTests.swift
//  hyperwhisperTests
//
//  Created by Rehman Amjad on 16/08/2025.
//

import Testing
import FluidAudio
@testable import HyperWhisper

struct hyperwhisperTests {

    @Test func wordBuilderUsesTranscriptSpacingWhenTokenBoundariesAreMissing() {
        let timings = [
            TokenTiming(token: "this", tokenId: 1, startTime: 0.0, endTime: 0.1, confidence: 0.9),
            TokenTiming(token: "is", tokenId: 2, startTime: 0.1, endTime: 0.2, confidence: 0.9),
            TokenTiming(token: "me", tokenId: 3, startTime: 0.2, endTime: 0.3, confidence: 0.9),
            TokenTiming(token: "test", tokenId: 4, startTime: 0.3, endTime: 0.4, confidence: 0.9),
            TokenTiming(token: "ing", tokenId: 5, startTime: 0.4, endTime: 0.5, confidence: 0.9),
        ]

        let words = WordAgreementEngine.words(
            from: timings,
            transcript: "this is me testing"
        )

        #expect(words.map(\.text) == ["this", "is", "me", "testing"])
    }

    @Test func agreementEngineCommitsStableSpeechWithoutSentenceEnders() {
        let config = AgreementConfig(
            transcribeIntervalSeconds: 1.0,
            tokenConfirmationsNeeded: 3,
            minWordsToConfirm: 5,
            minWordsToConfirmWithoutPunctuation: 8,
            trailingWordsToHoldWithoutPunctuation: 3,
            minPassConfidence: 0.15,
            minWordConfidence: 0.6
        )
        let engine = WordAgreementEngine(config: config)
        let words = makeWords([
            "this", "is", "me", "testing", "out",
            "to", "make", "sure", "it", "works"
        ])

        _ = engine.processTranscriptionResult(words: words, resultConfidence: 0.95)
        _ = engine.processTranscriptionResult(words: words, resultConfidence: 0.95)
        _ = engine.processTranscriptionResult(words: words, resultConfidence: 0.95)
        let result = engine.processTranscriptionResult(words: words, resultConfidence: 0.95)

        #expect(result.newlyConfirmedText == "this is me testing out to make")
        #expect(result.fullText == "this is me testing out to make sure it works")
    }

    private func makeWords(_ texts: [String]) -> [TimedWord] {
        texts.enumerated().map { index, text in
            let start = Double(index)
            return TimedWord(
                text: text,
                startTime: start,
                endTime: start + 0.5,
                confidence: 0.95
            )
        }
    }

}
