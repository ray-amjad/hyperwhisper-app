//
//  ModelDownloadPartialFiles.swift
//  hyperwhisper
//
//  Issues #1445 and #1723: a model download streams its bytes into an app-owned
//  `<filename>.<uuid><suffix>` file in our own models directory instead of URLSession's
//  `CFNetworkDownload_*.tmp` in the shared `$TMPDIR`, so cancel, failure, quit and the
//  launch sweep can delete it without touching anything that is not ours.
//
//  Whisper (`.partial`) and the GGUF post-processing models (`.llmpartial`) share this
//  one implementation so a cleanup fix lands on both.
//

import Foundation

/// In-flight download files by task identifier. The table is lock-guarded; an entry's
/// fields are touched only on the session's serial delegate queue.
///
/// Unlike a manager's model-id tracking, an entry is NOT removed by a cancel:
/// `didCompleteWithError` always follows the cancel and is what closes and deletes it.
final class ModelDownloadPartialFiles: @unchecked Sendable {

    /// One in-flight download's file.
    final class Entry: @unchecked Sendable {
        let url: URL
        let handle: FileHandle
        var bytesWritten: Int64 = 0
        var expectedBytes: Int64 = NSURLSessionTransferSizeUnknown
        /// For a manager that throttles progress hops by whole percent.
        var lastReportedPercent = -1
        /// Set when we abort the task ourselves (bad HTTP status, disk write error), so
        /// completion reports that instead of a bare "cancelled".
        var failure: Error?

        fileprivate init(url: URL, handle: FileHandle) {
            self.url = url
            self.handle = handle
        }

        /// The task has ended. Closes the file and returns the download's failure, if
        /// any: our own abort, the transport error, or a body shorter or longer than
        /// its Content-Length. On a failure the file is already deleted.
        func finish(transportError: Error?) -> Error? {
            try? handle.close()
            var result = failure ?? transportError
            if result == nil, expectedBytes > 0, bytesWritten != expectedBytes {
                result = URLError(.networkConnectionLost)
            }
            if result != nil {
                discard()
            }
            return result
        }

        /// Delete the file: the download failed, or it finished after its model was
        /// cancelled and nothing will move it into place.
        func discard() {
            try? FileManager.default.removeItem(at: url)
        }
    }

    private let lock = NSLock()
    private var entries: [Int: Entry] = [:]

    /// Create and open an empty file at `url`. Removes it again if it cannot be opened.
    static func open(at url: URL) throws -> Entry {
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        do {
            return Entry(url: url, handle: try FileHandle(forWritingTo: url))
        } catch {
            try? FileManager.default.removeItem(at: url)
            throw error
        }
    }

    func insert(_ entry: Entry, for taskIdentifier: Int) {
        lock.lock(); defer { lock.unlock() }
        entries[taskIdentifier] = entry
    }

    func remove(for taskIdentifier: Int) -> Entry? {
        lock.lock(); defer { lock.unlock() }
        return entries.removeValue(forKey: taskIdentifier)
    }

    private func entry(for taskIdentifier: Int) -> Entry? {
        lock.lock(); defer { lock.unlock() }
        return entries[taskIdentifier]
    }

    /// `didReceive response`: a non-2xx answer (an HTML error page, a 404) aborts the
    /// transfer instead of writing the page into the model file.
    func disposition(for taskIdentifier: Int, response: URLResponse,
                     errorDomain: String) -> URLSession.ResponseDisposition {
        guard let entry = entry(for: taskIdentifier) else { return .cancel }
        if let httpResponse = response as? HTTPURLResponse,
           !(200..<300).contains(httpResponse.statusCode) {
            entry.failure = NSError(domain: errorDomain,
                                    code: httpResponse.statusCode,
                                    userInfo: [NSLocalizedDescriptionKey: "HTTP \(httpResponse.statusCode)"])
            return .cancel
        }
        entry.expectedBytes = response.expectedContentLength
        return .allow
    }

    /// `didReceive data`: append a chunk. Returns the entry for progress reporting, or
    /// nil when the task is not ours or has already failed (a write error cancels it).
    func append(_ data: Data, for dataTask: URLSessionDataTask) -> Entry? {
        guard let entry = entry(for: dataTask.taskIdentifier), entry.failure == nil else { return nil }
        do {
            try entry.handle.write(contentsOf: data)
        } catch {
            entry.failure = error
            dataTask.cancel()
            return nil
        }
        entry.bytesWritten += Int64(data.count)
        return entry
    }

    /// The quit path: delete every in-flight file. The handles stay open; the process
    /// is about to exit.
    func removeAllFromDisk() {
        let urls: [URL] = { lock.lock(); defer { lock.unlock() }; return entries.values.map(\.url) }()
        for url in urls {
            try? FileManager.default.removeItem(at: url)
        }
    }
}
