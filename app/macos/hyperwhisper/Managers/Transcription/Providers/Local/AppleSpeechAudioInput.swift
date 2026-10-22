import Foundation
import AVFoundation
import os

// APPLE SPEECH AUDIO INPUT (#1515):
//
// Opens the recording that `AppleSpeechAnalyzerProvider` hands to
// `SpeechAnalyzer.analyzeSequence(from:)`, and refuses a file that holds no
// audio frames.
//
// WHY THE FRAME CHECK:
// `AVAudioFile(forReading:)` succeeds for a WAV whose `data` chunk is empty (a
// 44-byte header-only file) and for a WAV with a `fmt ` chunk and no `data`
// chunk at all. Its `length` is 0. Given such a file, `analyzeSequence` and the
// `transcriber.results` stream never finish, so the transcription awaits
// forever: a Local API request hung until the 600 s connection timeout, with
// its staged copy on disk all that time. A file with no frames cannot hold
// speech, so it fails here, before the analyzer runs, as
// `TranscriptionError.invalidAudioFormat` — the error an unopenable file
// already gets (Local API code `AUDIO_DECODE_FAILED`).
//
// This lives outside the provider, which is macOS 26 only and needs Speech
// assets, so the decision is unit-testable on any macOS.
enum AppleSpeechAudioInput {

    /// Opens `url` for analysis.
    ///
    /// - Throws: `TranscriptionError.invalidAudioFormat` when the file cannot be
    ///   opened, or when it opens with no audio frames (`length <= 0`).
    ///   Logs one line with the error domain/code or the frame count — never
    ///   the path.
    static func openForAnalysis(_ url: URL, logger: Logger) throws -> AVAudioFile {
        let audioFile: AVAudioFile
        do {
            audioFile = try AVAudioFile(forReading: url)
        } catch {
            let nsError = error as NSError
            logger.error("Failed to open SpeechAnalyzer audio file; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            throw TranscriptionError.invalidAudioFormat
        }

        let frames = audioFile.length
        guard frames > 0 else {
            logger.error("SpeechAnalyzer audio file has no audio frames; refusing it before analysis; frames=\(frames, privacy: .public)")
            throw TranscriptionError.invalidAudioFormat
        }

        return audioFile
    }
}
