//
//  LocalAPIRequestHeadTests.swift
//  hyperwhisperTests
//
//  The request-head limits from issue #1463, over a real socket.
//
//  FlyingFox reads the request line and the headers before any route runs, so
//  nothing short of a real connection reaches that code. These tests start a
//  `LocalAPIWireProbe` — a FlyingFox server built from the same
//  `LocalAPIServer.serverConfiguration` the Local API runs with — and talk raw
//  HTTP/1.1 to it over a POSIX socket, so every byte of the head is the test's
//  choice. This target links no FlyingFox, which is why the probe lives in the
//  app and the client here names no FlyingFox type.
//
//  Before the fix, the head reader was O(n²) per line with no limit and no
//  deadline: an 8 KB header line took ~2.5 s on a MacBook, and a 100 KB one
//  minutes. The refusal tests below therefore time out on the old reader, and
//  the stalled-head test waits forever for a close that never comes.
//
//  The blocking socket calls run on a Dispatch queue, never on the Swift
//  concurrency pool the server itself needs.
//

import Darwin
import Foundation
import Testing
@testable import HyperWhisper

@Suite(.serialized)
struct LocalAPIRequestHeadTests {

    // MARK: - The numbers

    /// Ray's decisions on #1463 (inbox ask #266): 8 KB a line, the request line
    /// included; 64 KB for the head; 10 s for the head to arrive.
    @Test func theLimitsAreTheOnesRaySet() {
        #expect(LocalAPIRequestHeadLimit.maxLineBytes == 8_192)
        #expect(LocalAPIRequestHeadLimit.maxHeadBytes == 65_536)
        #expect(LocalAPIRequestHeadLimit.readDeadline == 10)
    }

    // MARK: - Normal requests behave as before

    @Test func aNormalRequestStillGets200() async throws {
        try await withProbe { port in
            let reply = try await offPool {
                let connection = try RawConnection(port: port)
                defer { connection.close() }
                try connection.write(RawRequest.get(port: port))
                return try connection.readResponse()
            }
            #expect(reply?.status == 200)
            #expect(reply?.body == Data("ok".utf8))
        }
    }

    /// HTTP/1.1 keep-alive: the head reader must leave the shared byte buffer
    /// exactly at the start of the next request.
    @Test func keepAliveStillServesTheNextRequest() async throws {
        try await withProbe { port in
            let replies = try await offPool {
                let connection = try RawConnection(port: port)
                defer { connection.close() }
                try connection.write(RawRequest.get(port: port))
                let first = try connection.readResponse()
                try connection.write(RawRequest.get(port: port))
                let second = try connection.readResponse()
                return [first?.status, second?.status]
            }
            #expect(replies == [200, 200])
        }
    }

    /// A body after the head still reaches the handler whole: a small one
    /// (read with the head) and one larger than FlyingFox's 4 KB shared buffer
    /// (read lazily from the same stream the head reader used).
    @Test(arguments: [11, 20_000])
    func aBodyStillReachesTheHandler(size: Int) async throws {
        let body = Data((0..<size).map { UInt8(truncatingIfNeeded: $0 % 251) })
        try await withProbe { port in
            let reply = try await offPool {
                let connection = try RawConnection(port: port)
                defer { connection.close() }
                try connection.write(RawRequest.post(port: port, path: "/echo", body: body))
                return try connection.readResponse()
            }
            #expect(reply?.status == 200)
            #expect(reply?.body == body)
        }
    }

    /// A head that arrives in pieces, each well inside the deadline, is served.
    /// The deadline is far longer than the 0.3 s gap and than any stall a loaded
    /// runner adds between the two writes: at 2 s it raced the parallel suite
    /// on a hosted runner and failed there (issue #2398). What this test proves
    /// is that the pieces are joined, not how long the deadline is.
    @Test func aHeadDeliveredInPiecesWithinTheDeadlineIsServed() async throws {
        try await withProbe(headReadDeadline: 30) { port in
            let reply = try await offPool {
                let connection = try RawConnection(port: port, timeout: 60)
                defer { connection.close() }
                let request = RawRequest.get(port: port)
                let half = request.count / 2
                try connection.write(request.prefix(half))
                Thread.sleep(forTimeInterval: 0.3)
                try connection.write(request.dropFirst(half))
                return try connection.readResponse()
            }
            #expect(reply?.status == 200)
        }
    }

    // MARK: - The line limit

