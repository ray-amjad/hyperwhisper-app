//
//  LocalAPIFilePathCapTests.swift
//  hyperwhisperTests
//
//  The `file` upload cap, and the decisions that make it a cap rather than a
//  line of code that happens to mention a number.
//
//  PR #405 shipped the request cap and left the `file` path uncapped, so a
//  60 MiB recording transcribed on macOS and was refused by the .NET head
//  (`PortableLocalApi.cs:340-341`). That divergence was raised as an open
//  question and resolved as "cap macOS". This file pins the resolution.
//
//  # Why these call the production code
//
//  The first version of this file asserted entirely by substring-matching the
//  production source. That form cannot see which way a comparison points: turn
//  `> localApiMaxUploadBytes()` into `< localApiMaxUploadBytes()` and every one
//  of those assertions still held — the slice still mentioned the cap, still
//  mentioned the shared error, still preceded the copy loop — while macOS
//  refused every file *under* 48 MiB and accepted every file over it.
//
//  So the decision was lifted into `TranscribeEndpoint.uploadCapRefusal(forAudioBytes:)`,
//  and the size into `TranscribeEndpoint.fileStatus(forFileDescriptor:)`. Both
//  are called below, at 0, cap - 1, cap and cap + 1, and over real descriptors.
//  That is the same split `LocalAPIBodyLimit` made for `drain` and
//  `reservation(declared:received:)` in #405, for exactly this reason.
//
//  What is left to read out of the source is wiring that nothing in this target
//  can call: `stageValidatedAudioFile` takes a `ValidatedAudioFilePath` only the
//  `openat` walk can mint, `resolveAudioSource` is `private static`, and `handle`
//  needs a FlyingFox `HTTPResponse` and a live `TranscriptionPipeline`. Those few
//  checks use the shared `ProductionSource` fixture.
//

import Darwin
import Foundation
import Testing
@testable import HyperWhisper

struct LocalAPIFilePathCapTests {

    // MARK: - The decision itself, called

