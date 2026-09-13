//
//  SilenceTrimmerArtifactReadableTests.swift
//  hyperwhisperTests
//
//  Regression guard for issue #612: the VAD artifact must be COMPLETE on disk
//  by the time `trimSilence` returns.
//
//  WHY THIS TEST EXISTS:
//  =====================
//  `SilenceTrimmer.writeAudioFile` used to call `continuation.resume()` while
//  the `AVAudioFile` writer was still alive. An audio container is not valid
//  until its writer is disposed — that is when the RIFF sizes (WAV) or the
//  `moov` atom (MP4) are written — so the caller could resume and re-open a
//  file that was still a stub.
//
//  `FileTranscriptionFlow` does exactly that: it calls
//  `getAudioDuration(finalAudioURL)` — `AVURLAsset.load(.duration)` — on the
//  very next line after VAD processing. On an import that surfaced as the
//  alert "Transcription Error / Cannot Open", and after the artifact became a
//  `.wav` it degraded into a silent 0.0-second duration that defeats the
//  upload-duration guard.
//
//  The trimmer's contract is therefore: when `trimSilence` returns, the file at
//  `outputURL` is finished. This test asserts that contract the way the import
//  path consumes it.
//
//  THE FIXTURE:
//  ============
//  A synthetic 36-second `.m4a`, because that is what a video import feeds the
//  trimmer (`FileTranscriptionFlow` extracts a video's audio track to `.m4a`
//  before VAD runs) and because VAD only engages at 30 seconds or more.
//
//  The audio is generated, not committed, so the test target carries no binary
//  fixture. It is a source-filter model of speech — a glottal pulse train at a
//  moving F0, three formant resonators on a phone-rate formant track, a
//  syllabic amplitude envelope and fricative bursts — laid out as
//  silence / speech / a 12-second silent gap / speech / silence.
//
//  Silero v5.1.2 (`ggml-silero-v5.1.2.bin`, the model this app bundles) detects
//  both utterances at the app's default threshold of 0.50, and still detects
//  them at 0.95, so the fixture has a wide margin and does not depend on the
//  pseudo-random noise realisation.
//

import AVFoundation
import CoreMedia
import Foundation
import Testing
@testable import HyperWhisper

struct SilenceTrimmerArtifactReadableTests {

    /// Seconds by which the re-read duration may differ from `trimmedDuration`.
    ///
    /// The artifact is 16 kHz mono LPCM, so a complete file reports its exact
    /// frame count. This only absorbs the asset timescale's rounding — a stub
    /// file reports 0.0 and misses by the whole trimmed length.
    private let durationTolerance: TimeInterval = 0.1

    /// How many trims to run. The defect is a race between the writer's release
    /// on the GCD queue and the resumed caller, so one pass can win by luck.
    private let attempts = 3

