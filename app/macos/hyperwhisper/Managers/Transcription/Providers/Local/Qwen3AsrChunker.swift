import Foundation

// QWEN3 LONG-AUDIO CHUNKING:
// FluidAudio 0.15.2 converted the Qwen3 decoder with a 512-token KV cache
// (`Qwen3AsrConfig.maxCacheSeqLen`). The prompt, the audio (13 tokens per
// second) and the generated text all share it. One call with the whole
// recording silently lost the end of a clip over ~30 s and threw "Prompt length
// N exceeds cache capacity 512" over ~38 s, so a 5 minute recording gave no
// text. FluidAudio dropped Qwen3 in 0.15.3, so no upstream fix will come.
//
// The provider now splits the samples into chunks of at most 20 s (260 audio
// tokens, which leaves room for the prompt and the text) and joins the text in
// order:
// - A clip of 20 s or less stays 1 call.
// - A cut goes in the quietest 0.2 s of the 10-20 s window, when that is
//   silence. No word straddles that cut, so the chunks do not overlap.
// - With no silence in the window, the cut goes at the end of the window and
//   the next chunk starts 1 s earlier, so a word on the cut is heard whole by
//   at least one chunk. `join` removes the words that both chunks heard.
//
// Pure functions over `[Float]`, so the tests need no CoreML model.
enum Qwen3AsrChunker {

    struct Config: Equatable {
        var sampleRate = 16_000
        /// The longest chunk. 20 s is 260 audio tokens of the 512.
        var maxChunkSeconds: Double = 20
        /// The shortest chunk a cut can make, and the start of the window a
        /// cut is searched in.
        var minChunkSeconds: Double = 10
        /// How far the next chunk starts before a cut that found no silence.
        var overlapSeconds: Double = 1
        /// The RMS frame length.
        var frameSeconds: Double = 0.02
        /// The length of quiet a cut needs.
        var quietWindowSeconds: Double = 0.2
        /// RMS at or below this is silence on any clip (about -40 dBFS).
        var absoluteSilenceRMS: Float = 0.01
        /// RMS at or below this fraction of the clip's median frame RMS is
        /// also silence, so a pause in a noisy room still counts.
        var relativeSilenceRatio: Float = 0.2

        static let `default` = Config()
    }

    /// One span of samples for one Qwen3 call.
    struct Chunk: Equatable {
        /// The first sample, inclusive.
        let start: Int
        /// The last sample, exclusive.
        let end: Int
        /// True when this chunk starts before the previous chunk ended,
        /// because that cut found no silence.
        let overlapsPrevious: Bool
    }

    /// One chunk's text, for `join`.
    struct Piece: Equatable {
        let text: String
        let overlapsPrevious: Bool
    }

    // MARK: - Split

    static func plan(samples: [Float], config: Config = .default) -> [Chunk] {
        let count = samples.count
        let maxLength = Int(config.maxChunkSeconds * Double(config.sampleRate))
        guard count > maxLength else {
            return [Chunk(start: 0, end: count, overlapsPrevious: false)]
        }

        let minLength = Int(config.minChunkSeconds * Double(config.sampleRate))
        let overlap = Int(config.overlapSeconds * Double(config.sampleRate))
        let frameLength = max(1, Int(config.frameSeconds * Double(config.sampleRate)))
        let windowFrames = max(1, Int((config.quietWindowSeconds / config.frameSeconds).rounded()))

        // Mean square per frame, and a running sum so each window costs O(1).
        let frameCount = count / frameLength
        var meanSquares = [Float](repeating: 0, count: frameCount)
        for frame in 0..<frameCount {
            var sum: Float = 0
            let base = frame * frameLength
            for index in base..<(base + frameLength) {
                sum += samples[index] * samples[index]
            }
            meanSquares[frame] = sum / Float(frameLength)
        }
        // Double: a 5 minute clip sums 15,000 frames, and Float rounding would
        // blur a quiet window into its neighbours.
        var prefix = [Double](repeating: 0, count: frameCount + 1)
        for frame in 0..<frameCount {
            prefix[frame + 1] = prefix[frame] + Double(meanSquares[frame])
        }

        let threshold = silenceThreshold(meanSquares: meanSquares, config: config)

        var chunks: [Chunk] = []
        var start = 0
        var overlapsPrevious = false
        while count - start > maxLength {
            let low = start + minLength
            // Never leave a last chunk shorter than `minLength`.
            let high = min(start + maxLength, count - minLength)

            var bestFrame: Int?
            var bestMeanSquare = Double.greatestFiniteMagnitude
            let firstFrame = (low + frameLength - 1) / frameLength
            let lastFrame = high / frameLength - windowFrames
            if firstFrame <= lastFrame {
                for frame in firstFrame...lastFrame {
                    let meanSquare = (prefix[frame + windowFrames] - prefix[frame]) / Double(windowFrames)
                    // `<=`: on a tie, the later window makes a longer chunk.
                    if meanSquare <= bestMeanSquare {
                        bestMeanSquare = meanSquare
                        bestFrame = frame
                    }
                }
            }

            if let bestFrame, max(0, bestMeanSquare).squareRoot() <= Double(threshold) {
                let cut = (bestFrame + windowFrames / 2) * frameLength
                chunks.append(Chunk(start: start, end: cut, overlapsPrevious: overlapsPrevious))
                start = cut
                overlapsPrevious = false
            } else {
                chunks.append(Chunk(start: start, end: high, overlapsPrevious: overlapsPrevious))
                start = high - overlap
                overlapsPrevious = true
            }
        }
        chunks.append(Chunk(start: start, end: count, overlapsPrevious: overlapsPrevious))
        return chunks
    }

