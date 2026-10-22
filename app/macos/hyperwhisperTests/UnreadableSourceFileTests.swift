//
//  UnreadableSourceFileTests.swift
//  hyperwhisperTests
//
//  Issue #1551: an unreadable source file got the wrong cause, or none.
//  Transcribe File blamed the recordings folder (Foundation's copy error names
//  the destination), and the Local API `file` input answered FILE_NOT_FOUND for
//  a folder and for a file behind an unenterable folder, and a Full Disk Access
//  hint for a plain mode 000. Ray's codes (inbox #358): a folder is
//  INVALID_REQUEST, an unenterable folder is FILE_ACCESS_DENIED, and the Full
//  Disk Access hint is for a privacy (EPERM) block only.
//
//  The probe runs on real files and real modes, so it is the errno the kernel
//  gives, not one this file assumes.
//

import Darwin
import Foundation
import Testing
@testable import HyperWhisper

@Suite("An unreadable source file names its cause")
struct UnreadableSourceFileTests {

    /// A fresh folder with `inner.wav` in it (a few bytes; nothing decodes it).
    private func makeFolderWithFile() throws -> (folder: URL, file: URL) {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("unreadable-1551-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent("inner.wav")
        try Data("RIFF".utf8).write(to: file)
        return (folder, file)
    }

    /// Put modes back so the folder can be deleted.
    private func remove(_ folder: URL, file: URL) {
        chmod(folder.path, 0o700)
        chmod(file.path, 0o600)
        try? FileManager.default.removeItem(at: folder)
    }

    /// Root reads through any mode, so the two EACCES cases cannot happen.
    static var permissionsApply: Bool { geteuid() != 0 }

    // MARK: - The probe, on real files

    @Test func aReadableFileIsReadable() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        #expect(SourceFileAccess.probe(path: file.path) == .readable)
    }

