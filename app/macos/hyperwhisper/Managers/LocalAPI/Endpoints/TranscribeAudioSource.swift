//
//  TranscribeAudioSource.swift
//  hyperwhisper
//
//  Where `POST /transcribe`'s audio comes from: `file` (allow-listed to the
//  recordings folder, opened by an `O_NOFOLLOW` descriptor walk and staged in
//  a private temp folder) or `audio_base64`. Split out of `TranscribeEndpoint`
//  (issue #733) and moved unchanged; `resolveAudioSource(req:)` is the entry
//  point, and `TranscribeEndpoint` keeps one-line forwarders for the members
//  callers and tests name.
//

import Foundation
import Darwin

enum TranscribeAudioSource {

    typealias APIInputError = TranscribeEndpoint.APIInputError

    struct AudioResolution {
        let url: URL
        let cleanup: () -> Void
    }

    private struct StagedAudioFile {
        let fileURL: URL
        let directoryURL: URL
    }

    struct AllowListedAudioPath {
        let resolvedURL: URL
        let rootURL: URL
        let rootIdentity: FileIdentity
    }

    private struct ValidatedAudioFilePath {
        let allowListedPath: AllowListedAudioPath
        let fileIdentity: FileIdentity
    }

    /// Which inode a descriptor is bound to. Not `private`, because
    /// `fileStatus(forFileDescriptor:)` returns it and that is called from
    /// `LocalAPIFilePathCapTests`.
    struct FileIdentity: Equatable {
        let device: UInt64
        let inode: UInt64
    }

    /// Everything one `fstat` on an open descriptor tells this endpoint.
    ///
    /// The identity is what the TOCTOU walk compares; the size is what the
    /// upload cap compares. They come out of the same `struct stat` on purpose —
    /// see `fileStatus(forFileDescriptor:)`.
    struct FileStatus: Equatable {
        let identity: FileIdentity
        let size: UInt64
    }

    /// The "audio is too big" input error, with its wording read off Rust.
    ///
    /// An `APIInputError` rather than an `HTTPResponse` because the two size
    /// checks live inside `resolveAudioSource`, which is synchronous and
    /// throwing; `handle` catches `APIInputError` and renders it through
    /// `LocalAPIResponder.failure(code:message:hint:)`. That is HTTP 200 carrying
    /// `INVALID_REQUEST` — the same envelope `LocalAPIBodyLimit` produces for an
    /// over-cap request, and the same bytes the .NET head sends
    /// (`PortableLocalApi.cs:193`/`:245`/`:251`).
    private static func uploadTooLargeError() -> APIInputError {
        let failure = localApiUploadTooLargeFailure()
        return APIInputError(
            code: LocalAPIErrorCode(shared: failure.code),
            message: failure.message,
            hint: failure.hint
        )
    }

    /// The refusal `count` bytes of audio earns, or `nil` when it is within the
    /// shared upload cap.
    ///
    /// # Why this is a function rather than an `if`
    ///
    /// This *is* the cap: which limit, which direction, which error. Its two
    /// call sites cannot be driven from a test — `stageValidatedAudioFile` takes
    /// a `ValidatedAudioFilePath` only the `openat` walk can mint, and
    /// `resolveAudioSource` is reached through a FlyingFox `handle` this test
    /// target links nothing for. Reading the production text back instead proves
    /// only that `localApiMaxUploadBytes()` is *mentioned*: flip the `>` to `<`
    /// and every such assertion still holds while macOS refuses every file under
    /// the cap. So the decision is lifted out to somewhere a test can call it
    /// with 0, cap, and cap + 1 — the same split `LocalAPIBodyLimit` makes for
    /// `drain` and `reservation(declared:received:)`, for the same reason.
    ///
    /// The comparison is `>`, so exactly the cap is accepted: the comparison
    /// `PortableLocalApi.cs:340` makes, and the reason one caller gets one answer
    /// from every head instead of two.
    ///
    /// - Parameter count: bytes of audio. For a `file`, `st_size` from the
    ///   `fstat` that already proved the descriptor's identity — never a fresh
    ///   stat of the caller's path. For `audio_base64`, the decoded length.
    static func uploadCapRefusal(forAudioBytes count: UInt64) -> APIInputError? {
        guard count > localApiMaxUploadBytes() else { return nil }
        return Self.uploadTooLargeError()
    }

