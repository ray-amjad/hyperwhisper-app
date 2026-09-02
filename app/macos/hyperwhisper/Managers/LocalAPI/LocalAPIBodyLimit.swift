//
//  LocalAPIBodyLimit.swift
//  hyperwhisper
//
//  The one way a Local API endpoint reads a request body (issue #375).
//

import Foundation
import FlyingFox

/// A bounded read of a Local API request body.
///
/// # Why this exists
///
/// Issue #375: `LocalAPIServer` builds its `HTTPServer` with an address and a
/// timeout, and every write endpoint used to say `try await request.bodyData`.
/// FlyingFox has no server-level body limit to configure — `HTTPServer.Configuration`
/// carries `address`, `timeout`, `sharedRequestBufferSize`, `sharedRequestReplaySize`,
/// `pool` and `logger`, and nothing else — so the ceiling has to be applied where
/// the bytes are consumed. A 200 MB `POST` cost ~205 MB of resident memory before
/// it was refused, and the caller chose the number.
///
/// # Why both checks are needed
///
/// `HTTPDecoder` picks one of three body representations. Under
/// `sharedRequestBufferSize` (4 KiB) the body is already in memory; under
/// `sharedRequestReplaySize` (2 MiB) it is a replay-buffered lazy sequence; and
/// **above 2 MiB it is a lazy unbuffered sequence with no flusher**. That last
/// branch is the bug and also the fix: nothing is allocated until the sequence is
/// iterated, so stopping at the cap means the cap is all that is ever allocated.
///
/// - The `Content-Length` pre-check refuses the reported repro without reading a
///   byte off the socket.
/// - The running counter is *not* belt-and-braces. `app/macos/Package.swift` pins
///   FlyingFox as `from: "0.21.0"` with no committed `Package.resolved`, so CI
///   resolves the newest 0.x. FlyingFox 0.22+ decodes chunked request bodies, and
///   a chunked body has `bodySequence.count == nil` — a `Content-Length`-only
///   guard would be blind to it. A declared length can also simply lie.
///
/// # Consequence: an aborted read breaks keep-alive on that connection
///
/// Stopping mid-body leaves the unread remainder on the socket, so the *next*
/// request decoded on that keep-alive connection sees the leftover bytes and
/// fails. The rejection response itself is written first, so the client does get
/// its envelope; it is the connection reuse that is lost. `HTTPServer.handleRequest`
/// copies the request's `Connection` header onto the response, so a handler cannot
/// ask for `Connection: close` to make this tidy.
///
/// We accept that rather than draining the remainder. Draining is unbounded work
/// on a chunked body — it is the same denial-of-service this file exists to close,
/// only with the allocation moved to `/dev/null`.
///
/// # The rejection is HTTP 200, not 413
///
/// #375 suggests a 413. A 413 wants a `PAYLOAD_TOO_LARGE` code, which is outside
/// the closed 14-case `LocalAPIErrorCode`; a client sharing that `Codable` decoder
/// cannot decode *any* envelope carrying it, so the whole response becomes
/// unreadable rather than just the code. Both messages come from Rust
/// (`hw-localapi::limits`), which is what makes them the same bytes every head
/// sends.
enum LocalAPIBodyLimit {

    /// Outcome of a bounded body read.
    ///
    /// A two-case enum rather than `Result<Data, HTTPResponse>` because `Result`
    /// constrains `Failure: Error` and FlyingFox's `HTTPResponse` is a plain
    /// `Sendable` struct with no `Error` conformance.
    enum Outcome: Sendable {
        case body(Data)
        case rejected(HTTPResponse)
    }

    /// Outcome of the FlyingFox-free draining core.
    ///
    /// Kept separate from `Outcome` so `drain` can be tested: the
    /// `hyperwhisperTests` target has an empty Frameworks phase and does not link
    /// FlyingFox, so a test cannot build an `HTTPRequest` or name an
    /// `HTTPResponse`. Everything worth asserting about the guard lives below
    /// this line.
    enum DrainOutcome: Sendable, Equatable {
        case body(Data)
        case tooLarge
        case unreadable
    }

    /// Read `request`'s body, or return the response to send instead.
    ///
    /// The FlyingFox adapter over `drain`. The `.unreadable` message is the one
    /// all four call sites already sent for a failed `bodyData`, so folding it in
    /// here changes no bytes on the wire.
    static func read(_ request: HTTPRequest) async -> Outcome {
        let chunks = request.bodySequence
        switch await drain(count: chunks.count, limit: localApiMaxRequestBytes(), chunks: chunks) {
        case .body(let data):
            return .body(data)
        case .tooLarge:
            return .rejected(LocalAPIResponder.response(for: localApiRequestTooLargeFailure()))
        case .unreadable:
            return .rejected(LocalAPIResponder.badRequest(message: "Could not read request body"))
        }
    }

    /// Accumulate `chunks` into a `Data`, refusing anything over `limit` bytes.
    ///
    /// - Parameters:
    ///   - count: the decoder's declared body length — `nil` for a chunked body,
    ///     which declares none.
    ///   - limit: the ceiling in bytes, inclusive. `localApiMaxRequestBytes()` in
    ///     production; a small number in the tests.
    ///   - chunks: the body, lazily. Not iterated at all when `count` alone is
    ///     enough to refuse the request.
    ///
    /// The comparison is `>`, so a body of exactly `limit` is accepted — the same
    /// comparison `PortableLocalApi.cs:185` makes, and the reason a cross-head
    /// client sees one answer rather than two.
    static func drain<S: AsyncSequence & Sendable>(
        count: Int?,
        limit: UInt64,
        chunks: S
    ) async -> DrainOutcome where S.Element == Data {
        // Normalised to the unsigned type the Rust cap uses. A negative length is
        // not something FlyingFox produces, but `UInt64(negative)` traps, and a
        // trap on a network-facing path is a worse availability bug than the one
        // being fixed — so a nonsense length folds to 0 and is left to the
        // counter below.
        let declaredLength: UInt64? = count.map { (declared: Int) -> UInt64 in
            declared > 0 ? UInt64(declared) : 0
        }

        // The branch that fixes the reported repro: refused before a byte is read
        // off the socket, so the 200 MB body is never allocated.
        if let declaredLength, declaredLength > limit {
            return .tooLarge
        }

        var body = Data()
        if let count, count > 0 {
            // Bounded by the pre-check above: anything over the cap has already
            // returned, so this reserves at most `limit` bytes. A lying length
            // that is *under* the cap only over-reserves by less than the cap.
            body.reserveCapacity(count)
        }

        var received: UInt64 = 0
        do {
            for try await chunk in chunks {
                let (total, overflowed) = received.addingReportingOverflow(UInt64(chunk.count))
                if overflowed || total > limit {
                    // Stop here. The remainder stays on the socket — see the type
                    // doc on why we do not drain it.
                    return .tooLarge
                }
                received = total
                body.append(chunk)
            }
        } catch {
            // A dropped socket is not an oversized body, and must keep its own
            // 400. Reporting it as `.tooLarge` would tell a client to shrink a
            // payload that was never the problem.
            return .unreadable
        }
        return .body(body)
    }
}
