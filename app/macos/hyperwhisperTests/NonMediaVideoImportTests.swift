//
//  NonMediaVideoImportTests.swift
//  hyperwhisperTests
//
//  Issue #1849: Transcribe File on a text file named `notes.mp4` or `.mov`
//  showed "No Audio in Video". The converter's track check swallows the open
//  error, so a file AVFoundation cannot open read as a silent video. It now
//  shows the not-valid-audio alert (#1428) with the user's file name, and a
//  real video with no audio track still shows "No Audio in Video".
//

import AVFoundation
import CoreData
import CoreVideo
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("A non-media file with a video extension")
struct NonMediaVideoImportTests {

    private struct FixtureError: Error {}

    private func makeFlow(_ persistence: PersistenceController) -> FileTranscriptionFlow {
        FileTranscriptionFlow(
            transcriptionPipeline: nil,
            settingsManager: nil,
            appState: nil,
            licenseManager: nil,
            persistence: persistence
        )
    }

    /// A fresh temporary folder, so each file's name is exact.
    private func makeFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("import-1849-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Runs the import's video step on `video` (the renamed copy) and returns
    /// the `FileTranscriptionError` it throws, or nil if it threw none.
    private func videoStepError(_ video: URL, fileName: String) async throws -> FileTranscriptionError? {
        let persistence = PersistenceController(inMemory: true)
        let flow = makeFlow(persistence)
        let output = video.deletingPathExtension().appendingPathExtension("m4a")
        do {
            _ = try await flow.extractAudioFromVideo(video, to: output, fileName: fileName)
            return nil
        } catch let error as FileTranscriptionError {
            return error
        }
    }

    // MARK: - Not media at all

    @Test(arguments: ["notes.mp4", "notes.mov"])
    func textWithAVideoExtensionIsNotValidAudio(name: String) async throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let copy = folder.appendingPathComponent("imported_1_\(name)")
        let text = String(repeating: "These are my meeting notes, not a video.\n", count: 64)
        try Data(text.utf8).write(to: copy)

        let error = try await videoStepError(copy, fileName: name)
        guard case .unreadableAudio(let fileName)? = error else {
            Issue.record("Expected .unreadableAudio for \(name), got \(String(describing: error))")
            return
        }
        #expect(fileName == name)
        #expect(error?.localizedDescription == "transcribe.file.error.notAudio".localized(arguments: name))
    }

    // MARK: - A real video with no sound

    @Test(arguments: ["silent.mp4", "silent.mov"])
    func realVideoWithNoAudioTrackStillSaysNoAudio(name: String) async throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let copy = folder.appendingPathComponent("imported_1_\(name)")
        try await writeSilentVideo(to: copy)

        let error = try await videoStepError(copy, fileName: name)
        guard case .videoHasNoAudio? = error else {
            Issue.record("Expected .videoHasNoAudio for \(name), got \(String(describing: error))")
            return
        }
    }

    /// Writes a tiny H.264 video (ten 64x64 black frames, one second) with no
    /// audio track, so no binary fixture is committed.
    private func writeSilentVideo(to url: URL) async throws {
        let fileType: AVFileType = url.pathExtension.lowercased() == "mov" ? .mov : .mp4
        let writer = try AVAssetWriter(outputURL: url, fileType: fileType)
        let side = 64
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: side,
            AVVideoHeightKey: side,
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(
            assetWriterInput: input,
            sourcePixelBufferAttributes: [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: side,
                kCVPixelBufferHeightKey as String: side,
            ]
        )
        guard writer.canAdd(input) else { throw FixtureError() }
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? FixtureError() }
        writer.startSession(atSourceTime: .zero)

        for frame in 0..<10 {
            while !input.isReadyForMoreMediaData {
                try await Task.sleep(nanoseconds: 10_000_000)
            }
            guard let pool = adaptor.pixelBufferPool else { throw FixtureError() }
            var pixelBuffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &pixelBuffer)
            guard let buffer = pixelBuffer else { throw FixtureError() }
            CVPixelBufferLockBaseAddress(buffer, [])
            if let base = CVPixelBufferGetBaseAddress(buffer) {
                memset(base, 0, CVPixelBufferGetDataSize(buffer))
            }
            CVPixelBufferUnlockBaseAddress(buffer, [])
            let time = CMTime(value: CMTimeValue(frame), timescale: 10)
            guard adaptor.append(buffer, withPresentationTime: time) else {
                throw writer.error ?? FixtureError()
            }
        }
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(value: 10, timescale: 10))
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? FixtureError() }
    }
}
