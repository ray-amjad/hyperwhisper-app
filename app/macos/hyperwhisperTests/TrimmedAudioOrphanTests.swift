//
//  TrimmedAudioOrphanTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1524 (follow-up to #1513 / PR #1520).
//
//  Three narrow paths left a trimmed `.wav` or its `.m4a` in the recordings
//  folder with no Transcript row recording it, and builds before #1520 left
//  `*_trimmed.m4a` upload copies that nothing ever swept:
//
//  1. `setTrimmedAudioPath` when the row is gone or the save fails.
//  2. `VADProcessingService` when the trim fails validation.
//  3. A row deleted while its background WAV→M4A conversion runs.
//  4. The one-time launch sweep of old unreferenced `*_trimmed.m4a` files.
//
//  Each test works in its own temp "recordings folder" with an in-memory
//  store, and checks the folder holds no file that no row records.
//

import AVFoundation
import CoreData
import Foundation
import Testing

@testable import HyperWhisper

/// A writer whose saves always fail (the save-failure branch of item 1).
private final class FailingSaveTrimPersistenceController: PersistenceController {
    private struct ExpectedSaveFailure: Error {}

    override func saveWriterContext(_ context: NSManagedObjectContext) throws {
        throw ExpectedSaveFailure()
    }
}

/// Claims `claimOnLookup` for a retry the first time the writer runs a
/// lookup (`performWrite`): another retry starting while the cleanup's
/// Core Data query is pending (review R2).
private final class ClaimDuringLookupPersistenceController: PersistenceController {
    var claimOnLookup: [String] = []

    override func performWrite<T: Sendable>(_ block: @escaping (NSManagedObjectContext) -> T) async -> T {
        let paths = claimOnLookup
        claimOnLookup = []
        if !paths.isEmpty {
            await MainActor.run { TranscriptionRetryController.claimVADOutputs(paths) }
        }
        return await super.performWrite(block)
    }
}

@MainActor
struct TrimmedAudioOrphanTests {

    // MARK: - Fixtures

