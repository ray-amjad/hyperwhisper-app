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
}