    /// The answer to a `file` the probe found unreadable, or `nil` when it is
    /// readable (issue #1551; codes per Ray, inbox #358).
    ///
    /// Only `.privacyBlocked` (EPERM) carries the Full Disk Access hint: that
    /// is the one denial Full Disk Access can lift. A POSIX EACCES is the
    /// file's or a folder's mode, and the hint sent users to the wrong fix.
    ///
    /// A function rather than inline `throw`s so a test can call it for each
    /// case, the same split `uploadCapRefusal(forAudioBytes:)` makes.
    static func fileInputRefusal(for access: SourceFileAccess, displayPath: String) -> APIInputError? {
        switch access {
        case .readable:
            return nil
        case .missing:
            return APIInputError(
                code: .fileNotFound,
                message: "Audio file not found: \(displayPath)",
                hint: "Pass an absolute path the running app can read."
            )
        case .isFolder:
            // INVALID_REQUEST, not a new code: the error enum is a closed wire
            // contract, and the request is what is wrong, not the disk.
            return APIInputError(
                code: .invalidRequest,
                message: "\(displayPath) is a folder, not an audio file",
                hint: "Pass the path of an audio file."
            )
        case .folderInPathDenied:
            return APIInputError(
                code: .fileAccessDenied,
                message: "Permission denied on a folder in the path: \(displayPath)",
                hint: "Check the permissions of the folders that hold the file."
            )
        case .fileDenied:
            return APIInputError(
                code: .fileAccessDenied,
                message: "Permission denied: cannot read \(displayPath)",
                hint: "Check the file's permissions."
            )
        case .privacyBlocked:
            return Self.privacyBlockedError(displayPath: displayPath)
        case .unreadable:
            return APIInputError(
                code: .fileAccessDenied,
                message: "Cannot read \(displayPath)",
                hint: nil
            )
        }
    }

    private static func privacyBlockedError(displayPath: String) -> APIInputError {
        APIInputError(
            code: .fileAccessDenied,
            message: "Cannot read \(displayPath)",
            hint: "macOS may require granting Full Disk Access to HyperWhisper."
        )
    }

    /// The answer when a read fails after the probe passed (the file changed
    /// in between, or staging failed). The Full Disk Access hint only for EPERM.
    private static func cannotReadError(after error: Error, displayPath: String) -> APIInputError {
        let code: Int32?
        if let posix = error as? POSIXError {
            code = posix.code.rawValue
        } else if let underlying = (error as NSError).userInfo[NSUnderlyingErrorKey] as? NSError,
                  underlying.domain == NSPOSIXErrorDomain {
            code = Int32(underlying.code)
        } else {
            code = nil
        }
        if code == EPERM {
            return Self.privacyBlockedError(displayPath: displayPath)
        }
        return APIInputError(
            code: .fileAccessDenied,
            message: "Cannot read \(displayPath)",
            hint: nil
        )
    }