    private static func silenceThreshold(meanSquares: [Float], config: Config) -> Float {
        guard !meanSquares.isEmpty else { return config.absoluteSilenceRMS }
        let sorted = meanSquares.sorted()
        let medianRMS = sorted[sorted.count / 2].squareRoot()
        return max(config.absoluteSilenceRMS, config.relativeSilenceRatio * medianRMS)
    }

    // MARK: - Join

    /// The most words (or, in a script with no spaces, characters) the 1 s
    /// overlap can double.
    static let maxOverlapWords = 8
    static let maxOverlapCharacters = 20

    /// Join the chunk texts in order. An empty text is skipped. Where a chunk
    /// overlaps the previous one, the longest run that ends the text so far
    /// and starts the chunk is kept once.
    static func join(_ pieces: [Piece]) -> String {
        var result = ""
        for piece in pieces {
            var text = piece.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            guard !result.isEmpty else {
                result = text
                continue
            }
            if piece.overlapsPrevious {
                text = removingOverlap(from: text, after: result)
                guard !text.isEmpty else { continue }
            }
            let noSpace = result.last.map(isUnspacedScript) == true
                || text.first.map(isUnspacedScript) == true
            result += noSpace ? text : " " + text
        }
        return result
    }

    private static func removingOverlap(from text: String, after previous: String) -> String {
        if let last = previous.last, let first = text.first,
           isUnspacedScript(last) && isUnspacedScript(first) {
            return removingCharacterOverlap(from: text, after: previous)
        }
        return removingWordOverlap(from: text, after: previous)
    }

    private static func removingWordOverlap(from text: String, after previous: String) -> String {
        let previousWords = previous.split(whereSeparator: \.isWhitespace).map(normalized)
        let words = text.split(whereSeparator: \.isWhitespace)
        let normalizedWords = words.map(normalized)
        let limit = min(maxOverlapWords, previousWords.count, words.count)
        guard limit > 0 else { return text }
        for length in stride(from: limit, through: 1, by: -1) {
            let tail = previousWords.suffix(length)
            let head = normalizedWords.prefix(length)
            if tail.allSatisfy({ !$0.isEmpty }) && Array(tail) == Array(head) {
                return words.dropFirst(length).joined(separator: " ")
            }
        }
        return text
    }

    /// For Chinese, Japanese and Thai, which put no space between words. A
    /// match must be at least 2 characters, so one common character is not
    /// taken for an overlap.
    private static func removingCharacterOverlap(from text: String, after previous: String) -> String {
        let previousCharacters = Array(previous)
        let characters = Array(text)
        let limit = min(maxOverlapCharacters, previousCharacters.count, characters.count)
        guard limit >= 2 else { return text }
        for length in stride(from: limit, through: 2, by: -1) {
            if Array(previousCharacters.suffix(length)) == Array(characters.prefix(length)) {
                return String(characters.dropFirst(length))
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
        return text
    }

    /// Lowercase, letters and digits only: "Hello," and "hello" are one word.
    private static func normalized(_ word: Substring) -> String {
        String(word.lowercased().unicodeScalars.filter {
            CharacterSet.alphanumerics.contains($0)
        }.map(Character.init))
    }

    private static func isUnspacedScript(_ character: Character) -> Bool {
        guard let scalar = character.unicodeScalars.first else { return false }
        switch scalar.value {
        case 0x0E00...0x0E7F,   // Thai
             0x3000...0x303F,   // CJK punctuation
             0x3040...0x30FF,   // Hiragana, Katakana
             0x3400...0x4DBF,   // CJK Extension A
             0x4E00...0x9FFF,   // CJK Unified Ideographs
             0xF900...0xFAFF,   // CJK Compatibility Ideographs
             0xFF00...0xFFEF:   // Fullwidth forms
            return true
        default:
            return false
        }
    }
}
