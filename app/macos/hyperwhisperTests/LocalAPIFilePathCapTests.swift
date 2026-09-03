//
//  LocalAPIFilePathCapTests.swift
//  hyperwhisperTests
//
//  The `file` upload cap, and the four decisions that make it a cap rather than
//  a line of code that happens to mention a number.
//
//  PR #405 shipped the request cap and left the `file` path uncapped, so a
//  60 MiB recording transcribed on macOS and was refused by the .NET head
//  (`PortableLocalApi.cs:340-341`). That divergence was raised as an open
//  question and resolved as "cap macOS". This file pins the resolution.
//
//  `resolveAudioSource` and `stageValidatedAudioFile` are `private static` on
//  `TranscribeEndpoint`, and `handle` needs a FlyingFox `HTTPResponse` and a
//  live `TranscriptionPipeline` — this target links no FlyingFox and boots no
//  Core Data stack, so neither can be called here. So these read the production
//  source off disk, the same `#filePath`-relative trick the last four tests in
//  `LocalAPIBodyLimitTests` use for the wiring they cannot call either.
//  Scraping Swift text is cruder than calling the function; it is what makes
//  these assertions fail when the production line changes.
//
//  The wire values themselves — that `localApiUploadTooLargeFailure()` is
//  HTTP 200 carrying `INVALID_REQUEST` — are pinned in `LocalAPIContractTests`.
//  What is pinned here is that the `file` branch reaches for *that* failure.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalAPIFilePathCapTests {

    // MARK: - The cap itself

    /// The `file` branch is bounded by the shared upload cap.
    ///
    /// This is the whole change. Without it macOS answers a 60 MiB `file` with
    /// a transcript and Linux answers the identical request with
    /// `INVALID_REQUEST`, which is the per-head divergence `hw-localapi::limits`
    /// exists to prevent. It must be the *upload* cap: the request cap
    /// (52,428,800) bounds a whole HTTP body and never touches a path read off
    /// disk, so wiring that number here would cap the file 2 MiB too high.
    @Test func theFilePathIsCappedAtTheSharedUploadCap() throws {
        let stage = try Self.stageValidatedAudioFileBody()

        #expect(stage.contains("localApiMaxUploadBytes()"),
                "staging a `file` must compare its size against the shared upload cap")
        #expect(!stage.contains("localApiMaxRequestBytes()"),
                "the request cap bounds an HTTP body, not a file on disk — wrong number here")

        // And the `file` branch is what reaches staging, so the cap above is on
        // the path a `file` request actually takes.
        let fileBranch = try Self.fileBranchOfResolveAudioSource()
        #expect(fileBranch.contains("Self.stageValidatedAudioFile("),
                "the `file` branch must stage through the function that carries the cap")
    }

    /// The refusal is the shared business failure, not a 400 and not a new one.
    ///
    /// `LocalAPIResponder.failure(code:message:hint:)` always sends HTTP 200, so
    /// the choice that decides the wire answer is *which error value* the branch
    /// throws. `uploadTooLargeError()` reads its code, message and hint straight
    /// off `localApiUploadTooLargeFailure()` — the same bytes .NET sends at
    /// `PortableLocalApi.cs:341`. Throwing an ad-hoc `APIInputError`, or routing
    /// through `badRequest`, would give the same caller a different answer per
    /// head while every other test in this file still passed.
    @Test func theOverCapFileIsRefusedThroughTheSharedTooLargeError() throws {
        let stage = try Self.stageValidatedAudioFileBody()

        #expect(stage.contains("Self.uploadTooLargeError()"),
                "an over-cap `file` must throw the shared too-large error the base64 branch throws")
        #expect(!stage.contains("badRequest"),
                "an oversized upload is a business failure, not a 400")

        // The factory it reaches for is defined in terms of the shared failure,
        // not a hand-written copy of the .NET wording.
        let factory = try Self.uploadTooLargeErrorBody()
        #expect(factory.contains("localApiUploadTooLargeFailure()"),
                "uploadTooLargeError() must read its wire values off shared core")

        // Which is HTTP 200 + INVALID_REQUEST with the .NET message, verbatim.
        let failure = localApiUploadTooLargeFailure()
        #expect(failure.httpStatus == 200)
        #expect(LocalAPIErrorCode(shared: failure.code) == .invalidRequest)
        #expect(failure.message == "Audio exceeds the configured upload limit.")
    }

    /// No `413`, and no `PAYLOAD_TOO_LARGE`, anywhere in the endpoint.
    ///
    /// The obvious way to write "the file is too big" is the HTTP status that
    /// means it. `LocalAPIErrorCode` is a closed 14-case `Codable` enum and
    /// `LocalAPIContractTests.theOutOfEnumCodesStayOutOfTheEnum` requires
    /// `PAYLOAD_TOO_LARGE` to stay undecodable — a client sharing this decoder
    /// would fail to decode the *entire* envelope, not just the code. So this is
    /// the guard on the tempting wrong answer.
    @Test func theRefusalIntroducesNoOutOfEnumCode() throws {
        let source = try Self.endpointCode()

        #expect(!source.contains("PAYLOAD_TOO_LARGE"),
                "PAYLOAD_TOO_LARGE is outside the closed enum and breaks a sharing client's decoder")
        #expect(!source.contains("payloadTooLarge"),
                "the size refusal is INVALID_REQUEST on every head")
        #expect(!source.contains("413"),
                "a size refusal is HTTP 200 with a business code, never a 413")
    }

    // MARK: - Where the size is read (issue #713 — TOCTOU)

    /// The size comes off the validated descriptor, never a second look at the
    /// caller's path.
    ///
    /// This branch spends `openValidatedAudioFile` — an `openat` walk with
    /// `O_DIRECTORY | O_NOFOLLOW` at every component plus a device/inode
    /// identity check — closing the window where an attacker who can write into
    /// the recordings folder swaps a validated file for a symlink. A
    /// `FileManager.attributesOfItem(atPath:)` or `URL.resourceValues` call to
    /// get the size reopens exactly that window: it resolves the path a second
    /// time, so it can measure a *different* file from the one being staged —
    /// a small decoy passes the cap while the real bytes copied are whatever the
    /// path points at by then. `fstat` on the open descriptor cannot be aimed
    /// somewhere else, because the descriptor is already bound to the inode.
    @Test func theSizeIsReadFromTheAlreadyValidatedDescriptor() throws {
        let stage = try Self.stageValidatedAudioFileBody()

        #expect(stage.contains("input.fileDescriptor"),
                "the size must be measured on the descriptor openValidatedAudioFile returned")
        #expect(!stage.contains("attributesOfItem"),
                "re-statting the path reopens the symlink-swap window the O_NOFOLLOW walk closes")
        #expect(!stage.contains("resourceValues"),
                "re-resolving the URL reopens the symlink-swap window the O_NOFOLLOW walk closes")

        let helper = try Self.fileSizeHelperBody()
        #expect(helper.contains("fstat(fd"),
                "the size helper must fstat the descriptor, not stat a path")
    }

    /// `st_size` is clamped, not converted.
    ///
    /// `off_t` is signed. `UInt64(someNegativeValue)` is a runtime trap in
    /// Swift, and a trap on a request-handling path is a process abort — the
    /// Local API server dies, not the request. `LocalAPIBodyLimit`'s counter
    /// saturates for the same reason, and `hw-localapi::limits` says so out loud.
    @Test func theSizeConversionCannotTrap() throws {
        let helper = try Self.fileSizeHelperBody()

        #expect(helper.contains("max(0,"),
                "a signed st_size must be clamped before it reaches UInt64, or a negative value aborts the process")
    }

    // MARK: - When the size is read

    /// The cap is checked before the copy, not during it.
    ///
    /// `stageValidatedAudioFile` streams the recording through a 1 MiB window
    /// into a private temp file. Checking inside or after that loop still
    /// returns the right answer, but only after writing most of an over-cap
    /// recording to disk — the refusal would cost real I/O, which is the shape
    /// of amplification #375 was filed about. Checked first, an over-cap `file`
    /// costs one `fstat`.
    @Test func theCapIsCheckedBeforeTheStreamingCopy() throws {
        let stage = try Self.stageValidatedAudioFileBody()

        guard let capCheck = stage.range(of: "localApiMaxUploadBytes()") else {
            throw FilePathCapSourceError.anchorNotFound("localApiMaxUploadBytes()")
        }
        guard let copyLoop = stage.range(of: "input.read(upToCount:") else {
            throw FilePathCapSourceError.anchorNotFound("input.read(upToCount:")
        }

        #expect(capCheck.lowerBound < copyLoop.lowerBound, """
            The cap must be checked before the 1 MiB copy loop. Checked after it, an over-cap \
            recording is fully written to the staging directory before it is refused.
            """)
    }

    // MARK: - The refusal surviving the call site

    /// The `file` branch rethrows the too-large error instead of flattening it.
    ///
    /// The `do`/`catch` around staging existed to turn any staging failure into
    /// `FILE_ACCESS_DENIED` + "Cannot read <path>" + a Full Disk Access hint.
    /// The cap throws through that same call, so without an `APIInputError`
    /// passthrough the caller of a 60 MiB file is told to check permissions on a
    /// file they can read perfectly well, with a code the .NET head never sends
    /// for this case. Every other test in this file passes either way — the bug
    /// lives entirely in the catch.
    @Test func theTooLargeRefusalSurvivesTheStagingCatch() throws {
        let stagingCall = try Self.stagingCallAndItsCatches()

        #expect(stagingCall.contains("catch let inputError as APIInputError"), """
            The staging call must rethrow an APIInputError. Without this arm the upload-cap \
            refusal is rewritten as FILE_ACCESS_DENIED "Cannot read <path>".
            """)
        #expect(stagingCall.contains("throw inputError"),
                "the passthrough arm must rethrow the error it caught, unchanged")

        guard let passthrough = stagingCall.range(of: "catch let inputError as APIInputError"),
              let fallback = stagingCall.range(of: ".fileAccessDenied") else {
            throw FilePathCapSourceError.anchorNotFound("both catch arms around the staging call")
        }
        #expect(passthrough.lowerBound < fallback.lowerBound,
                "the typed passthrough must precede the catch-all, or Swift never reaches it")
    }
}

// MARK: - Production-source fixtures

private enum FilePathCapSourceError: Error, CustomStringConvertible {
    case unreadable(String)
    case anchorNotFound(String)

    var description: String {
        switch self {
        case .unreadable(let path):
            return "Could not read production source at \(path)"
        case .anchorNotFound(let anchor):
            return """
            Could not locate '\(anchor)' in TranscribeEndpoint.swift. It was probably renamed or \
            moved — update the anchor in LocalAPIFilePathCapTests rather than deleting the check.
            """
        }
    }
}

extension LocalAPIFilePathCapTests {

    /// Repo root, derived from this file's own compile-time path.
    fileprivate static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // hyperwhisperTests
            .deletingLastPathComponent()  // macos
            .deletingLastPathComponent()  // app
            .deletingLastPathComponent()  // <repo root>
    }

    /// `TranscribeEndpoint.swift` with every comment line removed.
    ///
    /// Comments go first so a doc comment that *describes* the cap cannot stand
    /// in for the cap — the whole point is to read the code.
    fileprivate static func endpointCode() throws -> String {
        let path = "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"
        let url = repoRoot.appendingPathComponent(path)
        guard let data = try? Data(contentsOf: url) else {
            throw FilePathCapSourceError.unreadable(url.path)
        }
        return String(decoding: data, as: UTF8.self)
            .components(separatedBy: .newlines)
            .filter { !$0.trimmingCharacters(in: .whitespaces).hasPrefix("//") }
            .joined(separator: "\n")
    }

    /// The production source between two anchors, comments already stripped.
    fileprivate static func slice(from opening: String, to closing: String) throws -> String {
        let code = try endpointCode()
        guard let start = code.range(of: opening) else {
            throw FilePathCapSourceError.anchorNotFound(opening)
        }
        let rest = code[start.upperBound...]
        guard let end = rest.range(of: closing) else {
            throw FilePathCapSourceError.anchorNotFound(closing)
        }
        return String(rest[..<end.lowerBound])
    }

    /// The `file` half of `resolveAudioSource`, ending where the base64 half
    /// begins (the `guard let raw = trimmedBase64` that opens it — the `//
    /// base64 path` comment above it is stripped before this runs).
    fileprivate static func fileBranchOfResolveAudioSource() throws -> String {
        try slice(from: "if let filePath = trimmedFile, hasFile {", to: "guard let raw = trimmedBase64")
    }

    /// The staging call inside the `file` branch together with both of its
    /// `catch` arms, ending at the `AudioResolution` the branch returns.
    fileprivate static func stagingCallAndItsCatches() throws -> String {
        try slice(
            from: "stagedFile = try Self.stageValidatedAudioFile(",
            to: "return AudioResolution("
        )
    }

    /// The body of `stageValidatedAudioFile(_:fileExtension:)`, ending at the
    /// next declaration. Anchored on the `func` keyword so the call site above
    /// it in `resolveAudioSource` is not what gets matched.
    fileprivate static func stageValidatedAudioFileBody() throws -> String {
        try slice(
            from: "private static func stageValidatedAudioFile(",
            to: "private static func stageAudioData("
        )
    }

    /// The body of `fileSize(forFileDescriptor:)`, ending at the next
    /// declaration.
    fileprivate static func fileSizeHelperBody() throws -> String {
        try slice(
            from: "private static func fileSize(forFileDescriptor",
            to: "private static func relativePathComponents("
        )
    }

    /// The body of `uploadTooLargeError()`, ending at the next declaration.
    fileprivate static func uploadTooLargeErrorBody() throws -> String {
        try slice(
            from: "private static func uploadTooLargeError()",
            to: "private static func resolveAudioSource("
        )
    }
}
