//
//  RetryTooShortTests.swift
//  hyperwhisperTests
//
//  Issue #1836: a first import of a too-short file says "This file is too
//  short to transcribe (0.1 s)." and leaves the row text empty (#1427), but a
//  History Retry said "record for longer" and wrote "Retry failed: …" as the
//  text, and a "Retry with..." wrote "Transcription failed: …", so Copy
//  copied the error.
//
//  Ray's rules (2026-10-16): every Retry and "Retry with..." that fails as
//  too short gets the file wording and empty text, the reason only in Reason,
//  whatever made the row. Other failures keep their old text.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("A Retry of a too-short file")
struct RetryTooShortTests {

    private let tooShort = TranscriptionError.audioTooShort(fileSize: 3278)

    private var fileWording: String {
        FileTranscriptionFlow.failureMessage(for: tooShort, audioDuration: 0.1)
    }

    private func reload(_ id: NSManagedObjectID, in persistence: PersistenceController) throws -> Transcript {
        let context = persistence.container.viewContext
        context.refreshAllObjects()
        return try #require(try context.existingObject(with: id) as? Transcript)
    }

    // MARK: - Retry in place

    /// The row's Reason and the alert both read `failedReason`.
    @Test func aRetryThatFailsAsTooShortHasEmptyTextAndTheFileWording() {
        let failure = TranscriptionRetryController.failedRow(after: tooShort, audioDuration: 0.1)
        #expect(failure.failedReason == fileWording)
        #expect(!failure.failedReason.contains("record for longer"))
        #expect(failure.text == "")
    }

    @Test func aRetryThatFailsForAnotherReasonKeepsRetryFailed() {
        let error = TranscriptionError.noSpeechDetected
        let failure = TranscriptionRetryController.failedRow(after: error, audioDuration: 0.1)
        #expect(failure.failedReason == error.localizedDescription)
        #expect(failure.text == "Retry failed: \(error.localizedDescription)")
    }

    // MARK: - Retry with...

    @Test func aRetryWithThatFailsAsTooShortHasEmptyTextAndTheFileWording() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 0.1, mode: "LocNemo", audioFilePath: "/test/tiny.wav")

        await TranscriptActionHandler.markRetryWithFailed(
            row.objectID,
            after: tooShort,
            audioDuration: 0.1,
            persistence: persistence
        )

        let saved = try reload(row.objectID, in: persistence)
        #expect(saved.value(forKey: "status") as? String == "failed")
        #expect(saved.value(forKey: "failedReason") as? String == fileWording)
        #expect(saved.text == "")
    }

    @Test func aRetryWithThatFailsForAnotherReasonKeepsTranscriptionFailed() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 0.1, mode: "LocNemo", audioFilePath: "/test/tiny2.wav")
        let error = TranscriptionError.noSpeechDetected

        await TranscriptActionHandler.markRetryWithFailed(
            row.objectID,
            after: error,
            audioDuration: 0.1,
            persistence: persistence
        )

        let saved = try reload(row.objectID, in: persistence)
        #expect(saved.value(forKey: "failedReason") as? String == error.localizedDescription)
        #expect(saved.text == "Transcription failed: \(error.localizedDescription)")
    }
}
