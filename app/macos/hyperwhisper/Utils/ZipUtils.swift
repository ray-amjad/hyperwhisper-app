//
//  ZipUtils.swift
//  hyperwhisper
//
//  Centralized ZIP extraction utility used by multiple managers.
//  Avoids duplicate implementations and keeps heavy I/O off the main thread.
//

import Foundation

enum ZipUtils {
    /// Extract a ZIP archive to a destination folder using the system `ditto` command.
    /// - Parameters:
    ///   - zipURL: Source ZIP file location.
    ///   - destinationFolder: Destination directory (created if missing).
    /// - Throws: Error if extraction fails.
    static func extractZip(at zipURL: URL, to destinationFolder: URL) async throws {
        // Ensure destination exists (non-main; heavy I/O should not run on main actor)
        try? FileManager.default.createDirectory(at: destinationFolder, withIntermediateDirectories: true)

        // Use /usr/bin/ditto -x -k zip dest
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        process.arguments = ["-x", "-k", zipURL.path, destinationFolder.path]

        let stderrPipe = Pipe()
        process.standardError = stderrPipe
        process.standardOutput = Pipe()

        try process.run()

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            process.terminationHandler = { proc in
                if proc.terminationStatus == 0 {
                    continuation.resume()
                } else {
                    let data = stderrPipe.fileHandleForReading.readDataToEndOfFile()
                    let msg = String(data: data, encoding: .utf8) ?? "Unknown error"
                    continuation.resume(throwing: NSError(
                        domain: "ZipExtract",
                        code: Int(proc.terminationStatus),
                        userInfo: [NSLocalizedDescriptionKey: msg]
                    ))
                }
            }
        }
    }
}