    /// A header line of exactly 8,192 bytes (CRLF not counted) is accepted,
    /// with a 200 rather than a close at the read deadline. The server reads
    /// the whole head under its own deadline and answers a head that misses it
    /// with nothing but a close, so the 200 itself proves the head was read in
    /// time. The test keeps no wall-clock bound of its own: a 1 s budget
    /// failed on a loaded hosted runner (#2221), and a runner that stalls the
    /// test process for longer than the deadline (#2526) would fail any other.
    /// The trade: a reader that went back to O(n²) still fails the 64 KB head
    /// test below at the issue's ~2.5 s a line (8 lines, past the deadline),
    /// but a much faster machine could read it in time. No test here bounds
    /// speed tighter.
    @Test func aHeaderLineAtTheLimitIsAccepted() async throws {
        try await withProbe { port in
            let line = RawRequest.headerLine(name: "X-Pad", length: LocalAPIRequestHeadLimit.maxLineBytes)
            let reply = try await offPool {
                let connection = try RawConnection(port: port, timeout: slowRunnerTimeout)
                defer { connection.close() }
                try connection.write(RawRequest.get(port: port, extraLines: [line]))
                return try connection.readResponse()
            }
            #expect(reply?.status == 200)
        }
    }

    @Test func aHeaderLineOneByteOverTheLimitGets431AndAClose() async throws {
        try await withProbe { port in
            let line = RawRequest.headerLine(name: "X-Pad", length: LocalAPIRequestHeadLimit.maxLineBytes + 1)
            let outcome = try await offPool {
                try RawConnection.sendAndReadToClose(port: port, RawRequest.get(port: port, extraLines: [line]))
            }
            #expect(outcome.reply?.status == 431)
            #expect(outcome.reply?.headers["connection"]?.lowercased() == "close")
            #expect(outcome.closedAfterReply, "the server must close the connection after a 431")
        }
    }

    /// The request line counts as a line too: a long query string is refused
    /// the same way as a long header.
    @Test func aRequestLineOverTheLimitGets431() async throws {
        try await withProbe { port in
            let query = String(repeating: "a", count: LocalAPIRequestHeadLimit.maxLineBytes)
            let request = Data("GET /health?q=\(query) HTTP/1.1\r\nHost: 127.0.0.1:\(port)\r\n\r\n".utf8)
            let outcome = try await offPool { try RawConnection.sendAndReadToClose(port: port, request) }
            #expect(outcome.reply?.status == 431)
            #expect(outcome.closedAfterReply)
        }
    }

    // MARK: - The head limit

    /// Exactly 65,536 bytes of head (every line under the line limit, CRLFs and
    /// the final blank line counted) is served inside the head deadline: 8
    /// lines of about 8 KB each. One byte more is refused. The 200 is the
    /// deadline check, for the reason on the 8 KB test above (#2221, #2526).
    @Test func aHeadAtTheLimitIsServedAndOneByteMoreGets431() async throws {
        try await withProbe { port in
            let atLimit = RawRequest.getWithHead(port: port, totalBytes: LocalAPIRequestHeadLimit.maxHeadBytes)
            let overLimit = RawRequest.getWithHead(port: port, totalBytes: LocalAPIRequestHeadLimit.maxHeadBytes + 1)
            #expect(atLimit.count == 65_536)
            #expect(overLimit.count == 65_537)

            let accepted = try await offPool {
                let connection = try RawConnection(port: port, timeout: slowRunnerTimeout)
                defer { connection.close() }
                try connection.write(atLimit)
                return try connection.readResponse()
            }
            #expect(accepted?.status == 200)

            let refused = try await offPool { try RawConnection.sendAndReadToClose(port: port, overLimit) }
            #expect(refused.reply?.status == 431)
            #expect(refused.closedAfterReply)
        }
    }

    /// The issue's shape: one 100 KB header line. Refused with a 431 inside the
    /// head deadline, not after minutes of CPU. A head that missed the
    /// deadline would get a bare close, so the 431 is the deadline check; no
    /// wall-clock bound of the test's own (#2221, #2526).
    @Test func aHundredKilobyteHeaderIsRefusedBeforeTheDeadline() async throws {
        try await withProbe { port in
            let line = RawRequest.headerLine(name: "X-Junk", length: 100 * 1024)
            let request = RawRequest.get(port: port, extraLines: [line])
            let outcome = try await offPool {
                try RawConnection.sendAndReadToClose(port: port, request, timeout: slowRunnerTimeout)
            }
            #expect(outcome.reply?.status == 431)
            #expect(outcome.closedAfterReply)
        }
    }

    /// The server still answers a normal request while it refuses big heads.
    @Test func refusingBigHeadsLeavesTheServerAnswering() async throws {
        try await withProbe { port in
            let line = RawRequest.headerLine(name: "X-Junk", length: 300 * 1024)
            let big = RawRequest.get(port: port, extraLines: [line])
            let statuses = try await offPool {
                var statuses: [Int?] = []
                for _ in 0..<6 {
                    statuses.append(try RawConnection.sendAndReadToClose(port: port, big).reply?.status)
                }
                let connection = try RawConnection(port: port)
                defer { connection.close() }
                try connection.write(RawRequest.get(port: port))
                statuses.append(try connection.readResponse()?.status)
                return statuses
            }
            #expect(statuses == [431, 431, 431, 431, 431, 431, 200])
        }
    }

