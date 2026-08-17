//
//  FileDeletion.swift
//  hyperwhisper
//
//  Batch file deletion that never runs on the caller's actor.
//
//  `fileExists`, `attributesOfItem` and `removeItem` are blocking filesystem
//  syscalls. Run inline from a `@MainActor` type they put a whole batch's worth
//  of `stat`/`unlink` on the main thread, which hangs the app when the backlog is
//  large or the recordings folder lives on iCloud Drive or a network volume
//  (HYPERWHISPER-HF: "App hanging for at least 10000 ms", caught at
//  `FileManager.fileExists` -> `stat`).
//
//  This lives in Utilities rather than on any one service because there is
//  nothing feature-specific about it: it is a pure `[String] -> [FileDeletionResult]`
//  function. `AutoDeleteCleanupService` is the first caller; the same hang exists
//  on other user-facing delete paths, which can now reach the same helper.
//

import Foundation

// MARK: - Result

/// The outcome of trying to delete one file.
///
/// A plain `Sendable` value type, so a deletion batch can run off the caller's
/// actor and hand its results back with nothing managed — and nothing
/// actor-isolated — crossing the boundary.
///
/// It is declared at file scope because it is a cross-cutting value type in its
/// own right, not an implementation detail of any single caller. (Nesting it
/// would be fine on isolation grounds: a nested type does NOT inherit its
/// enclosing type's global-actor isolation, and returning an isolated type from
/// a `nonisolated` function is legal anyway.)
struct FileDeletionResult: Sendable {
    /// Whether the file existed and was removed.
    let deleted: Bool
    /// Size of the removed file in bytes; `0` when nothing was deleted.
    let bytesFreed: Int64
    /// Localized description of the failure, or `nil` when the file was deleted
    /// or simply wasn't there. The caller logs it: a `Logger` is very often
    /// actor-isolated state, so the message travels back rather than being
    /// emitted in place.
    let failureDescription: String?
}

// MARK: - Deletion

/// Namespace for off-actor filesystem deletion.
enum FileDeletion {

    /// Deletes each file in `paths` that exists, off the caller's actor.
    ///
    /// **Why detached:** being `async` on a non-isolated type is what takes the
    /// work off the caller's actor; the `Task.detached` on top pins it to a fixed
    /// `.userInitiated` priority instead of inheriting the caller's, matching the
    /// established shape in
    /// `CrashRecoveryManager.scanForUnclaimedWAVCandidates(in:)`.
    ///
    /// One detached task covers the whole batch: the hop is the expensive part,
    /// and one task per file would reintroduce per-file overhead for no gain.
    ///
    /// Taking plain `[String]` is structural, not stylistic — it is what keeps
    /// managed objects, contexts and `self` out of the closure.
    ///
    /// - Parameter paths: The file paths to delete, in order. Duplicates are
    ///   preserved and processed sequentially, so a repeated path deletes on its
    ///   first appearance and reports "not found" on the next. Callers that count
    ///   bytes freed depend on this — de-duplicating here would change their stats.
    /// - Returns: One result per input path, index-aligned with `paths`.
    static func deleteFiles(at paths: [String]) async -> [FileDeletionResult] {
        await Task.detached(priority: .userInitiated) { () -> [FileDeletionResult] in
            let fileManager = FileManager.default
            var results: [FileDeletionResult] = []
            results.reserveCapacity(paths.count)

            for path in paths {
                guard fileManager.fileExists(atPath: path) else {
                    results.append(FileDeletionResult(deleted: false, bytesFreed: 0, failureDescription: nil))
                    continue
                }

                // Get file size before deletion
                var fileSize: Int64 = 0
                if let attrs = try? fileManager.attributesOfItem(atPath: path),
                   let size = attrs[.size] as? Int64 {
                    fileSize = size
                }

                do {
                    try fileManager.removeItem(atPath: path)
                    results.append(FileDeletionResult(deleted: true, bytesFreed: fileSize, failureDescription: nil))
                } catch {
                    results.append(FileDeletionResult(deleted: false, bytesFreed: 0, failureDescription: error.localizedDescription))
                }
            }

            return results
        }.value
    }
}
