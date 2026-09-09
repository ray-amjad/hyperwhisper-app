//
//  WordAgreementEngine.swift
//  hyperwhisper
//
//  Stabilizes on-device Parakeet streaming output by comparing per-pass
//  transcriptions. A prefix becomes "confirmed" only when 3 consecutive
//  passes agree, a 3-sentence-ender punctuation rule is satisfied, and
//  the last 3 boundary words each exceed the confidence floor.
//
//  That algorithm now lives in Rust — `hw_text::PairwiseAgreement`, reached
//  through `HwWordAgreementSession` — so macOS and the Linux Parakeet daemon
//  share one implementation (#286). Word normalization moved with it. What
//  stays here is the macOS-only half: rebuilding whole words from FluidAudio's
//  sub-word token timings.
//

import FluidAudio
import Foundation

// MARK: - Data Types

struct TimedWord {
    let text: String
    let startTime: Double
    let endTime: Double
    let confidence: Float

    init(text: String, startTime: Double, endTime: Double, confidence: Float = 1.0) {
        self.text = text
        self.startTime = startTime
        self.endTime = endTime
        self.confidence = confidence
    }
}

struct AgreementConfig {
    var transcribeIntervalSeconds: Double = 1.0
    var tokenConfirmationsNeeded: Int = 3
    var minWordsToConfirm: Int = 5
    var minWordsToConfirmWithoutPunctuation: Int = 8
    var trailingWordsToHoldWithoutPunctuation: Int = 3
    // Passes below this threshold are shown as hypothesis but don't count toward confirmation.
    var minPassConfidence: Float = 0.15
    // All words in the last 3 positions before a sentence boundary must meet this threshold to be confirmed.
    var minWordConfidence: Float = 0.6
}

struct AgreementResult {
    let fullText: String
    let newlyConfirmedText: String
}

// `AgreementConfig` keeps its `Int` counts; the FFI record takes `UInt32`.
// Clamp rather than convert, so a negative or out-of-range value cannot trap:
// every count is only ever compared against a word count, which makes 0 and
// `UInt32.max` bounds that behave exactly as the `Int` they replace did.
private func ffiCount(_ value: Int) -> UInt32 {
    UInt32(clamping: value)
}

// MARK: - Word Agreement Engine

@available(macOS 13.0, *)
final class WordAgreementEngine {

    // Every pass of state lives behind this handle. The Swift binding is
    // reference-counted, so `deinit` frees the Rust `Arc` — nothing to dispose.
    private let session: HwWordAgreementSession

    private(set) var confirmedEndTime: Double = 0.0
    // Start time of the first unconfirmed word; used as the audio seek/trim point after confirmation.
    private(set) var hypothesisStartTime: Double = 0.0

    var confirmedText: String {
        session.confirmedText()
    }

    init(config: AgreementConfig = AgreementConfig()) {
        // `transcribeIntervalSeconds` deliberately does not cross: it drives the
        // decode timer in ParakeetStreamingSession and never reached the engine.
        session = HwWordAgreementSession(config: HwAgreementConfig(
            tokenConfirmationsNeeded: ffiCount(config.tokenConfirmationsNeeded),
            minWordsToConfirm: ffiCount(config.minWordsToConfirm),
            minWordsToConfirmWithoutPunctuation: ffiCount(config.minWordsToConfirmWithoutPunctuation),
            trailingWordsToHoldWithoutPunctuation: ffiCount(config.trailingWordsToHoldWithoutPunctuation),
            minPassConfidence: config.minPassConfidence,
            minWordConfidence: config.minWordConfidence
        ))
    }

    func reset() {
        session.reset()
        confirmedEndTime = 0.0
        hypothesisStartTime = 0.0
    }

    // Compare current pass words against previous pass to find stable agreements.
    func processTranscriptionResult(words: [TimedWord], resultConfidence: Float = 1.0) -> AgreementResult {
        let pass = session.observe(
            words: words.map {
                HwTimedWord(
                    text: $0.text,
                    startTime: $0.startTime,
                    endTime: $0.endTime,
                    confidence: $0.confidence
                )
            },
            passConfidence: resultConfidence
        )

        // Returned on every pass, committing or not, so ParakeetStreamingSession's
        // three reads of these two stay on Swift properties instead of the FFI.
        confirmedEndTime = pass.confirmedEndTime
        hypothesisStartTime = pass.hypothesisStartTime

        return AgreementResult(
            fullText: pass.fullText,
            newlyConfirmedText: pass.newlyConfirmedText
        )
    }

    // MARK: - Token-to-Word Merging

