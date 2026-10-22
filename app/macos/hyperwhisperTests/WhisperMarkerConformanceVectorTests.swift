//
//  WhisperMarkerConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/whisper-marker-vectors.json` against the Swift
//  UniFFI binding. Issue #1811 moved the rule "which Whisper output is a
//  non-speech marker, not words" out of three private copies (this head's
//  `LibWhisper.cleanTranscription` list, Windows `TranscriptionService` and the
//  shared .NET `WhisperNonSpeechMarkers`) into one `hw-text` function, which
//  `cleanTranscription` now calls first. Rust and C# run the same file:
//
//    shared-core-rs/crates/hw-text/tests/whisper_marker_vectors.rs
//    app/shared-dotnet/HyperWhisper.LocalInference.Tests/Program.cs
//    app/windows/HyperWhisper.SmokeTests/Program.cs
//

import Foundation
import Testing
@testable import HyperWhisper

struct WhisperMarkerConformanceVectorTests {

    struct Document: Decodable {
        let description: String
        let cases: [MarkerVector]
    }

    struct MarkerVector: Decodable {
        let name: String
        let input: String
        let expected: String
    }

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/whisper-marker-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    @Test("Whisper's non-speech markers strip as the shared vectors say")
    func markersMatchVectors() {
        #expect(document.cases.count >= 20, "whisper-marker-vectors.json looks truncated")
        for vector in document.cases {
            let actual = stripWhisperNonSpeechMarkers(text: vector.input)
            #expect(actual == vector.expected, "\(vector.name): got '\(actual)'")
        }
    }
}