    // MARK: - The deadline

    /// A client that sends part of a head and then goes quiet loses the
    /// connection at the deadline, with no answer, and does not hold its server
    /// task. The deadline is injected (0.5 s) so the test does not wait 10 s.
    /// The upper bound is the production deadline, not a few seconds: a 3 s
    /// bound failed once in 20 full-bundle runs on a loaded runner (#2221).
    /// Closing before 10 s still proves the injected deadline closed it, and
    /// `readToClose` throws `timedOut` if the server never closes at all.
    @Test func aHeadThatStallsIsDroppedAtTheDeadline() async throws {
        try await withProbe(headReadDeadline: 0.5) { port in
            let (rest, elapsed) = try await offPool {
                let connection = try RawConnection(port: port, timeout: slowRunnerTimeout)
                defer { connection.close() }
                let start = Date()
                try connection.write(Data("GET /health HTTP/1.1\r\nHost: 127.0.0.1:\(port)\r\n".utf8))
                let rest = try connection.readToClose()
                return (rest, Date().timeIntervalSince(start))
            }
            #expect(rest.isEmpty, "a stalled head gets no response, only a close")
            #expect(elapsed >= 0.4, "closed after \(elapsed) s, before the deadline")
            #expect(
                elapsed < LocalAPIRequestHeadLimit.readDeadline,
                "closed after \(elapsed) s, not by the injected 0.5 s deadline"
            )
        }
    }

    /// A connection that never sends a byte is closed at the deadline too.
    /// Bounded the same way as the stalled head above.
    @Test func aSilentConnectionIsDroppedAtTheDeadline() async throws {
        try await withProbe(headReadDeadline: 0.5) { port in
            let elapsed = try await offPool {
                let connection = try RawConnection(port: port, timeout: slowRunnerTimeout)
                defer { connection.close() }
                let start = Date()
                _ = try connection.readToClose()
                return Date().timeIntervalSince(start)
            }
            #expect(
                elapsed < LocalAPIRequestHeadLimit.readDeadline,
                "a silent connection stayed open \(elapsed) s, past the injected 0.5 s deadline"
            )
        }
    }
}

// MARK: - Fixtures

/// The client's socket timeout in the tests that wait out a head deadline:
/// past the deadline, so the server's answer or close always comes first on a
/// slow runner. It guards against a hang; it is not a speed bound.
private let slowRunnerTimeout: TimeInterval = 2 * LocalAPIRequestHeadLimit.readDeadline

private func withProbe(
    headReadDeadline: TimeInterval = LocalAPIRequestHeadLimit.readDeadline,
    _ body: (UInt16) async throws -> Void
) async throws {
    let probe = try await LocalAPIWireProbe.start(headReadDeadline: headReadDeadline)
    do {
        try await body(probe.port)
    } catch {
        await probe.stop()
        throw error
    }
    await probe.stop()
}

/// Runs blocking socket work on a Dispatch queue, off the Swift concurrency
/// pool, so a blocked `recv` can never starve the server under test.
private func offPool<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
        DispatchQueue.global(qos: .userInitiated).async {
            continuation.resume(with: Result { try work() })
        }
    }
}

private enum RawRequest {

    static func get(port: UInt16, extraLines: [String] = []) -> Data {
        var head = "GET /health HTTP/1.1\r\nHost: 127.0.0.1:\(port)\r\n"
        for line in extraLines {
            head += line + "\r\n"
        }
        head += "\r\n"
        return Data(head.utf8)
    }

    static func post(port: UInt16, path: String, body: Data) -> Data {
        let head = "POST \(path) HTTP/1.1\r\nHost: 127.0.0.1:\(port)\r\n"
            + "Content-Type: application/octet-stream\r\nContent-Length: \(body.count)\r\n\r\n"
        return Data(head.utf8) + body
    }

    /// `name: aaaa…`, exactly `length` bytes long without its CRLF.
    static func headerLine(name: String, length: Int) -> String {
        let prefix = "\(name): "
        return prefix + String(repeating: "a", count: length - prefix.utf8.count)
    }

    /// A GET whose whole head (CRLFs and the final blank line included) is
    /// exactly `totalBytes`, padded with header lines shorter than 8 KB.
    static func getWithHead(port: UInt16, totalBytes: Int) -> Data {
        let base = get(port: port).count
        let padding = totalBytes - base
        let maxLineWithCRLF = 8_000 + 2
        let count = (padding + maxLineWithCRLF - 1) / maxLineWithCRLF
        var lines: [String] = []
        for index in 0..<count {
            let share = padding / count + (index < padding % count ? 1 : 0)
            lines.append(headerLine(name: "X-Pad-\(index)", length: share - 2))
        }
        return get(port: port, extraLines: lines)
    }
}

