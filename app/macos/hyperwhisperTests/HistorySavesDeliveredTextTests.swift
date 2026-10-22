//
//  HistorySavesDeliveredTextTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1638.
//
//  With AI post-processing off, the pipeline still removes filler words,
//  applies dictated "new line" commands and Vocabulary swaps before it pastes.
//  The four History saves (dictation, Transcribe File, History Retry, Retry
//  with another mode) passed `postProcessedText` only when `wasPostProcessed`
//  was true, so the row kept the engine's raw text and History and
//  `GET /recordings/{id}` showed text the user never received.
//
//  The fix: every save passes `TranscriptionResult.historyPostProcessedText`,
//  the delivered text whenever it differs from the raw text, and both row
//  writers overwrite `postProcessedText` and `postProcessingProvider`
//  (clearing them to nil), so a Retry never keeps an older run's text or
//  its AI provider badge.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("History saves the delivered text with post-processing off (#1638)")
struct HistorySavesDeliveredTextTests {

    // MARK: - Fixtures

    /// The issue's evidence: Nemotron, post-processing off, Vocabulary
    /// `fox` -> `fox terrier` and `art` -> `ART`.
    private static let raw = "The quick brown fox jumps. Our quarterly art report is ready"
    private static let pasted = "The quick brown fox terrier jumps. Our quarterly ART report is ready"

    private func makeResult(
        text: String = pasted,
        rawText: String = raw,
        postProcessed: Bool = false
    ) -> TranscriptionResult {
        TranscriptionResult(
            text: text,
            rawText: rawText,
            timestamp: Date(),
            duration: 0,
            mode: nil,
            provider: "Nemotron",
            wasPostProcessed: postProcessed,
            postProcessingProvider: postProcessed ? "openai" : nil
        )
    }

    private struct Row: Sendable {
        let text: String?
        let transcribedText: String?
        let postProcessedText: String?
        let postProcessingProvider: String?
    }

    private func row(_ id: NSManagedObjectID, in persistence: PersistenceController) async throws -> Row {
        let context = persistence.container.newBackgroundContext()
        let row = await context.perform { () -> Row? in
            guard let transcript = try? context.existingObject(with: id) as? Transcript else { return nil }
            return Row(
                text: transcript.text,
                transcribedText: transcript.value(forKey: "transcribedText") as? String,
                postProcessedText: transcript.value(forKey: "postProcessedText") as? String,
                postProcessingProvider: transcript.value(forKey: "postProcessingProvider") as? String
            )
        }
        return try #require(row)
    }

    /// What History, the Recording dialog and the Local API show: they read
    /// `postProcessedText` before `text`.
    private func displayed(_ row: Row) -> String? {
        if let postProcessed = row.postProcessedText, !postProcessed.isEmpty { return postProcessed }
        return row.text
    }

    // MARK: - The value each save passes

    @Test func postProcessingOffSavesTheSwappedText() {
        #expect(makeResult().historyPostProcessedText == Self.pasted)
    }

    @Test func postProcessingOffWithNothingChangedSavesNoSecondText() {
        #expect(makeResult(text: Self.raw).historyPostProcessedText == nil)
    }

    @Test func aPostProcessedRunStillSavesItsText() {
        #expect(makeResult(text: Self.raw, postProcessed: true).historyPostProcessedText == Self.raw)
        #expect(makeResult(postProcessed: true).historyPostProcessedText == Self.pasted)
    }

    // MARK: - The row writes

    /// The dictation save (`saveBatchTranscript`, also the pending-file Retry).
    @MainActor
    @Test func aDictationRowShowsThePastedText() async throws {
        let persistence = PersistenceController(inMemory: true)
        let created = await persistence.createProcessingTranscriptInBackground(
            duration: 3,
            mode: "Nemotron",
            audioFilePath: "/tmp/hw-1638-dictation.wav",
            trimmedAudioPath: nil
        )
        let id = try #require(created)

        let didSave = await RecordingTranscriptionFlow.saveBatchTranscript(makeResult(), to: id, persistence: persistence)
        #expect(didSave)

        let saved = try await row(id, in: persistence)
        #expect(saved.text == Self.pasted)
        #expect(displayed(saved) == Self.pasted)
        #expect(saved.transcribedText == Self.raw, "the raw transcript must stay the engine's text")
    }

    /// The main-actor writer used by Transcribe File, History Retry and Retry
    /// with another mode.
    @MainActor
    @Test func aFileOrRetryRowShowsThePastedText() async throws {
        let persistence = PersistenceController(inMemory: true)
        let transcript = persistence.createProcessingTranscript(
            duration: 3,
            mode: "Nemotron",
            audioFilePath: "/tmp/hw-1638-file.wav"
        )
        let result = makeResult()

        persistence.updateTranscriptWithTranscription(
            transcript,
            transcribedText: result.rawText,
            postProcessedText: result.historyPostProcessedText,
            transcriptionProvider: result.provider,
            postProcessingProvider: result.postProcessingProvider
        )

        let saved = try await row(transcript.objectID, in: persistence)
        #expect(saved.text == Self.pasted)
        #expect(displayed(saved) == Self.pasted)
        #expect(saved.transcribedText == Self.raw)
    }

