//
//  DeleteCancelsRetryWithTests.swift
//  hyperwhisperTests
//
//  Issue #1458: History > Retry with… makes a new "Processing" row and starts
//  its transcription in an unstructured task nobody kept. Deleting that row
//  removed it from the list and the store, but the engine ran on for minutes
//  with nothing to write to. `TranscriptActionHandler` now keeps each
//  "Retry with" task by the new row's ID, and PersistenceController's delete
//  cancels it (#1743), whichever path deletes the row.
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
        TranscriptActionHandler.trackRetryTask(task, for: rowId, token: UUID())
        #expect(TranscriptActionHandler.hasRetryTask(for: rowId))

        TranscriptActionHandler.cancelRetryTask(for: rowId)

        // Before the fix nothing held the task, so it would run until the engine finished.
        await task.value
        #expect(task.isCancelled)
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }

    @Test func cancellingOneRowLeavesAnotherRowsRetryRunning() async {
        let deletedRow = UUID()
        let keptRow = UUID()
        let deletedTask = makeRunningTask()
        let keptTask = makeRunningTask()
        TranscriptActionHandler.trackRetryTask(deletedTask, for: deletedRow, token: UUID())
        TranscriptActionHandler.trackRetryTask(keptTask, for: keptRow, token: UUID())

        TranscriptActionHandler.cancelRetryTask(for: deletedRow)
        await deletedTask.value

        #expect(deletedTask.isCancelled)
        #expect(!keptTask.isCancelled)
        #expect(TranscriptActionHandler.hasRetryTask(for: keptRow))

        TranscriptActionHandler.cancelRetryTask(for: keptRow)
        await keptTask.value
    }

    @Test func cancellingARowWithNoRetryIsANoOp() {
        let rowId = UUID()
        TranscriptActionHandler.cancelRetryTask(for: rowId)
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }

    // MARK: - Both History delete paths go through the store

    /// The single and the bulk History delete leave the cancel to
    /// PersistenceController's delete (pinned for real in
    /// `DeleteCancelsPlainRetryTests`), so one place owns the rule. Sliced from
    /// the source, because driving them needs a live `TranscriptionPipeline`
    /// and a confirmation alert.
    @Test func bothDeletePathsLeaveTheCancelToTheStore() throws {
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
            #expect(body.contains("persistenceController.deleteTranscript("), "\(start) must delete through PersistenceController")
            #expect(!body.contains("cancelRetryTask(for:"), "\(start) must leave the cancel to PersistenceController")
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