private struct RawReply: Sendable {
    var status: Int?
    var headers: [String: String]
    var body: Data
}

private struct RawOutcome: Sendable {
    var reply: RawReply?
    /// True when the server closed the connection after its reply.
    var closedAfterReply: Bool
}

private enum RawSocketError: Error {
    case syscall(String, Int32)
    case timedOut
    case truncated
}

/// A blocking HTTP/1.1 client over one POSIX TCP socket to 127.0.0.1.
private final class RawConnection {

    private let fd: Int32
    private var buffer = Data()

    init(port: UInt16, timeout: TimeInterval = 5) throws {
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw RawSocketError.syscall("socket", errno) }

        var on: Int32 = 1
        _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        let seconds = Int(timeout)
        var interval = timeval(tv_sec: seconds, tv_usec: Int32((timeout - Double(seconds)) * 1_000_000))
        _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &interval, socklen_t(MemoryLayout<timeval>.size))
        _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &interval, socklen_t(MemoryLayout<timeval>.size))

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard result == 0 else {
            let code = errno
            _ = Darwin.close(fd)
            throw RawSocketError.syscall("connect", code)
        }
        self.fd = fd
    }

    func close() {
        _ = Darwin.close(fd)
    }

    /// Writes all of `data`. Returns false if the server closed the connection
    /// first, which a refusal is allowed to do.
    @discardableResult
    func write(_ data: Data) throws -> Bool {
        let bytes = [UInt8](data)
        var offset = 0
        while offset < bytes.count {
            let sent = bytes.withUnsafeBytes { raw in
                Darwin.send(fd, raw.baseAddress! + offset, bytes.count - offset, 0)
            }
            if sent > 0 {
                offset += sent
                continue
            }
            let code = errno
            if code == EINTR { continue }
            if code == EPIPE || code == ECONNRESET { return false }
            if code == EAGAIN || code == EWOULDBLOCK { throw RawSocketError.timedOut }
            throw RawSocketError.syscall("send", code)
        }
        return true
    }

    /// One `recv` into the buffer. False at end of stream.
    private func fill() throws -> Bool {
        var chunk = [UInt8](repeating: 0, count: 65_536)
        while true {
            let received = chunk.withUnsafeMutableBytes { raw in
                Darwin.recv(fd, raw.baseAddress, raw.count, 0)
            }
            if received > 0 {
                buffer.append(contentsOf: chunk[0..<received])
                return true
            }
            if received == 0 { return false }
            let code = errno
            if code == EINTR { continue }
            if code == ECONNRESET { return false }
            if code == EAGAIN || code == EWOULDBLOCK { throw RawSocketError.timedOut }
            throw RawSocketError.syscall("recv", code)
        }
    }

    /// The next response: status, headers (lower-cased names) and a
    /// `Content-Length` body. Nil if the stream ends before a byte arrives.
    func readResponse() throws -> RawReply? {
        let separator = Data("\r\n\r\n".utf8)
        var headEnd = buffer.range(of: separator)
        while headEnd == nil {
            guard try fill() else {
                if buffer.isEmpty { return nil }
                throw RawSocketError.truncated
            }
            headEnd = buffer.range(of: separator)
        }
        guard let headEnd else { throw RawSocketError.truncated }

        let head = String(decoding: buffer.subdata(in: 0..<headEnd.lowerBound), as: UTF8.self)
        var lines = head.components(separatedBy: "\r\n")
        let statusLine = lines.removeFirst()
        let parts = statusLine.split(separator: " ", maxSplits: 2)
        let status = parts.count >= 2 ? Int(parts[1]) : nil
        var headers: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].lowercased()
            headers[name] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }

        let length = Int(headers["content-length"] ?? "0") ?? 0
        let bodyStart = headEnd.upperBound
        while buffer.count - bodyStart < length {
            guard try fill() else { throw RawSocketError.truncated }
        }
        let body = buffer.subdata(in: bodyStart..<(bodyStart + length))
        buffer = buffer.subdata(in: (bodyStart + length)..<buffer.count)
        return RawReply(status: status, headers: headers, body: body)
    }

    /// Everything until the server closes the connection.
    func readToClose() throws -> Data {
        while try fill() {}
        defer { buffer = Data() }
        return buffer
    }

    /// Connect, send `request`, read one response, then read on until the
    /// server closes.
    static func sendAndReadToClose(
        port: UInt16,
        _ request: Data,
        timeout: TimeInterval = 5
    ) throws -> RawOutcome {
        let connection = try RawConnection(port: port, timeout: timeout)
        defer { connection.close() }
        try connection.write(request)
        let reply = try connection.readResponse()
        let rest = try connection.readToClose()
        return RawOutcome(reply: reply, closedAfterReply: rest.isEmpty)
    }
}
