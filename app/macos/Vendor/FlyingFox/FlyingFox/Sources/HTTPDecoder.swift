//
//  HTTPDecoder.swift
//  FlyingFox
//
//  Created by Simon Whitty on 13/02/2022.
//  Copyright © 2022 Simon Whitty. All rights reserved.
//
//  Distributed under the permissive MIT license
//  Get the latest version from here:
//
//  https://github.com/swhitty/FlyingFox
//
//  Permission is hereby granted, free of charge, to any person obtaining a copy
//  of this software and associated documentation files (the "Software"), to deal
//  in the Software without restriction, including without limitation the rights
//  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//  copies of the Software, and to permit persons to whom the Software is
//  furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in all
//  copies or substantial portions of the Software.
//
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//  SOFTWARE.
//

import FlyingSocks
import Foundation

struct HTTPDecoder {

    var sharedRequestBufferSize: Int
    var sharedRequestReplaySize: Int
    // HyperWhisper patch (#1463): see HTTPServer.RequestHeadLimits.
    var requestHeadLimits: HTTPServer.RequestHeadLimits = .unlimited

    func decodeRequest(from bytes: some AsyncBufferedSequence<UInt8>) async throws -> HTTPRequest {
        // HyperWhisper patch (#1463): the request line and the headers are read
        // together, in linear time, within the byte limits and the deadline.
        let head = try await readRequestHead(from: bytes)
        let comps = head.startLine
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true)
        guard comps.count == 3 else {
            throw Error("No HTTP Method")
        }

        let method = HTTPMethod(String(comps[0]))
        let version = HTTPVersion(String(comps[2]))
        let target = makeTarget(from: comps[1])
        let headers = head.headers
        let body = try await readBody(
            from: bytes,
            contentLength: headers[.contentLength],
            transferEncoding: headers[.transferEncoding]
        )

        return HTTPRequest(
            method: method,
            version: version,
            target: target,
            headers: HTTPHeaders(headers),
            body: body
        )
    }

    func decodeResponse(from bytes: some AsyncBufferedSequence<UInt8>) async throws -> HTTPResponse {
        // HyperWhisper patch (#1463): linear head reader, no deadline.
        let head = try await readHead(from: bytes)
        let comps = head.startLine
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true)
        guard comps.count == 3,
              let code = Int(comps[1]) else {
            throw Error("Invalid Status Line")
        }

        let version = HTTPVersion(String(comps[0]))
        let statusCode = HTTPStatusCode(code, phrase: String(comps[2]))

        let headers = head.headers
        let body = try await readBody(
            from: bytes,
            contentLength: headers[.contentLength],
            transferEncoding: headers[.transferEncoding]
        )

        return HTTPResponse(
            version: version,
            statusCode: statusCode,
            headers: HTTPHeaders(headers),
            body: try await body.get()
        )
    }

    func readComponents(from target: String) -> (path: String, query: [HTTPRequest.QueryItem]) {
        makeComponents(from: makeTarget(from: target))
    }

    func makeTarget(from target: some StringProtocol) -> HTTPRequest.Target {
        let comps = target.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let path = comps.first ?? ""
        let query = comps.count > 1 ? comps[1] : ""
        return HTTPRequest.Target(
            path: String(path),
            query: String(query)
        )
    }

    func makeComponents(from target: HTTPRequest.Target) -> (path: String, query: [HTTPRequest.QueryItem]) {
        makeComponents(from: URLComponents(string: target.rawValue))
    }

    func makeComponents(from comps: URLComponents?) -> (path: String, query: [HTTPRequest.QueryItem]) {
        let path = (comps?.percentEncodedPath).flatMap(HTTPDecoder.standardizePath) ?? ""
        let query = comps?.queryItems?.map {
            HTTPRequest.QueryItem(name: $0.name, value: $0.value ?? "")
        }
        return (path, query ?? [])
    }

    @Sendable
    func readHeader(from line: String) -> (header: HTTPHeader, value: String)? {
        let comps = line.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: true)
        guard comps.count > 1 else { return nil }
        let name = comps[0].trimmingCharacters(in: .whitespacesAndNewlines)
        let value = comps[1].trimmingCharacters(in: .whitespacesAndNewlines)
        return (HTTPHeader(name), value)
    }

    // HyperWhisper patch (#1463). Upstream read every line through
    // `bytes.lines`, whose `CollectUntil` re-checked the whole line's suffix
    // after each byte: O(n²) per line, with no limit on a line or on the head.
    // These read each byte once and stop at `requestHeadLimits`.

    struct Head: Sendable {
        var startLine: String
        var headers: [HTTPHeader: String]
    }

    /// Reads a request head within `requestHeadLimits.readTimeout`, when set.
    func readRequestHead(from bytes: some AsyncBufferedSequence<UInt8>) async throws -> Head {
        guard let timeout = requestHeadLimits.readTimeout else {
            return try await readHead(from: bytes)
        }
        return try await withThrowingTimeout(seconds: timeout) {
            try await readHead(from: bytes)
        }
    }

    /// The start line and the header lines, through the blank line that ends
    /// them. Throws `SequenceTerminationError` when the stream ends before the
    /// first byte, as upstream's `lines.takeNext()` did.
    func readHead(from bytes: some AsyncBufferedSequence<UInt8>) async throws -> Head {
        var reader = HeadLineReader(limits: requestHeadLimits)
        var iterator = bytes.makeAsyncIterator()
        guard let startLine = try await reader.readLine(from: &iterator) else {
            throw SequenceTerminationError()
        }
        let headers = try await readHeaderLines(from: &iterator, reader: &reader)
        return Head(startLine: startLine, headers: headers)
    }

    func readHeaders(from bytes: some AsyncBufferedSequence<UInt8>) async throws -> [HTTPHeader : String] {
        var reader = HeadLineReader(limits: requestHeadLimits)
        var iterator = bytes.makeAsyncIterator()
        return try await readHeaderLines(from: &iterator, reader: &reader)
    }

    /// Same semantics as upstream: stop at an empty line (CRLF or bare LF) or
    /// at the end of the stream, skip a line with no colon, last value wins.
    private func readHeaderLines<I: AsyncIteratorProtocol>(
        from iterator: inout I,
        reader: inout HeadLineReader
    ) async throws -> [HTTPHeader: String] where I.Element == UInt8 {
        var headers = [HTTPHeader: String]()
        while let line = try await reader.readLine(from: &iterator), line != "\r", line != "" {
            if let header = readHeader(from: line) {
                headers[header.header] = header.value
            }
        }
        return headers
    }

    func readBody(
        from bytes: some AsyncBufferedSequence<UInt8>,
        contentLength: String?,
        transferEncoding: String?
    ) async throws -> HTTPBodySequence {
        guard sharedRequestBufferSize > 0 else {
            throw SocketError.disconnected
        }

        // RFC 9112 §6.1 — reject simultaneous Content-Length and Transfer-Encoding.
        if transferEncoding != nil && contentLength != nil {
            throw Error("Content-Length and Transfer-Encoding cannot both be present")
        }

        // RFC 9112 §6.3 #3 — Transfer-Encoding takes precedence. §6.1 requires
        // `chunked` to be the final coding when present; only `chunked` is supported here.
        if let transferEncoding {
            let tokens = transferEncoding
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            guard tokens.last == "chunked" else {
                throw Error("Unsupported Transfer-Encoding: \(transferEncoding)")
            }
            return HTTPBodySequence(
                chunked: bytes,
                suggestedBufferSize: sharedRequestBufferSize
            )
        }

        // RFC 9112 §6.3 #5 — invalid Content-Length is an unrecoverable framing error.
        let length: Int
        if let contentLength {
            guard let parsed = Int(contentLength), parsed >= 0 else {
                throw Error("Invalid Content-Length: \(contentLength)")
            }
            length = parsed
        } else {
            length = 0
        }

        if length <= sharedRequestBufferSize {
            return try await HTTPBodySequence(data: readData(from: bytes, length: length), suggestedBufferSize: length)
        } else if length <= sharedRequestReplaySize {
            return HTTPBodySequence(shared: bytes, count: length, suggestedBufferSize: sharedRequestBufferSize)
        } else {
            let prefix = AsyncBufferedPrefixSequence(base: bytes, count: length)
            return HTTPBodySequence(from: prefix, count: length, suggestedBufferSize: sharedRequestBufferSize)
        }
    }

    private func readData(from bytes: some AsyncBufferedSequence<UInt8>, length: Int) async throws -> Data {
        var iterator = bytes.makeAsyncIterator()
        guard let buffer = try await iterator.nextBuffer(count: length),
              buffer.count == length else {
            throw SocketError.disconnected
        }
        return Data(buffer)
    }
}

