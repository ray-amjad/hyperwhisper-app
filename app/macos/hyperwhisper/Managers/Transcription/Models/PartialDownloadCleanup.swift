//
//  PartialDownloadCleanup.swift
//  hyperwhisper
//
//  Issue #1445: a cancelled, failed or interrupted model download left its partial
//  files on disk for good. Ray's rule (2026-10-09): clean up, never resume, and delete
//  ONLY files the app can prove are its own. The app is not sandboxed, so `$TMPDIR` is
//  shared with every other URLSession client — never sweep it by a name pattern.
//
//  The two kinds of proof used here:
//  - A path inside a directory only our code writes to (the Whisper `.partial` file in
//    our models directory, a Nemotron variant directory we computed).
//  - The resume data of a download WE started. A cancelled `URLSession.download(for:)`
//    (what FluidAudio uses) keeps its `CFNetworkDownload_*.tmp` for a resume and names
//    it inside the resume data carried by the thrown error; nobody resumes it, so it
//    leaks. That name came from our own task, so removing it touches nothing else.
//

import Foundation
import os

enum PartialDownloadCleanup {

    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "PartialDownloadCleanup")

    // MARK: - Resume-data tmp file (FluidAudio / async URLSession downloads)

    /// Resume-data keys that name the tmp file. Version 2+ stores only the file name
    /// (inside the temp directory); version 1 stored the full path.
    private static let tempFileNameKey = "NSURLSessionResumeInfoTempFileName"
    private static let localPathKey = "NSURLSessionResumeInfoLocalPath"

    /// The tmp file a failed or cancelled download kept for a resume, read from the
    /// resume data the error carries. Walks `NSUnderlyingErrorKey` so a wrapped
    /// transport error still resolves. `nil` when the error carries no resume data.
    static func resumeTempFile(
        for error: Error,
        temporaryDirectory: URL = FileManager.default.temporaryDirectory
    ) -> URL? {
        var current: NSError? = error as NSError
        var depth = 0
        while let nsError = current, depth < 8 {
            if let data = nsError.userInfo[NSURLSessionDownloadTaskResumeData] as? Data,
               let url = resumeTempFile(fromResumeData: data, temporaryDirectory: temporaryDirectory) {
                return url
            }
            current = nsError.userInfo[NSUnderlyingErrorKey] as? NSError
            depth += 1
        }
        return nil
    }

    /// Parse resume data for its tmp file. Resume data is a property list: either the
    /// info dictionary itself, or (newer systems) an `NSKeyedArchiver` archive of it.
    static func resumeTempFile(fromResumeData data: Data, temporaryDirectory: URL) -> URL? {
        guard let info = resumeInfo(from: data) else { return nil }
        if let name = info[tempFileNameKey] as? String, isPlainFileName(name) {
            return temporaryDirectory.appendingPathComponent(name, isDirectory: false)
        }
        if let path = info[localPathKey] as? String, path.hasPrefix("/") {
            return URL(fileURLWithPath: path, isDirectory: false)
        }
        return nil
    }

    /// Remove the tmp file named by `error`'s resume data, if it is still there.
    /// Returns the removed file, for the log line and for tests.
    @discardableResult
    static func removeResumeTempFile(
        for error: Error,
        temporaryDirectory: URL = FileManager.default.temporaryDirectory
    ) -> URL? {
        guard let url = resumeTempFile(for: error, temporaryDirectory: temporaryDirectory) else { return nil }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              !isDirectory.boolValue else { return nil }
        do {
            try FileManager.default.removeItem(at: url)
            logger.info("Removed the abandoned download tmp file \(url.lastPathComponent, privacy: .public)")
            return url
        } catch {
            logger.error("Could not remove the abandoned download tmp file \(url.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    private static func resumeInfo(from data: Data) -> [String: Any]? {
        guard let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil) else {
            return nil
        }
        if let dict = plist as? [String: Any], dict["$archiver"] == nil {
            return dict
        }
        // Keyed archive: unarchive the root object (a dictionary of plist types and an
        // NSURLRequest). Secure coding is off because the archive came from our own task.
        guard let unarchiver = try? NSKeyedUnarchiver(forReadingFrom: data) else { return nil }
        unarchiver.requiresSecureCoding = false
        defer { unarchiver.finishDecoding() }
        return unarchiver.decodeObject(forKey: NSKeyedArchiveRootObjectKey) as? [String: Any]
    }

    /// A bare file name: no separators, no `..`. Keeps a malformed value from
    /// pointing the delete outside the temp directory.
    private static func isPlainFileName(_ name: String) -> Bool {
        !name.isEmpty && !name.contains("/") && name != "." && name != ".."
    }

    // MARK: - Directories the app owns

    /// Remove a directory the app owns outright (a partial model install). No-op
    /// when it is not there.
    @discardableResult
    static func removeOwnedDirectory(_ directory: URL, reason: String) -> Bool {
        guard FileManager.default.fileExists(atPath: directory.path) else { return false }
        do {
            try FileManager.default.removeItem(at: directory)
            logger.info("Removed partial download \(directory.path, privacy: .public) (\(reason, privacy: .public))")
            return true
        } catch {
            logger.error("Could not remove partial download \(directory.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    /// Remove every file in `directory` whose name ends in `suffix`. Used only on a
    /// directory the app owns (the Whisper models directory), never on `$TMPDIR`.
    @discardableResult
    static func removeFiles(withSuffix suffix: String, in directory: URL) -> [URL] {
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: nil,
            options: []
        ) else { return [] }
        var removed: [URL] = []
        for file in files where file.lastPathComponent.hasSuffix(suffix) {
            do {
                try FileManager.default.removeItem(at: file)
                removed.append(file)
                logger.info("Removed leftover partial download \(file.lastPathComponent, privacy: .public)")
            } catch {
                logger.error("Could not remove leftover partial download \(file.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
        return removed
    }
}