    /// Resolve `file` or `audio_base64` into a concrete file URL on disk.
    /// When base64 is used, writes a temp file and returns a cleanup closure
    /// that deletes it after the transcription run.
    static func resolveAudioSource(req: TranscribeRequest) throws -> AudioResolution {
        let trimmedFile = req.file?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedBase64 = req.audio_base64?.trimmingCharacters(in: .whitespacesAndNewlines)

        let hasFile = trimmedFile?.isEmpty == false
        let hasBase64 = trimmedBase64?.isEmpty == false

        if hasFile && hasBase64 {
            throw APIInputError(
                code: .invalidRequest,
                message: "Pass either 'file' or 'audio_base64', not both",
                hint: nil
            )
        }
        if !hasFile && !hasBase64 {
            throw APIInputError(
                code: .invalidRequest,
                message: "Provide 'file' (absolute path) or 'audio_base64' + 'mime_type'",
                hint: nil
            )
        }

        if let filePath = trimmedFile, hasFile {
            let url = URL(fileURLWithPath: filePath)
            // SECURITY (issue #713): the shipping app is NOT sandboxed, so the
            // process can read any user-readable file (and Full Disk Access content
            // if granted). A Local API token holder (MCP/CLI agent) is trusted to
            // transcribe the user's recordings, not to read arbitrary files — and a
            // cloud engine would upload the raw bytes off-device. Canonicalize the
            // requested path and require it to live inside the recordings folder.
            // Reject anything outside; untrusted callers should send ad-hoc audio
            // via `audio_base64`.
            guard let allowListedPath = try? Self.resolvedURLWithinAllowedRoots(url) else {
                throw APIInputError(
                    code: .fileAccessDenied,
                    message: "File path is outside the directories this API may read",
                    hint: "Use a file inside the recordings folder, or send the audio via 'audio_base64' instead of 'file'."
                )
            }
            // One probe names the cause (issue #1551): missing, a folder, a
            // folder in the path that cannot be entered, the file's own
            // permissions, or a privacy block. One `fileExists` guard used to
            // answer FILE_NOT_FOUND for the first three.
            if let refusal = Self.fileInputRefusal(
                for: SourceFileAccess.probe(path: allowListedPath.resolvedURL.path),
                displayPath: filePath
            ) {
                throw refusal
            }
            let fileIdentity: FileIdentity
            do {
                fileIdentity = try Self.fileIdentity(for: allowListedPath.resolvedURL)
            } catch {
                throw Self.cannotReadError(after: error, displayPath: filePath)
            }
            // TOCTOU hardening (issue #713): the allow-list check above runs now,
            // but providers re-open the file by PATH much later (cloud providers do
            // `Data(contentsOf: audioURL)` only after async provider resolution +
            // network setup). An attacker who can write into the recordings folder
            // could pass a real file that validates, then swap that path for a
            // symlink to a sensitive file before the provider opens it — re-resolving
            // the path here can't help because the swap happens AFTER validation.
            // Close the window by opening the validated file through a no-symlink
            // descriptor walk from the recordings root, then staging those bytes in
            // a private per-request temp directory. Providers receive a normal file
            // path for retry compatibility, never the caller-controlled path.
            let validatedPath = ValidatedAudioFilePath(
                allowListedPath: allowListedPath,
                fileIdentity: fileIdentity
            )
            // SIZE (issue #375): the `file` path IS capped at
            // `localApiMaxUploadBytes()`, like the `audio_base64` branch below.
            // That is parity with `PortableLocalApi.ReadAllowedFileAsync`
            // (`PortableLocalApi.cs:340-341`), which answers `Failure(200,
            // INVALID_REQUEST, "Audio exceeds the configured upload limit.")`
            // for the same file; the open question #405 left — cap macOS, or
            // lift the .NET cap? — was resolved as "cap macOS".
            //
            // The honest caveat: .NET buffers the file whole (`new
            // MemoryStream((int)input.Length)`, `PortableLocalApi.cs:342`) so
            // there the cap is a memory guard, while `stageValidatedAudioFile`
            // streams through a 1 MiB window and never holds the recording.
            // This cap buys no memory back on macOS. It exists so the same
            // request gets the same answer from every head, nothing else.
            //
            // The decision itself is `uploadCapRefusal(forAudioBytes:)` — a
            // function so a test can call it rather than read it — applied in
            // `stageValidatedAudioFile` to the size the validated descriptor
            // reported. See the notes on both.
            //
            // .NET also refuses an empty file first (`input.Length <= 0` → 400
            // "Audio must not be empty."); macOS still has no equivalent, and
            // that stays a separate wire change rather than a rider on the cap.
            let stagedFile: StagedAudioFile
            do {
                stagedFile = try Self.stageValidatedAudioFile(
                    validatedPath,
                    fileExtension: allowListedPath.resolvedURL.pathExtension.isEmpty ? "wav" : allowListedPath.resolvedURL.pathExtension
                )
            } catch let inputError as APIInputError {
                // The over-cap refusal is already the answer to send. Letting it
                // fall into the arm below would flatten a 200 + INVALID_REQUEST
                // into FILE_ACCESS_DENIED and tell the caller to check
                // permissions on a file they can read perfectly well.
                throw inputError
            } catch {
                throw Self.cannotReadError(after: error, displayPath: filePath)
            }
            return AudioResolution(url: stagedFile.fileURL, cleanup: {
                try? FileManager.default.removeItem(at: stagedFile.directoryURL)
            })
        }

        // base64 path
        guard let raw = trimmedBase64 else {
            throw APIInputError(
                code: .audioDecodeFailed,
                message: "'audio_base64' is not valid base64",
                hint: nil
            )
        }
        // SIZE (issue #375), before the decode: `Data(base64Encoded:)` allocates
        // the decoded buffer, so checking the decoded size alone would still let
        // a caller pay for the allocation first. The ceiling is the encoded
        // length that `localApiMaxUploadBytes()` expands to, exactly as
        // `PortableLocalApi.cs:243-245` computes it.
        //
        // Honest scope: at the shipped defaults this cannot fire on this head.
        // 48 MiB of audio expands to a ~64 MiB base64 string, which is already
        // over the 50 MiB request cap `LocalAPIBodyLimit` enforces first, so the
        // request guard always wins. It is defence-in-depth for a head with a
        // looser request cap (and for the multipart `audio` part the .NET head
        // has), not the fix for the reported repro.
        if UInt64(raw.utf8.count) > localApiMaxBase64LengthForUpload() {
            throw Self.uploadTooLargeError()
        }
        guard let data = Data(base64Encoded: raw, options: [.ignoreUnknownCharacters]) else {
            throw APIInputError(
                code: .audioDecodeFailed,
                message: "'audio_base64' is not valid base64",
                hint: nil
            )
        }
        // And the decoded bytes, the comparison `PortableLocalApi.cs:251` makes.
        // Also unreachable today — an encoded string under the ceiling above
        // cannot decode to more than the upload cap, by construction — but it is
        // the check that binds if either cap is ever loosened independently, and
        // it is the one that would still be here if the decode moved.
        if let refusal = Self.uploadCapRefusal(forAudioBytes: UInt64(data.count)) {
            throw refusal
        }
        let ext = Self.extensionForMime(req.mime_type)
        let stagedFile: StagedAudioFile
        do {
            stagedFile = try Self.stageAudioData(data, fileExtension: ext)
        } catch {
            throw APIInputError(
                code: .audioDecodeFailed,
                message: "Failed to write decoded audio to temp file: \(error.localizedDescription)",
                hint: nil
            )
        }
        return AudioResolution(url: stagedFile.fileURL, cleanup: {
            try? FileManager.default.removeItem(at: stagedFile.directoryURL)
        })
    }

