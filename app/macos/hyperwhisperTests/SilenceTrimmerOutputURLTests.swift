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
