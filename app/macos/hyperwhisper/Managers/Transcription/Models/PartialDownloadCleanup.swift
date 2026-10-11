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
//  - The resume data of a download task in our own process. A cancelled
//    `URLSession.download(for:)` (what FluidAudio uses) keeps its
//    `CFNetworkDownload_*.tmp`, and nobody resumes it, so it leaks. Its name is only
//    handed back when the task is cancelled with `cancel(byProducingResumeData:)` (or
//    a transport failure carries resume data); that name came from our own task, so
//    removing it touches nothing else.
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
        guard let url = resumeTempFile(for: error, temporaryDirectory: temporaryDirectory),
              removeFile(url) else { return nil }
        return url
    }

    /// Remove one abandoned tmp file (never a directory). `false` when it is not there.
    private static func removeFile(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              !isDirectory.boolValue else { return false }
        do {
            try FileManager.default.removeItem(at: url)
            logger.info("Removed the abandoned download tmp file \(url.lastPathComponent, privacy: .public)")
            return true
        } catch {
            logger.error("Could not remove the abandoned download tmp file \(url.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    private static func resumeInfo(from data: Data) -> [String: Any]? {
        guard let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil) else {
            return nil
        }
        if let dict = plist as? [String: Any], dict["$archiver"] == nil {
            return dict
        }
        // Keyed archive: unarchive the root object (a dictionary of plist types and
        // NSData). Secure coding is off because the archive came from our own task.
        // CFNetwork files the root under the literal key "NSKeyedArchiveRootObjectKey"
        // (measured on macOS 26, 2026-10-10), not under `NSKeyedArchiveRootObjectKey`,
        // whose value is "root"; read both.
        guard let unarchiver = try? NSKeyedUnarchiver(forReadingFrom: data) else { return nil }
        unarchiver.requiresSecureCoding = false
        defer { unarchiver.finishDecoding() }
        for key in [archivedRootKey, NSKeyedArchiveRootObjectKey] {
            if let info = unarchiver.decodeObject(forKey: key) as? [String: Any] {
                return info
            }
        }
        return nil
    }

    private static let archivedRootKey = "NSKeyedArchiveRootObjectKey"

    // MARK: - Cancelling a download we did not start the task for

    /// Cancel every download task in `session` whose ORIGINAL request path contains
    /// `pathFragment`, asking each for resume data, and remove the tmp file that data
    /// names. `completion` runs once every matching task has answered, on no
    /// particular queue, with the files removed.
    ///
    /// Why not a plain cancel: a plain `cancel()` — and a Swift `Task` cancel of an
    /// async `download(for:)`, which is how FluidAudio downloads — throws a bare
    /// `URLError.cancelled` with NO resume data and leaves the tmp file behind with
    /// nothing naming it (measured on macOS 26, 2026-10-10). Only
    /// `cancel(byProducingResumeData:)` hands the tmp file's name back.
    static func cancelDownloadTasks(
        in session: URLSession,
        whereOriginalPathContains pathFragment: String,
        temporaryDirectory: URL = FileManager.default.temporaryDirectory,
        completion: @escaping @Sendable ([URL]) -> Void
    ) {
        session.getAllTasks { tasks in
            let matching = tasks.compactMap { $0 as? URLSessionDownloadTask }.filter {
                $0.originalRequest?.url?.path.contains(pathFragment) == true
            }
            guard !matching.isEmpty else {
                completion([])
                return
            }
            let group = DispatchGroup()
            let removed = RemovedFiles()
            for task in matching {
                group.enter()
                task.cancel(byProducingResumeData: { data in
                    if let data,
                       let url = resumeTempFile(fromResumeData: data, temporaryDirectory: temporaryDirectory),
                       removeFile(url) {
                        removed.append(url)
                    }
                    group.leave()
                })
            }
            group.notify(queue: .global(qos: .utility)) {
                completion(removed.all)
            }
        }
    }

    /// The files removed by concurrent resume-data callbacks.
    private final class RemovedFiles: @unchecked Sendable {
        private let lock = NSLock()
        private var urls: [URL] = []
        func append(_ url: URL) { lock.lock(); urls.append(url); lock.unlock() }
        var all: [URL] { lock.lock(); defer { lock.unlock() }; return urls }
    }

    /// `cancelDownloadTasks(in:whereOriginalPathContains:completion:)`, awaited.
    @discardableResult
    static func cancelDownloadTasks(
        in session: URLSession,
        whereOriginalPathContains pathFragment: String
    ) async -> [URL] {
        await withCheckedContinuation { continuation in
            cancelDownloadTasks(in: session, whereOriginalPathContains: pathFragment) {
                continuation.resume(returning: $0)
            }
        }
    }

    /// `cancelDownloadTasks(in:whereOriginalPathContains:completion:)`, blocking the
    /// caller for at most `timeout`. For the quit path, which cannot await. Safe on
    /// the main thread: URLSession answers on its own delegate queue.
    static func cancelDownloadTasksBlocking(
        in session: URLSession,
        whereOriginalPathContains pathFragment: String,
        timeout: TimeInterval
    ) {
        let done = DispatchSemaphore(value: 0)
        cancelDownloadTasks(in: session, whereOriginalPathContains: pathFragment) { _ in
            done.signal()
        }
        if done.wait(timeout: .now() + timeout) == .timedOut {
            logger.warning("Timed out cancelling downloads under \(pathFragment, privacy: .public); their tmp files may remain")
        }
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
