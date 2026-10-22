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
//  - A file descriptor this process holds open (issue #1723). FluidAudio downloads a
//    Parakeet file on a private per-file session the app cannot reach, so there is no
//    task to ask for resume data. While the transfer runs, though, its
//    `CFNetworkDownload_*.tmp` is open in OUR process, and that names it.
//

import Foundation
import Darwin
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

    // MARK: - Tmp files this process holds open (issue #1723)

    /// The `CFNetworkDownload_*.tmp` files directly in `temporaryDirectory` that this
    /// process holds open right now: the in-flight transfers of its own download tasks.
    /// The open descriptor is the proof the file is ours; the name only narrows it to
    /// URLSession's download files.
    static func openDownloadTempFiles(
        temporaryDirectory: URL = FileManager.default.temporaryDirectory
    ) -> Set<URL> {
        guard let root = realPath(temporaryDirectory.path) else { return [] }
        var found: Set<URL> = []
        for path in openFilePaths() {
            let url = URL(fileURLWithPath: path, isDirectory: false)
            let name = url.lastPathComponent
            guard url.deletingLastPathComponent().path == root,
                  name.hasPrefix("CFNetworkDownload_"), name.hasSuffix(".tmp") else { continue }
            found.insert(url)
        }
        return found
    }

    /// Remove each of `candidates` (files `openDownloadTempFiles` returned earlier)
    /// that this process no longer holds open. A file still open belongs to a transfer
    /// that is still running, maybe another model's, and is left alone. A closed one
    /// is a transfer that ended: a finished one has been moved away by its owner, so
    /// what is left there is abandoned. Returns the files removed.
    @discardableResult
    static func removeAbandonedDownloadTempFiles(
        _ candidates: Set<URL>,
        temporaryDirectory: URL = FileManager.default.temporaryDirectory
    ) -> [URL] {
        guard !candidates.isEmpty else { return [] }
        let stillOpen = openDownloadTempFiles(temporaryDirectory: temporaryDirectory)
        return candidates.subtracting(stillOpen)
            .sorted { $0.path < $1.path }
            .filter { removeFile($0) }
    }

    /// The quit path: remove every download tmp file this process holds open. Each
    /// is an in-process transfer that dies with the process, and nothing resumes it.
    @discardableResult
    static func removeOpenDownloadTempFiles(
        temporaryDirectory: URL = FileManager.default.temporaryDirectory
    ) -> [URL] {
        openDownloadTempFiles(temporaryDirectory: temporaryDirectory)
            .sorted { $0.path < $1.path }
            .filter { removeFile($0) }
    }

    /// The paths of this process's open vnode descriptors.
    private static func openFilePaths() -> [String] {
        let pid = getpid()
        let needed = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
        guard needed > 0 else { return [] }
        let stride = MemoryLayout<proc_fdinfo>.stride
        // Room for descriptors opened between the two calls.
        var fds = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(needed) / stride + 32)
        let used = fds.withUnsafeMutableBytes { buffer in
            proc_pidinfo(pid, PROC_PIDLISTFDS, 0, buffer.baseAddress, Int32(buffer.count))
        }
        guard used > 0 else { return [] }
        var paths: [String] = []
        var path = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        for info in fds.prefix(Int(used) / stride) where info.proc_fdtype == UInt32(PROX_FDTYPE_VNODE) {
            if fcntl(info.proc_fd, F_GETPATH, &path) != -1 {
                paths.append(String(cString: path))
            }
        }
        return paths
    }

    /// `realpath(3)`: `$TMPDIR` is `/var/folders/…`, a descriptor's path is `/private/var/folders/…`.
    private static func realPath(_ path: String) -> String? {
        guard let resolved = realpath(path, nil) else { return nil }
        defer { free(resolved) }
        return String(cString: resolved)
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

/// The `CFNetworkDownload_*.tmp` files one FluidAudio download opened (issue #1723).
///
/// FluidAudio fetches each Parakeet file on a private session the app cannot reach,
/// and a cancel or a failure there leaves the file's tmp behind with nothing naming it
/// (measured on macOS 26.6, 2026-10-13). Sampling this process's open download files
/// while the download runs names them; `removeAbandoned()` then drops the ones no
/// transfer holds any more. Files that were already open when the download began
/// belong to another download and are never collected.
///
/// The sampling runs on its own timer: FluidAudio 0.15.2 calls its progress handler
/// only between files, when no tmp file is open (measured: 7 calls in 20 s).
@MainActor
final class DownloadTempFileTracker {
    private let temporaryDirectory: URL
    private let interval: TimeInterval
    private let preexisting: Set<URL>
    private(set) var seen: Set<URL> = []
    private var sampler: Task<Void, Never>?

    init(temporaryDirectory: URL = FileManager.default.temporaryDirectory, interval: TimeInterval = 0.5) {
        self.temporaryDirectory = temporaryDirectory
        self.interval = interval
        preexisting = PartialDownloadCleanup.openDownloadTempFiles(temporaryDirectory: temporaryDirectory)
    }

    /// Sample every `interval` until `removeAbandoned()`. Each sample is one
    /// `proc_pidinfo` call plus an `fcntl` per open file.
    func start() {
        guard sampler == nil else { return }
        let nanoseconds = UInt64(interval * 1_000_000_000)
        sampler = Task { [weak self] in
            while !Task.isCancelled {
                self?.sample()
                try? await Task.sleep(nanoseconds: nanoseconds)
            }
        }
    }

    /// Record the download files open right now. Call it just before a cancel too,
    /// so a file opened since the last tick is named while it is still open.
    func sample() {
        seen.formUnion(PartialDownloadCleanup.openDownloadTempFiles(temporaryDirectory: temporaryDirectory)
            .subtracting(preexisting))
    }

    /// Stop sampling and remove every collected file that is no longer open. Call it
    /// once the download has returned, whatever the outcome.
    @discardableResult
    func removeAbandoned() -> [URL] {
        sampler?.cancel()
        sampler = nil
        return PartialDownloadCleanup.removeAbandonedDownloadTempFiles(seen, temporaryDirectory: temporaryDirectory)
    }
}