    @Test func aMissingFileIsMissing() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        #expect(SourceFileAccess.probe(path: folder.appendingPathComponent("absent.wav").path) == .missing)
    }

    @Test func aDanglingSymlinkIsStillMissing() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        let link = folder.appendingPathComponent("dangling.wav")
        try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: "nowhere.wav")
        #expect(SourceFileAccess.probe(path: link.path) == .missing)
    }

    @Test func aFolderIsAFolder() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        let bundle = folder.appendingPathComponent("bundle.wav", isDirectory: true)
        try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: false)
        #expect(SourceFileAccess.probe(path: bundle.path) == .isFolder)
    }

    @Test(.enabled(if: UnreadableSourceFileTests.permissionsApply)) func aMode000FileIsDeniedOnTheFile() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        #expect(chmod(file.path, 0o000) == 0)
        #expect(SourceFileAccess.probe(path: file.path) == .fileDenied)
    }

    /// Case B and `nox/in.wav`: a folder with no execute bit cannot be entered,
    /// so the `stat` fails, not the file's own mode.
    @Test(.enabled(if: UnreadableSourceFileTests.permissionsApply)) func aFolderWithNoExecuteBitIsDeniedOnTheFolder() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        #expect(chmod(folder.path, 0o600) == 0)
        #expect(SourceFileAccess.probe(path: file.path) == .folderInPathDenied)
    }

    @Test func epermIsAPrivacyBlockAndEaccesIsNot() {
        #expect(SourceFileAccess.classify(statErrno: EPERM) == .privacyBlocked)
        #expect(SourceFileAccess.classify(openErrno: EPERM) == .privacyBlocked)
        #expect(SourceFileAccess.classify(statErrno: EACCES) == .folderInPathDenied)
        #expect(SourceFileAccess.classify(openErrno: EACCES) == .fileDenied)
        #expect(SourceFileAccess.classify(statErrno: ELOOP) == .missing)
    }

    // MARK: - Local API answers

    private let fullDiskAccessHint = "Full Disk Access"

    @Test func localAPIAnswersAReadableFileWithNoRefusal() {
        #expect(TranscribeEndpoint.fileInputRefusal(for: .readable, displayPath: "/r/ok.wav") == nil)
    }

    @Test func localAPIAnswersAFolderWithInvalidRequest() throws {
        let refusal = try #require(TranscribeEndpoint.fileInputRefusal(for: .isFolder, displayPath: "/r/bundle.wav"))
        #expect(refusal.code == .invalidRequest)
        #expect(refusal.message.contains("/r/bundle.wav is a folder, not an audio file"))
        #expect(refusal.hint?.contains(fullDiskAccessHint) != true)
    }

    @Test func localAPIAnswersAnUnenterableFolderWithAccessDeniedAndNoFullDiskAccessHint() throws {
        let refusal = try #require(TranscribeEndpoint.fileInputRefusal(for: .folderInPathDenied, displayPath: "/r/nox/in.wav"))
        #expect(refusal.code == .fileAccessDenied)
        #expect(refusal.message.contains("Permission denied on a folder in the path"))
        #expect(refusal.hint?.contains(fullDiskAccessHint) != true)
    }

    @Test func localAPIAnswersAMode000FileWithAccessDeniedAndNoFullDiskAccessHint() throws {
        let refusal = try #require(TranscribeEndpoint.fileInputRefusal(for: .fileDenied, displayPath: "/r/noperm.wav"))
        #expect(refusal.code == .fileAccessDenied)
        #expect(refusal.message.contains("/r/noperm.wav"))
        #expect(refusal.hint?.contains(fullDiskAccessHint) != true)
    }

    @Test func localAPIKeepsTheFullDiskAccessHintForAPrivacyBlockOnly() throws {
        let refusal = try #require(TranscribeEndpoint.fileInputRefusal(for: .privacyBlocked, displayPath: "/r/tcc.wav"))
        #expect(refusal.code == .fileAccessDenied)
        #expect(refusal.hint?.contains(fullDiskAccessHint) == true)
    }

    @Test func localAPIStillAnswersAMissingFileWithFileNotFound() throws {
        let refusal = try #require(TranscribeEndpoint.fileInputRefusal(for: .missing, displayPath: "/r/absent.wav"))
        #expect(refusal.code == .fileNotFound)
    }

    // MARK: - Transcribe File alerts

    @Test(.enabled(if: UnreadableSourceFileTests.permissionsApply)) func transcribeFileNamesTheSourceForAMode000File() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        #expect(chmod(file.path, 0o000) == 0)

        do {
            try FileTranscriptionFlow.checkSourceReadable(file)
            Issue.record("a mode 000 file passed the check")
        } catch let error as FileTranscriptionError {
            guard case .sourcePermissionDenied(let fileName) = error else {
                Issue.record("expected sourcePermissionDenied, got \(error)")
                return
            }
            #expect(fileName == "inner.wav")
            let message = try #require(error.errorDescription)
            #expect(message.contains("inner.wav"))
            #expect(!message.contains(folder.lastPathComponent), "the alert must not name a folder for a file-mode denial")
        }
    }

    @Test(.enabled(if: UnreadableSourceFileTests.permissionsApply)) func transcribeFileNamesTheFolderCaseForALockedFolder() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        #expect(chmod(folder.path, 0o600) == 0)

        do {
            try FileTranscriptionFlow.checkSourceReadable(file)
            Issue.record("a file behind a folder with no execute bit passed the check")
        } catch let error as FileTranscriptionError {
            guard case .sourceFolderPermissionDenied(let fileName) = error else {
                Issue.record("expected sourceFolderPermissionDenied, got \(error)")
                return
            }
            #expect(fileName == "inner.wav")
        }
    }

    @Test func transcribeFileLetsAReadableFileThrough() throws {
        let (folder, file) = try makeFolderWithFile()
        defer { remove(folder, file: file) }
        try FileTranscriptionFlow.checkSourceReadable(file)
    }

    @Test func transcribeFileGivesFullDiskAccessOnlyForAPrivacyBlock() throws {
        let privacy = try #require(FileTranscriptionFlow.sourceAccessError(for: .privacyBlocked, fileName: "a.wav"))
        let permission = try #require(FileTranscriptionFlow.sourceAccessError(for: .fileDenied, fileName: "a.wav"))
        let folder = try #require(FileTranscriptionFlow.sourceAccessError(for: .folderInPathDenied, fileName: "a.wav"))
        #expect(privacy.errorDescription != permission.errorDescription)
        #expect(folder.errorDescription != permission.errorDescription)
        #expect(FileTranscriptionFlow.sourceAccessError(for: .readable, fileName: "a.wav") == nil)
        #expect(FileTranscriptionFlow.sourceAccessError(for: .missing, fileName: "a.wav") == nil)
    }
}
