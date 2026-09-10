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
        assertOutputName(inputName: "recording.m4a", expectedName: "recording_trimmed.wav")
    }

    @Test func mp3InputProducesWAVOutputName() {
        assertOutputName(inputName: "interview.mp3", expectedName: "interview_trimmed.wav")
    }

    @Test func wavInputProducesWAVOutputName() {
        assertOutputName(inputName: "voice-note.wav", expectedName: "voice-note_trimmed.wav")
    }

    private func assertOutputName(inputName: String, expectedName: String) {
        let inputURL = directory.appendingPathComponent(inputName)

        let outputURL = SilenceTrimmer().generateOutputURL(for: inputURL)

        #expect(outputURL.deletingLastPathComponent() == directory)
        #expect(outputURL.lastPathComponent == expectedName)
        #expect(outputURL.pathExtension == "wav")
    }
}