    @Test func trimmedArtifactIsReadableImmediatelyAfterTrimming() async throws {
        let workingDirectory = try makeWorkingDirectory()
        defer { try? FileManager.default.removeItem(at: workingDirectory) }

        let inputURL = workingDirectory.appendingPathComponent("import-fixture.m4a")
        try writeFixtureM4A(to: inputURL)

        let trimmer = SilenceTrimmer()

        for attempt in 1...attempts {
            let result = try await trimmer.trimSilence(from: inputURL)

            // The artifact is always 16 kHz mono Int16 LPCM, so it is always a WAV.
            #expect(
                result.outputURL.pathExtension == "wav",
                "attempt \(attempt): expected a .wav artifact, got .\(result.outputURL.pathExtension)"
            )

            // VAD must actually have trimmed something, or the test is asserting
            // nothing about the writer.
            #expect(
                result.trimmedDuration > 1,
                "attempt \(attempt): VAD found no usable speech in the fixture (trimmed \(result.trimmedDuration)s of \(result.originalDuration)s) — the assertions below would be vacuous"
            )
            #expect(
                result.silenceRemoved > AudioConstants.minimumSilenceRemoved,
                "attempt \(attempt): expected the 12-second silent gap to be removed, only \(result.silenceRemoved)s went"
            )

            // THE REGRESSION: re-open the artifact the way FileTranscriptionFlow
            // does, on the line after the trim returns. No sleep, no retry.
            let asset = AVURLAsset(url: result.outputURL)
            let duration = try await asset.load(.duration)
            let seconds = CMTimeGetSeconds(duration)

            #expect(
                abs(seconds - result.trimmedDuration) < durationTolerance,
                "attempt \(attempt): artifact re-read as \(seconds)s but the trim reported \(result.trimmedDuration)s — the writer had not finished the file when trimSilence returned"
            )

            // A second, independent probe of the same defect: an unflushed writer
            // leaves a header-only file on disk.
            let fileSize = try fileSize(of: result.outputURL)
            let expectedAudioBytes = Int(result.trimmedDuration * 16000) * 2
            #expect(
                fileSize >= expectedAudioBytes,
                "attempt \(attempt): artifact is \(fileSize) bytes but \(result.trimmedDuration)s of 16 kHz mono Int16 LPCM needs at least \(expectedAudioBytes)"
            )
        }
    }

    // MARK: - Working directory

    private func makeWorkingDirectory() throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("hyperwhisper-trimmer-artifact-tests", isDirectory: true)
        try? FileManager.default.removeItem(at: directory)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func fileSize(of url: URL) throws -> Int {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes[.size] as? Int) ?? 0
    }

    // MARK: - Fixture

    /// Write the synthetic import fixture as an AAC `.m4a`.
    ///
    /// The writer is scoped so the container is finalized before this returns —
    /// which is precisely the discipline this test exists to assert in the
    /// production trimmer.
    private func writeFixtureM4A(to url: URL) throws {
        let samples = SyntheticSpeech.importFixture()

        guard let bufferFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: SyntheticSpeech.sampleRate,
            channels: 1,
            interleaved: false
        ) else {
            throw FixtureError.formatUnavailable
        }

        guard let buffer = AVAudioPCMBuffer(
            pcmFormat: bufferFormat,
            frameCapacity: AVAudioFrameCount(samples.count)
        ) else {
            throw FixtureError.bufferUnavailable
        }

        buffer.frameLength = AVAudioFrameCount(samples.count)
        guard let channel = buffer.floatChannelData?[0] else {
            throw FixtureError.bufferUnavailable
        }
        for (index, sample) in samples.enumerated() {
            channel[index] = sample
        }

        try autoreleasepool {
            let file = try AVAudioFile(
                forWriting: url,
                settings: [
                    AVFormatIDKey: Int(kAudioFormatMPEG4AAC),
                    AVSampleRateKey: SyntheticSpeech.sampleRate,
                    AVNumberOfChannelsKey: 1
                ]
            )
            try file.write(from: buffer)
        }
    }

    private enum FixtureError: Error {
        case formatUnavailable
        case bufferUnavailable
    }
}

// MARK: - SyntheticSpeech

/// A source-filter generator for speech-like audio that Silero VAD accepts.
///
/// Deterministic: the only pseudo-random source is a fixed-seed LCG, and it
/// feeds the fricatives and the silence floor only. The voiced energy, which is
/// what VAD keys on, is closed-form.
private enum SyntheticSpeech {

    static let sampleRate: Double = 16000

    /// Three vowel-ish formant triples, cycled one per 110 ms phone.
    private static let formantTrack: [(Double, Double, Double)] = [
        (700, 1220, 2600),
        (400, 2000, 2550),
        (280, 2250, 2900),
        (450, 800, 2830),
        (325, 700, 2700),
        (600, 1700, 2400)
    ]

    /// 36 seconds: 3 s silence, 5 s speech, a 12 s silent gap, 6 s speech, 10 s silence.
    static func importFixture() -> [Float] {
        var generator = LinearCongruential(seed: 49_668_273)
        var samples: [Float] = []
        samples.append(contentsOf: silence(seconds: 3, generator: &generator))
        samples.append(contentsOf: speech(seconds: 5, generator: &generator))
        samples.append(contentsOf: silence(seconds: 12, generator: &generator))
        samples.append(contentsOf: speech(seconds: 6, generator: &generator))
        samples.append(contentsOf: silence(seconds: 10, generator: &generator))
        return samples
    }

    // MARK: - Parts

    private static func silence(seconds: Double, generator: inout LinearCongruential) -> [Float] {
        let count = Int(seconds * sampleRate)
        return (0..<count).map { _ in Float(generator.next() * 0.0004) }
    }