    // Rebuild word timings from the decoded transcript text first. This is more
    // robust than trusting token boundary markers because some streaming slices
    // can return token timings without reliable leading-space markers even when
    // `result.text` itself is spaced correctly.
    static func words(from timings: [TokenTiming], transcript: String, timeOffset: Double = 0.0) -> [TimedWord] {
        guard !timings.isEmpty else { return [] }

        let normalizedTranscript = transcript
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let transcriptWords = normalizedTranscript
            .split(whereSeparator: \.isWhitespace)
            .map(String.init)

        guard !transcriptWords.isEmpty else {
            return mergeTokensToWords(timings, timeOffset: timeOffset)
        }

        struct TokenPiece {
            let text: String
            let startTime: Double
            let endTime: Double
            let confidence: Float
        }

        let pieces = timings.compactMap { timing -> TokenPiece? in
            let piece = stripWordBoundaryPrefix(timing.token)
            guard !piece.isEmpty, piece != "<blank>", piece != "<pad>" else {
                return nil
            }

            return TokenPiece(
                text: piece,
                startTime: timing.startTime + timeOffset,
                endTime: timing.endTime + timeOffset,
                confidence: timing.confidence
            )
        }

        guard !pieces.isEmpty else {
            return mergeTokensToWords(timings, timeOffset: timeOffset)
        }

        let compactTranscript = transcriptWords.joined()
        let compactPieces = pieces.map(\.text).joined()
        guard compactTranscript == compactPieces else {
            return mergeTokensToWords(timings, timeOffset: timeOffset)
        }

        var words: [TimedWord] = []
        var pieceIndex = 0
        var consumedCharactersInPiece = 0

        for word in transcriptWords {
            var remainingCharacters = word.count
            var firstPieceIndex: Int?
            var lastPieceIndex: Int?
            var confidences: [Float] = []

            while remainingCharacters > 0, pieceIndex < pieces.count {
                let piece = pieces[pieceIndex]
                let availableCharacters = piece.text.count - consumedCharactersInPiece

                if availableCharacters <= 0 {
                    pieceIndex += 1
                    consumedCharactersInPiece = 0
                    continue
                }

                if firstPieceIndex == nil {
                    firstPieceIndex = pieceIndex
                }
                lastPieceIndex = pieceIndex
                confidences.append(piece.confidence)

                if availableCharacters <= remainingCharacters {
                    remainingCharacters -= availableCharacters
                    pieceIndex += 1
                    consumedCharactersInPiece = 0
                } else {
                    consumedCharactersInPiece += remainingCharacters
                    remainingCharacters = 0
                }
            }

            guard remainingCharacters == 0,
                  let firstPieceIndex,
                  let lastPieceIndex else {
                return mergeTokensToWords(timings, timeOffset: timeOffset)
            }

            let averageConfidence = confidences.isEmpty ? 1.0 :
                confidences.reduce(0, +) / Float(confidences.count)
            words.append(TimedWord(
                text: word,
                startTime: pieces[firstPieceIndex].startTime,
                endTime: pieces[lastPieceIndex].endTime,
                confidence: averageConfidence
            ))
        }

        if pieceIndex != pieces.count || consumedCharactersInPiece != 0 {
            return mergeTokensToWords(timings, timeOffset: timeOffset)
        }

        return words
    }

    // Merge SentencePiece sub-word tokens into whole words. Tokens starting with `▁` mark boundaries.
    static func mergeTokensToWords(_ timings: [TokenTiming], timeOffset: Double = 0.0) -> [TimedWord] {
        guard !timings.isEmpty else { return [] }

        var words: [TimedWord] = []
        var currentText = ""
        var wordStart = 0.0
        var wordEnd = 0.0
        var currentConfidences: [Float] = []

        for timing in timings {
            let token = timing.token
            if token.isEmpty || token == "<blank>" || token == "<pad>" {
                continue
            }

            if token.hasPrefix("▁") || token.hasPrefix(" ") {
                if !currentText.isEmpty {
                    let avgConfidence = currentConfidences.isEmpty ? 1.0 :
                        currentConfidences.reduce(0, +) / Float(currentConfidences.count)
                    words.append(TimedWord(
                        text: currentText,
                        startTime: wordStart + timeOffset,
                        endTime: wordEnd + timeOffset,
                        confidence: avgConfidence
                    ))
                }
                let stripped = stripWordBoundaryPrefix(token)
                currentText = stripped
                wordStart = timing.startTime
                wordEnd = timing.endTime
                currentConfidences = [timing.confidence]
            } else {
                if currentText.isEmpty {
                    wordStart = timing.startTime
                }
                currentText += token
                wordEnd = timing.endTime
                currentConfidences.append(timing.confidence)
            }
        }

        if !currentText.isEmpty {
            let avgConfidence = currentConfidences.isEmpty ? 1.0 :
                currentConfidences.reduce(0, +) / Float(currentConfidences.count)
            words.append(TimedWord(
                text: currentText,
                startTime: wordStart + timeOffset,
                endTime: wordEnd + timeOffset,
                confidence: avgConfidence
            ))
        }

        return words
    }

    // MARK: - Private

    private static func stripWordBoundaryPrefix(_ token: String) -> String {
        var stripped = token
        while let first = stripped.first, first == "▁" || first.isWhitespace {
            stripped.removeFirst()
        }
        return stripped
    }
}
