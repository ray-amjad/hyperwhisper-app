//
//  TranscriptionRetryDeletedModeTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1440.
//
//  History > failed row > Retry looked the row's mode up, got `nil` when that
//  mode had been deleted, and still called the pipeline with `mode: nil`. The
//  router turns a nil mode into HyperWhisper Cloud, so audio an on-device mode
//  recorded was sent to the Cloud without asking, and the failure then
//  overwrote the row's original `text` and `failedReason`.
//
//  Ray (2026-10-08): Retry stops, sends nothing, and leaves the row alone.
//
//  The controller runs on an in-memory store with a NIL pipeline. That nil is
//  the proof that no request is made: had the controller got past the guard,
//  it would flip the row to "processing", bump `retryCount`, and then throw
//  `providerNotAvailable` and rewrite the row as "Retry failed: …". The control
//  test shows exactly that happens when the mode still exists.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Retry refuses when the row's mode was deleted (#1440)")
struct TranscriptionRetryDeletedModeTests {

    private static let originalText = "Transcription failed: Nemotron could not decode the audio"
    private static let originalReason = "Nemotron could not decode the audio"

    // MARK: - Fixtures

    /// The store is held by the TEST for its whole length: a controller
    /// created and dropped inside a helper deallocates on return and every
    /// attribute reads back as its zero value.
    @MainActor
    private func makeMode(
        in persistence: PersistenceController,
        name: String,
        model: String,
        isDefault: Bool
    ) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = name
        mode.model = model
        mode.isDefault = isDefault
        // Mandatory with no default: without them `save()` only logs a
        // validation failure and nothing is written.
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        return mode
    }

    @MainActor
    private func makeFailedTranscript(
        in persistence: PersistenceController,
        audioFilePath: String,
        modeName: String?,
        modeRelationship: Mode?
    ) -> Transcript {
        let transcript = Transcript(context: persistence.container.viewContext)
        transcript.id = UUID()
        transcript.date = Date()
        transcript.duration = 2
        transcript.audioFilePath = audioFilePath
        transcript.mode = modeName
        transcript.modeRelationship = modeRelationship
        transcript.text = Self.originalText
        transcript.setValue("failed", forKey: "status")
        transcript.setValue(Self.originalReason, forKey: "failedReason")
        return transcript
    }

    /// A real file, so the audio-file check passes and the mode guard is what
    /// decides. Its bytes are never read on the refused path.
    private func makeAudioFile() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("retry-1440-\(UUID().uuidString).wav")
        try Data(repeating: 0, count: 64).write(to: url)
        return url
    }

    @MainActor
    private func expectRowUntouched(_ transcript: Transcript) {
        #expect(transcript.text == Self.originalText)
        #expect(transcript.value(forKey: "failedReason") as? String == Self.originalReason)
        #expect(transcript.value(forKey: "status") as? String == "failed")
        #expect(transcript.value(forKey: "retryCount") as? Int16 == 0)
        #expect(transcript.value(forKey: "lastRetryDate") == nil)
        #expect(transcript.hasChanges == false)
    }

    // MARK: - Refusals

    @MainActor
    @Test func aRowWhoseModeWasDeletedIsRefusedAndLeftAlone() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        // The live default is a Cloud mode, as on a fresh install. Retry must
        // not fall back to it.
        _ = makeMode(in: persistence, name: "Hyper", model: "cloud", isDefault: true)
        let onDevice = makeMode(
            in: persistence, name: "LocNemo", model: "nemotron-asr-3.5-latin", isDefault: false
        )
        let transcript = makeFailedTranscript(
            in: persistence,
            audioFilePath: audio.path,
            modeName: "LocNemo",
            modeRelationship: onDevice
        )
        persistence.save()

        persistence.deleteMode(onDevice)
        #expect(transcript.modeRelationship == nil)
        #expect(transcript.mode == "LocNemo")

        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)
        await #expect(throws: TranscriptionRetryError.originalModeDeleted) {
            _ = try await controller.retryTranscription(for: transcript)
        }

        expectRowUntouched(transcript)
    }

    @MainActor
    @Test func aRowThatNamesNoModeIsRefusedAndLeftAlone() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        _ = makeMode(in: persistence, name: "Hyper", model: "cloud", isDefault: true)
        let transcript = makeFailedTranscript(
            in: persistence,
            audioFilePath: audio.path,
            modeName: nil,
            modeRelationship: nil
        )
        persistence.save()

        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)
        await #expect(throws: TranscriptionRetryError.originalModeDeleted) {
            _ = try await controller.retryTranscription(for: transcript)
        }

        expectRowUntouched(transcript)
    }

    @Test func theRefusalSaysTheModeWasDeletedAndNamesRetryWith() {
        let message = TranscriptionRetryError.originalModeDeleted.errorDescription ?? ""
        // A missing key would come back as the identifier itself.
        #expect(message != "transcripts.retry.error.modeDeleted")
        #expect(message.contains("history.context.retryWith".localized))
    }

    // MARK: - Control

    /// The guard only refuses an unresolvable mode. A live mode, found by name
    /// through the injected store, goes on to the pipeline step exactly as
    /// before — here the nil pipeline then fails it the old way.
    @MainActor
    @Test func aRowWhoseModeStillExistsIsNotRefused() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        _ = makeMode(in: persistence, name: "LocNemo", model: "nemotron-asr-3.5-latin", isDefault: true)
        let transcript = makeFailedTranscript(
            in: persistence,
            audioFilePath: audio.path,
            modeName: "LocNemo",
            modeRelationship: nil
        )
        persistence.save()

        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)
        do {
            _ = try await controller.retryTranscription(for: transcript)
            Issue.record("Expected the nil pipeline to fail the retry")
        } catch let error as TranscriptionError {
            guard case .providerNotAvailable = error else {
                Issue.record("Expected providerNotAvailable, got \(error)")
                return
            }
        } catch {
            Issue.record("Expected providerNotAvailable, got \(error)")
        }

        #expect(transcript.value(forKey: "retryCount") as? Int16 == 1)
        #expect(transcript.text?.hasPrefix("Retry failed:") == true)
    }
}
