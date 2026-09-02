//
//  LocalAPIBodyLimitTests.swift
//  hyperwhisperTests
//
//  The bounded body reader from issue #375, tested at the only layer this
//  target can reach.
//
//  `LocalAPIBodyLimit.read(_:)` takes a FlyingFox `HTTPRequest` and returns a
//  FlyingFox `HTTPResponse`, and this target links no FlyingFox — its Frameworks
//  phase is empty and FlyingFox is a product dependency of the app target alone.
//  That is why the reader is split in two: `drain` holds every decision (the
//  pre-check, the running counter, the boundary, the error mapping) and names no
//  FlyingFox type, and `read` is the four-line adapter over it. The behaviour
//  tests below exercise `drain` directly.
//
//  The caps are passed in, so no test here allocates more than a few bytes. The
//  real numbers are pinned in `LocalAPIContractTests`.
//
//  The last three tests cover what `drain` cannot: the *wiring* in `read` and in
//  the endpoints, read out of the production source. See the MARK comment above
//  them for why that is the only reachable form.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalAPIBodyLimitTests {

    // MARK: - The declared-length branch

    /// The branch that fixes the reported repro, and the property that makes it
    /// a fix at all: an over-cap `Content-Length` is refused **without reading a
    /// byte**. #375 is not "the request is accepted", it is "205 MB is allocated
    /// before the request is refused" — so a version of this guard that drained
    /// first and complained afterwards would pass a naive test and fix nothing.
    @Test func knownLengthOverTheCapIsRejectedWithoutReading() async {
        let recorder = IterationRecorder()
        let outcome = await LocalAPIBodyLimit.drain(
            count: 11,
            limit: 10,
            chunks: ChunkStream(chunks: [bodyBytes(11)], recorder: recorder)
        )

        #expect(outcome == .tooLarge)
        let iterated = await recorder.didIterate
        #expect(iterated == false, "the body must not be touched when the declared length already refuses it")
    }

    /// The cap is inclusive: `>`, not `>=`. `PortableLocalApi.cs:185` compares
    /// the same way, so an off-by-one here is a body one head accepts and its
    /// sibling refuses for the same caller.
    @Test func exactlyTheCapIsAccepted() async {
        let outcome = await LocalAPIBodyLimit.drain(
            count: 10,
            limit: 10,
            chunks: ChunkStream(chunks: [bodyBytes(10)], recorder: IterationRecorder())
        )

        #expect(outcome == .body(bodyBytes(10)))
    }

    /// The other side of that boundary, on the declared-length branch.
    @Test func oneByteOverTheCapIsRejected() async {
        let outcome = await LocalAPIBodyLimit.drain(
            count: 11,
            limit: 10,
            chunks: ChunkStream(chunks: [bodyBytes(11)], recorder: IterationRecorder())
        )

        #expect(outcome == .tooLarge)
    }

    // MARK: - The streaming branch

    /// A chunked body declares no length, so `count` is `nil` and the pre-check
    /// cannot fire. FlyingFox 0.22+ decodes chunked request bodies and
    /// `app/macos/Package.swift` pins `from: "0.21.0"` with no committed
    /// `Package.resolved`, so CI resolves a version where this is reachable — the
    /// counter is the guard, not a nicety.
    ///
    /// Also asserts the loop *stops*: 30 bytes arrive in 3-byte chunks and the
    /// counter passes 10 on the fourth, so a reader that kept accumulating and
    /// checked at the end would be caught here.
    @Test func streamingOverflowIsCaughtWhenTheLengthIsUnknown() async {
        let recorder = IterationRecorder()
        let outcome = await LocalAPIBodyLimit.drain(
            count: nil,
            limit: 10,
            chunks: ChunkStream(chunks: Array(repeating: bodyBytes(3), count: 10), recorder: recorder)
        )

        #expect(outcome == .tooLarge)
        let delivered = await recorder.chunksDelivered
        #expect(delivered == 4, "must stop at the chunk that crosses the cap, not drain all 10")
    }

    /// The boundary again, with nothing declared. The two branches are two
    /// separate comparisons in two separate places, so holding on one says
    /// nothing about the other.
    @Test func oneByteOverTheCapIsRejectedWithNoDeclaredLength() async {
        let over = await LocalAPIBodyLimit.drain(
            count: nil,
            limit: 10,
            chunks: ChunkStream(chunks: [bodyBytes(11)], recorder: IterationRecorder())
        )
        #expect(over == .tooLarge)

        // And exactly the cap still gets through on this branch too.
        let atCap = await LocalAPIBodyLimit.drain(
            count: nil,
            limit: 10,
            chunks: ChunkStream(chunks: [bodyBytes(10)], recorder: IterationRecorder())
        )
        #expect(atCap == .body(bodyBytes(10)))
    }

    /// A declared length is a claim, not a fact. If the pre-check were the only
    /// check, `Content-Length: 1` followed by megabytes would buffer every one of
    /// them — the exact hole the guard exists to close, reopened by trusting a
    /// caller-controlled header.
    @Test func aLyingContentLengthDoesNotDefeatTheCounter() async {
        let outcome = await LocalAPIBodyLimit.drain(
            count: 1,
            limit: 10,
            chunks: ChunkStream(chunks: Array(repeating: bodyBytes(3), count: 10), recorder: IterationRecorder())
        )

        #expect(outcome == .tooLarge)
    }

    // MARK: - The cases that must not be rejected

    /// An empty body is a body. A `Content-Length: 0` POST must decode into the
    /// endpoint's normal "invalid JSON" answer, not into a size failure — and the
    /// reserve-capacity path is where an off-by-one would turn 0 into a refusal.
    @Test func anEmptyBodyIsAcceptedNotRejected() async {
        let outcome = await LocalAPIBodyLimit.drain(
            count: 0,
            limit: 10,
            chunks: ChunkStream(chunks: [], recorder: IterationRecorder())
        )

        #expect(outcome == .body(Data()))
    }

    /// A dropped socket keeps its own answer. `.unreadable` becomes the 400
    /// "Could not read request body" all four call sites already sent; folding it
    /// into `.tooLarge` would tell a client to shrink a payload that was never
    /// the problem, and would hide a real transport fault behind a size error.
    @Test func aGenuineReadErrorStaysAReadError() async {
        let outcome = await LocalAPIBodyLimit.drain(
            count: nil,
            limit: 10,
            chunks: ChunkStream(
                chunks: [bodyBytes(2)],
                failAfterChunks: 1,
                recorder: IterationRecorder()
            )
        )

        #expect(outcome == .unreadable)
        #expect(outcome != .tooLarge)
    }

    // MARK: - The wiring the tests above cannot reach

    // Everything above injects its own `limit:` into `drain`. That is the only
    // way this target can drive the reader — but it means the two decisions
    // `read(_:)` makes are untested by construction: *which* cap production
    // uses, and *which* response `.tooLarge` becomes. Wire `read` to
    // `localApiMaxUploadBytes()` by mistake, or map `.tooLarge` to a 400, and
    // every test above still passes.
    //
    // `read` takes a FlyingFox `HTTPRequest` and returns a FlyingFox
    // `HTTPResponse`, and this target links no FlyingFox, so it cannot be
    // called here. So read the production source instead — the same
    // `#filePath`-relative trick `HyperWhisperCloudEntitlementTests` and
    // `CloudSttTierParityTests` use for values a test cannot construct.
    // Scraping Swift text is cruder than calling the function, but it is what
    // makes these assertions fail when the production line changes.

    /// `read` uses the **request** cap, not the upload cap.
    ///
    /// The two are different numbers (52,428,800 and 50,331,648) and they mean
    /// different things: the request cap bounds the whole body, the upload cap
    /// bounds one piece of audio inside it. Reading a body at the upload cap
    /// would refuse a legal 49 MiB request that every other head accepts.
    @Test func theProductionReadUsesTheRequestCap() throws {
        let read = try Self.readFunctionBody()

        #expect(read.contains("localApiMaxRequestBytes()"),
                "read(_:) must pass the shared request cap to drain")
        #expect(!read.contains("localApiMaxUploadBytes()"),
                "read(_:) bounds the whole body, so the upload cap is the wrong number here")
    }

    /// `.tooLarge` becomes the shared business failure, and `.unreadable` keeps
    /// its own 400.
    ///
    /// `LocalAPIContractTests` already pins that
    /// `localApiRequestTooLargeFailure()` is HTTP 200 carrying
    /// `INVALID_REQUEST`. What was missing is the link between the two: that
    /// the over-cap arm actually reaches for *that* failure rather than for
    /// `badRequest`, which is HTTP 400 and would put an oversized body in the
    /// protocol-error bucket where no wrapper looks for it.
    @Test func theOverCapArmSendsTheSharedBusinessFailure() throws {
        let read = try Self.readFunctionBody()

        let tooLargeArm = try Self.arm(named: "case .tooLarge:", in: read)
        #expect(tooLargeArm.contains("localApiRequestTooLargeFailure()"),
                "the over-cap arm must answer with the shared 200 + INVALID_REQUEST failure")
        #expect(!tooLargeArm.contains("badRequest"),
                "an oversized body is a business failure, not a 400")

        let unreadableArm = try Self.arm(named: "case .unreadable:", in: read)
        #expect(unreadableArm.contains("Could not read request body"),
                "a dropped socket keeps the 400 all four call sites already sent")
    }

    /// Nothing in the Local API reads a body except the bounded reader.
    ///
    /// This is the guard on the hole the router-level `LocalAPIServer.bodied`
    /// wrapper narrows but cannot close on its own: a sixth endpoint, added a
    /// year from now, that writes `try await request.bodyData` — the idiom
    /// every one of these files used before #375 — is uncapped again, and
    /// nothing else in the suite would notice. `bodyData` and `bodySequence`
    /// are the only two ways FlyingFox hands over a body, so banning both
    /// outside `LocalAPIBodyLimit.swift` makes `bodied` the only door.
    ///
    /// If this fails on a legitimate new reader, the fix is to route it through
    /// `bodied`, not to add the file to the exemption below.
    @Test func theOnlyBodyReadInTheLocalApiIsTheBoundedOne() throws {
        let directory = Self.repoRoot.appendingPathComponent("app/macos/hyperwhisper/Managers/LocalAPI")
        let files = try Self.swiftFiles(under: directory)
        #expect(files.count >= 12, "the Local API source tree was not found where this test expects it")

        var offenders: [String] = []
        for file in files where file.lastPathComponent != "LocalAPIBodyLimit.swift" {
            let source = try Self.contents(of: file)
            for (offset, line) in source.components(separatedBy: .newlines).enumerated() {
                // Prose about the ban is not a violation of it.
                guard !line.trimmingCharacters(in: .whitespaces).hasPrefix("//") else { continue }
                guard line.contains(".bodyData") || line.contains(".bodySequence") else { continue }
                offenders.append("\(file.lastPathComponent):\(offset + 1)")
            }
        }

        #expect(offenders.isEmpty, """
            Unbounded body read outside LocalAPIBodyLimit.swift at \(offenders.joined(separator: ", ")). \
            Read the body through LocalAPIServer.bodied(_:_:) instead — that is what applies the \
            shared request cap from issue #375.
            """)
    }
}

// MARK: - Production-source fixtures

private enum ProductionSourceError: Error, CustomStringConvertible {
    case unreadable(String)
    case anchorNotFound(String)

    var description: String {
        switch self {
        case .unreadable(let path):
            return "Could not read production source at \(path)"
        case .anchorNotFound(let anchor):
            return """
            Could not locate '\(anchor)' in LocalAPIBodyLimit.swift. It was probably renamed or \
            moved — update the anchor in LocalAPIBodyLimitTests rather than deleting the check.
            """
        }
    }
}

extension LocalAPIBodyLimitTests {

    /// Repo root, derived from this file's own compile-time path.
    fileprivate static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // hyperwhisperTests
            .deletingLastPathComponent()  // macos
            .deletingLastPathComponent()  // app
            .deletingLastPathComponent()  // <repo root>
    }

    fileprivate static func contents(of url: URL) throws -> String {
        guard let data = try? Data(contentsOf: url) else {
            throw ProductionSourceError.unreadable(url.path)
        }
        return String(decoding: data, as: UTF8.self)
    }

    fileprivate static func swiftFiles(under directory: URL) throws -> [URL] {
        guard let walker = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: nil
        ) else {
            throw ProductionSourceError.unreadable(directory.path)
        }
        return walker
            .compactMap { $0 as? URL }
            .filter { $0.pathExtension == "swift" }
            .sorted { $0.path < $1.path }
    }

    /// The body of `LocalAPIBodyLimit.read(_:)`, comment lines removed.
    ///
    /// Comments go first so a doc comment that *describes* the production wiring
    /// cannot stand in for the wiring — the whole point is to read the code.
    fileprivate static func readFunctionBody() throws -> String {
        let path = "app/macos/hyperwhisper/Managers/LocalAPI/LocalAPIBodyLimit.swift"
        let source = try contents(of: repoRoot.appendingPathComponent(path))
        let code = source
            .components(separatedBy: .newlines)
            .filter { !$0.trimmingCharacters(in: .whitespaces).hasPrefix("//") }
            .joined(separator: "\n")

        guard let start = code.range(of: "static func read(") else {
            throw ProductionSourceError.anchorNotFound("static func read(")
        }
        let rest = code[start.upperBound...]
        guard let end = rest.range(of: "static func drain") else {
            throw ProductionSourceError.anchorNotFound("static func drain")
        }
        return String(rest[..<end.lowerBound])
    }

    /// One `switch` arm out of `body`, from its `case` label to the next `case`
    /// label or the end.
    fileprivate static func arm(named label: String, in body: String) throws -> String {
        guard let start = body.range(of: label) else {
            throw ProductionSourceError.anchorNotFound(label)
        }
        let rest = body[start.upperBound...]
        guard let end = rest.range(of: "case .") else {
            return String(rest)
        }
        return String(rest[..<end.lowerBound])
    }
}

// MARK: - Fixtures

private func bodyBytes(_ count: Int) -> Data {
    Data(repeating: 0x41, count: count)
}

private enum ChunkStreamFailure: Error {
    case socketDropped
}

/// Records what `drain` did to the body sequence.
///
/// An actor rather than a counter class because the iterator is driven from
/// whatever executor the nonisolated `drain` lands on;
/// `HyperWhisperCloudLicenseRecoveryTests` uses the same shape for the same
/// reason.
private actor IterationRecorder {
    private(set) var didIterate = false
    private(set) var chunksDelivered = 0

    func noteIterationStarted() {
        didIterate = true
    }

    func noteChunkDelivered() {
        chunksDelivered += 1
    }
}

/// A body sequence built from an array of chunks, which reports whether it was
/// iterated and how far.
///
/// Hand-rolled rather than an `AsyncStream` so "was it iterated at all" is
/// observable: an `AsyncStream` producer closure runs eagerly and cannot answer
/// that question, which is the single most important assertion in this file.
/// `failAfterChunks` makes the sequence throw once that many chunks have been
/// handed out, standing in for a socket that goes away mid-body.
private struct ChunkStream: AsyncSequence, Sendable {
    typealias Element = Data

    let chunks: [Data]
    let failAfterChunks: Int?
    let recorder: IterationRecorder

    init(chunks: [Data], failAfterChunks: Int? = nil, recorder: IterationRecorder) {
        self.chunks = chunks
        self.failAfterChunks = failAfterChunks
        self.recorder = recorder
    }

    func makeAsyncIterator() -> Iterator {
        Iterator(chunks: chunks, failAfterChunks: failAfterChunks, recorder: recorder)
    }

    struct Iterator: AsyncIteratorProtocol {
        let chunks: [Data]
        let failAfterChunks: Int?
        let recorder: IterationRecorder
        var index = 0

        mutating func next() async throws -> Data? {
            await recorder.noteIterationStarted()
            if let failAfterChunks, index >= failAfterChunks {
                throw ChunkStreamFailure.socketDropped
            }
            guard index < chunks.count else { return nil }
            let chunk = chunks[index]
            index += 1
            await recorder.noteChunkDelivered()
            return chunk
        }
    }
}
