//
//  PartialDownloadCleanupTests.swift
//  hyperwhisperTests
//
//  Issue #1445: a cancelled or interrupted model download left its partial files on
//  disk. The app may delete only files it can prove are its own (Ray, 2026-10-09):
//  the tmp file named in its own download's resume data, and files in directories it
//  owns. These tests pin that the helper finds exactly that file and touches nothing
//  else in the directory.
//

import Foundation
import Testing
@testable import HyperWhisper

struct PartialDownloadCleanupTests {

    private static func makeDirectory() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("PartialDownloadCleanupTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private static func touch(_ url: URL) {
        FileManager.default.createFile(atPath: url.path, contents: Data("partial".utf8))
    }

    /// A cancelled-download error as URLSession builds it: `NSURLErrorCancelled`
    /// with the resume data in `userInfo`.
    private static func cancelledError(resumeData: Data) -> Error {
        NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled,
                userInfo: [NSURLSessionDownloadTaskResumeData: resumeData])
    }

    private static func plistResumeData(_ info: [String: Any]) throws -> Data {
        try PropertyListSerialization.data(fromPropertyList: info, format: .binary, options: 0)
    }

    // MARK: Resume data → tmp file

    /// Version 2+ resume data names the file inside the temp directory.
    @Test func plainResumeDataResolvesTheTempFileName() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let data = try Self.plistResumeData([
            "NSURLSessionResumeInfoVersion": 2,
            "NSURLSessionResumeInfoTempFileName": "CFNetworkDownload_AbC123.tmp",
            "NSURLSessionResumeBytesReceived": 210_405_789
        ])

        let url = PartialDownloadCleanup.resumeTempFile(for: Self.cancelledError(resumeData: data),
                                                        temporaryDirectory: tmp)
        #expect(url == tmp.appendingPathComponent("CFNetworkDownload_AbC123.tmp"))
    }

    /// Version 1 resume data carries the full path instead.
    @Test func versionOneResumeDataResolvesTheLocalPath() throws {
        let data = try Self.plistResumeData([
            "NSURLSessionResumeInfoLocalPath": "/private/var/folders/xy/T/CFNetworkDownload_Old.tmp"
        ])
        let url = PartialDownloadCleanup.resumeTempFile(fromResumeData: data,
                                                        temporaryDirectory: URL(fileURLWithPath: "/unused"))
        #expect(url?.path == "/private/var/folders/xy/T/CFNetworkDownload_Old.tmp")
    }

    /// Newer systems archive the info dictionary with NSKeyedArchiver.
    @Test func keyedArchiveResumeDataResolvesTheTempFileName() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let info: NSDictionary = [
            "NSURLSessionResumeInfoVersion": 4,
            "NSURLSessionResumeInfoTempFileName": "CFNetworkDownload_Keyed.tmp"
        ]
        let data = try NSKeyedArchiver.archivedData(withRootObject: info, requiringSecureCoding: false)

        let url = PartialDownloadCleanup.resumeTempFile(fromResumeData: data, temporaryDirectory: tmp)
        #expect(url == tmp.appendingPathComponent("CFNetworkDownload_Keyed.tmp"))
    }

    /// The shape CFNetwork really writes (macOS 26, 2026-10-10): a keyed archive whose
    /// root sits under the literal key "NSKeyedArchiveRootObjectKey", not under "root".
    @Test func cfNetworkKeyedArchiveResumeDataResolvesTheTempFileName() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let info: NSDictionary = [
            "NSURLSessionResumeInfoVersion": 6,
            "NSURLSessionResumeInfoTempFileName": "CFNetworkDownload_9pgW3C.tmp",
            "NSURLSessionResumeBytesReceived": 27_407_183
        ]
        let archiver = NSKeyedArchiver(requiringSecureCoding: false)
        archiver.encode(info, forKey: "NSKeyedArchiveRootObjectKey")
        archiver.finishEncoding()

        let url = PartialDownloadCleanup.resumeTempFile(fromResumeData: archiver.encodedData,
                                                        temporaryDirectory: tmp)
        #expect(url == tmp.appendingPathComponent("CFNetworkDownload_9pgW3C.tmp"))
    }

    /// The Nemotron cancel touches only the cancelled variant's requests in
    /// FluidAudio's shared session.
    @Test func nemotronTransferPathMatchesOnlyItsOwnVariant() throws {
        let url = try #require(URL(string:
            "https://huggingface.co/FluidInference/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b-CoreML/resolve/main/multilingual/2240ms/encoder.mlmodelc/weights/weight.bin"))
        #expect(url.path.contains(NemotronModelManager.transferPathFragment(for: .multilingual)))
        #expect(!url.path.contains(NemotronModelManager.transferPathFragment(for: .latin)))
    }

    /// A transport error wrapped by a library still resolves through `NSUnderlyingErrorKey`.
    @Test func wrappedErrorResolvesThroughTheUnderlyingError() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let data = try Self.plistResumeData(["NSURLSessionResumeInfoTempFileName": "CFNetworkDownload_W.tmp"])
        let wrapped = NSError(domain: "FluidAudio", code: 1,
                              userInfo: [NSUnderlyingErrorKey: Self.cancelledError(resumeData: data)])

        #expect(PartialDownloadCleanup.resumeTempFile(for: wrapped, temporaryDirectory: tmp)
                == tmp.appendingPathComponent("CFNetworkDownload_W.tmp"))
    }

    /// No resume data, no file: a cancel before any bytes, or a `CancellationError`.
    @Test func anErrorWithoutResumeDataNamesNoFile() {
        #expect(PartialDownloadCleanup.resumeTempFile(for: URLError(.cancelled)) == nil)
        #expect(PartialDownloadCleanup.resumeTempFile(for: CancellationError()) == nil)
        #expect(PartialDownloadCleanup.resumeTempFile(
            for: Self.cancelledError(resumeData: Data("not a plist".utf8))) == nil)
    }

    /// A name that is not a bare file name must not steer the delete out of the temp directory.
    @Test func aTempFileNameWithASeparatorIsRejected() throws {
        let data = try Self.plistResumeData(["NSURLSessionResumeInfoTempFileName": "../Documents/important.txt"])
        #expect(PartialDownloadCleanup.resumeTempFile(fromResumeData: data,
                                                      temporaryDirectory: URL(fileURLWithPath: "/tmp")) == nil)
    }

    /// The cancel path removes exactly the file its resume data names, and leaves
    /// another app's `CFNetworkDownload_*.tmp` beside it alone.
    @Test func removeResumeTempFileDeletesOnlyTheNamedFile() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let ours = tmp.appendingPathComponent("CFNetworkDownload_Ours.tmp")
        let theirs = tmp.appendingPathComponent("CFNetworkDownload_Theirs.tmp")
        Self.touch(ours)
        Self.touch(theirs)
        let data = try Self.plistResumeData(["NSURLSessionResumeInfoTempFileName": ours.lastPathComponent])

        let removed = PartialDownloadCleanup.removeResumeTempFile(for: Self.cancelledError(resumeData: data),
                                                                  temporaryDirectory: tmp)

        #expect(removed == ours)
        #expect(!FileManager.default.fileExists(atPath: ours.path))
        #expect(FileManager.default.fileExists(atPath: theirs.path))
    }

    // MARK: Directories the app owns

    /// The Whisper launch sweep removes `.partial` files and keeps installed models.
    @Test func removeFilesWithSuffixKeepsEverythingElse() throws {
        let models = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: models) }
        let partial = models.appendingPathComponent("ggml-small.en.bin.\(UUID().uuidString)\(WhisperModelManager.partialFileSuffix)")
        let installed = models.appendingPathComponent("ggml-base.en.bin")
        Self.touch(partial)
        Self.touch(installed)

        let removed = PartialDownloadCleanup.removeFiles(withSuffix: WhisperModelManager.partialFileSuffix, in: models)

        #expect(removed.map(\.lastPathComponent) == [partial.lastPathComponent])
        #expect(!FileManager.default.fileExists(atPath: partial.path))
        #expect(FileManager.default.fileExists(atPath: installed.path))
    }

    /// The Nemotron path: a partial variant directory goes, its siblings stay.
    @Test func removeOwnedDirectoryRemovesOnlyThatDirectory() throws {
        let repo = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: repo) }
        let variant = repo.appendingPathComponent("multilingual/2240ms", isDirectory: true)
        let sibling = repo.appendingPathComponent("latin/2240ms", isDirectory: true)
        try FileManager.default.createDirectory(at: variant.appendingPathComponent("encoder.mlmodelc/weights"),
                                                withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: sibling, withIntermediateDirectories: true)

        #expect(PartialDownloadCleanup.removeOwnedDirectory(variant, reason: "test"))
        #expect(!FileManager.default.fileExists(atPath: variant.path))
        #expect(FileManager.default.fileExists(atPath: sibling.path))
        #expect(PartialDownloadCleanup.removeOwnedDirectory(variant, reason: "test") == false)
    }

    /// The GGUF partial file must not end in Whisper's suffix: Whisper's launch sweep
    /// runs over the same models directory and could delete a live GGUF download (#1723).
    @Test func theGGUFPartialSuffixIsNotWhispers() {
        // Compare paths: one URL is built with isDirectory: true (a trailing slash), so URL == differs.
        #expect(LocalModelManager.modelsDirectory.standardizedFileURL.path == WhisperModelManager.modelsDirectory.standardizedFileURL.path)
        #expect(!LocalModelManager.partialFileSuffix.hasSuffix(WhisperModelManager.partialFileSuffix))
        let gguf = "gemma-4-E2B-it-Q4_K_M.gguf.\(UUID().uuidString)\(LocalModelManager.partialFileSuffix)"
        #expect(!gguf.hasSuffix(WhisperModelManager.partialFileSuffix))
        #expect(URL(fileURLWithPath: gguf).pathExtension != "gguf")
    }

    // MARK: Shared partial download file (#1445, #1723)

    /// A body as long as its Content-Length is kept for the caller to move into place;
    /// `discard` (a cancel that landed after the last byte) still deletes it.
    @Test func aCompleteDownloadIsKeptUntilDiscarded() throws {
        let models = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: models) }
        let entry = try ModelDownloadPartialFiles.open(at: models.appendingPathComponent("m.gguf.x.llmpartial"))
        try entry.handle.write(contentsOf: Data("model".utf8))
        entry.bytesWritten = 5
        entry.expectedBytes = 5

        #expect(entry.finish(transportError: nil) == nil)
        #expect(FileManager.default.fileExists(atPath: entry.url.path))
        entry.discard()
        #expect(!FileManager.default.fileExists(atPath: entry.url.path))
    }

    /// A short body and a transport error are failures, and the file is gone.
    @Test func aFailedDownloadDeletesItsFile() throws {
        let models = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: models) }
        let short = try ModelDownloadPartialFiles.open(at: models.appendingPathComponent("a.partial"))
        short.bytesWritten = 3
        short.expectedBytes = 5
        let cancelled = try ModelDownloadPartialFiles.open(at: models.appendingPathComponent("b.partial"))

        #expect(short.finish(transportError: nil) != nil)
        #expect(cancelled.finish(transportError: URLError(.cancelled)) != nil)
        #expect(!FileManager.default.fileExists(atPath: short.url.path))
        #expect(!FileManager.default.fileExists(atPath: cancelled.url.path))
    }

    // MARK: Tmp files this process holds open (#1723)

    /// Only a `CFNetworkDownload_*.tmp` this process holds open is reported; a closed
    /// one beside it (another app's, as far as we can prove) and an open file with
    /// another name are not.
    @Test func openDownloadTempFilesReportsOnlyFilesThisProcessHoldsOpen() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let open = tmp.appendingPathComponent("CFNetworkDownload_Open.tmp")
        let closed = tmp.appendingPathComponent("CFNetworkDownload_Closed.tmp")
        let otherName = tmp.appendingPathComponent("unrelated.tmp")
        [open, closed, otherName].forEach(Self.touch)
        let openHandle = try FileHandle(forWritingTo: open)
        let otherHandle = try FileHandle(forWritingTo: otherName)
        defer { try? openHandle.close(); try? otherHandle.close() }

        let found = PartialDownloadCleanup.openDownloadTempFiles(temporaryDirectory: tmp)

        #expect(found.map(\.lastPathComponent) == [open.lastPathComponent])
    }

    /// A candidate that is still open (a transfer still running) stays; one that was
    /// closed goes; a file that was never a candidate stays.
    @Test func removeAbandonedDownloadTempFilesKeepsOpenFiles() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let running = tmp.appendingPathComponent("CFNetworkDownload_Running.tmp")
        let abandoned = tmp.appendingPathComponent("CFNetworkDownload_Abandoned.tmp")
        let theirs = tmp.appendingPathComponent("CFNetworkDownload_Theirs.tmp")
        [running, abandoned, theirs].forEach(Self.touch)
        let runningHandle = try FileHandle(forWritingTo: running)
        defer { try? runningHandle.close() }
        let abandonedHandle = try FileHandle(forWritingTo: abandoned)
        let candidates = PartialDownloadCleanup.openDownloadTempFiles(temporaryDirectory: tmp)
        #expect(candidates.count == 2)
        try abandonedHandle.close()

        let removed = PartialDownloadCleanup.removeAbandonedDownloadTempFiles(candidates, temporaryDirectory: tmp)

        #expect(removed.map(\.lastPathComponent) == [abandoned.lastPathComponent])
        #expect(FileManager.default.fileExists(atPath: running.path))
        #expect(!FileManager.default.fileExists(atPath: abandoned.path))
        #expect(FileManager.default.fileExists(atPath: theirs.path))
    }

    /// The tracker ignores a download file that was already open when it started:
    /// that one belongs to another download.
    @MainActor
    @Test func theTrackerIgnoresFilesOpenBeforeItStarted() throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let before = tmp.appendingPathComponent("CFNetworkDownload_Before.tmp")
        let during = tmp.appendingPathComponent("CFNetworkDownload_During.tmp")
        Self.touch(before)
        let beforeHandle = try FileHandle(forWritingTo: before)
        let tracker = DownloadTempFileTracker(temporaryDirectory: tmp)
        Self.touch(during)
        let duringHandle = try FileHandle(forWritingTo: during)
        tracker.sample()
        try beforeHandle.close()
        try duringHandle.close()

        let removed = tracker.removeAbandoned()

        #expect(removed.map(\.lastPathComponent) == [during.lastPathComponent])
        #expect(FileManager.default.fileExists(atPath: before.path))
    }

    /// Started, the tracker names a file opened later on its own, with no progress
    /// callback: FluidAudio sends none while a file is in flight.
    @MainActor
    @Test func theStartedTrackerSamplesOnItsOwn() async throws {
        let tmp = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: tmp) }
        let tracker = DownloadTempFileTracker(temporaryDirectory: tmp, interval: 0.05)
        tracker.start()
        let file = tmp.appendingPathComponent("CFNetworkDownload_Later.tmp")
        Self.touch(file)
        let handle = try FileHandle(forWritingTo: file)
        try await Task.sleep(nanoseconds: 500_000_000)
        try handle.close()

        #expect(tracker.seen.map(\.lastPathComponent) == [file.lastPathComponent])
        #expect(tracker.removeAbandoned().map(\.lastPathComponent) == [file.lastPathComponent])
        #expect(!FileManager.default.fileExists(atPath: file.path))
    }

    // MARK: Parakeet download marker (#1723)

    /// With the marker, the cache directory is an unfinished download: both go.
    /// Without it, the directory is left alone.
    @Test func parakeetMarkerGatesTheRemoval() throws {
        let root = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = root.appendingPathComponent("parakeet-tdt-0.6b-v3", isDirectory: true)
        let marker = root.appendingPathComponent("parakeet-tdt-0.6b-v3.download-in-progress")
        try FileManager.default.createDirectory(at: cache.appendingPathComponent("Encoder.mlmodelc"),
                                                withIntermediateDirectories: true)

        #expect(!ParakeetModelManager.removeUnfinishedDownload(in: cache, marker: marker, reason: "test"))
        #expect(FileManager.default.fileExists(atPath: cache.path))

        Self.touch(marker)
        #expect(ParakeetModelManager.removeUnfinishedDownload(in: cache, marker: marker, reason: "test"))
        #expect(!FileManager.default.fileExists(atPath: cache.path))
        #expect(!FileManager.default.fileExists(atPath: marker.path))
    }

    /// The marker lives in our own folder, never inside FluidAudio's cache directory,
    /// which FluidAudio deletes itself on a forced or retried download.
    @Test func parakeetMarkerLivesOutsideTheCacheDirectory() {
        let cache = URL(fileURLWithPath: "/Users/x/Library/Application Support/FluidAudio/Models/parakeet-tdt-0.6b-v3")
        let marker = ParakeetModelManager.downloadMarker(forCacheDirectory: cache)
        #expect(!marker.path.hasPrefix(cache.path))
        #expect(marker.deletingLastPathComponent().lastPathComponent == "hyperwhisper")
        #expect(marker.lastPathComponent == "parakeet-tdt-0.6b-v3.download-in-progress")
    }

    // MARK: Nemotron in-progress marker

    /// `metadata.json` beside the marker is a download that never finished; without the
    /// marker it is an install, as every pre-#1445 install is.
    @Test func theInProgressMarkerOverridesMetadata() throws {
        let variant = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: variant) }
        let metadata = variant.appendingPathComponent(NemotronModelManager.Constants.metadataFileName)
        let marker = variant.appendingPathComponent(NemotronModelManager.Constants.inProgressMarkerFileName)

        #expect(!NemotronModelManager.variantDirectoryIsInstalled(variant))
        Self.touch(metadata)
        #expect(NemotronModelManager.variantDirectoryIsInstalled(variant))
        Self.touch(marker)
        #expect(!NemotronModelManager.variantDirectoryIsInstalled(variant))
        NemotronModelManager.clearInProgressMarker(in: variant)
        #expect(!FileManager.default.fileExists(atPath: marker.path))
        #expect(NemotronModelManager.variantDirectoryIsInstalled(variant))
    }

    /// A new download drops what an unfinished run left, then writes only the marker.
    @Test func beginningADownloadReplacesLeftoversWithTheMarker() throws {
        let repo = try Self.makeDirectory()
        defer { try? FileManager.default.removeItem(at: repo) }
        let variant = repo.appendingPathComponent("multilingual/2240ms", isDirectory: true)
        try FileManager.default.createDirectory(at: variant, withIntermediateDirectories: true)
        Self.touch(variant.appendingPathComponent(NemotronModelManager.Constants.metadataFileName))

        #expect(NemotronModelManager.beginInProgressMarker(in: variant))
        let contents = try FileManager.default.contentsOfDirectory(atPath: variant.path)
        #expect(contents == [NemotronModelManager.Constants.inProgressMarkerFileName])
        #expect(!NemotronModelManager.variantDirectoryIsInstalled(variant))
    }
}