    private func makeRecordingsFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("TrimmedAudioOrphanTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    @discardableResult
    private func makeFile(_ name: String, in folder: URL, bytes: Int = 64, modified: Date? = nil) throws -> URL {
        let url = folder.appendingPathComponent(name)
        try Data(repeating: 0x41, count: bytes).write(to: url)
        if let modified {
            try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        }
        return url
    }

    /// A real AAC `.m4a` of `seconds` of silence, the shape of a VAD upload
    /// copy by default (16 kHz mono), for the sweep's format check.
    @discardableResult
    private func makeAudioFile(
        _ name: String,
        in folder: URL,
        sampleRate: Double = 16_000,
        channels: AVAudioChannelCount = 1,
        modified: Date? = nil
    ) throws -> URL {
        let url = folder.appendingPathComponent(name)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channels,
        ]
        do {
            let file = try AVAudioFile(forWriting: url, settings: settings)
            let format = try #require(AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels))
            let frames = AVAudioFrameCount(sampleRate / 2)
            let buffer = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames))
            buffer.frameLength = frames
            try file.write(from: buffer)
        }
        if let modified {
            try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        }
        return url
    }

    private func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    @discardableResult
    private func insertTranscript(
        in persistence: PersistenceController,
        audioFilePath: String?,
        trimmedAudioFilePath: String? = nil,
        status: String = "completed"
    ) throws -> Transcript {
        let context = persistence.container.viewContext
        let transcript = Transcript(context: context)
        transcript.id = UUID()
        transcript.text = "test transcript"
        transcript.date = Date()
        transcript.duration = 60
        transcript.audioFilePath = audioFilePath
        transcript.setValue(trimmedAudioFilePath, forKey: "trimmedAudioFilePath")
        transcript.setValue(status, forKey: "status")
        try context.save()
        return transcript
    }

    /// Names of the files in `folder` that no row records or derives — the
    /// leak this issue is about. Compared by name: a temp folder can list as
    /// `/private/var/…` while the rows hold `/var/…`.
    private func filesNoRowRecords(in folder: URL, persistence: PersistenceController) throws -> [String] {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        let rows = try persistence.container.viewContext.fetch(request)
        let owned = TranscriptAudioFiles.protectedPaths(for: rows.map(PersistenceController.recordedAudioPaths(of:)))
        let ownedNames = Set(owned.map { URL(fileURLWithPath: $0).lastPathComponent })
        return try FileManager.default.contentsOfDirectory(atPath: folder.path)
            .filter { !ownedNames.contains($0) }
            .sorted()
    }

    /// A trimmed WAV and its upload copy, named as the producers name them.
    private func makeTrimmedPair(for source: URL, in folder: URL) throws -> (wav: URL, m4a: URL) {
        let wav = SilenceTrimmer().generateOutputURL(for: source)
        let m4a = TranscriptAudioFiles.uploadCopyURL(forTrimmedWAV: wav)
        try makeFile(wav.lastPathComponent, in: folder, bytes: 256)
        try makeFile(m4a.lastPathComponent, in: folder, bytes: 128)
        return (wav, m4a)
    }

    // MARK: - 1. setTrimmedAudioPath failure path

    /// The row was deleted before the trimmed path could be saved: both the
    /// trimmed WAV and its `.m4a` upload copy go, and nothing is left behind.
    @Test func aDeletedRowsTrimmedWAVAndUploadCopyAreBothRemoved() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: source.path, status: "processing")
        persistence.deleteTranscript(transcript)
        let trimmed = try makeTrimmedPair(for: source, in: folder)

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: trimmed.wav.path)

        #expect(!saved)
        #expect(!exists(trimmed.wav))
        #expect(!exists(trimmed.m4a))
        #expect(try filesNoRowRecords(in: folder, persistence: persistence).isEmpty)
    }

    /// The save failed on a row whose job is over: no upload can use the copy.
    @Test func aFailedSaveOnAFinishedRowRemovesTheUploadCopy() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = FailingSaveTrimPersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: source.path, status: "failed")
        let trimmed = try makeTrimmedPair(for: source, in: folder)

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: trimmed.wav.path)

        #expect(!saved)
        #expect(!exists(trimmed.wav))
        #expect(!exists(trimmed.m4a))
        #expect(exists(source))
        #expect(try filesNoRowRecords(in: folder, persistence: persistence).isEmpty)
    }

    /// Ray's exception: the row is still "processing" (a file import uploads
    /// the copy right after this call), so the copy stays for its upload.
    @Test func aFailedSaveOnAProcessingRowKeepsTheUploadCopy() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = FailingSaveTrimPersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: source.path, status: "processing")
        let trimmed = try makeTrimmedPair(for: source, in: folder)

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: trimmed.wav.path)

        #expect(!saved)
        #expect(!exists(trimmed.wav))
        #expect(exists(trimmed.m4a), "an upload may still read the copy")
    }

    /// A running retry claims the VAD names, so its upload copy stays.
    @Test func anUploadCopyARunningRetryClaimsIsKept() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: source.path)
        persistence.deleteTranscript(transcript)
        let trimmed = try makeTrimmedPair(for: source, in: folder)
        let claimed = TranscriptionRetryController.vadOutputPaths(for: source)
        TranscriptionRetryController.claimVADOutputs(claimed)
        defer { TranscriptionRetryController.releaseVADOutputs(claimed) }

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: trimmed.wav.path)

        #expect(!saved)
        #expect(exists(trimmed.m4a))
    }

    /// Another row made from the same audio derives the same copy: kept.
    @Test func anUploadCopyAnotherRowDerivesIsKept() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let trimmed = try makeTrimmedPair(for: source, in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: source.path)
        try insertTranscript(in: persistence, audioFilePath: source.path, trimmedAudioFilePath: trimmed.wav.path)
        let context = persistence.container.viewContext
        context.delete(transcript)
        try context.save()

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: trimmed.wav.path)

        #expect(!saved)
        #expect(exists(trimmed.m4a))
    }

    /// Review R2: a retry that claims the copy while the ownership query is
    /// pending may be uploading it, so the failed-save cleanup keeps it.
    @Test func anUploadCopyClaimedDuringTheLookupIsKept() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = ClaimDuringLookupPersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: source.path)
        persistence.deleteTranscript(transcript)
        let trimmed = try makeTrimmedPair(for: source, in: folder)
        let claimed = TranscriptionRetryController.vadOutputPaths(for: source)
        persistence.claimOnLookup = claimed
        defer { TranscriptionRetryController.releaseVADOutputs(claimed) }

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: trimmed.wav.path)

        #expect(!saved)
        #expect(TranscriptionRetryController.vadOutputClaimCount(trimmed.m4a.path) == 1, "the claim landed during the lookup")
        #expect(exists(trimmed.m4a), "the new retry may be uploading the copy")
    }

    // MARK: - 2. VAD validation failure

    private func stubTrim(
        writing bytes: Int,
        originalDuration: TimeInterval,
        trimmedDuration: TimeInterval
    ) -> (URL) async throws -> TrimResult {
        { source in
            let output = SilenceTrimmer().generateOutputURL(for: source)
            try Data(repeating: 0x41, count: bytes).write(to: output)
            return TrimResult(
                outputURL: output,
                originalDuration: originalDuration,
                trimmedDuration: trimmedDuration,
                segmentCount: 1
            )
        }
    }

    /// Too little silence removed: VAD falls back to the original and removes
    /// the trimmed WAV it wrote, which no caller would record.
    @Test func aTrimThatFailsValidationLeavesNoTrimmedWAV() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        try insertTranscript(in: persistence, audioFilePath: source.path)
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 10_000, originalDuration: 60, trimmedDuration: 59.8),
            unownedAudioPaths: { await persistence.unownedAudioPathsInBackground($0) }
        )

        let result = await service.processAudioForTranscription(audioURL: source, duration: 60, vadEnabled: true)

        #expect(!result.wasProcessed)
        #expect(result.finalAudioURL == source)
        let trimmedURL = try #require(result.trimResult?.outputURL)
        #expect(!exists(trimmedURL))
        #expect(exists(source))
        #expect(try filesNoRowRecords(in: folder, persistence: persistence).isEmpty)
    }

    /// A trim too small to be real (the "file invalid" check) goes the same way.
    @Test func aTrimWithATooSmallFileLeavesNoTrimmedWAV() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        try insertTranscript(in: persistence, audioFilePath: source.path)
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 100, originalDuration: 60, trimmedDuration: 30),
            unownedAudioPaths: { await persistence.unownedAudioPathsInBackground($0) }
        )

        let result = await service.processAudioForTranscription(audioURL: source, duration: 60, vadEnabled: true)

        #expect(!result.wasProcessed)
        #expect(try filesNoRowRecords(in: folder, persistence: persistence).isEmpty)
    }

    /// A row made from the same audio records that trimmed name: kept.
    @Test func aRejectedTrimAnotherRowRecordsIsKept() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        let trimmedName = SilenceTrimmer().generateOutputURL(for: source)
        try insertTranscript(in: persistence, audioFilePath: source.path, trimmedAudioFilePath: trimmedName.path)
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 10_000, originalDuration: 60, trimmedDuration: 59.8),
            unownedAudioPaths: { await persistence.unownedAudioPathsInBackground($0) }
        )

        let result = await service.processAudioForTranscription(audioURL: source, duration: 60, vadEnabled: true)

        #expect(!result.wasProcessed)
        #expect(exists(trimmedName))
    }

    /// Review R1 [P1]: two retries of rows from the same audio share the
    /// trimmed name. One's rejected trim must not delete the WAV the other
    /// run wrote and has not yet recorded on its row.
    @Test func aRejectedRetryTrimKeepsTheWAVAnotherRetryClaims() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        let claimed = TranscriptionRetryController.vadOutputPaths(for: source)
        // This retry's own claim, then the other run's.
        TranscriptionRetryController.claimVADOutputs(claimed)
        TranscriptionRetryController.claimVADOutputs(claimed)
        defer {
            TranscriptionRetryController.releaseVADOutputs(claimed)
            TranscriptionRetryController.releaseVADOutputs(claimed)
        }
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 10_000, originalDuration: 60, trimmedDuration: 59.8),
            unownedAudioPaths: { await persistence.unownedAudioPathsInBackground($0) }
        )

        let result = await service.processAudioForTranscription(
            audioURL: source, duration: 60, vadEnabled: true, context: .retry
        )

        #expect(!result.wasProcessed)
        let trimmedURL = try #require(result.trimResult?.outputURL)
        #expect(exists(trimmedURL), "the other retry may still record this WAV")
    }

    /// Review R2: another retry claims the name while the ownership lookup is
    /// pending; the rejected trim's cleanup must not delete that run's WAV.
    @Test func aRejectedTrimKeepsAWAVClaimedDuringTheLookup() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        let claimed = TranscriptionRetryController.vadOutputPaths(for: source)
        defer { TranscriptionRetryController.releaseVADOutputs(claimed) }
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 10_000, originalDuration: 60, trimmedDuration: 59.8),
            unownedAudioPaths: { paths in
                await MainActor.run { TranscriptionRetryController.claimVADOutputs(claimed) }
                return await persistence.unownedAudioPathsInBackground(paths)
            }
        )

        let result = await service.processAudioForTranscription(audioURL: source, duration: 60, vadEnabled: true)

        #expect(!result.wasProcessed)
        let trimmedURL = try #require(result.trimResult?.outputURL)
        #expect(TranscriptionRetryController.vadOutputClaimCount(trimmedURL.path) == 1, "the claim landed during the lookup")
        #expect(exists(trimmedURL), "the other retry claimed it before the delete")
    }

    /// Control: a retry holding only its own claim still removes its rejected WAV.
    @Test func aRejectedRetryTrimWithOnlyItsOwnClaimIsRemoved() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        try insertTranscript(in: persistence, audioFilePath: source.path)
        let claimed = TranscriptionRetryController.vadOutputPaths(for: source)
        TranscriptionRetryController.claimVADOutputs(claimed)
        defer { TranscriptionRetryController.releaseVADOutputs(claimed) }
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 10_000, originalDuration: 60, trimmedDuration: 59.8),
            unownedAudioPaths: { await persistence.unownedAudioPathsInBackground($0) }
        )

        let result = await service.processAudioForTranscription(
            audioURL: source, duration: 60, vadEnabled: true, context: .retry
        )

        #expect(!result.wasProcessed)
        #expect(try filesNoRowRecords(in: folder, persistence: persistence).isEmpty)
    }

    /// Control: a trim that passes keeps its WAV for the caller to record.
    @Test func aValidTrimKeepsItsWAV() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let source = try makeFile("rec-\(UUID().uuidString).wav", in: folder, bytes: 10_000)
        let service = VADProcessingService(
            trimSilence: stubTrim(writing: 10_000, originalDuration: 60, trimmedDuration: 30),
            unownedAudioPaths: { await persistence.unownedAudioPathsInBackground($0) }
        )

        let result = await service.processAudioForTranscription(audioURL: source, duration: 60, vadEnabled: true)

        #expect(result.wasProcessed)
        let trimmedURL = try #require(result.trimResult?.outputURL)
        #expect(result.finalAudioURL == trimmedURL)
        #expect(exists(trimmedURL))
    }

    // MARK: - 3. Background WAV→M4A conversion

    /// The row was deleted while the conversion ran: the M4A it produced is
    /// removed instead of landing with no row.
    @Test func aConversionThatEndsAfterItsRowsDeleteLeavesNoM4A() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let wav = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: wav.path)
        let transcriptID = transcript.objectID
        persistence.deleteTranscript(transcript)
        // The conversion finishes writing after the delete.
        let m4a = try makeFile(wav.deletingPathExtension().appendingPathExtension("m4a").lastPathComponent, in: folder)

        let adopted = await persistence.adoptConvertedAudioInBackground(
            transcriptID: transcriptID,
            sourcePath: wav.path,
            convertedPath: m4a.path
        )

        #expect(!adopted)
        #expect(!exists(m4a))
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).isEmpty)
    }

    /// The row no longer records the WAV the conversion read: the M4A goes,
    /// and the row's own file stays.
    @Test func aConversionForARowThatMovedOnLeavesNoM4A() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let wav = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let other = try makeFile("other-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: other.path)
        let m4a = try makeFile(wav.deletingPathExtension().appendingPathExtension("m4a").lastPathComponent, in: folder)

        let adopted = await persistence.adoptConvertedAudioInBackground(
            transcriptID: transcript.objectID,
            sourcePath: wav.path,
            convertedPath: m4a.path
        )

        #expect(!adopted)
        #expect(!exists(m4a))
        #expect(exists(other))
        #expect(transcript.audioFilePath == other.path)
        #expect(try filesNoRowRecords(in: folder, persistence: persistence).isEmpty)
    }

    /// Control: the row is still there, so it records the M4A.
    @Test func aConversionForALiveRowRecordsTheM4A() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let persistence = PersistenceController(inMemory: true)
        let wav = try makeFile("rec-\(UUID().uuidString).wav", in: folder)
        let transcript = try insertTranscript(in: persistence, audioFilePath: wav.path)
        let m4a = try makeFile(wav.deletingPathExtension().appendingPathExtension("m4a").lastPathComponent, in: folder)

        let adopted = await persistence.adoptConvertedAudioInBackground(
            transcriptID: transcript.objectID,
            sourcePath: wav.path,
            convertedPath: m4a.path
        )

        #expect(adopted)
        #expect(exists(m4a))
        persistence.container.viewContext.refresh(transcript, mergeChanges: false)
        #expect(transcript.audioFilePath == m4a.path)
    }

    /// Both conversion stages go through the checked write, and remove the
    /// WAV only after it.
    @Test func bothConversionStagesRecordTheM4AThroughTheCheckedWrite() throws {
        let source = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/Recording/RecordingLifecycle.swift"
        )
        #expect(source.components(separatedBy: "PersistenceController.shared.adoptConvertedAudioInBackground(").count - 1 == 2)
        #expect(!source.contains("updateTranscriptAudioFilePathInBackground"))
    }

    // MARK: - 4. One-time launch sweep

    private func makeDefaults() throws -> (name: String, defaults: UserDefaults) {
        let name = "TrimmedAudioOrphanTests-\(UUID().uuidString)"
        return (name, try #require(UserDefaults(suiteName: name)))
    }

    private static let now = Date(timeIntervalSince1970: 1_800_000_000)
    private static let old = now.addingTimeInterval(-25 * 60 * 60)
    private static let young = now.addingTimeInterval(-23 * 60 * 60)

    /// Deletes old unreferenced `*_trimmed.m4a` files and keeps every other one.
    @Test func theSweepDeletesOnlyOldUnreferencedUploadCopies() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let suite = try makeDefaults()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let persistence = PersistenceController(inMemory: true)

        let orphan = try makeAudioFile("a_wav_trimmed.m4a", in: folder, modified: Self.old)
        let secondOrphan = try makeAudioFile("b_m4a_trimmed.m4a", in: folder, modified: Self.old)
        let young = try makeAudioFile("c_wav_trimmed.m4a", in: folder, modified: Self.young)
        // Derived from a row's trimmed WAV (the #1513 upload copy).
        let derivedTrimmed = folder.appendingPathComponent("d_wav_trimmed.wav")
        let derived = try makeAudioFile("d_wav_trimmed.m4a", in: folder, modified: Self.old)
        try insertTranscript(in: persistence, audioFilePath: folder.appendingPathComponent("d.wav").path, trimmedAudioFilePath: derivedTrimmed.path)
        // Recorded directly as a row's original and as a row's trimmed file.
        let recordedOriginal = try makeAudioFile("e_wav_trimmed.m4a", in: folder, modified: Self.old)
        try insertTranscript(in: persistence, audioFilePath: recordedOriginal.path)
        let recordedTrimmed = try makeAudioFile("f_wav_trimmed.m4a", in: folder, modified: Self.old)
        try insertTranscript(in: persistence, audioFilePath: nil, trimmedAudioFilePath: recordedTrimmed.path)
        // A row records the same name through another folder path.
        let sameName = try makeAudioFile("g_wav_trimmed.m4a", in: folder, modified: Self.old)
        try insertTranscript(in: persistence, audioFilePath: "/Volumes/Elsewhere/G_WAV_TRIMMED.m4a")
        // Not upload copies.
        let plainM4A = try makeFile("h.m4a", in: folder, modified: Self.old)
        let trimmedWAV = try makeFile("i_wav_trimmed.wav", in: folder, modified: Self.old)

        let run = await persistence.sweepOrphanedTrimmedUploadCopiesIfNeeded(
            recordingsFolder: folder,
            now: Self.now,
            defaults: suite.defaults
        )
        let deleted = try #require(run)

        #expect(Set(deleted.map(\.lastPathComponent)) == [orphan.lastPathComponent, secondOrphan.lastPathComponent])
        #expect(!exists(orphan))
        #expect(!exists(secondOrphan))
        for kept in [young, derived, recordedOriginal, recordedTrimmed, sameName, plainM4A, trimmedWAV] {
            #expect(exists(kept), "\(kept.lastPathComponent) must stay")
        }
        #expect(suite.defaults.bool(forKey: PersistenceController.orphanedTrimmedUploadCopySweepDefaultsKey))
    }

    /// A `*_trimmed.m4a` that is not in the VAD output's format (16 kHz mono)
    /// is the user's own file in a folder they picked: it stays, and it does
    /// not hold the one-time flag back.
    @Test func theSweepKeepsFilesNotInTheVADFormat() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let suite = try makeDefaults()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let persistence = PersistenceController(inMemory: true)

        let orphan = try makeAudioFile("a_wav_trimmed.m4a", in: folder, modified: Self.old)
        let stereo = try makeAudioFile("interview_trimmed.m4a", in: folder, sampleRate: 44_100, channels: 2, modified: Self.old)
        let wideband = try makeAudioFile("memo_trimmed.m4a", in: folder, sampleRate: 48_000, modified: Self.old)
        let notAudio = try makeFile("notes_trimmed.m4a", in: folder, modified: Self.old)

        let run = await persistence.sweepOrphanedTrimmedUploadCopiesIfNeeded(
            recordingsFolder: folder, now: Self.now, defaults: suite.defaults
        )
        let deleted = try #require(run)

        #expect(deleted.map(\.lastPathComponent) == [orphan.lastPathComponent])
        for kept in [stereo, wideband, notAudio] {
            #expect(exists(kept), "\(kept.lastPathComponent) must stay")
        }
        #expect(suite.defaults.bool(forKey: PersistenceController.orphanedTrimmedUploadCopySweepDefaultsKey))
    }

    /// One time only: a second launch deletes nothing, even a new orphan.
    @Test func theSweepRunsOnlyOnce() async throws {
        let folder = try makeRecordingsFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let suite = try makeDefaults()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let persistence = PersistenceController(inMemory: true)
        let first = try makeAudioFile("a_wav_trimmed.m4a", in: folder, modified: Self.old)

        let firstRun = await persistence.sweepOrphanedTrimmedUploadCopiesIfNeeded(
            recordingsFolder: folder, now: Self.now, defaults: suite.defaults
        )
        #expect(firstRun?.count == 1)
        #expect(!exists(first))

        let second = try makeAudioFile("b_wav_trimmed.m4a", in: folder, modified: Self.old)
        let secondRun = await persistence.sweepOrphanedTrimmedUploadCopiesIfNeeded(
            recordingsFolder: folder, now: Self.now, defaults: suite.defaults
        )
        #expect(secondRun == nil)
        #expect(exists(second))
    }

    /// A folder that cannot be listed is not a successful run: no flag.
    @Test func aMissingFolderLeavesTheFlagUnset() async throws {
        let suite = try makeDefaults()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let persistence = PersistenceController(inMemory: true)
        let missing = FileManager.default.temporaryDirectory
            .appendingPathComponent("TrimmedAudioOrphanTests-missing-\(UUID().uuidString)", isDirectory: true)

        let run = await persistence.sweepOrphanedTrimmedUploadCopiesIfNeeded(
            recordingsFolder: missing, now: Self.now, defaults: suite.defaults
        )

        #expect(run == nil)
        #expect(!suite.defaults.bool(forKey: PersistenceController.orphanedTrimmedUploadCopySweepDefaultsKey))
    }

    /// The sweep never reads `~/Documents` before the user is asked about it.
    @Test func theLaunchSweepWaitsForTheDocumentsQuestion() throws {
        let suite = try makeDefaults()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let documents = try #require(FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first)
        let inDocuments = documents.appendingPathComponent("hyperwhisper/recordings").path

        suite.defaults.set(inDocuments, forKey: "recordingsFolder")
        #expect(PersistenceController.recordingsFolderForLaunchSweep(defaults: suite.defaults) == nil)

        suite.defaults.set(true, forKey: "documentsPermissionExplained")
        #expect(PersistenceController.recordingsFolderForLaunchSweep(defaults: suite.defaults)?.path == inDocuments)

        let outside = "/tmp/TrimmedAudioOrphanTests-\(UUID().uuidString)"
        suite.defaults.set(false, forKey: "documentsPermissionExplained")
        suite.defaults.set(outside, forKey: "recordingsFolder")
        #expect(PersistenceController.recordingsFolderForLaunchSweep(defaults: suite.defaults)?.path == outside)
    }

    /// The launch hook starts the sweep from the on-disk store's launch repairs.
    @Test func theSweepIsScheduledFromTheLaunchRepairs() throws {
        let launch = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/CoreData/PersistenceController.swift",
            from: "repairStaleProcessingTranscriptsOnLaunch()",
            to: "private func purgeLeakedLocalAPITransientModesOnLaunch()"
        )
        #expect(launch.contains("scheduleOrphanedTrimmedUploadCopySweepOnLaunch()"))
    }
}
