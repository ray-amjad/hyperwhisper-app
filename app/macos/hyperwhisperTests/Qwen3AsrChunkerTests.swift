//
//  Qwen3AsrChunkerTests.swift
//  hyperwhisperTests
//
//  Pins how Qwen3 ASR splits long audio and joins the text. FluidAudio 0.15.2
//  gives Qwen3 a 512-token cache, so one call with the whole recording lost the
//  end of a clip over ~30 s and threw over ~38 s (#1400). These tests drive the
//  pure split and join functions on synthetic samples, with no CoreML model.
//

import Foundation
import Testing
@testable import HyperWhisper

struct Qwen3AsrChunkerTests {

    private static let rate = 16_000

    /// A 440 Hz tone: every frame is loud, so there is no silence anywhere.
    private static func tone(seconds: Double, amplitude: Float = 0.3) -> [Float] {
        let count = Int(seconds * Double(rate))
        return (0..<count).map { index in
            amplitude * Float(sin(2 * Double.pi * 440 * Double(index) / Double(rate)))
        }
    }

    /// `tone`, with the samples from `from` to `to` seconds replaced by `level`.
    private static func tone(seconds: Double, gapFrom from: Double, to: Double, level: Float = 0) -> [Float] {
        var samples = tone(seconds: seconds)
        for index in Int(from * Double(rate))..<Int(to * Double(rate)) {
            samples[index] = level * (index.isMultiple(of: 2) ? 1 : -1)
        }
        return samples
    }

    private static func seconds(_ sample: Int) -> Double {
        Double(sample) / Double(rate)
    }

    // MARK: - Split

    @Test func aClipUnderTheLimitStaysOneCall() {
        for length in [1.0, 15.0, 20.0] {
            let samples = Self.tone(seconds: length)
            let chunks = Qwen3AsrChunker.plan(samples: samples)
            #expect(chunks == [Qwen3AsrChunker.Chunk(start: 0, end: samples.count, overlapsPrevious: false)])
        }
    }

    @Test func aCutLandsInSilenceWhenThereIsSome() {
        // 34 s, the clip whose end main lost, with one 0.5 s pause at 14 s.
        let samples = Self.tone(seconds: 34, gapFrom: 14.0, to: 14.5)
        let chunks = Qwen3AsrChunker.plan(samples: samples)

        #expect(chunks.count == 2)
        let cut = Self.seconds(chunks[0].end)
        #expect(cut > 14.0 && cut < 14.5, "cut at \(cut) s is not in the pause")
        #expect(chunks[1].start == chunks[0].end)
        #expect(chunks[1].overlapsPrevious == false)
        #expect(chunks[1].end == samples.count)
    }

    @Test func aPauseInANoisyRoomStillCountsAsSilence() {
        // The pause sits at -36 dBFS, above the absolute floor, but far under
        // the speech around it.
        let samples = Self.tone(seconds: 30, gapFrom: 12.0, to: 12.5, level: 0.016)
        let chunks = Qwen3AsrChunker.plan(samples: samples)

        #expect(chunks.count == 2)
        let cut = Self.seconds(chunks[0].end)
        #expect(cut > 12.0 && cut < 12.5, "cut at \(cut) s is not in the pause")
        #expect(chunks[1].overlapsPrevious == false)
    }

    @Test func aLongRunWithNoSilenceStillGetsCut() {
        // 5 minutes with no pause at all: the recording main gave no text for.
        let samples = Self.tone(seconds: 300)
        let chunks = Qwen3AsrChunker.plan(samples: samples)
        let config = Qwen3AsrChunker.Config.default
        let maxLength = Int(config.maxChunkSeconds * Double(Self.rate))
        let overlap = Int(config.overlapSeconds * Double(Self.rate))

        #expect(chunks.count > 1)
        #expect(chunks.first?.start == 0)
        #expect(chunks.last?.end == samples.count)
        for chunk in chunks {
            #expect(chunk.end - chunk.start <= maxLength)
        }
        for (previous, next) in zip(chunks, chunks.dropFirst()) {
            // Each cut found no silence, so the next chunk re-hears the last 1 s.
            #expect(next.overlapsPrevious)
            #expect(next.start == previous.end - overlap)
        }
    }

