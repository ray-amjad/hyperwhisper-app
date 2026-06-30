//
//  FileWatcher.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import Atomics

/// Utility for waiting for file write completion using DispatchSource
///
/// **Purpose:**
/// Provides a robust way to wait for files to be ready after asynchronous operations.
/// This is critical for avoiding race conditions where we try to access audio files
/// before the file system has finished writing them.
///
/// **Problem It Solves:**
/// When we stop recording and convert to M4A, the file may not be immediately readable:
/// 1. File creation takes time (filesystem latency)
/// 2. Large files are written in chunks
/// 3. AVAssetExportSession writes asynchronously
///
/// Without FileWatcher, we might try to transcribe a file that's still being written,
/// leading to corrupted reads or "file not found" errors.
///
/// **How It Works:**
/// 1. Poll for file existence (up to 2 seconds with 100ms intervals)
/// 2. If file already has data (size > 0), return immediately (optimization)
/// 3. Otherwise, use DispatchSource to monitor for write events
/// 4. Create a timeout timer to prevent indefinite waiting
/// 5. Resume when write event occurs or timeout fires
///
/// **Technical Implementation:**
/// Uses `O_EVTONLY` flag with `open()` to create a file descriptor that doesn't
/// affect the file but allows kqueue monitoring. This is a macOS-specific optimization.
///
/// **Thread Safety:**
/// Async/await based, can be called from any actor context. Uses internal
/// DispatchQueue for file monitoring events.
@MainActor
class FileWatcher {

    // MARK: - Public Methods

    /// Waits for the first write event on a file using DispatchSource
    ///
    /// **When to Use:**
    /// Call this after any async file operation where you need to ensure the file
    /// is fully written before reading it. Primary use case is after M4A conversion.
    ///
    /// **Flow:**
    /// 1. **Existence Check**: Poll up to 2 seconds for file to be created
    /// 2. **Size Check**: If file already has data, skip waiting (optimization)
    /// 3. **Watch Setup**: Open file descriptor and create DispatchSource
    /// 4. **Event Monitoring**: Wait for either:
    ///    - Write event (file modified)
    ///    - Timeout expiration
    ///
    /// **Parameters:**
    /// - `url`: The URL of the file to watch
    /// - `timeout`: Maximum time to wait for write event (typically 10 seconds)
    ///
    /// **Throws:**
    /// - `FileWatcherError.fileNotCreated`: File doesn't exist after 2 seconds
    /// - `FileWatcherError.failedToOpenFileDescriptor`: Cannot open file for monitoring
    /// - `FileWatcherError.timeout`: No write event within timeout period
    ///
    /// **Performance:**
    /// - Fast path: If file already has data, returns immediately (no waiting)
    /// - Typical wait: 100-500ms for AVAssetExportSession to finish writing
    /// - Worst case: Full timeout period if file is never written
    func waitForFirstWrite(to url: URL, timeout: TimeInterval) async throws {
        // STEP 1: Poll briefly for file existence to handle creation delay
        // Some filesystems have a delay between the operation completing and
        // the file actually appearing in the directory listing
        var fileExists = false
        for _ in 1...20 { // Poll for up to 2 seconds (20 * 100ms)
            if FileManager.default.fileExists(atPath: url.path) {
                fileExists = true
                break
            }
            try await Task.sleep(nanoseconds: 100_000_000) // 100ms
        }

        guard fileExists else {
            AppLogger.audio.error("FileWatcher: File was not created in time at \(url.path, privacy: .public)")
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "File watcher missing file",
                    category: "audio.recording",
                    level: .error,
                    data: [
                        "path": url.path
                    ]
                )
            }
            throw FileWatcherError.fileNotCreated
        }

        // STEP 2: Check if file is already complete (fast path optimization)
        // If the file already has data, we can skip the watch entirely
        // This is common for small files that write atomically
        if let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
           let size = attrs[.size] as? Int64, size > 0 {
            AppLogger.audio.debug("File already exists and has size > 0, skipping wait.")
            return
        }

        // STEP 3: Set up DispatchSource to monitor for write events
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            // O_EVTONLY is a special macOS flag for kqueue that opens the file for event
            // notifications only, without affecting the file itself or requiring read/write access
            let fileDescriptor = open(url.path, O_EVTONLY)

            guard fileDescriptor != -1 else {
                AppLogger.audio.error("FileWatcher: Failed to open file descriptor for \(url.path, privacy: .public)")
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "File watcher failed to open descriptor",
                        category: "audio.recording",
                        level: .error,
                        data: ["path": url.path]
                    )
                }
                continuation.resume(throwing: FileWatcherError.failedToOpenFileDescriptor)
                return
            }
            AppLogger.audio.debug("FileWatcher: Started watching \(url.path, privacy: .public)")

            // Create a dedicated queue for file watching to avoid blocking main thread
            let queue = DispatchQueue(label: "com.hyperwhisper.file-watcher", qos: .userInitiated)

            // Create a dispatch source to monitor the file for write events
            // This is a low-level kqueue-based mechanism that's very efficient
            let source = DispatchSource.makeFileSystemObjectSource(
                fileDescriptor: fileDescriptor,
                eventMask: .write,  // Trigger on write events
                queue: queue
            )

            // ATOMIC GUARD FOR CONTINUATION SAFETY:
            // ManagedAtomic prevents double-resume of the continuation by ensuring only
            // one of the two event handlers (timer or file write) can resume the continuation.
            // The atomic exchange operation is a single CPU instruction - faster than NSLock.
            let finished = ManagedAtomic(false)

            // STEP 4: Create timeout timer
            // If the file is never written, we need to fail gracefully
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + timeout)
            timer.setEventHandler {
                // Atomically set finished to true and check if we were first
                // If exchange returns false, we're the first to finish - safe to resume
                // If exchange returns true, another handler already resumed - do nothing
                if finished.exchange(true, ordering: .acquiring) == false {
                    source.cancel()
                    AppLogger.audio.warning("FileWatcher: Timed out waiting for write event on \(url.path, privacy: .public)")
                    if AppLogger.isErrorLoggingEnabled {
                        SentryService.addBreadcrumb(
                            message: "File watcher timeout",
                            category: "audio.recording",
                            level: .warning,
                            data: [
                                "path": url.path,
                                "timeoutSeconds": timeout
                            ]
                        )
                    }
                    continuation.resume(throwing: FileWatcherError.timeout)
                }
            }
            timer.resume()

            // STEP 5: Define the event handler for write events
            // This fires when the file is modified (data written)
            source.setEventHandler {
                // Atomically set finished to true and check if we were first
                if finished.exchange(true, ordering: .acquiring) == false {
                    timer.cancel()
                    source.cancel()
                    AppLogger.audio.debug("FileWatcher: Received write event for \(url.path, privacy: .public)")
                    continuation.resume()
                }
            }

            // STEP 6: Clean up file descriptor when source is cancelled
            // This is called automatically when we cancel the source
            source.setCancelHandler {
                close(fileDescriptor)
            }

            // Start monitoring
            source.resume()
        }
    }
}
