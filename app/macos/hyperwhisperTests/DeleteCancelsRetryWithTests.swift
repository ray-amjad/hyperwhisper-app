//
//  DeleteCancelsRetryWithTests.swift
//  hyperwhisperTests
//
//  Issue #1458: History > Retry with… makes a new "Processing" row and starts
//  its transcription in an unstructured task nobody kept. Deleting that row
//  removed it from the list and the store, but the engine ran on for minutes
//  with nothing to write to. `TranscriptActionHandler` now keeps each
//  "Retry with" task by the new row's ID, and both delete paths cancel it.
//  The cancel reaches the engine through `TranscriptionPipeline.value(of:)`
//  (#1507, covered in `FileTranscriptionCancelStopsEngineTests`).
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct DeleteCancelsRetryWithTests {

    /// Stands in for the retry's transcription: it runs until it is cancelled.
    private func makeRunningTask() -> Task<Void, Never> {
        Task {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
        }
    }

    @Test func cancellingARowStopsItsRetryTask() async {
        let rowId = UUID()
        let task = makeRunningTask()
        TranscriptActionHandler.trackRetryWithTask(task, for: rowId)
        #expect(TranscriptActionHandler.hasRetryWithTask(for: rowId))

        TranscriptActionHandler.cancelRetryWithTask(for: rowId)

        // Before the fix nothing held the task, so it would run until the engine finished.
        await task.value
        #expect(task.isCancelled)
        #expect(!TranscriptActionHandler.hasRetryWithTask(for: rowId))
    }

    @Test func cancellingOneRowLeavesAnotherRowsRetryRunning() async {
        let deletedRow = UUID()
        let keptRow = UUID()
        let deletedTask = makeRunningTask()
        let keptTask = makeRunningTask()
        TranscriptActionHandler.trackRetryWithTask(deletedTask, for: deletedRow)
        TranscriptActionHandler.trackRetryWithTask(keptTask, for: keptRow)

        TranscriptActionHandler.cancelRetryWithTask(for: deletedRow)
        await deletedTask.value

        #expect(deletedTask.isCancelled)
        #expect(!keptTask.isCancelled)
        #expect(TranscriptActionHandler.hasRetryWithTask(for: keptRow))

        TranscriptActionHandler.cancelRetryWithTask(for: keptRow)
        await keptTask.value
    }

    @Test func cancellingARowWithNoRetryIsANoOp() {
        let rowId = UUID()
        TranscriptActionHandler.cancelRetryWithTask(for: rowId)
        #expect(!TranscriptActionHandler.hasRetryWithTask(for: rowId))
    }

    // MARK: - Both delete paths cancel

    /// The single and the bulk delete each cancel the row's retry before the
    /// row is deleted. Sliced from the source, because driving them needs a
    /// live `TranscriptionPipeline` and a confirmation alert.
    @Test func bothDeletePathsCancelBeforeDeleting() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift")
        let source = try String(contentsOf: url, encoding: .utf8)

        for (start, end) in [
            ("func deleteTranscript(_ transcript: Transcript", "func deleteTranscripts("),
            ("func deleteTranscripts(", "// MARK: - Copy Action"),
        ] {
            let body = try #require(slice(source, from: start, to: end))
            let cancel = try #require(body.range(of: "Self.cancelRetryWithTask(for:"))
            let delete = try #require(body.range(of: "persistenceController.deleteTranscript("))
            #expect(cancel.lowerBound < delete.lowerBound, "\(start) must cancel the retry before it deletes the row")
        }
    }

    private func slice(_ source: String, from start: String, to end: String) -> Substring? {
        guard let startRange = source.range(of: start),
              let endRange = source.range(of: end, range: startRange.upperBound..<source.endIndex) else {
            return nil
        }
        return source[startRange.lowerBound..<endRange.lowerBound]
    }
}
