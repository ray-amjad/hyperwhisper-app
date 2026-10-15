//
//  NoSpeechPendingRetryFailsRowTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1815, a sibling of #1429.
//
//  #1429 made an empty engine result fail the History row on Transcribe File,
//  History Retry and Retry with another mode. The recording pill's
//  pending-file Retry still saved "" through `savePendingRetryTranscript`, so
//  a silent file completed the failed "Audio file could not be read" row with
//  blank text. And History Retry checked for speech before it recorded its
//  VAD trim on the row, so the trimmed WAV of a no-speech retry was left on
//  disk with no row to own it.
//
//  The fix: the pending-file Retry calls `failPendingRetryIfNoSpeech` before it
//  delivers or saves, which fails the row with the "no speech" reason; History
//  Retry checks for speech after `setTrimmedAudioPath`.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("A pending-file Retry with no speech fails the History row (#1815)")
struct NoSpeechPendingRetryFailsRowTests {

    private struct Row: Sendable {
        let status: String?
        let failedReason: String?
        let text: String?
        let transcribedText: String?
        let audioFilePath: String?
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
                    transcribedText: transcript.value(forKey: "transcribedText") as? String,
                    audioFilePath: transcript.audioFilePath,
                    retryCount: transcript.value(forKey: "retryCount") as? Int16 ?? 0
                )
            }
        }
    }

    private static let audioPath = "/tmp/hw-1815-silent-recording.wav"
    private static let noSpeech = TranscriptionError.noSpeechDetected.localizedDescription

    /// The row the stop flow writes when the recorded file cannot be read.
    @MainActor
    private func makeFailedRow(in persistence: PersistenceController) async throws -> NSManagedObjectID {
        let id = await persistence.createFailedTranscriptInBackground(
            duration: 4.2,
            mode: "LocNemo",
            audioFilePath: Self.audioPath,
            failedReason: "Audio file could not be read",
            errorText: "Error: Audio file could not be read"
        )
        return try #require(id)
    }

    private func makeResult(rawText: String) -> TranscriptionResult {
        TranscriptionResult(
            text: rawText,
            rawText: rawText,
            timestamp: Date(),
            duration: 0,
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

    // MARK: - The pending-file Retry's row

    /// The issue's own sequence: a silent file is retried. The failed row
    /// stays failed, with the no-speech reason, and the retry is counted.
    @MainActor
    @Test(arguments: ["", " \n"])
    func aSilentRetryLeavesTheRowFailedWithTheNoSpeechReason(rawText: String) async throws {
        let persistence = PersistenceController(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)

        do {
            try await RecordingTranscriptionFlow.failPendingRetryIfNoSpeech(
                makeResult(rawText: rawText),
                failedRowWrite: Task { failedID },
                audioURL: URL(fileURLWithPath: Self.audioPath),
                modeName: "LocNemo",
                persistence: persistence
            )
            Issue.record("a silent retry passed the speech check")
        } catch {
            #expect(isNoSpeech(error), "expected .noSpeechDetected, got \(error)")
        }

        let all = await rows(in: persistence)
        #expect(all.count == 1, "the retry added a row instead of failing the existing one")
        let row = try #require(all.first)
        #expect(row.status == "failed")
        #expect(row.failedReason == Self.noSpeech)
        #expect(row.transcribedText == nil, "a blank transcript was saved")
        #expect(row.retryCount == 1)
        #expect(row.audioFilePath == Self.audioPath)
    }

    /// No failed row to update (its write failed, or the user deleted it):
    /// a new failed row records the attempt, never a blank completed one.
    @MainActor
    @Test func withNoFailedRowANewFailedRowRecordsTheRetry() async throws {
        let persistence = PersistenceController(inMemory: true)

        await #expect(throws: TranscriptionError.self) {
            try await RecordingTranscriptionFlow.failPendingRetryIfNoSpeech(
                makeResult(rawText: ""),
                failedRowWrite: nil,
                audioURL: URL(fileURLWithPath: Self.audioPath),
                modeName: "LocNemo",
                persistence: persistence
            )
        }

        let all = await rows(in: persistence)
        #expect(all.count == 1)
        let row = try #require(all.first)
        #expect(row.status == "failed")
        #expect(row.failedReason == Self.noSpeech)
        #expect(row.audioFilePath == Self.audioPath)
    }

    /// Control: a retry that heard speech passes and writes nothing here; the
    /// success path's `savePendingRetryTranscript` completes the row.
    @MainActor
    @Test func aRetryWithSpeechPassesAndLeavesTheRowAlone() async throws {
        let persistence = PersistenceController(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)

        try await RecordingTranscriptionFlow.failPendingRetryIfNoSpeech(
            makeResult(rawText: "hello"),
            failedRowWrite: Task { failedID },
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "LocNemo",
            persistence: persistence
        )

        let all = await rows(in: persistence)
        let row = try #require(all.first)
        #expect(row.failedReason == "Audio file could not be read")
        #expect(row.retryCount == 0)
    }

    // MARK: - Call sites, read from the production source

    private static let errorHandlingPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift"
    private static let retryControllerPath =
        "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptionRetryController.swift"

    /// The pending-file Retry checks for speech before it delivers or saves.
    @Test func thePendingRetryChecksForSpeechBeforeItDeliversOrSaves() throws {
        let beforeDelivery = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "transcribeWithDetails(",
            to: "deliverBatchTranscript("
        )
        #expect(beforeDelivery.contains("try await Self.failPendingRetryIfNoSpeech("),
                "the pending-file Retry delivers a result without checking it holds speech: \(beforeDelivery)")
        #expect(!beforeDelivery.contains("savePendingRetryTranscript("),
                "the pending-file Retry saves before its speech check")
    }

    /// History Retry records its VAD trim on the row before the speech check,
    /// so a no-speech retry's trimmed WAV is owned by the failed row and swept
    /// with it, not left on disk.
    @Test func historyRetryRecordsTheTrimBeforeTheSpeechCheck() throws {
        let afterEngine = try ProductionSource.slice(
            of: Self.retryControllerPath,
            from: "transcribeWithDetails(",
            to: "persistence.updateTranscriptWithTranscription("
        )
        let trim = try #require(afterEngine.range(of: "persistence.setTrimmedAudioPath("),
                                "the retry no longer records its trimmed WAV")
        let check = try #require(afterEngine.range(of: "try result.throwIfNoSpeech()"))
        #expect(trim.lowerBound < check.lowerBound,
                "the speech check throws before the trimmed WAV is recorded on the row")
    }
}