    private static func speech(seconds: Double, generator: inout LinearCongruential) -> [Float] {
        let count = Int(seconds * sampleRate)

        // Glottal excitation: an impulse per pitch period, F0 drifting so the
        // utterance has intonation rather than a steady buzz.
        var excitation = [Double](repeating: 0, count: count)
        var noise = [Double](repeating: 0, count: count)
        var phase = 0.0
        for index in 0..<count {
            let time = Double(index) / sampleRate
            let f0 = 118
                + 14 * sin(2 * Double.pi * 0.9 * time)
                + 6 * sin(2 * Double.pi * 2.3 * time)
            phase += f0 / sampleRate
            if phase >= 1 {
                phase -= 1
                excitation[index] = 1
            }
            noise[index] = generator.next()
        }

        // Formant track: resonate each 110 ms phone through its own triple.
        var voiced = [Double](repeating: 0, count: count)
        let phoneLength = Int(0.11 * sampleRate)
        var phoneIndex = 0
        var start = 0
        while start < count {
            let end = min(count, start + phoneLength)
            let chunk = Array(excitation[start..<end])
            let (f1, f2, f3) = formantTrack[phoneIndex % formantTrack.count]
            let first = resonate(chunk, frequency: f1, bandwidth: 80)
            let second = resonate(chunk, frequency: f2, bandwidth: 100)
            let third = resonate(chunk, frequency: f3, bandwidth: 140)
            for offset in 0..<(end - start) {
                voiced[start + offset] = first[offset] + 0.5 * second[offset] + 0.25 * third[offset]
            }
            start = end
            phoneIndex += 1
        }

        // Fricatives: high-band noise, used for every fourth syllable's onset.
        let fricative = resonate(
            resonate(noise, frequency: 4800, bandwidth: 1400),
            frequency: 6200,
            bandwidth: 1600
        )

        // Syllabic envelope: a 260 ms raised-cosine syllable, then a 60 ms gap.
        var shaped = [Double](repeating: 0, count: count)
        let syllableLength = Int(0.26 * sampleRate)
        let syllableGap = Int(0.06 * sampleRate)
        let fricativeLength = Int(0.07 * sampleRate)
        var position = 0
        var syllable = 0
        while position < count {
            let length = min(syllableLength, count - position)
            for offset in 0..<length {
                let window = 0.5 - 0.5 * cos(2 * Double.pi * Double(offset) / Double(max(1, length - 1)))
                let index = position + offset
                if syllable % 4 == 3 && offset < fricativeLength {
                    shaped[index] = 0.6 * window * fricative[index]
                } else {
                    shaped[index] = window * voiced[index]
                }
            }
            position += length + syllableGap
            syllable += 1
        }

        // Normalise the utterance to a 0.3 peak.
        let peak = shaped.reduce(0.0) { max($0, abs($1)) }
        guard peak > 0 else { return shaped.map { Float($0) } }
        return shaped.map { Float($0 / peak * 0.3) }
    }

    // MARK: - Two-pole resonator

    /// `y[n] = x[n] + a1·y[n-1] + a2·y[n-2]` — one formant.
    private static func resonate(_ input: [Double], frequency: Double, bandwidth: Double) -> [Double] {
        let radius = exp(-Double.pi * bandwidth / sampleRate)
        let a1 = 2 * radius * cos(2 * Double.pi * frequency / sampleRate)
        let a2 = -radius * radius

        var output = [Double](repeating: 0, count: input.count)
        var previous = 0.0
        var beforePrevious = 0.0
        for index in 0..<input.count {
            let value = input[index] + a1 * previous + a2 * beforePrevious
            output[index] = value
            beforePrevious = previous
            previous = value
        }
        return output
    }

    // MARK: - Deterministic noise

    /// A fixed-seed LCG, so the fixture is byte-identical on every run.
    /// `SystemRandomNumberGenerator` would make a failure unreproducible.
    private struct LinearCongruential {
        private var state: UInt64

        init(seed: UInt64) {
            state = seed
        }

        /// The next sample in `[-1, 1)`.
        mutating func next() -> Double {
            state = (state &* 1_103_515_245 &+ 12_345) & 0x7FFF_FFFF
            return Double(state) / Double(0x4000_0000) - 1.0
        }
    }
}
