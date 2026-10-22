//
//  TooShortImportTests.swift
//  hyperwhisperTests
//
//  Issue #1427: a 0.1 s imported file said "Nemotron is not available: Audio
//  file is too small (3278 bytes). Please record for longer.", and the failed
//  History row stored that error as its transcript text.
//
//  Ray's rules (2026-10-08): the local engines keep their 5,000-byte check but
//  throw their own "too short" error; a file import says "This file is too
//  short to transcribe (0.1 s)."; dictation keeps "record for longer"; and a
//  failed import row has empty text, with the reason only in its Reason field.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("A too-short file import")
struct TooShortImportTests {

    /// The 44-byte `header-only.wav` from the issue: a WAV header, no samples.
    private func makeHeaderOnlyWAV() throws -> URL {
        var data = Data("RIFF".utf8)
        data.append(contentsOf: [36, 0, 0, 0])
        data.append(contentsOf: Array("WAVEfmt ".utf8))
        data.append(contentsOf: [16, 0, 0, 0, 1, 0, 1, 0, 0x80, 0x3E, 0, 0, 0, 0x7D, 0, 0, 2, 0, 16, 0])
        data.append(contentsOf: Array("data".utf8))
        data.append(contentsOf: [0, 0, 0, 0])
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("header-only-\(UUID().uuidString).wav")
        try data.write(to: url)
        return url
    }

    // MARK: - The engines

    /// Nemotron's size check runs before its model check, so this needs no model.
    @Test func nemotronThrowsTooShortNotProviderNotAvailable() async throws {
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.model = NemotronModelManager.Constants.latinModelId
        let audio = try makeHeaderOnlyWAV()
        defer { try? FileManager.default.removeItem(at: audio) }

        do {
            _ = try await NemotronProvider().transcribe(audioURL: audio, language: nil, mode: mode, vocabulary: [])
            Issue.record("Expected a too-short error")
        } catch let error as TranscriptionError {
            guard case .audioTooShort(let fileSize) = error else {
                Issue.record("Expected .audioTooShort, got \(error)")
                return
            }
            #expect(fileSize == 44)
        }
    }

    @Test func qwen3ThrowsTooShortNotProviderNotAvailable() async throws {
        guard #available(macOS 15.0, *) else { return }
        let audio = try makeHeaderOnlyWAV()
        defer { try? FileManager.default.removeItem(at: audio) }

        do {
            _ = try await Qwen3AsrProvider().transcribe(audioURL: audio, language: nil, mode: nil, vocabulary: [])
            Issue.record("Expected a too-short error")
        } catch let error as TranscriptionError {
            guard case .audioTooShort = error else {
                Issue.record("Expected .audioTooShort, got \(error)")
                return
            }
        }
    }

    /// Parakeet checks its model before the file, so CI (no model) cannot reach
    /// its size check. Read the source instead: no engine may report a short
    /// file as "not available" again.
    @Test func noEngineReportsAShortFileAsNotAvailable() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("hyperwhisper/Managers/Transcription/Providers/Local")
        for name in ["NemotronProvider", "ParakeetProvider", "Qwen3AsrProvider"] {
            let source = try String(contentsOf: root.appendingPathComponent("\(name).swift"), encoding: .utf8)
            #expect(!source.contains("Please record for longer"), "\(name) still says record for longer")
            #expect(source.contains("throw TranscriptionError.audioTooShort(fileSize: size)"), "\(name) lost its too-short throw")
        }
    }

    // MARK: - The wording

    @Test func dictationKeepsRecordForLonger() {
        let message = TranscriptionError.audioTooShort(fileSize: 3278).localizedDescription
        #expect(message.contains("record for longer"))
        #expect(!message.contains("not available"))
    }

    @Test func fileImportNamesTheFileAndItsLength() {
        let message = FileTranscriptionFlow.failureMessage(
            for: TranscriptionError.audioTooShort(fileSize: 3278),
            audioDuration: 0.1
        )
        let seconds = (0.1).formatted(.number.precision(.fractionLength(1)))
        #expect(message == "transcribe.file.error.tooShort".localized(arguments: seconds))
        #expect(!message.contains("record for longer"))
        #expect(!message.contains("not available"))
    }

    @Test func otherErrorsKeepTheirOwnDescription() {
        let error = TranscriptionError.noSpeechDetected
        #expect(FileTranscriptionFlow.failureMessage(for: error, audioDuration: 0.1) == error.localizedDescription)
    }

    // MARK: - The History row

    /// The row is failed, its Reason is the file message, and its text is empty,
    /// so the preview and Copy carry no error string.
    @Test func theFailedRowHasEmptyTextAndTheReason() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 0.1, mode: "LocNemo", audioFilePath: "/test/tiny.wav")
        let flow = FileTranscriptionFlow(
            transcriptionPipeline: nil,
            settingsManager: nil,
            appState: nil,
            licenseManager: nil,
            persistence: persistence
        )
        flow.processingTranscriptID = row.objectID

        await flow.markProcessingTranscriptFailed(
            after: TranscriptionError.audioTooShort(fileSize: 3278),
            audioDuration: 0.1
        )

        let context = persistence.container.viewContext
        context.refreshAllObjects()
        let saved = try #require(try context.existingObject(with: row.objectID) as? Transcript)
        #expect(saved.value(forKey: "status") as? String == "failed")
        #expect(saved.value(forKey: "failedReason") as? String
                == FileTranscriptionFlow.failureMessage(for: TranscriptionError.audioTooShort(fileSize: 3278), audioDuration: 0.1))
        #expect(saved.text == "")
    }
}