extension HTTPDecoder {

    init() {
        self.init(sharedRequestBufferSize: 128, sharedRequestReplaySize: 1024)
    }

    struct Error: LocalizedError {
        var errorDescription: String?

        init(_ description: String) {
            self.errorDescription = description
        }
    }

    // HyperWhisper patch (#1463): HTTPServer answers this with 431.
    struct HeadTooLargeError: LocalizedError {
        var errorDescription: String?

        init(_ description: String) {
            self.errorDescription = description
        }
    }
}

// HyperWhisper patch (#1463).
/// Reads the lines of one message head, one byte at a time, counting every
/// byte against the head limit and each line against the line limit.
struct HeadLineReader {
    let maxLineBytes: Int
    let maxHeadBytes: Int
    private(set) var headBytes = 0

    init(limits: HTTPServer.RequestHeadLimits) {
        self.maxLineBytes = limits.maxLineBytes
        self.maxHeadBytes = limits.maxHeadBytes
    }

    /// The bytes before the next LF, as a string that keeps a trailing CR
    /// (upstream's `lines` did the same; callers trim it). The LF is consumed
    /// and not returned. Returns nil when the stream ends before any byte, and
    /// the partial line when it ends mid-line.
    ///
    /// A line's length is its bytes less one trailing CR, so a line of exactly
    /// `maxLineBytes` is allowed with or without its CR.
    mutating func readLine<I: AsyncIteratorProtocol>(
        from iterator: inout I
    ) async throws -> String? where I.Element == UInt8 {
        var line = [UInt8]()
        var sawByte = false
        while let byte = try await iterator.next() {
            sawByte = true
            headBytes += 1
            guard headBytes <= maxHeadBytes else {
                throw HTTPDecoder.HeadTooLargeError("Request head exceeds \(maxHeadBytes) bytes")
            }
            if byte == 0x0A { break }
            line.append(byte)
            let length = byte == 0x0D ? line.count - 1 : line.count
            guard length <= maxLineBytes else {
                throw HTTPDecoder.HeadTooLargeError("Request head line exceeds \(maxLineBytes) bytes")
            }
        }
        guard sawByte else { return nil }
        guard let string = String(bytes: line, encoding: .utf8) else {
            throw AsyncSequenceError("Invalid String Conversion")
        }
        return string
    }
}

extension AsyncSequence where Element == UInt8 {

    // some AsyncSequence<String>
    var lines: AsyncThrowingMapSequence<CollectUntil<Self>, String> {
        collectStrings(separatedBy: "\n")
    }
}
