//
//  WordAgreementEngine.swift
//  hyperwhisper
//
//  Stabilizes on-device Parakeet streaming output by comparing per-pass
//  transcriptions. A prefix becomes "confirmed" only when 3 consecutive
//  passes agree, a 3-sentence-ender punctuation rule is satisfied, and
//  the last 3 boundary words each exceed the confidence floor.
//

import FluidAudio
import Foundation

// MARK: - Data Types

struct TimedWord {
    let text: String
    let normalizedText: String
    let startTime: Double
    let endTime: Double
    let confidence: Float

    init(text: String, startTime: Double, endTime: Double, confidence: Float = 1.0) {
        self.text = text
        self.normalizedText = Self.normalize(text)
        self.startTime = startTime
        self.endTime = endTime
        self.confidence = confidence
    }

    private static func normalize(_ text: String) -> String {
        String(text.lowercased()
            .replacingOccurrences(of: "-", with: " ")
            .filter { $0.isLetter || $0.isNumber || $0.isWhitespace })
            .trimmingCharacters(in: .whitespacesAndNewlines)
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

// MARK: - Word Agreement Engine

@available(macOS 13.0, *)
final class WordAgreementEngine {

    private let config: AgreementConfig

    private var confirmedWords: [TimedWord] = []
    private var previousWords: [TimedWord] = []
    private var consecutiveAgreementCount: Int = 0
    private var isFirstPass: Bool = true

    private(set) var confirmedEndTime: Double = 0.0
    // Start time of the first unconfirmed word; used as the audio seek/trim point after confirmation.
    private(set) var hypothesisStartTime: Double = 0.0

    var confirmedText: String {
        confirmedWords.map(\.text).joined(separator: " ")
    }

    init(config: AgreementConfig = AgreementConfig()) {
        self.config = config
    }

    func reset() {
        confirmedWords = []
        previousWords = []
        consecutiveAgreementCount = 0
        isFirstPass = true
        confirmedEndTime = 0.0
        hypothesisStartTime = 0.0
    }

    // Compare current pass words against previous pass to find stable agreements.
    func processTranscriptionResult(words: [TimedWord], resultConfidence: Float = 1.0) -> AgreementResult {
        guard !words.isEmpty else {
            return makeResult(hypothesisWords: [], newlyConfirmedWords: [])
        }

        if isFirstPass {
            isFirstPass = false
            previousWords = words
            return makeResult(hypothesisWords: words, newlyConfirmedWords: [])
        }

        // Low-confidence pass: show as hypothesis but don't count toward agreement.
        if resultConfidence < config.minPassConfidence {
            consecutiveAgreementCount = 0
            previousWords = words
            return makeResult(hypothesisWords: words, newlyConfirmedWords: [])
        }

        let commonPrefix = findLongestCommonPrefix(current: words, previous: previousWords)
        previousWords = words

        if commonPrefix.count >= config.minWordsToConfirm {
            consecutiveAgreementCount += 1
        } else {
            consecutiveAgreementCount = 0
            return makeResult(hypothesisWords: words, newlyConfirmedWords: [])
        }

        guard consecutiveAgreementCount >= config.tokenConfirmationsNeeded else {
            return makeResult(hypothesisWords: words, newlyConfirmedWords: [])
        }

        let confirmUpTo = confirmationWordCount(words: Array(words.prefix(commonPrefix.count)))

        guard confirmUpTo > 0 else {
            return makeResult(hypothesisWords: words, newlyConfirmedWords: [])
        }

        // All 3 words at the confirmation boundary must meet the minimum confidence threshold.
        let boundaryWords = Array(words.prefix(confirmUpTo).suffix(3))
        let minBoundaryConfidence = boundaryWords.map(\.confidence).min() ?? 1.0
        guard minBoundaryConfidence >= config.minWordConfidence else {
            return makeResult(hypothesisWords: words, newlyConfirmedWords: [])
        }

        let newlyConfirmed = Array(words.prefix(confirmUpTo))
        let hypothesis = Array(words.dropFirst(confirmUpTo))

        confirmedWords.append(contentsOf: newlyConfirmed)
        if let lastConfirmed = newlyConfirmed.last {
            confirmedEndTime = lastConfirmed.endTime
        }

        hypothesisStartTime = hypothesis.first?.startTime ?? confirmedEndTime

        // Remaining hypothesis words already appeared in this pass, so start their count at 1.
        consecutiveAgreementCount = hypothesis.isEmpty ? 0 : 1
        previousWords = hypothesis
        isFirstPass = hypothesis.isEmpty

        return makeResult(hypothesisWords: hypothesis, newlyConfirmedWords: newlyConfirmed)
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

    private func findLongestCommonPrefix(current: [TimedWord], previous: [TimedWord]) -> [TimedWord] {
        let minCount = min(current.count, previous.count)
        var prefixLength = 0

        for i in 0..<minCount {
            if current[i].normalizedText == previous[i].normalizedText {
                prefixLength = i + 1
            } else {
                break
            }
        }

        return Array(current.prefix(prefixLength))
    }

    // Confirms at sentence boundaries; needs 3 enders, keeps last 2 sentences as hypothesis.
    private func confirmationWordCount(words: [TimedWord]) -> Int {
        guard !words.isEmpty else { return 0 }
        let sentenceEnders: Set<Character> = [".", "!", "?", ";"]
        var punctuationIndices: [Int] = []
        for i in 0..<words.count {
            if let lastChar = words[i].text.last, sentenceEnders.contains(lastChar) {
                punctuationIndices.append(i)
            }
        }

        if punctuationIndices.count >= 3 {
            let cutIndex = punctuationIndices[punctuationIndices.count - 3]
            let confirmCount = cutIndex + 1
            if confirmCount >= config.minWordsToConfirm {
                return confirmCount
            }
        }

        let trailingWordsToHold = max(1, config.trailingWordsToHoldWithoutPunctuation)
        guard words.count >= config.minWordsToConfirmWithoutPunctuation else { return 0 }

        let fallbackConfirmCount = words.count - trailingWordsToHold
        guard fallbackConfirmCount >= config.minWordsToConfirm else { return 0 }
        return fallbackConfirmCount
    }

    private func makeResult(hypothesisWords: [TimedWord], newlyConfirmedWords: [TimedWord]) -> AgreementResult {
        let confirmedText = confirmedWords.map(\.text).joined(separator: " ")
        let hypothesisText = hypothesisWords.map(\.text).joined(separator: " ")
        let newlyConfirmedText = newlyConfirmedWords.map(\.text).joined(separator: " ")

        var fullParts: [String] = []
        if !confirmedText.isEmpty { fullParts.append(confirmedText) }
        if !hypothesisText.isEmpty { fullParts.append(hypothesisText) }
        let fullText = fullParts.joined(separator: " ")

        return AgreementResult(
            fullText: fullText,
            newlyConfirmedText: newlyConfirmedText
        )
    }

    private static func stripWordBoundaryPrefix(_ token: String) -> String {
        var stripped = token
        while let first = stripped.first, first == "▁" || first.isWhitespace {
            stripped.removeFirst()
        }
        return stripped
    }
}