    /// The cap is the upload cap, and the comparison points at oversized files.
    ///
    /// This is the whole change, and the assertion the source-scraping version
    /// of this file could not make. An inverted comparison refuses every file
    /// *under* 48 MiB — the exact opposite of the feature — so the table below
    /// walks both sides of the boundary rather than the refusing side alone.
    ///
    /// It must be the *upload* cap. The request cap (52,428,800) bounds a whole
    /// HTTP body and never touches a path read off disk, so wiring that number
    /// here would cap the file 2 MiB too high; the last assertion is the one
    /// that would catch it.
    @Test func theCapRefusesOversizedFilesAndOnlyOversizedFiles() {
        let cap = localApiMaxUploadBytes()

        // Under, including the degenerate end.
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: 0) == nil,
                "an empty file is not an oversized one — macOS sends no empty-file refusal at all")
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: 1) == nil)
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: cap - 1) == nil)

        // The boundary is inclusive: `>`, not `>=`, the comparison
        // `PortableLocalApi.cs:340` makes. An off-by-one here is a file one head
        // accepts and its sibling refuses, for the same caller.
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: cap) == nil,
                "a file of exactly the cap is accepted, as it is on the .NET head")

        // Over.
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: cap + 1) != nil)
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: UInt64.max) != nil)

        // And it is the audio cap, not the request cap. A file the size of the
        // request cap is over the audio cap and must be refused; wire
        // `localApiMaxRequestBytes()` in by mistake and this is what fails.
        let requestCap = localApiMaxRequestBytes()
        #expect(cap < requestCap, "the two shared caps are different numbers, and the file path wants the smaller")
        #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: requestCap) != nil,
                "the request cap bounds an HTTP body, not a file on disk — wrong number here")
    }

    /// The refusal is the shared business failure, byte for byte.
    ///
    /// `LocalAPIResponder.failure(code:message:hint:)` always sends HTTP 200, so
    /// the choice that decides the wire answer is *which error value* comes back.
    /// An ad-hoc `APIInputError`, or a route through `badRequest`, would give the
    /// same caller a different answer per head. Every field is compared against
    /// `localApiUploadTooLargeFailure()` — the shared-core value the .NET head
    /// sends at `PortableLocalApi.cs:341` — rather than against a copy of the
    /// wording pasted into this test.
    @Test func theRefusalIsTheSharedTooLargeFailure() throws {
        let failure = localApiUploadTooLargeFailure()
        let refusal = try #require(
            TranscribeEndpoint.uploadCapRefusal(forAudioBytes: localApiMaxUploadBytes() + 1),
            "an over-cap file must produce a refusal to throw"
        )

        #expect(refusal.code == LocalAPIErrorCode(shared: failure.code))
        #expect(refusal.message == failure.message)
        #expect(refusal.hint == failure.hint)

        // Spelled out, because the point of the change is that these exact bytes
        // are what a cross-head client already gets from Linux.
        #expect(refusal.code == .invalidRequest)
        #expect(refusal.message == "Audio exceeds the configured upload limit.")
        #expect(failure.httpStatus == 200)
    }

    // MARK: - The size, measured on a real descriptor

    /// `fileStatus(forFileDescriptor:)` reports `st_size`, and the cap agrees
    /// with it at the boundary.
    ///
    /// The pure test above proves the comparison; this proves the number fed
    /// into it is the file's length and not, say, `st_blocks` or `st_blksize` —
    /// a plausible slip now that one `fstat` serves both the identity check and
    /// the cap. Sizes are set with `ftruncate` rather than by writing bytes:
    /// `st_size` is exactly what the cap reads, and a sparse 48 MiB file costs
    /// no disk and no time.
    @Test func theSizeIsStSizeOfTheOpenDescriptor() throws {
        let cap = localApiMaxUploadBytes()

        for size: UInt64 in [0, 1, 4096, cap - 1] {
            try Self.withOpenFile(ofSize: size) { fd in
                let status = try TranscribeEndpoint.fileStatus(forFileDescriptor: fd)
                #expect(status.size == size, "fstat reported \(status.size) for a \(size)-byte file")
                #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: status.size) == nil)
            }
        }

        try Self.withOpenFile(ofSize: cap) { fd in
            let status = try TranscribeEndpoint.fileStatus(forFileDescriptor: fd)
            #expect(status.size == cap)
            #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: status.size) == nil,
                    "a file of exactly the cap is transcribed, not refused")
        }

        try Self.withOpenFile(ofSize: cap + 1) { fd in
            let status = try TranscribeEndpoint.fileStatus(forFileDescriptor: fd)
            #expect(status.size == cap + 1)
            #expect(TranscribeEndpoint.uploadCapRefusal(forAudioBytes: status.size) != nil,
                    "one byte over the cap is refused, on a real file, through the real helper")
        }
    }

    /// The same `fstat` still answers the identity question it was written for.
    ///
    /// Merging `fileSize(forFileDescriptor:)` into `fileIdentity(forFileDescriptor:)`
    /// touched the TOCTOU guard of issue #713, so the identity half is checked
    /// here too: two different files must never compare equal, and two
    /// descriptors on the same file always must.
    @Test func theStatusStillIdentifiesTheInode() throws {
        try Self.withOpenFile(ofSize: 3) { first in
            let firstStatus = try TranscribeEndpoint.fileStatus(forFileDescriptor: first)

            // Same inode, second descriptor.
            let duplicate = dup(first)
            #expect(duplicate >= 0)
            defer { close(duplicate) }
            let duplicateStatus = try TranscribeEndpoint.fileStatus(forFileDescriptor: duplicate)
            #expect(duplicateStatus.identity == firstStatus.identity,
                    "two descriptors on one file are one identity, or the walk's ESTALE check misfires")

            // A different file, same size.
            try Self.withOpenFile(ofSize: 3) { second in
                let secondStatus = try TranscribeEndpoint.fileStatus(forFileDescriptor: second)
                #expect(secondStatus.identity != firstStatus.identity,
                        "two files must not share an identity, or a swapped file passes validation")
                #expect(secondStatus.size == firstStatus.size)
            }
        }
    }

    // MARK: - The wiring nothing here can call

    /// Staging applies the cap to the walk's own size, before the copy loop.
    ///
    /// `stageValidatedAudioFile` takes a `ValidatedAudioFilePath` that only the
    /// `openat` walk from the recordings root can produce, so this is read
    /// rather than called. Two properties: the value handed to the cap is
    /// `opened.size` — `st_size` from the `fstat` that proved the descriptor's
    /// identity — and the check happens before the 1 MiB copy loop. Checked
    /// after it, an over-cap recording is written to the staging directory in
    /// full before it is refused, which is the amplification shape #375 was
    /// filed about.
    @Test func theCapIsAppliedToTheWalksSizeBeforeTheCopy() throws {
        let stage = try Self.stageValidatedAudioFileBody()

        #expect(stage.contains("Self.uploadCapRefusal(forAudioBytes: opened.size)"), """
            stageValidatedAudioFile must apply uploadCapRefusal(forAudioBytes:) to the size that \
            came back from openValidatedAudioFile.
            """)

        guard let capCheck = stage.range(of: "uploadCapRefusal("),
              let copyLoop = stage.range(of: "input.read(upToCount:") else {
            throw FilePathCapError.anchorNotFound("the cap check and the copy loop")
        }
        #expect(capCheck.lowerBound < copyLoop.lowerBound,
                "the cap must be checked before the 1 MiB copy loop, not during or after it")
    }

    /// The size is never taken by re-resolving the caller's path.
    ///
    /// This branch spends `openValidatedAudioFile` — an `openat` walk with
    /// `O_DIRECTORY | O_NOFOLLOW` at every component plus a device/inode
    /// identity check — closing the window where an attacker who can write into
    /// the recordings folder swaps a validated file for a symlink. A
    /// `FileManager.attributesOfItem(atPath:)` or `URL.resourceValues` call to
    /// get the size reopens exactly that window: it resolves the path a second
    /// time, so it can measure a *different* file from the one being staged — a
    /// small decoy passes the cap while the real bytes copied are whatever the
    /// path points at by then. `fstat` on the open descriptor cannot be aimed
    /// somewhere else, because the descriptor is already bound to the inode.
    ///
    /// Not expressible as a call: it is the absence of an idiom.
    @Test func theSizeIsNeverReReadFromTheCallersPath() throws {
        for region in [try Self.stageValidatedAudioFileBody(), try Self.openValidatedAudioFileBody()] {
            #expect(!region.contains("attributesOfItem"),
                    "re-statting the path reopens the symlink-swap window the O_NOFOLLOW walk closes")
            #expect(!region.contains("resourceValues"),
                    "re-resolving the URL reopens the symlink-swap window the O_NOFOLLOW walk closes")
        }
    }

    /// One `fstat` per `file` request, not two.
    ///
    /// The identity check and the upload cap read the same `struct stat`. Before
    /// the merge there were two near-identical helpers, so every `file` request
    /// paid two `fstat`s and the two had to be kept in step by hand.
    @Test func theEndpointStatsADescriptorExactlyOnce() throws {
        let source = try Self.endpointCode()
        let callSites = source.components(separatedBy: "fstat(").count - 1

        #expect(callSites == 1, """
            \(callSites) fstat call sites in TranscribeEndpoint.swift. The identity check and the \
            upload cap take both answers off one struct stat — see fileStatus(forFileDescriptor:).
            """)
    }

    /// The `file` branch is what reaches the capped staging function.
    ///
    /// The cap could be perfect and unreachable: `resolveAudioSource`'s `file`
    /// half has to route through `stageValidatedAudioFile` for any of it to run.
    @Test func theFileBranchStagesThroughTheCappedFunction() throws {
        let fileBranch = try Self.fileBranchOfResolveAudioSource()

        #expect(fileBranch.contains("Self.stageValidatedAudioFile("),
                "the `file` branch must stage through the function that carries the cap")
    }

    /// The `file` branch rethrows the refusal instead of flattening it.
    ///
    /// The `do`/`catch` around staging existed to turn any staging failure into
    /// `FILE_ACCESS_DENIED` + "Cannot read <path>" + a Full Disk Access hint.
    /// The cap throws through that same call, so without an `APIInputError`
    /// passthrough the caller of a 60 MiB file is told to check permissions on a
    /// file they can read perfectly well, with a code the .NET head never sends
    /// for this case. Every other test in this file passes either way — the bug
    /// would live entirely in the catch.
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
            throw FilePathCapError.anchorNotFound("both catch arms around the staging call")
        }
        #expect(passthrough.lowerBound < fallback.lowerBound,
                "the typed passthrough must precede the catch-all, or Swift never reaches it")
    }

    /// No out-of-enum code, and no 413 in a status position.
    ///
    /// The obvious way to write "the file is too big" is the HTTP status that
    /// means it. `LocalAPIErrorCode` is a closed 14-case `Codable` enum and
    /// `LocalAPIContractTests.theOutOfEnumCodesStayOutOfTheEnum` requires
    /// `PAYLOAD_TOO_LARGE` to stay undecodable — a client sharing this decoder
    /// would fail to decode the *entire* envelope, not just the code.
    ///
    /// The 413 half is scoped to lines that also name a status, rather than
    /// banning the digits outright: a buffer size of `413_000` is not an HTTP
    /// status code, and failing the suite with a message about status codes
    /// because of one would send the next reader somewhere there is no bug.
    @Test func theRefusalIntroducesNoOutOfEnumCode() throws {
        let source = try Self.endpointCode()

        #expect(!source.contains("PAYLOAD_TOO_LARGE"),
                "PAYLOAD_TOO_LARGE is outside the closed enum and breaks a sharing client's decoder")
        #expect(!source.contains("payloadTooLarge"),
                "the size refusal is INVALID_REQUEST on every head")

        let statusLines = source
            .components(separatedBy: .newlines)
            .filter { line in
                line.contains("413")
                    && (line.contains("statusCode") || line.contains("HTTPResponse") || line.contains("status"))
            }
        #expect(statusLines.isEmpty, """
            A size refusal is HTTP 200 with a business code, never a 413: \
            \(statusLines.map { $0.trimmingCharacters(in: .whitespaces) }.joined(separator: " / "))
            """)
    }
}

// MARK: - Fixtures

private enum FilePathCapError: Error, CustomStringConvertible {
    case anchorNotFound(String)
    case fixture(String, CInt)

    var description: String {
        switch self {
        case .anchorNotFound(let anchor):
            return """
            Could not locate '\(anchor)' in TranscribeEndpoint.swift. It was probably renamed or \
            moved — update the anchor in LocalAPIFilePathCapTests rather than deleting the check.
            """
        case .fixture(let step, let code):
            return "Could not \(step) the temp file this test sizes with ftruncate (errno \(code))"
        }
    }
}

extension LocalAPIFilePathCapTests {

    fileprivate static let endpointPath =
        "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"

    /// A temp file of exactly `size` bytes, open for reading, closed and deleted
    /// when `body` returns.
    ///
    /// `ftruncate` rather than writing bytes. `st_size` is precisely what the
    /// cap reads, and a sparse file gives the boundary cases at the real 48 MiB
    /// cap for no disk and no wall-clock; writing them would make this test a
    /// ~100 MiB affair for no extra coverage.
    fileprivate static func withOpenFile(ofSize size: UInt64, _ body: (CInt) throws -> Void) throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("hyperwhisper-filecap-\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw FilePathCapError.fixture("create", 0)
        }
        defer { try? FileManager.default.removeItem(at: url) }

        let fd = open(url.path, O_RDWR)
        guard fd >= 0 else {
            throw FilePathCapError.fixture("open", errno)
        }
        defer { close(fd) }

        guard ftruncate(fd, off_t(size)) == 0 else {
            throw FilePathCapError.fixture("size", errno)
        }
        try body(fd)
    }

    /// `TranscribeEndpoint.swift` with every comment line removed.
    fileprivate static func endpointCode() throws -> String {
        try ProductionSource.code(of: endpointPath)
    }

    /// The `file` half of `resolveAudioSource`, ending where the base64 half
    /// begins (the `guard let raw = trimmedBase64` that opens it).
    fileprivate static func fileBranchOfResolveAudioSource() throws -> String {
        try ProductionSource.slice(
            of: endpointPath,
            from: "if let filePath = trimmedFile, hasFile {",
            to: "guard let raw = trimmedBase64"
        )
    }

    /// The staging call inside the `file` branch together with both of its
    /// `catch` arms, ending at the `AudioResolution` the branch returns.
    fileprivate static func stagingCallAndItsCatches() throws -> String {
        try ProductionSource.slice(
            of: endpointPath,
            from: "stagedFile = try Self.stageValidatedAudioFile(",
            to: "return AudioResolution("
        )
    }

    /// The body of `stageValidatedAudioFile(_:fileExtension:)`, ending at the
    /// next declaration. Anchored on the `func` keyword so the call site above
    /// it in `resolveAudioSource` is not what gets matched.
    fileprivate static func stageValidatedAudioFileBody() throws -> String {
        try ProductionSource.slice(
            of: endpointPath,
            from: "private static func stageValidatedAudioFile(",
            to: "private static func stageAudioData("
        )
    }

    /// The body of `openValidatedAudioFile(_:)`, ending at the next declaration.
    fileprivate static func openValidatedAudioFileBody() throws -> String {
        try ProductionSource.slice(
            of: endpointPath,
            from: "private static func openValidatedAudioFile(",
            to: "private static func openDirectoryRefusingSymlinks("
        )
    }
}
