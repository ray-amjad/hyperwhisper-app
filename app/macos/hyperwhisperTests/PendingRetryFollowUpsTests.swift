//
//  PendingRetryFollowUpsTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1643, the follow-ups PR #1640 (#1636) left on
//  the recording pill's pending-file Retry.
//
//  1. A Quick Capture recording whose file could not be read was PASTED on a
//     successful Retry: its Quick Capture context ends at the stop, and the
//     Retry delivered with `isQuickCaptureRouting: false`. Ray (inbox ask
//     #490): it goes to Notes, like any Quick Capture dictation. The stop now
//     keeps the routing beside the pending path, and the Retry reads it.
//  2. A failed pill Retry wrote "Error: …" on the row. History's Retry writes
//     "Retry failed: …" (and a too-short file's length). Both now share
//     `TranscriptionRetryController.failedRow`.
//  3. `RecordingDialog.retryTranscription(_:)` had no caller. Ray: delete it.
//     Already gone on main; the guard below keeps it gone.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Pending-file Retry follow-ups: Quick Capture routing and the failed row (#1643)")
struct PendingRetryFollowUpsTests {

    private struct Row: Sendable {
        let status: String?
        let failedReason: String?
        let text: String?
        let retryCount: Int16
    }

    @MainActor
    private func rows(in persistence: PersistenceController) async -> [Row] {
        let context = persistence.container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
            let fetched = (try? context.fetch(request)) ?? []
            return fetched.map { transcript in
                Row(
                    status: transcript.value(forKey: "status") as? String,
                    failedReason: transcript.value(forKey: "failedReason") as? String,
                    text: transcript.text,
                    retryCount: transcript.value(forKey: "retryCount") as? Int16 ?? 0
                )
            }
        }
    }

    private static let audioPath = "/tmp/hw-1643-unreadable-recording.wav"

    @MainActor
    private func makeFailedRow(in persistence: PersistenceController) async throws -> NSManagedObjectID {
        let id = await persistence.createFailedTranscriptInBackground(
            duration: 4.2,
            mode: "Quick",
            audioFilePath: Self.audioPath,
            failedReason: "Audio file could not be read",
            errorText: "Error: Audio file could not be read"
        )
        return try #require(id)
    }

    // MARK: - 2. A failed pill Retry words the row as History's Retry does

    /// The row a failed pill Retry leaves reads the same as the row a failed
    /// History Retry leaves for the same error.
    @MainActor
    @Test func aFailedPillRetryWritesHistoryRetrysRowText() async throws {
        let persistence = PersistenceController(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)
        let error = TranscriptionError.noSpeechDetected

        await RecordingTranscriptionFlow.savePendingRetryFailure(
            error,
            failedRowWrite: Task { failedID },
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "Quick",
            persistence: persistence
        )

        let history = TranscriptionRetryController.failedRow(after: error, audioDuration: 0)
        let all = await rows(in: persistence)
        #expect(all.count == 1, "the retry added a row instead of failing the existing one")
        let row = try #require(all.first)
        #expect(row.status == "failed")
        #expect(row.text == history.text, "the pill Retry's row text differs from History Retry's: \(row.text ?? "nil")")
        #expect(row.text?.hasPrefix("Retry failed: ") == true, "\(row.text ?? "nil")")
        #expect(row.failedReason == history.failedReason)
        #expect(row.retryCount == 1)
    }

    /// With no failed row left to update, the new row records the attempt in
    /// the same words.
    @MainActor
    @Test func withNoFailedRowTheNewRowIsWordedTheSame() async throws {
        let persistence = PersistenceController(inMemory: true)
        let error = TranscriptionError.noSpeechDetected

        await RecordingTranscriptionFlow.savePendingRetryFailure(
            error,
            failedRowWrite: nil,
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "Quick",
            persistence: persistence
        )

        let all = await rows(in: persistence)
        let row = try #require(all.first)
        #expect(row.status == "failed")
        #expect(row.text == "Retry failed: \(error.localizedDescription)")
    }

    // MARK: - 1. The Quick Capture routing's lifetime

    /// The routing belongs to one pending file, like its row write: a new
    /// file, or none, drops it, so a later paste-mode file is never sent to
    /// Notes.
    @MainActor
    @Test func theQuickCaptureRoutingResetsWhenThePendingFileChanges() {
        let appState = AppState()

        appState.pendingRetryAudioPath = "/tmp/a.wav"
        appState.pendingRetryIsQuickCapture = true

        appState.pendingRetryAudioPath = "/tmp/a.wav"
        #expect(appState.pendingRetryIsQuickCapture, "re-setting the same file must keep its routing")

        appState.pendingRetryAudioPath = "/tmp/b.wav"
        #expect(!appState.pendingRetryIsQuickCapture)

        appState.pendingRetryIsQuickCapture = true
        appState.pendingRetryAudioPath = nil
        #expect(!appState.pendingRetryIsQuickCapture)
    }

    // MARK: - Call sites, read from the production source

    private static let errorHandlingPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift"
    private static let stopPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StopRecording.swift"
    private static let dialogPath =
        "app/macos/hyperwhisper/Views/RecordingDialog.swift"

    /// The stop keeps the Quick Capture routing in the same main-actor turn
    /// as the path that shows the Retry button, after the path (whose didSet
    /// drops it) and while the session's context still exists.
    @Test func theUnreadableFileBranchKeepsTheQuickCaptureRouting() throws {
        let branch = try ProductionSource.slice(
            of: Self.stopPath,
            from: "Audio file not readable after stop-flow check.",
            to: "let vadStart = Date()"
        )
        let path = try #require(branch.range(of: "appState?.pendingRetryAudioPath = audioURL.path"), "\(branch)")
        let routing = try #require(
            branch.range(of: "appState?.pendingRetryIsQuickCapture = quickCaptureContext != nil"),
            "the stop drops the recording's Quick Capture routing: \(branch)"
        )
        #expect(path.lowerBound < routing.lowerBound, "the path's didSet drops a routing set before it")
        let turn = String(branch[path.lowerBound..<routing.lowerBound])
        #expect(!turn.contains("await"), "the path and the routing are set in different main-actor turns: \(turn)")
    }

    /// The Retry delivers with the recording's routing, read before the path
    /// is cleared, never a hard-coded paste.
    @Test func theRetryDeliversWithTheRecordingsRouting() throws {
        let body = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "private func retryTranscriptionFromPendingPath(",
            to: "func handleRecordingStartFailure("
        )
        #expect(!body.contains("isQuickCaptureRouting: false"), "the Retry pastes a Quick Capture recording again")
        let read = try #require(body.range(of: "appState.pendingRetryIsQuickCapture"), "\(body)")
        let clear = try #require(body.range(of: "appState.pendingRetryAudioPath = nil", range: read.upperBound..<body.endIndex),
                                 "the routing is read after the path is cleared: \(body)")
        let deliver = try #require(body.range(of: "isQuickCaptureRouting: isQuickCaptureRetry"), "\(body)")
        #expect(read.lowerBound < clear.lowerBound)
        #expect(read.lowerBound < deliver.lowerBound)
    }

    /// 3. The dead dialog Retry stays deleted (Ray, inbox ask #490).
    @Test func theRecordingDialogHasNoRetryOfItsOwn() throws {
        let dialog = try ProductionSource.code(of: Self.dialogPath)
        #expect(!dialog.contains("func retryTranscription("), "RecordingDialog.retryTranscription is back")
    }
}
