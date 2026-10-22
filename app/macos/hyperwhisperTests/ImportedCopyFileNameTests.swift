//
//  ImportedCopyFileNameTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

/// Issue #1554: Transcribe File refused a source name of 236-255 bytes,
/// because `imported_<timestamp>_` pushed the copy's name past APFS's 255 bytes.
@MainActor
struct ImportedCopyFileNameTests {

    private let timestamp = 1_791_447_602
    private let limit = FileTranscriptionFlow.importedCopyNameMaxBytes

    private func name(_ stem: String, _ ext: String = "wav") -> String {
        FileTranscriptionFlow.importedCopyFileName(originalName: stem, fileExtension: ext, timestamp: timestamp)
    }

    @Test func shortNameIsKeptWhole() {
        #expect(name("voice-note") == "imported_1791447602_voice-note.wav")
    }

    @Test func noExtensionGainsNoTrailingDot() {
        #expect(name("voice-note", "") == "imported_1791447602_voice-note")
    }

    @Test(arguments: [231, 232, 246, 251])
    func longASCIINameFitsWithExtensionKept(stemLength: Int) {
        let copy = name(String(repeating: "L", count: stemLength))
        #expect(copy.utf8.count == limit)
        #expect(copy.hasPrefix("imported_1791447602_LLL"))
        #expect(copy.hasSuffix("L.wav"))
    }

    @Test func derivedVADAndVideoNamesStayUnderAPFSLimit() {
        let copy = URL(fileURLWithPath: "/tmp/hw-tests/imports")
            .appendingPathComponent(name(String(repeating: "V", count: 251), "mp4"))
        let extracted = TranscriptAudioFiles.extractedAudioURL(forVideo: copy)
        let trimmed = SilenceTrimmer().generateOutputURL(for: extracted)
        let uploadCopy = TranscriptAudioFiles.uploadCopyURL(forTrimmedWAV: trimmed)
        for url in [copy, extracted, trimmed, uploadCopy] {
            #expect(url.lastPathComponent.utf8.count <= 255)
        }
    }

    @Test func multiByteNameIsCutOnACharacterBoundary() {
        // é decomposes to e + U+0301 (3 bytes) on disk.
        let copy = name(String(repeating: "é", count: 120))
        #expect(copy.decomposedStringWithCanonicalMapping.utf8.count <= limit)
        #expect(copy.hasSuffix("é.wav"))
        let emoji = name(String(repeating: "👩‍👩‍👧", count: 20))
        #expect(emoji.utf8.count <= limit)
        #expect(emoji.hasSuffix("👩‍👩‍👧.wav"))
    }
}
