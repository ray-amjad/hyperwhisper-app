//
//  TranscriptAudioFileDeletionTests.swift
//  hyperwhisperTests
//
//  Issue #1513: VAD writes `<trimmed>.m4a` beside a large trimmed WAV, and a
//  video import writes `<video>.m4a` beside the copied video. No Core Data
//  column records either file, so History's Delete left them on disk while
//  its dialog said the audio files were deleted. These tests drive the real
//  delete paths against real files in a temporary folder. Auto-delete has its
//  own cases in `AutoDeleteCleanupServiceTests`.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct TranscriptAudioFileDeletionTests {

    // MARK: - Fixtures

    private func makeTemporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("TranscriptAudioFileDeletionTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// Writes a small file named `name` in `directory` and returns its path.
    private func makeFile(_ name: String, in directory: URL) throws -> String {
        let url = directory.appendingPathComponent(name)
        try Data(repeating: 0x41, count: 64).write(to: url)
        return url.path
    }

    /// The three files an import with a large VAD output leaves on disk.
    private struct ImportFiles {
        let original: String
        let trimmed: String
        let uploadCopy: String

        var all: [String] { [original, trimmed, uploadCopy] }
    }

    private func makeImportFiles(stem: String, in directory: URL) throws -> ImportFiles {
        ImportFiles(
            original: try makeFile("\(stem).wav", in: directory),
            trimmed: try makeFile("\(stem)_wav_trimmed.wav", in: directory),
            uploadCopy: try makeFile("\(stem)_wav_trimmed.m4a", in: directory)
        )
    }

    @discardableResult
    private func insertTranscript(
        into context: NSManagedObjectContext,
        audioFilePath: String?,
        trimmedAudioFilePath: String? = nil
    ) -> Transcript {
        let transcript = Transcript(context: context)
        transcript.id = UUID()
        transcript.text = "test transcript"
        transcript.date = Date()
        transcript.duration = 1
        transcript.audioFilePath = audioFilePath
        transcript.setValue(trimmedAudioFilePath, forKey: "trimmedAudioFilePath")
        return transcript
    }

    private func exists(_ path: String) -> Bool {
        FileManager.default.fileExists(atPath: path)
    }

    // MARK: - History single Delete

    @Test func singleDeleteRemovesTheTrimmedWAVsUploadCopy() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let deleted = try makeImportFiles(stem: "imported_1_long", in: directory)
        let kept = try makeImportFiles(stem: "imported_2_other", in: directory)
        let unrelated = try makeFile("recording_unrelated.m4a", in: directory)

        let transcript = insertTranscript(
            into: context,
            audioFilePath: deleted.original,
            trimmedAudioFilePath: deleted.trimmed
        )
        insertTranscript(into: context, audioFilePath: kept.original, trimmedAudioFilePath: kept.trimmed)
        try context.save()

        persistence.deleteTranscript(transcript)

        #expect(deleted.all.allSatisfy { !exists($0) })
        #expect(kept.all.allSatisfy { exists($0) })
        #expect(exists(unrelated))
    }

    // MARK: - History bulk Delete

    @Test func bulkDeleteRemovesEveryRowsUploadCopy() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let first = try makeImportFiles(stem: "imported_1_a", in: directory)
        let second = try makeImportFiles(stem: "imported_2_b", in: directory)
        let kept = try makeImportFiles(stem: "imported_3_c", in: directory)
        let unrelated = try makeFile("recording_unrelated.wav", in: directory)

        let firstRow = insertTranscript(into: context, audioFilePath: first.original, trimmedAudioFilePath: first.trimmed)
        let secondRow = insertTranscript(into: context, audioFilePath: second.original, trimmedAudioFilePath: second.trimmed)
        insertTranscript(into: context, audioFilePath: kept.original, trimmedAudioFilePath: kept.trimmed)
        try context.save()

        persistence.deleteTranscripts([firstRow, secondRow])

        #expect((first.all + second.all).allSatisfy { !exists($0) })
        #expect(kept.all.allSatisfy { exists($0) })
        #expect(exists(unrelated))
    }

    // MARK: - Video import

    @Test func deleteRemovesAVideoImportsExtractedAudio() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let video = try makeFile("imported_1_clip.mp4", in: directory)
        let extracted = try makeFile("imported_1_clip.m4a", in: directory)
        let trimmed = try makeFile("imported_1_clip_m4a_trimmed.wav", in: directory)
        let uploadCopy = try makeFile("imported_1_clip_m4a_trimmed.m4a", in: directory)

        let transcript = insertTranscript(into: context, audioFilePath: video, trimmedAudioFilePath: trimmed)
        try context.save()

        persistence.deleteTranscript(transcript)

        #expect([video, extracted, trimmed, uploadCopy].allSatisfy { !exists($0) })
    }

    // MARK: - Ownership

    /// A derived name is a guess from a naming rule. A file another transcript
    /// records as its own audio must survive.
    @Test func deleteKeepsADerivedNameAnotherTranscriptRecords() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let video = try makeFile("imported_1_talk.mp4", in: directory)
        // A separate import of an `.m4a` that landed on the same name.
        let otherImport = try makeFile("imported_1_talk.m4a", in: directory)

        let transcript = insertTranscript(into: context, audioFilePath: video)
        insertTranscript(into: context, audioFilePath: otherImport)
        try context.save()

        persistence.deleteTranscript(transcript)

        #expect(!exists(video))
        #expect(exists(otherImport))
    }

    /// Two rows that share one trimmed WAV share its upload copy. Deleting one
    /// row keeps the copy for the survivor; deleting both removes it.
    @Test func deleteKeepsAnUploadCopyASurvivorDerives() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let files = try makeImportFiles(stem: "recording_shared", in: directory)

        let first = insertTranscript(into: context, audioFilePath: files.original, trimmedAudioFilePath: files.trimmed)
        let second = insertTranscript(into: context, audioFilePath: files.original, trimmedAudioFilePath: files.trimmed)
        try context.save()

        persistence.deleteTranscript(first)
        #expect(exists(files.uploadCopy))

        persistence.deleteTranscripts([second])
        #expect(!exists(files.uploadCopy))
    }

    // MARK: - Naming rules

    @Test func onlyAWAVTrimmedFileHasAnUploadCopy() {
        let wav = TranscriptRecordedAudioPaths(
            audioFilePath: "/r/recording.wav",
            trimmedAudioFilePath: "/r/recording_wav_trimmed.wav"
        )
        #expect(TranscriptAudioFiles.derivedArtifactPaths(for: wav) == ["/r/recording_wav_trimmed.m4a"])

        // A pre-upgrade trimmed file kept its source extension; VAD never
        // converts a non-WAV, and the derived name would be the file itself.
        let legacyM4A = TranscriptRecordedAudioPaths(
            audioFilePath: "/r/recording.m4a",
            trimmedAudioFilePath: "/r/recording_trimmed.m4a"
        )
        #expect(TranscriptAudioFiles.derivedArtifactPaths(for: legacyM4A).isEmpty)

        // An audio original never gets a derived `.m4a` — `recording.m4a`
        // beside `recording.wav` is not this row's file to take.
        let audioOnly = TranscriptRecordedAudioPaths(audioFilePath: "/r/recording.wav", trimmedAudioFilePath: nil)
        #expect(TranscriptAudioFiles.derivedArtifactPaths(for: audioOnly).isEmpty)
    }

    @Test func theProducersUseTheSameNames() {
        let trimmed = URL(fileURLWithPath: "/r/imported_1_x_wav_trimmed.wav")
        #expect(TranscriptAudioFiles.uploadCopyURL(forTrimmedWAV: trimmed).path == "/r/imported_1_x_wav_trimmed.m4a")

        let video = URL(fileURLWithPath: "/r/imported_1_x.MOV")
        #expect(TranscriptAudioFiles.isVideoFile(video))
        #expect(TranscriptAudioFiles.extractedAudioURL(forVideo: video).path == "/r/imported_1_x.m4a")
        #expect(!TranscriptAudioFiles.isVideoFile(URL(fileURLWithPath: "/r/imported_1_x.m4a")))
    }

    @Test func recordedPathsKeepTheirOrderAndDerivedPathsFollowOnce() {
        let rows = [
            TranscriptRecordedAudioPaths(audioFilePath: "/r/a.wav", trimmedAudioFilePath: "/r/a_wav_trimmed.wav"),
            TranscriptRecordedAudioPaths(audioFilePath: "/r/b.wav", trimmedAudioFilePath: "/r/a_wav_trimmed.wav")
        ]

        let paths = TranscriptAudioFiles.pathsToDelete(for: rows, protectedPaths: [])
        #expect(paths == [
            "/r/a.wav", "/r/a_wav_trimmed.wav", "/r/a_wav_trimmed.m4a",
            "/r/b.wav", "/r/a_wav_trimmed.wav"
        ])

        let protected = TranscriptAudioFiles.pathsToDelete(for: rows, protectedPaths: ["/r/a_wav_trimmed.m4a"])
        #expect(!protected.contains("/r/a_wav_trimmed.m4a"))
    }
}