    private static func extensionForMime(_ mime: String?) -> String {
        guard let mime = mime?.lowercased() else { return "wav" }
        switch mime {
        case "audio/wav", "audio/x-wav", "audio/wave": return "wav"
        case "audio/m4a", "audio/x-m4a", "audio/mp4": return "m4a"
        case "audio/mpeg", "audio/mp3": return "mp3"
        case "audio/flac", "audio/x-flac": return "flac"
        case "audio/ogg", "audio/x-ogg", "audio/vorbis": return "ogg"
        case "audio/webm": return "webm"
        case "audio/aac": return "aac"
        default: return "wav"
        }
    }

    private static func stageValidatedAudioFile(_ source: ValidatedAudioFilePath, fileExtension ext: String) throws -> StagedAudioFile {
        let stagedFile = try Self.makePrivateStagedAudioFile(fileExtension: ext)
        do {
            let opened = try Self.openValidatedAudioFile(source)
            let input = opened.handle
            defer { try? input.close() }

            // SIZE (issue #375): `opened.size` is the `st_size` from the very
            // `fstat` that proved this descriptor is the inode validation
            // approved. It is not a second stat, and above all not a fresh
            // `FileManager` stat of the caller's path — statting the path again
            // would reopen the swap window the `O_NOFOLLOW` walk closes, and
            // could measure a file other than the one about to be staged.
            // Applied before the copy loop, so an over-cap file costs no I/O.
            if let refusal = Self.uploadCapRefusal(forAudioBytes: opened.size) {
                throw refusal
            }

            guard FileManager.default.createFile(
                atPath: stagedFile.fileURL.path,
                contents: nil,
                attributes: [.posixPermissions: 0o600]
            ) else {
                throw POSIXError(.EEXIST)
            }

            let output = try FileHandle(forWritingTo: stagedFile.fileURL)
            defer { try? output.close() }

            while let chunk = try input.read(upToCount: 1024 * 1024), !chunk.isEmpty {
                try output.write(contentsOf: chunk)
            }
            return stagedFile
        } catch {
            try? FileManager.default.removeItem(at: stagedFile.directoryURL)
            throw error
        }
    }