    /// A History Retry overwrites a row an earlier run already completed.
    /// When the new text equals its raw text, the old `postProcessedText`
    /// must go, or History keeps showing the old run's text.
    @MainActor
    @Test func aRetryClearsTheEarlierRunsText() async throws {
        let persistence = PersistenceController(inMemory: true)
        let transcript = persistence.createProcessingTranscript(
            duration: 3,
            mode: "Nemotron",
            audioFilePath: "/tmp/hw-1638-retry.wav"
        )
        let first = makeResult()
        persistence.updateTranscriptWithTranscription(
            transcript,
            transcribedText: first.rawText,
            postProcessedText: first.historyPostProcessedText
        )

        let retry = makeResult(text: "a different take", rawText: "a different take")
        persistence.updateTranscriptWithTranscription(
            transcript,
            transcribedText: retry.rawText,
            postProcessedText: retry.historyPostProcessedText
        )

        let saved = try await row(transcript.objectID, in: persistence)
        #expect(saved.postProcessedText == nil, "the earlier run's text survived the Retry")
        #expect(displayed(saved) == "a different take")
    }

    /// Same for the serial writer (a pending-file Retry completes a row in place).
    @MainActor
    @Test func theBackgroundWriterClearsTheEarlierRunsText() async throws {
        let persistence = PersistenceController(inMemory: true)
        let created = await persistence.createProcessingTranscriptInBackground(
            duration: 3,
            mode: "Nemotron",
            audioFilePath: "/tmp/hw-1638-background.wav",
            trimmedAudioPath: nil
        )
        let id = try #require(created)
        await RecordingTranscriptionFlow.saveBatchTranscript(makeResult(), to: id, persistence: persistence)
        await RecordingTranscriptionFlow.saveBatchTranscript(
            makeResult(text: "a different take", rawText: "a different take"),
            to: id,
            persistence: persistence
        )

        let saved = try await row(id, in: persistence)
        #expect(saved.postProcessedText == nil, "the earlier run's text survived the second save")
        #expect(displayed(saved) == "a different take")
    }

    /// A Retry with AI off after an AI run: the old run's provider must go
    /// with its text, or History badges non-AI text as AI.
    @MainActor
    @Test func aRetryWithAIOffClearsTheEarlierRunsProvider() async throws {
        let persistence = PersistenceController(inMemory: true)
        let transcript = persistence.createProcessingTranscript(
            duration: 3,
            mode: "Nemotron",
            audioFilePath: "/tmp/hw-1638-retry-provider.wav"
        )
        for result in [makeResult(postProcessed: true), makeResult()] {
            persistence.updateTranscriptWithTranscription(
                transcript,
                transcribedText: result.rawText,
                postProcessedText: result.historyPostProcessedText,
                transcriptionProvider: result.provider,
                postProcessingProvider: result.postProcessingProvider
            )
        }

        let saved = try await row(transcript.objectID, in: persistence)
        #expect(saved.postProcessingProvider == nil, "the earlier run's AI provider survived the Retry")
        #expect(displayed(saved) == Self.pasted)
    }

    /// Same for the serial writer.
    @MainActor
    @Test func theBackgroundWriterClearsTheEarlierRunsProvider() async throws {
        let persistence = PersistenceController(inMemory: true)
        let created = await persistence.createProcessingTranscriptInBackground(
            duration: 3,
            mode: "Nemotron",
            audioFilePath: "/tmp/hw-1638-background-provider.wav",
            trimmedAudioPath: nil
        )
        let id = try #require(created)
        await RecordingTranscriptionFlow.saveBatchTranscript(makeResult(postProcessed: true), to: id, persistence: persistence)
        #expect(try await row(id, in: persistence).postProcessingProvider == "openai")
        await RecordingTranscriptionFlow.saveBatchTranscript(makeResult(), to: id, persistence: persistence)

        let saved = try await row(id, in: persistence)
        #expect(saved.postProcessingProvider == nil, "the earlier run's AI provider survived the second save")
        #expect(displayed(saved) == Self.pasted)
    }

    // MARK: - The four save sites, read from the production source

    /// Each site's row write, from the call to the provider argument after the text.
    static let sites: [(path: String, call: String)] = [
        ("app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+Delivery.swift",
         "persistence.updateTranscriptWithTranscriptionInBackground("),
        ("app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift",
         "persistence.updateTranscriptWithTranscription("),
        ("app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptionRetryController.swift",
         "persistence.updateTranscriptWithTranscription("),
        ("app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift",
         "persistenceController.updateTranscriptWithTranscription("),
    ]

    @Test(arguments: HistorySavesDeliveredTextTests.sites.map(\.path))
    func everySaveSitePassesTheDeliveredText(path: String) throws {
        let call = try #require(Self.sites.first { $0.path == path }?.call)
        let write = try ProductionSource.slice(of: path, from: call, to: "transcriptionProvider:")
        #expect(write.contains("postProcessedText: ") && write.contains(".historyPostProcessedText"),
                "\(path) no longer saves the delivered text: \(write)")
        #expect(!write.contains("wasPostProcessed"),
                "\(path) gates the saved text on AI post-processing again: \(write)")
    }
}
