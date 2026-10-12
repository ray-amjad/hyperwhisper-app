//
//  DeleteCancelsPlainRetryTests.swift
//  hyperwhisperTests
//
//  Issue #1743, sibling of #1458: History > failed row > Retry sets that same
//  row to "processing" and awaits the transcription from a task nobody kept by
//  the row. Deleting the row left the engine running. `TranscriptActionHandler`
//  now runs the plain Retry in a task kept by the row's ID, both delete paths
//  cancel it (pinned for both kinds of retry in `DeleteCancelsRetryWithTests`),
//  and `TranscriptionRetryController` writes nothing to a row whose retry was
//  cancelled.
//
//  A newer dictation that takes the pipeline over (#1425) cancels only the
//  retry's engine run, not the retry task, so that row still records its
//  failure (the control test below).
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Deleting a row cancels its plain Retry (#1743)")
struct DeleteCancelsPlainRetryTests {

    private static let originalText = "Transcription failed: the engine could not decode the audio"

    // MARK: - Fixtures

    @MainActor
    private func makeFailedRow(in persistence: PersistenceController, audioFilePath: String) -> Transcript {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = "LocNemo"
        mode.model = "nemotron-asr-3.5-latin"
        mode.isDefault = true
        mode.createdDate = Date()
        mode.modifiedDate = Date()

        let transcript = Transcript(context: persistence.container.viewContext)
        transcript.id = UUID()
        transcript.date = Date()
        transcript.duration = 2
        transcript.audioFilePath = audioFilePath
        transcript.mode = mode.name
        transcript.modeRelationship = mode
        transcript.text = Self.originalText
        transcript.setValue("failed", forKey: "status")
        persistence.save()
        return transcript
    }

    private func makeAudioFile() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("retry-1743-\(UUID().uuidString).wav")
        try Data(repeating: 0, count: 64).write(to: url)
        return url
    }

    // MARK: - Cancelled retry

    /// The delete cancels the retry task before it removes the row. The
    /// controller then stops before the engine and writes no "Retry failed"
    /// onto the row the user is deleting.
    @MainActor
    @Test func aCancelledRetryStopsBeforeTheEngineAndWritesNoFailure() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        let transcript = makeFailedRow(in: persistence, audioFilePath: audio.path)
        // A nil pipeline: had the controller got past the cancel check, it
        // would throw `providerNotAvailable` and write "Retry failed: …".
        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)

        let retry = Task { try await controller.retryTranscription(for: transcript) }
        retry.cancel()

        do {
            _ = try await retry.value
            Issue.record("Expected the cancelled retry to throw")
        } catch is CancellationError {
            // Expected: stopped by the cancel, not by the engine.
        } catch {
            Issue.record("Expected CancellationError, got \(error)")
        }

        #expect(transcript.text == Self.originalText)
        #expect(transcript.value(forKey: "failedReason") == nil)
    }

    // MARK: - Control (#1425)

    /// Not cancelled: a failure from the engine (here the nil pipeline, which
    /// stands for a newer dictation superseding the run) still marks the row
    /// failed, exactly as before.
    @MainActor
    @Test func aRetryThatWasNotCancelledStillRecordsItsFailure() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        let transcript = makeFailedRow(in: persistence, audioFilePath: audio.path)
        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)

        let retry = Task { try await controller.retryTranscription(for: transcript) }
        await #expect(throws: TranscriptionError.self) {
            _ = try await retry.value
        }

        #expect(transcript.value(forKey: "status") as? String == "failed")
        #expect(transcript.text?.hasPrefix("Retry failed:") == true)
    }

    // MARK: - Tracking

    /// A finished run of a row does not untrack a newer run of the same row
    /// (History and the recording dialog each have a handler that can retry it).
    @MainActor
    @Test func anOlderRunDoesNotUntrackANewerRunOfTheSameRow() async {
        let rowId = UUID()
        let older = Task<Void, Never> {}
        let newer = Task<Void, Never> {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        let olderToken = UUID()
        TranscriptActionHandler.trackRetryTask(older, for: rowId, token: olderToken)
        TranscriptActionHandler.trackRetryTask(newer, for: rowId, token: UUID())

        TranscriptActionHandler.untrackRetryTask(for: rowId, token: olderToken)
        #expect(TranscriptActionHandler.hasRetryTask(for: rowId))

        TranscriptActionHandler.cancelRetryTask(for: rowId)
        await newer.value
        #expect(newer.isCancelled)
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }

    /// The plain Retry runs the pipeline in a task it tracks by the row's ID
    /// before it awaits it, so a delete during the retry finds it. Sliced from
    /// the source, because driving it needs a live `TranscriptionPipeline`.
    @Test func plainRetryTracksItsTaskBeforeAwaitingIt() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift")
        let source = try String(contentsOf: url, encoding: .utf8)

        let start = try #require(source.range(of: "func retryTranscription(_ transcript: Transcript) async -> Bool"))
        let end = try #require(source.range(of: "func retryTranscription(_ transcript: Transcript, with mode: Mode)", range: start.upperBound..<source.endIndex))
        let body = source[start.lowerBound..<end.lowerBound]

        let run = try #require(body.range(of: "pipeline.retryTranscription(for: transcript)"))
        let track = try #require(body.range(of: "Self.trackRetryTask(retryTask, for: transcriptId"))
        let wait = try #require(body.range(of: "try await retryTask.value"))
        #expect(run.lowerBound < track.lowerBound)
        #expect(track.lowerBound < wait.lowerBound, "the retry must be tracked before it is awaited")
    }
}
