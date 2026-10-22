//
//  EmptyOrNotAudioImportTests.swift
//  hyperwhisperTests
//
//  Issue #1428: Transcribe File on a 0-byte .wav, 200 KB of random bytes named
//  .wav, or a text file named .mp3 showed AVFoundation's raw "Operation
//  Stopped" / "The operation could not be completed". The alert now says the
//  file is empty, or is not valid audio, and names the file.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("An empty or non-audio file import")
struct EmptyOrNotAudioImportTests {

    private func makeFlow(_ persistence: PersistenceController) -> FileTranscriptionFlow {
        FileTranscriptionFlow(
            transcriptionPipeline: nil,
            settingsManager: nil,
            appState: nil,
            licenseManager: nil,
            persistence: persistence
        )
    }

    private func makeMode(_ persistence: PersistenceController, model: String, cloudProvider: String? = nil) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.model = model
        mode.cloudProvider = cloudProvider
        return mode
    }

    /// A file named `name` in its own temporary folder, so the name is exact.
    private func makeFile(named name: String, contents: Data) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("import-1428-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(name)
        try contents.write(to: url)
        return url
    }

    private func remove(_ url: URL) {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
    }

    // MARK: - Empty file

    @Test(arguments: [NemotronModelManager.Constants.latinModelId, "cloud"])
    func zeroByteFileIsEmptyBeforeAnyDecoding(model: String) throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: model)
        let file = try makeFile(named: "empty-0byte.wav", contents: Data())
        defer { remove(file) }

        do {
            try flow.validateFileSize(file, for: mode)
            Issue.record("Expected an empty-file error")
        } catch let error as FileTranscriptionError {
            guard case .emptyFile(let fileName) = error else {
                Issue.record("Expected .emptyFile, got \(error)")
                return
            }
            #expect(fileName == "empty-0byte.wav")
        }
    }

    @Test func oneByteFileIsNotEmpty() throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: NemotronModelManager.Constants.latinModelId)
        let file = try makeFile(named: "one-byte.wav", contents: Data([0]))
        defer { remove(file) }

        try flow.validateFileSize(file, for: mode)
    }

    // MARK: - Not audio

    @Test func randomBytesAreNotValidAudio() async throws {
        var bytes = Data(count: 200 * 1024)
        for index in bytes.indices { bytes[index] = UInt8.random(in: 0...255) }
        try await expectNotAudio(named: "random-bytes.wav", contents: bytes)
    }

    @Test func textNamedMP3IsNotValidAudio() async throws {
        let text = String(repeating: "This is a text file, not audio.\n", count: 64)
        try await expectNotAudio(named: "text-as.mp3", contents: Data(text.utf8))
    }

    /// The duration read is the first step that decodes a local-mode import.
    /// It runs on the renamed copy, so the alert's name is passed in.
    private func expectNotAudio(named name: String, contents: Data) async throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let file = try makeFile(named: "imported_1_\(name)", contents: contents)
        defer { remove(file) }

        do {
            _ = try await flow.getAudioDuration(file, fileName: name)
            Issue.record("Expected \(name) to be refused as not audio")
        } catch let error as FileTranscriptionError {
            guard case .unreadableAudio(let fileName) = error else {
                Issue.record("Expected .unreadableAudio, got \(error)")
                return
            }
            #expect(fileName == name)
        }
    }

    // MARK: - The alert text

    @Test func alertTextSaysWhatIsWrongAndNamesTheFile() {
        let empty = FileTranscriptionError.emptyFile(fileName: "empty-0byte.wav").localizedDescription
        #expect(empty == "transcribe.file.error.empty".localized(arguments: "empty-0byte.wav"))
        #expect(empty.contains("empty-0byte.wav"))
        #expect(empty != "transcribe.file.error.empty")

        let notAudio = FileTranscriptionError.unreadableAudio(fileName: "text-as.mp3").localizedDescription
        #expect(notAudio == "transcribe.file.error.notAudio".localized(arguments: "text-as.mp3"))
        #expect(notAudio.contains("text-as.mp3"))
        #expect(notAudio != "transcribe.file.error.notAudio")
    }

    @Test func aPercentInTheNameIsNotAFormatDirective() {
        let text = FileTranscriptionError.emptyFile(fileName: "100%@done.wav").localizedDescription
        #expect(text.contains("100%@done.wav"))
    }
}