    @Test func everyChunkStaysUnderTheLimitAndNoSampleIsSkipped() {
        // A 4 minute clip with a short pause every 7 s, like real speech.
        var samples = Self.tone(seconds: 240)
        var pause = 7.0
        while pause < 240 {
            for index in Int(pause * Double(Self.rate))..<Int((pause + 0.3) * Double(Self.rate)) {
                samples[index] = 0
            }
            pause += 7
        }
        let chunks = Qwen3AsrChunker.plan(samples: samples)
        let maxLength = Int(Qwen3AsrChunker.Config.default.maxChunkSeconds * Double(Self.rate))

        #expect(chunks.first?.start == 0)
        #expect(chunks.last?.end == samples.count)
        for chunk in chunks {
            #expect(chunk.end - chunk.start <= maxLength)
            #expect(chunk.overlapsPrevious == false)
        }
        for (previous, next) in zip(chunks, chunks.dropFirst()) {
            #expect(next.start == previous.end)
        }
    }

    @Test func theLastChunkIsNeverASliver() {
        // 20.5 s with no silence: a cut at 20 s would leave a 0.5 s tail that
        // Qwen3 cannot read.
        let samples = Self.tone(seconds: 20.5)
        let chunks = Qwen3AsrChunker.plan(samples: samples)
        let minLength = Int(Qwen3AsrChunker.Config.default.minChunkSeconds * Double(Self.rate))

        #expect(chunks.count == 2)
        #expect(chunks[1].end - chunks[1].start >= minLength)
    }

    // MARK: - Join

    @Test func theOverlapDoesNotDoubleWords() {
        let text = Qwen3AsrChunker.join([
            .init(text: "The quick brown fox jumps", overlapsPrevious: false),
            .init(text: "fox jumps over the lazy dog.", overlapsPrevious: true),
        ])
        #expect(text == "The quick brown fox jumps over the lazy dog.")
    }

    @Test func theOverlapMatchIgnoresCaseAndPunctuation() {
        let text = Qwen3AsrChunker.join([
            .init(text: "and then we said hello,", overlapsPrevious: false),
            .init(text: "Hello. How are you?", overlapsPrevious: true),
        ])
        #expect(text == "and then we said hello, How are you?")
    }

    @Test func aRepeatedWordAtASilenceCutIsKept() {
        // No overlap, so a word on both sides was said twice.
        let text = Qwen3AsrChunker.join([
            .init(text: "I said no", overlapsPrevious: false),
            .init(text: "no, not today", overlapsPrevious: false),
        ])
        #expect(text == "I said no no, not today")
    }

    @Test func anOverlapWithNoMatchKeepsAllTheText() {
        let text = Qwen3AsrChunker.join([
            .init(text: "first part", overlapsPrevious: false),
            .init(text: "second part", overlapsPrevious: true),
        ])
        #expect(text == "first part second part")
    }

    @Test func emptyChunksAreSkippedAndTheOrderIsKept() {
        let text = Qwen3AsrChunker.join([
            .init(text: " one ", overlapsPrevious: false),
            .init(text: "", overlapsPrevious: false),
            .init(text: "two", overlapsPrevious: false),
            .init(text: "\n", overlapsPrevious: true),
            .init(text: "three", overlapsPrevious: false),
        ])
        #expect(text == "one two three")
    }

    @Test func anOverlapAfterAnEmptyChunkKeepsTheRepeatedWord() {
        // The middle chunk heard no speech. The last chunk overlaps it, not
        // the first one, so its "stop" was said again and must stay.
        let text = Qwen3AsrChunker.join([
            .init(text: "stop", overlapsPrevious: false),
            .init(text: "", overlapsPrevious: true),
            .init(text: "stop now", overlapsPrevious: true),
        ])
        #expect(text == "stop stop now")
    }

    @Test func chineseJoinsWithNoSpaceAndDropsTheOverlap() {
        let text = Qwen3AsrChunker.join([
            .init(text: "今天天气很好", overlapsPrevious: false),
            .init(text: "很好我们去公园", overlapsPrevious: true),
            .init(text: "然后回家", overlapsPrevious: false),
        ])
        #expect(text == "今天天气很好我们去公园然后回家")
    }
}
