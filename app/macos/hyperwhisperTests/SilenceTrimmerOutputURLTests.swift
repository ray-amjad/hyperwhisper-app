//
//  SilenceTrimmerOutputURLTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Regression guard for the VAD artifact name matching its LPCM/WAV contents.
struct SilenceTrimmerOutputURLTests {

    private let directory = URL(fileURLWithPath: "/tmp/hw-tests/imports", isDirectory: true)

    @Test func m4aInputProducesWAVOutputName() {
        assertOutputName(inputName: "recording.m4a", expectedName: "recording_m4a_trimmed.wav")
    }

    @Test func mp3InputProducesWAVOutputName() {
        assertOutputName(inputName: "interview.mp3", expectedName: "interview_mp3_trimmed.wav")
    }

    @Test func wavInputProducesWAVOutputName() {
        assertOutputName(inputName: "voice-note.wav", expectedName: "voice-note_wav_trimmed.wav")
    }

    @Test func sameBasenameWithDifferentExtensionsProducesUniqueWAVOutputNames() {
        let m4aOutput = outputURL(inputName: "recording.m4a")
        let mp3Output = outputURL(inputName: "recording.mp3")

        #expect(m4aOutput != mp3Output)
        #expect(m4aOutput.pathExtension == "wav")
        #expect(mp3Output.pathExtension == "wav")
    }

    @Test func largeTrimmedWAVFromCompressedInputUsesConversionPath() {
        let trimmedURL = outputURL(inputName: "recording.m4a")

        #expect(VADProcessingService.shouldConvertTrimmedWAV(
            pathExtension: trimmedURL.pathExtension,
            fileSize: AudioConstants.maxWAVFileSizeForUpload
        ))
    }

    @Test func smallTrimmedWAVDoesNotUseConversionPath() {
        #expect(!VADProcessingService.shouldConvertTrimmedWAV(
            pathExtension: "wav",
            fileSize: AudioConstants.maxWAVFileSizeForUpload - 1
        ))
    }

    @Test func diagnosticFormatVocabularyCoversSelectableFormats() {
        let selectableFormats = [
            "aac", "aif", "aifc", "aiff", "amr", "caf", "flac", "m4a",
            "mov", "mp3", "mp4", "mpeg", "mpga", "oga", "ogg", "opus",
            "wav", "webm"
        ]

        for format in selectableFormats {
            #expect(AudioConstants.diagnosticAudioFormat(format.uppercased()) == format)
        }
        #expect(AudioConstants.diagnosticAudioFormat("private-name") == "other")
    }

    @Test func vadContextsHaveFixedDiagnosticLabels() {
        #expect(VADProcessingContext.unspecified.logPrefix == "")
        #expect(VADProcessingContext.recording.logPrefix == "[Recording] ")
        #expect(VADProcessingContext.fileImport.logPrefix == "[FileImport] ")
        #expect(VADProcessingContext.retry.logPrefix == "[Retry] ")
    }

    @Test func internalTrimErrorsUseStableDiagnosticMetadata() {
        let metadata = TrimError.diagnosticMetadata(for: TrimError.invalidAudioFormat)

        #expect(metadata.stage == "output_format")
        #expect(metadata.domain == "com.hyperwhisper.silence-trimmer")
        #expect(metadata.code == "invalid_audio_format")
    }

    @Test func frameworkTrimErrorsKeepStageAndFrameworkIdentifiers() {
        let cause = NSError(domain: "AVFoundationErrorDomain", code: -11829)
        let metadata = TrimError.diagnosticMetadata(for: TrimError.audioLoadFailed(cause))

        #expect(metadata.stage == "audio_load")
        #expect(metadata.domain == "AVFoundationErrorDomain")
        #expect(metadata.code == "-11829")
    }

    private func assertOutputName(inputName: String, expectedName: String) {
        let outputURL = outputURL(inputName: inputName)

        #expect(outputURL.deletingLastPathComponent() == directory)
        #expect(outputURL.lastPathComponent == expectedName)
        #expect(outputURL.pathExtension == "wav")
    }

    private func outputURL(inputName: String) -> URL {
        let inputURL = directory.appendingPathComponent(inputName)
        return SilenceTrimmer().generateOutputURL(for: inputURL)
    }
}
