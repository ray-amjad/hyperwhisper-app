//
//  FileTranscriptionCancelNoAlertTests.swift
//  hyperwhisperTests
//
//  Issue #1871: Cancel on a local Whisper Transcribe File job showed a false
//  "Streaming interrupted" error alert. Local Whisper reports its abort as
//  `TranscriptionError.streamingInterrupted` (#1743), and the flow treated only
//  `CancellationError` as a cancel, so the abort fell through to the catch-all
//  that shows the alert. A cancelled task is now a cancel whatever it threw.
//

import Foundation
import Testing
@testable import HyperWhisper

struct FileTranscriptionCancelNoAlertTests {

    private static let flowPath =
        "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"

    // MARK: - What counts as a cancel

    @Test func aCancelledWhisperAbortIsACancel() {
        #expect(FileTranscriptionFlow.isCancel(TranscriptionError.streamingInterrupted, taskCancelled: true))
    }

    @Test func anyErrorFromACancelledTaskIsACancel() {
        #expect(FileTranscriptionFlow.isCancel(TranscriptionError.audioConversionFailed, taskCancelled: true))
    }

    @Test func aCancellationErrorIsACancel() {
        #expect(FileTranscriptionFlow.isCancel(CancellationError(), taskCancelled: false))
    }

    /// A real Whisper failure the user did not cancel still reaches the alert.
    @Test func anUncancelledWhisperFailureIsNotACancel() {
        #expect(!FileTranscriptionFlow.isCancel(TranscriptionError.streamingInterrupted, taskCancelled: false))
        #expect(!FileTranscriptionFlow.isCancel(TranscriptionError.audioConversionFailed, taskCancelled: false))
    }

    // MARK: - The import's catch order, read from the production source

    /// The cancel arm asks `isCancel` with the task's own flag, and it comes
    /// before the arms that show an alert.
    @Test func theCancelArmComesBeforeEveryAlertArm() throws {
        let catches = try ProductionSource.slice(
            of: Self.flowPath,
            from: "let result = try await transcriptionPipeline.transcribeWithDetails(",
            to: "nonisolated static func checkSourceReadable("
        )
        let cancel = try #require(
            catches.range(of: "} catch where Self.isCancel(error, taskCancelled: Task.isCancelled) {"),
            "the import has no cancel arm that reads the task's cancelled flag"
        )
        let known = try #require(catches.range(of: "} catch let error as FileTranscriptionError {"))
        let catchAll = try #require(catches.range(of: "} catch {"))
        #expect(cancel.lowerBound < known.lowerBound)
        #expect(cancel.lowerBound < catchAll.lowerBound)
        #expect(!catches[cancel.upperBound..<known.lowerBound].contains("showErrorAlert("),
                "the cancel arm must not show an alert")
    }
}
