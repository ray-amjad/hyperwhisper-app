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
//  FlyingFox type, and `read` is the four-line adapter over it. Everything below
//  exercises `drain` directly.
//
//  The caps are passed in, so no test here allocates more than a few bytes. The
//  real numbers are pinned in `LocalAPIContractTests`.
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
