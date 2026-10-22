//
//  NoSpeechResultFailsRowTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1429.
//
//  Some local engines return "" for silence instead of throwing
//  `.noSpeechDetected` (Apple Speech, the Nemotron/Parakeet family). Transcribe
//  File, History Retry and Retry with another mode saved that "" through
//  `updateTranscriptWithTranscription`, which marks the row `completed`, so a
//  silent file gave a finished History row with no text and no notice.
//
//  The fix: each of the three paths calls `TranscriptionResult.throwIfNoSpeech()`
//  before its save, so the row goes to its failed path with the "no speech"
//  reason instead.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("A result with no speech fails the History row (#1429)")
struct NoSpeechResultFailsRowTests {

    private func makeResult(rawText: String, text: String? = nil) -> TranscriptionResult {
        TranscriptionResult(
            text: text ?? rawText,
            rawText: rawText,
            timestamp: Date(),
            duration: 30,
            mode: nil,
            provider: "Nemotron",
            wasPostProcessed: false,
            postProcessingProvider: nil
        )
    }

    private func isNoSpeech(_ error: Error) -> Bool {
        if let error = error as? TranscriptionError, case .noSpeechDetected = error { return true }
        return false
    }

    @Test(arguments: ["", " ", "\n", " \t\n "])
    func anEmptyResultThrowsNoSpeech(rawText: String) {
        #expect(makeResult(rawText: rawText).isNoSpeech)
        do {
            try makeResult(rawText: rawText).throwIfNoSpeech()
            Issue.record("an empty result passed the speech check")
        } catch {
            #expect(isNoSpeech(error), "expected .noSpeechDetected, got \(error)")
        }
    }

    @Test func aResultWithSpeechPasses() throws {
        #expect(!makeResult(rawText: "hello").isNoSpeech)
        try makeResult(rawText: "hello").throwIfNoSpeech()
    }

    /// The failed row a file import writes carries the no-speech message as
    /// its reason, so History says why, and no text (#1427).
    @MainActor
    @Test func theFailedRowSaysNoSpeech() {
        let outcome = FileTranscriptionFlow.failureOutcome(for: TranscriptionError.noSpeechDetected)
        let message = TranscriptionError.noSpeechDetected.localizedDescription
        #expect(outcome.failedReason == message)
        #expect(outcome.errorText.isEmpty)
    }

    // MARK: - The three save sites, read from the production source

    /// Each site's code from the engine call to its row save.
    static let sites: [(path: String, save: String)] = [
        ("app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift",
         "persistence.updateTranscriptWithTranscription("),
        ("app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptionRetryController.swift",
         "persistence.updateTranscriptWithTranscription("),
        ("app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift",
         "persistenceController.updateTranscriptWithTranscription("),
    ]

    @Test(arguments: NoSpeechResultFailsRowTests.sites.map(\.path))
    func everySaveSiteChecksForSpeechFirst(path: String) throws {
        let save = try #require(Self.sites.first { $0.path == path }?.save)
        let beforeSave = try ProductionSource.slice(of: path, from: "transcribeWithDetails(", to: save)
        #expect(beforeSave.contains("try result.throwIfNoSpeech()"),
                "\(path) saves a result without checking it holds speech: \(beforeSave)")
    }
}
