//
//  RetryWithFailedRowTests.swift
//  hyperwhisperTests
//
//  A "Retry with" that fails must replace its row's "Processing
//  transcription..." placeholder (#1457), the way a failed file import does
//  (#1392), not only set the status and reason.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("Retry with resolves its failed History row text")
struct RetryWithFailedRowTests {

    private func reload(_ id: NSManagedObjectID, in persistence: PersistenceController) throws -> Transcript {
        let context = persistence.container.viewContext
        context.refreshAllObjects()
        return try #require(try context.existingObject(with: id) as? Transcript)
    }

    @Test func failedRetryReplacesProcessingText() async throws {
        struct ConversionFailed: LocalizedError {
            var errorDescription: String? { "Nemotron is not available: Audio conversion failed" }
        }
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 3, mode: "LocNemo", audioFilePath: "/test/retry.mp3")
        let placeholder = row.text

        await TranscriptActionHandler.markRetryWithFailed(row.objectID, after: ConversionFailed(), persistence: persistence)

        let saved = try reload(row.objectID, in: persistence)
        #expect(saved.value(forKey: "status") as? String == "failed")
        #expect(saved.value(forKey: "failedReason") as? String == "Nemotron is not available: Audio conversion failed")
        #expect(saved.text == "Transcription failed: Nemotron is not available: Audio conversion failed")
        #expect(saved.text != placeholder)
        #expect(saved.audioFilePath == "/test/retry.mp3")
    }

    @Test func cancelledRetryReadsCancelled() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 3, mode: "LocQwen", audioFilePath: "/test/retry2.mp3")

        await TranscriptActionHandler.markRetryWithFailed(row.objectID, after: CancellationError(), persistence: persistence)

        let saved = try reload(row.objectID, in: persistence)
        #expect(saved.value(forKey: "status") as? String == "failed")
        #expect(saved.value(forKey: "failedReason") as? String == "cancelled")
        #expect(saved.text == "Transcription cancelled")
    }
}