    private static func stageAudioData(_ data: Data, fileExtension ext: String) throws -> StagedAudioFile {
        let stagedFile = try Self.makePrivateStagedAudioFile(fileExtension: ext)
        do {
            try data.write(to: stagedFile.fileURL, options: .atomic)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: stagedFile.fileURL.path
            )
            return stagedFile
        } catch {
            try? FileManager.default.removeItem(at: stagedFile.directoryURL)
            throw error
        }
    }

    /// The folder name carries this process's pid so a later launch can tell a
    /// folder a dead request left (issue #1484) from one another running copy
    /// of the app is still using. `LocalAPIStagingSweep` owns the format.
    private static func makePrivateStagedAudioFile(fileExtension ext: String) throws -> StagedAudioFile {
        let directoryURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(LocalAPIStagingSweep.directoryName(), isDirectory: true)
        try FileManager.default.createDirectory(
            at: directoryURL,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700]
        )
        let fileURL = directoryURL
            .appendingPathComponent("audio")
            .appendingPathExtension(ext)
        return StagedAudioFile(fileURL: fileURL, directoryURL: directoryURL)
    }

    /// A validated audio file, opened, with the size the walk's own `fstat`
    /// reported.
    ///
    /// The size travels with the handle so the upload cap needs no `fstat` of
    /// its own: the walk already has to stat the final descriptor to compare its
    /// identity, and `st_size` is in that same `struct stat`. Two stats would be
    /// a wasted syscall on every `file` request and a second place for the two
    /// answers to drift apart.
    private struct OpenedAudioFile {
        let handle: FileHandle
        let size: UInt64
    }

    /// Open `source` by walking from the validated recordings root with `openat`
    /// and `O_NOFOLLOW`, returning a descriptor-backed handle and its size.
    ///
    /// SECURITY (issue #713 — TOCTOU): `O_NOFOLLOW` on the final file is not
    /// enough because a writable ancestor directory could be swapped for a symlink
    /// after validation. Walking every directory component with `openat(...,
    /// O_DIRECTORY | O_NOFOLLOW)` and comparing file identities prevents root,
    /// ancestor, and final-file replacement from redirecting the provider's read
    /// outside the validated recordings root.
    private static func openValidatedAudioFile(_ source: ValidatedAudioFilePath) throws -> OpenedAudioFile {
        let rootFD = try Self.openDirectoryRefusingSymlinks(at: source.allowListedPath.rootURL)
        defer { close(rootFD) }

        guard try Self.fileStatus(forFileDescriptor: rootFD).identity == source.allowListedPath.rootIdentity else {
            throw POSIXError(.ESTALE)
        }

        let components = Self.relativePathComponents(
            for: source.allowListedPath.resolvedURL,
            inside: source.allowListedPath.rootURL
        )
        guard let fileName = components.last, !fileName.isEmpty else {
            throw POSIXError(.EISDIR)
        }

        var directoryFD = rootFD
        var openedDirectoryFDs: [CInt] = []
        defer {
            for fd in openedDirectoryFDs.reversed() {
                close(fd)
            }
        }

        for component in components.dropLast() {
            guard Self.isSafeRelativePathComponent(component) else {
                throw POSIXError(.EINVAL)
            }
            let nextFD = openat(directoryFD, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard nextFD >= 0 else {
                throw Self.currentPOSIXError()
            }
            openedDirectoryFDs.append(nextFD)
            directoryFD = nextFD
        }

        guard Self.isSafeRelativePathComponent(fileName) else {
            throw POSIXError(.EINVAL)
        }
        let fd = openat(directoryFD, fileName, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else {
            throw Self.currentPOSIXError()
        }
        // Not a `do`/`catch` around the identity check: `close(fd)` followed by
        // `throw` inside a `do` lands in that `catch`, which closes `fd` a second
        // time — and by then the number can name a descriptor another thread has
        // just opened. Each exit closes exactly once.
        let status: FileStatus
        do {
            status = try Self.fileStatus(forFileDescriptor: fd)
        } catch {
            close(fd)
            throw error
        }
        guard status.identity == source.fileIdentity else {
            close(fd)
            throw POSIXError(.ESTALE)
        }
        return OpenedAudioFile(
            handle: FileHandle(fileDescriptor: fd, closeOnDealloc: true),
            size: status.size
        )
    }

    private static func openDirectoryRefusingSymlinks(at url: URL) throws -> CInt {
        let components = url.standardizedFileURL.pathComponents
        guard components.first == "/" else {
            throw POSIXError(.EINVAL)
        }

        let rootFD = open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        guard rootFD >= 0 else {
            throw Self.currentPOSIXError()
        }

        var openedFDs: [CInt] = [rootFD]
        do {
            var directoryFD = rootFD
            for component in components.dropFirst() {
                guard Self.isSafeRelativePathComponent(component) else {
                    throw POSIXError(.EINVAL)
                }
                let nextFD = openat(directoryFD, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                guard nextFD >= 0 else {
                    throw Self.currentPOSIXError()
                }
                openedFDs.append(nextFD)
                directoryFD = nextFD
            }

            let finalFD = openedFDs.removeLast()
            for fd in openedFDs.reversed() {
                close(fd)
            }
            return finalFD
        } catch {
            for fd in openedFDs.reversed() {
                close(fd)
            }
            throw error
        }
    }

    private static func currentPOSIXError() -> POSIXError {
        POSIXError(POSIXErrorCode(rawValue: errno) ?? .EACCES)
    }

    private static func fileIdentity(for url: URL) throws -> FileIdentity {
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let device = attrs[.systemNumber] as? NSNumber,
              let inode = attrs[.systemFileNumber] as? NSNumber else {
            throw CocoaError(.fileReadUnknown)
        }
        return FileIdentity(device: device.uint64Value, inode: inode.uint64Value)
    }

    /// One `fstat` on an already-open descriptor: the identity the TOCTOU walk
    /// compares, and the size the upload cap compares.
    ///
    /// Both answers come out of the same `struct stat` deliberately. The walk
    /// has to stat the final descriptor anyway to prove it is the inode
    /// validation approved, and `st_size` is sitting in that struct — a second
    /// `fstat` for the size would be a wasted syscall on every `file` request,
    /// two near-identical helpers to keep in step, and a window (however small)
    /// for the identity and the size to describe different states of the file.
    ///
    /// `st_size` is a signed `off_t`, so it is clamped rather than converted: a
    /// negative value would trap the `UInt64` initializer, and a trap on a
    /// network-facing path is a process abort. Same reasoning
    /// `LocalAPIBodyLimit`'s counter gives for saturating instead of overflowing.
    ///
    /// Not `private`: `LocalAPIFilePathCapTests` calls it on a real descriptor.
    static func fileStatus(forFileDescriptor fd: CInt) throws -> FileStatus {
        var statInfo = stat()
        guard fstat(fd, &statInfo) == 0 else {
            throw Self.currentPOSIXError()
        }
        return FileStatus(
            identity: FileIdentity(device: UInt64(statInfo.st_dev), inode: UInt64(statInfo.st_ino)),
            size: UInt64(max(0, Int64(statInfo.st_size)))
        )
    }

    private static func relativePathComponents(for url: URL, inside root: URL) -> [String] {
        Array(url.pathComponents.dropFirst(root.pathComponents.count))
    }

    private static func isSafeRelativePathComponent(_ component: String) -> Bool {
        !component.isEmpty && component != "." && component != ".." && !component.contains("/")
    }

    // MARK: - Path allow-list (issue #713)

    /// Canonicalize `candidate` and verify it is contained within the single
    /// directory the Local API is allowed to read: the user's recordings folder.
    /// Both the candidate and the root are resolved through symlinks and
    /// standardized so that `..` traversal and symlink escapes can't smuggle a
    /// path outside the root.
    ///
    /// Returns the canonicalized (symlink-resolved, standardized) URL when the
    /// candidate is inside the allowed root, or `nil` otherwise. The returned URL
    /// is only the value the caller validates and immediately copies through a
    /// descriptor-backed read; it is never handed to the provider directly.
    static func resolvedURLWithinAllowedRoots(_ candidate: URL) throws -> AllowListedAudioPath? {
        let resolvedCandidate = candidate.resolvingSymlinksInPath().standardizedFileURL
        for root in allowedRoots() {
            if isURL(resolvedCandidate, containedIn: root) {
                return AllowListedAudioPath(
                    resolvedURL: resolvedCandidate,
                    rootURL: root,
                    rootIdentity: try Self.fileIdentity(for: root)
                )
            }
        }
        return nil
    }

    /// The single directory the `file` parameter may reference: the user's
    /// recordings folder. Read directly from the persisted default so this stays
    /// correct even if the user moved their recordings folder (the default points
    /// at the active recordings subdirectory in every fallback tier too). The root
    /// is symlink-resolved + standardized.
    ///
    /// SECURITY (issue #713): deliberately NARROW. The app data folder
    /// (~/Library/Application Support/HyperWhisper) is NOT an allowed root — it
    /// holds the Local API bearer token (`local-api.json`), logs, and models, so a
    /// token holder must not be able to read those back through `file`. The system
    /// temp dir is also NOT a root: the `audio_base64` branch returns its own
    /// app-written temp file directly without consulting this allow-list, so temp
    /// never needs to be reachable via the `file` parameter.
    private static func allowedRoots() -> [URL] {
        let defaults = UserDefaults.standard
        let recordingsPath: String
        if let recordings = defaults.string(forKey: "recordingsFolder"), !recordings.isEmpty {
            recordingsPath = recordings
        } else {
            let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            recordingsPath = (documents?.appendingPathComponent("hyperwhisper/recordings").path) ?? ""
        }
        guard !recordingsPath.isEmpty else { return [] }
        return [URL(fileURLWithPath: recordingsPath).resolvingSymlinksInPath().standardizedFileURL]
    }

    /// True when `url` is `root` itself or a descendant of `root`, compared on
    /// path components so a sibling like `/a/recordings-evil` can't match
    /// `/a/recordings`.
    private static func isURL(_ url: URL, containedIn root: URL) -> Bool {
        let rootComponents = root.pathComponents
        let urlComponents = url.pathComponents
        guard urlComponents.count >= rootComponents.count else { return false }
        return Array(urlComponents.prefix(rootComponents.count)) == rootComponents
    }
}
