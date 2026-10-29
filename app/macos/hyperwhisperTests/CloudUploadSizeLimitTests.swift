//
//  CloudUploadSizeLimitTests.swift
//  hyperwhisperTests
//
//  Issue #2030: the HyperWhisper Cloud server refuses a body over 128 MB
//  (MAX_AUDIO_SIZE_BYTES) with Bun's bare 413, but the app checked 2 GB, so a
//  129 MB-2 GB file uploaded and failed with no size message. The 128 MB cap
//  is checked on the file that is uploaded; the source keeps a 2 GB copy guard
//  because a video's extracted audio, or VAD's M4A, can be far smaller.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("HyperWhisper Cloud upload size cap")
struct CloudUploadSizeLimitTests {

    private static let serverCap: Int64 = 128 * 1024 * 1024

    private func makeFlow(_ persistence: PersistenceController) -> FileTranscriptionFlow {
        FileTranscriptionFlow(
            transcriptionPipeline: nil,
            settingsManager: nil,
            appState: nil,
            licenseManager: nil,
            persistence: persistence
        )
    }

    private func makeMode(_ persistence: PersistenceController, model: String, cloudProvider: String?) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.model = model
        mode.cloudProvider = cloudProvider
        return mode
    }

    /// A sparse file of exactly `size` bytes, so a 2 GB case costs no disk.
    private func makeFile(named name: String, size: Int64) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("upload-cap-2030-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(name)
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: UInt64(size))
        try handle.close()
        return url
    }

    private func remove(_ url: URL) {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
    }

    @Test func cloudCapMatchesTheServer() {
        #expect(CloudProvider.hyperwhisper.maxFileSizeBytes == Self.serverCap)
        #expect(CloudProvider.hyperwhisper.maxSourceFileSizeBytes == 2 * 1024 * 1024 * 1024)
        // Every other provider checks the source against its own cap, as before.
        for provider in CloudProvider.allCases where provider != .hyperwhisper {
            #expect(provider.maxSourceFileSizeBytes == provider.maxFileSizeBytes, "\(provider)")
        }
    }

    @Test func uploadAtTheCapPasses() throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: "cloud", cloudProvider: "hyperwhisper")
        let file = try makeFile(named: "at-cap.m4a", size: Self.serverCap)
        defer { remove(file) }

        try flow.validateUploadSize(file, for: mode)
    }

    @Test func uploadOverTheCapIsTooLargeBeforeTheUpload() throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: "cloud", cloudProvider: "hyperwhisper")
        let file = try makeFile(named: "over-cap.wav", size: Self.serverCap + 1)
        defer { remove(file) }

        do {
            try flow.validateUploadSize(file, for: mode)
            Issue.record("Expected fileTooLarge for a 128 MB + 1 byte upload")
        } catch let error as FileTranscriptionError {
            guard case .fileTooLarge(let fileSize, let limit, let providerName) = error else {
                Issue.record("Expected .fileTooLarge, got \(error)")
                return
            }
            #expect(fileSize == Self.serverCap + 1)
            #expect(limit == Self.serverCap)
            #expect(providerName == CloudProvider.hyperwhisper.displayName)
        }
    }

    /// A 600 MB screen recording whose audio track is small must still reach
    /// audio extraction: the source is not held to the 128 MB upload cap.
    @Test func largeVideoSourcePassesTheSourceCheck() throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: "cloud", cloudProvider: "hyperwhisper")
        let file = try makeFile(named: "screen-recording.mov", size: 600 * 1024 * 1024)
        defer { remove(file) }

        try flow.validateFileSize(file, for: mode)
    }

    @Test func sourceOverTheCopyGuardIsTooLarge() throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: "cloud", cloudProvider: "hyperwhisper")
        let file = try makeFile(named: "huge.mov", size: 2 * 1024 * 1024 * 1024 + 1)
        defer { remove(file) }

        #expect(throws: FileTranscriptionError.self) {
            try flow.validateFileSize(file, for: mode)
        }
    }

    @Test func localModeUploadHasNoCap() throws {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let mode = makeMode(persistence, model: NemotronModelManager.Constants.latinModelId, cloudProvider: "hyperwhisper")
        let file = try makeFile(named: "local.wav", size: Self.serverCap + 1)
        defer { remove(file) }

        try flow.validateUploadSize(file, for: mode)
    }
}
