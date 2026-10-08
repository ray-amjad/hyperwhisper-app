//
//  FileTranscriptionFailedRowTests.swift
//  hyperwhisperTests
//
//  A file import that fails or is cancelled after its History row exists must
//  mark that row failed — with the audio file kept, so HistoryView offers Retry —
//  instead of leaving it at "Processing transcription..." until the next launch.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("File import resolves its processing History row")
struct FileTranscriptionFailedRowTests {

    // MARK: - Helpers

    /// A flow bound to its own in-memory store, never to the real recordings.
    private func makeFlow(_ persistence: PersistenceController) -> FileTranscriptionFlow {
        FileTranscriptionFlow(
            transcriptionPipeline: nil,
            settingsManager: nil,
            appState: nil,
            licenseManager: nil,
            persistence: persistence
        )
    }

    /// A real audio file on disk, the way an import's copy sits in the recordings folder.
    private func makeAudioFile() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("imported_\(UUID().uuidString).wav")
        try Data([0x52, 0x49, 0x46, 0x46]).write(to: url)
        return url
    }

    /// The row as the store holds it after the background writer saved.
    private func reload(_ id: NSManagedObjectID, in persistence: PersistenceController) throws -> Transcript {
        let context = persistence.container.viewContext
        context.refreshAllObjects()
        return try #require(try context.existingObject(with: id) as? Transcript)
    }

    private func status(_ transcript: Transcript) -> String? {
        transcript.value(forKey: "status") as? String
    }

    private func failedReason(_ transcript: Transcript) -> String? {
        transcript.value(forKey: "failedReason") as? String
    }

    // MARK: - Error path

    /// An error after the row exists marks it failed, with the dictation text.
    @Test func errorAfterRowExistsMarksRowFailed() async throws {
        let persistence = PersistenceController(inMemory: true)
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }
        let row = persistence.createProcessingTranscript(duration: 3, mode: "Cloud", audioFilePath: audio.path)
        #expect(status(row) == "processing")

        let flow = makeFlow(persistence)
        flow.processingTranscriptID = row.objectID

        await flow.markProcessingTranscriptFailed(after: FileImportFailure.pipelineUnavailable)

        let saved = try reload(row.objectID, in: persistence)
        #expect(status(saved) == "failed")
        #expect(failedReason(saved) == "Transcription manager unavailable")
        #expect(saved.text == "Transcription failed: Transcription manager unavailable")
        #expect(flow.processingTranscriptID == nil)
        // Retry needs the audio: canRetry = failed + audio file on disk.
        #expect(saved.audioFilePath == audio.path)
        #expect(FileManager.default.fileExists(atPath: audio.path))
    }

    /// A provider error (any non-cancel error) uses its own description.
    @Test func providerErrorTextNamesTheError() {
        struct NoKey: LocalizedError {
            var errorDescription: String? { "No API key for OpenAI" }
        }
        let outcome = FileTranscriptionFlow.failureOutcome(for: NoKey())
        #expect(outcome.failedReason == "No API key for OpenAI")
        #expect(outcome.errorText == "Transcription failed: No API key for OpenAI")
    }

    /// A failure before the row exists has nothing to resolve and writes nothing.
    @Test func errorBeforeRowExistsWritesNothing() async throws {
        let persistence = PersistenceController(inMemory: true)
        let other = persistence.createProcessingTranscript(duration: 1, mode: "Dictation", audioFilePath: "/test/other.wav")

        let flow = makeFlow(persistence)
        await flow.markProcessingTranscriptFailed(after: FileTranscriptionError.cannotReadFile)

        #expect(status(try reload(other.objectID, in: persistence)) == "processing")
    }

    // MARK: - Cancel path

    /// Cancel mid-transcription marks the row failed as "cancelled", and keeps the
    /// audio file the row points at so Retry still works.
    @Test func cancelMarksRowFailedAndKeepsAudio() async throws {
        let persistence = PersistenceController(inMemory: true)
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }
        let row = persistence.createProcessingTranscript(duration: 3, mode: "Cloud", audioFilePath: audio.path)

        let flow = makeFlow(persistence)
        flow.processingTranscriptID = row.objectID

        flow.cancelTranscription()
        let write = try #require(flow.pendingFailureWrite)
        await write.value

        let saved = try reload(row.objectID, in: persistence)
        #expect(status(saved) == "failed")
        #expect(failedReason(saved) == "cancelled")
        #expect(saved.text == "Transcription cancelled")
        #expect(flow.processingTranscriptID == nil)
        #expect(FileManager.default.fileExists(atPath: audio.path))
    }

    /// The cancel owns the row: the task's catch that runs after it (with the
    /// error the cancelled request threw) must not relabel it.
    @Test func errorAfterCancelDoesNotRelabelTheRow() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 3, mode: "Cloud", audioFilePath: "/test/a.wav")

        let flow = makeFlow(persistence)
        flow.processingTranscriptID = row.objectID

        flow.cancelTranscription()
        await flow.markProcessingTranscriptFailed(after: URLError(.cancelled))
        await flow.pendingFailureWrite?.value

        let saved = try reload(row.objectID, in: persistence)
        #expect(failedReason(saved) == "cancelled")
        #expect(saved.text == "Transcription cancelled")
    }

    /// A cancel after the row completed (the flow clears the ID with the
    /// completed write) leaves the finished transcript alone.
    @Test func cancelAfterCompletionLeavesRowCompleted() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 3, mode: "Cloud", audioFilePath: "/test/b.wav")
        persistence.updateTranscriptWithTranscription(row, transcribedText: "hello world")

        let flow = makeFlow(persistence)
        flow.cancelTranscription()

        #expect(flow.pendingFailureWrite == nil)
        let saved = try reload(row.objectID, in: persistence)
        #expect(status(saved) == "completed")
        #expect(saved.text == "hello world")
    }

    @Test func cancellationMapsToTheDictationCancelText() {
        let outcome = FileTranscriptionFlow.failureOutcome(for: CancellationError())
        #expect(outcome.failedReason == "cancelled")
        #expect(outcome.errorText == "Transcription cancelled")
    }
}
