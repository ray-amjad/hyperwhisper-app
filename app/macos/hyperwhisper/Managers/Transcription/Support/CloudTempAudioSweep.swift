//
//  CloudTempAudioSweep.swift
//  hyperwhisper
//
//  Deletes the temp copies of the user's audio a dead process left in the
//  temp directory from a cloud request (issue #1581, sibling of #1484).
//

import Darwin
import Foundation

/// Names, and later deletes, the temp files the cloud transcription pipeline
/// writes the user's audio into.
///
/// # Why (issue #1581)
///
/// Four temp files hold a copy of the user's audio while a cloud request runs:
/// the multipart request body and the base64 JSON body (`RustHTTPExecutor`),
/// the re-encoded WAV of the format recovery (`CloudAudioFormatRecovery`), and
/// the PCM16 WAV for AssemblyAI Dictation (`AssemblyAIDictationAudio`). Each is
/// deleted only by an in-process `defer`, so a `kill -9`, a crash or a power
/// loss mid-request left it in `$TMPDIR` for good. `sweep()` runs once per
/// launch and removes them.
///
/// # Which files are stale
///
/// The same rule as `LocalAPIStagingSweep`, applied to regular files: a new
/// name carries the pid of the process that made it,
/// `<prefix><pid>-<UUID><suffix>`, and is kept only while that process is
/// alive and started no later than the file was last written. This process's
/// own files are never removed. A pre-fix name (`<prefix><UUID><suffix>`, no
/// pid) is removed at once when no other copy of the app runs, else after
/// `LocalAPIStagingSweep.liveOwnerMaxAge`.
///
/// The app is not sandboxed, so `$TMPDIR` is shared with the user's other
/// apps. The old Dictation name `dictation-<UUID>.wav` is too generic to claim,
/// so the new one is `hw-dictation-…` and a bare `dictation-…` is never swept.
enum CloudTempAudioSweep {

    /// One kind of temp file, by its fixed name parts.
    enum Kind: CaseIterable {
        /// `RustHTTPExecutor`: the whole multipart/form-data body, audio included.
        case multipartBody
        /// `RustHTTPExecutor`: a JSON body with the audio inlined as base64.
        case jsonBase64Body
        /// `CloudAudioFormatRecovery`: the 16 kHz WAV a rejected upload is retried with.
        case reencodedWAV
        /// `AssemblyAIDictationAudio`: the PCM16 WAV Dictation uploads.
        case dictationWAV

        var prefix: String {
            switch self {
            case .multipartBody: return "hw-multipart-"
            case .jsonBase64Body: return "hw-jsonb64-"
            case .reencodedWAV: return "hw-reencode-"
            case .dictationWAV: return "hw-dictation-"
            }
        }

        var suffix: String {
            switch self {
            case .multipartBody, .jsonBase64Body: return ".tmp"
            case .reencodedWAV, .dictationWAV: return ".wav"
            }
        }

        /// Whether a pre-fix build wrote this exact prefix with a bare UUID.
        /// `hw-dictation-` is new, so a bare-UUID one is not ours.
        var hasLegacyName: Bool { self != .dictationWAV }
    }

    // MARK: - Naming

    /// The name of a new temp file: `<prefix><pid>-<UUID><suffix>`.
    static func fileName(_ kind: Kind, pid: pid_t = getpid(), id: UUID = UUID()) -> String {
        "\(kind.prefix)\(pid)-\(id.uuidString)\(kind.suffix)"
    }

    /// A new temp file URL of this kind in the temp directory.
    static func temporaryURL(_ kind: Kind) -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent(fileName(kind))
    }

    /// Parses a temp-directory entry name. Anything but the exact shapes
    /// `fileName` and the pre-fix builds produced is `.notOurs`.
    static func owner(ofEntryNamed name: String) -> LocalAPIStagingSweep.Owner {
        for kind in Kind.allCases {
            guard name.hasPrefix(kind.prefix), name.hasSuffix(kind.suffix),
                  name.count > kind.prefix.count + kind.suffix.count
            else { continue }
            let tag = String(name.dropFirst(kind.prefix.count).dropLast(kind.suffix.count))
            switch LocalAPIStagingSweep.owner(ofTag: tag) {
            case .process(let pid): return .process(pid)
            case .legacy: return kind.hasLegacyName ? .legacy : .notOurs
            case .notOurs: continue
            }
        }
        return .notOurs
    }

    // MARK: - Sweep

    /// Removes every stale cloud temp audio file directly inside `directory`.
    /// Only regular files (never a symlink or a folder) owned by this user.
    ///
    /// - Returns: the files it removed.
    @discardableResult
    static func sweep(
        in directory: URL = FileManager.default.temporaryDirectory,
        now: Date = Date(),
        currentPID: pid_t = getpid(),
        currentUID: uid_t = getuid(),
        isProcessAlive: (pid_t) -> Bool = LocalAPIStagingSweep.isProcessAlive,
        processStartTime: (pid_t) -> Date? = LocalAPIStagingSweep.processStartTime,
        anotherCopyIsRunning: () -> Bool = LocalAPIStagingSweep.anotherCopyIsRunning,
        fileManager: FileManager = .default
    ) -> [URL] {
        LocalAPIStagingSweep.removeStaleEntries(
            in: directory,
            ofType: .typeRegular,
            ownerOf: owner(ofEntryNamed:),
            label: "Cloud temp audio sweep",
            now: now,
            currentPID: currentPID,
            currentUID: currentUID,
            isProcessAlive: isProcessAlive,
            processStartTime: processStartTime,
            anotherCopyIsRunning: anotherCopyIsRunning,
            fileManager: fileManager
        )
    }
}
