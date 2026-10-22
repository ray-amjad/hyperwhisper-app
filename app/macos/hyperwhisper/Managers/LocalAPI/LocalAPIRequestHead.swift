//
//  LocalAPIRequestHead.swift
//  hyperwhisper
//
//  Bounds on reading a request head: the request line and the headers
//  (issue #1463).
//
//  FlyingFox reads the head before any route runs, so the origin guard, the
//  bearer check and the body cap cannot bound it. Upstream FlyingFox read each
//  line in O(n²) with no limit and no deadline: an 8 KB header took seconds, and
//  a few 300 KB headers took the whole Local API down. The app now builds a
//  patched copy (`app/macos/Vendor/FlyingFox`, see its VENDORED.md) whose
//  reader is linear and enforces the limits below.
//

import Foundation
import FlyingFox

/// The request-head limits Ray set for issue #1463 (inbox ask #266, 2026-10-08).
///
/// Plain numbers with no FlyingFox type, so `hyperwhisperTests`, which links no
/// FlyingFox, can pin them.
enum LocalAPIRequestHeadLimit {
    /// One line of the head, the request line included, without its CRLF.
    static let maxLineBytes = 8 * 1024
    /// The whole head: every line, its CRLF, and the blank line that ends it.
    static let maxHeadBytes = 64 * 1024
    /// Seconds the server waits for a complete head. On a keep-alive connection
    /// the wait starts after the previous response, so this is also how long an
    /// idle connection stays open.
    static let readDeadline: TimeInterval = 10
}

extension LocalAPIServer {

    /// The FlyingFox configuration the Local API runs with.
    ///
    /// A head over a byte limit gets `431 Request Header Fields Too Large` and
    /// the connection closes. A head not complete within `headReadDeadline`
    /// gets no answer: the connection closes and its server task ends.
    ///
    /// `headReadDeadline` is a parameter only so a test need not wait 10 s.
    nonisolated static func serverConfiguration(
        address: sockaddr_in,
        headReadDeadline: TimeInterval = LocalAPIRequestHeadLimit.readDeadline
    ) -> HTTPServer.Configuration {
        // Transcription/post-processing jobs can run much longer than the
        // FlyingFox default (15s) — a large-v3 pass on a 30s clip or a slow
        // cloud LLM round-trip routinely takes 30-90s. Allow up to 10 min
        // per request so long jobs don't return an empty body.
        HTTPServer.Configuration(
            address: address,
            timeout: 600,
            requestHeadLimits: HTTPServer.RequestHeadLimits(
                maxLineBytes: LocalAPIRequestHeadLimit.maxLineBytes,
                maxHeadBytes: LocalAPIRequestHeadLimit.maxHeadBytes,
                readTimeout: headReadDeadline
            )
        )
    }
}

/// A FlyingFox server built from `LocalAPIServer.serverConfiguration`, on an
/// ephemeral 127.0.0.1 port, with two fixed routes.
///
/// It exists for `hyperwhisperTests` (`LocalAPIRequestHeadTests`). That target
/// links no FlyingFox, so it cannot build an `HTTPServer` itself, and the real
/// `LocalAPIServer` needs the Keychain and the app's managers. This puts the
/// production configuration on a real socket with no dependencies. The app
/// never starts one.
///
/// - `GET /health` answers 200 with the body `ok`.
/// - `POST /echo` answers 200 with the request body, read through
///   `LocalAPIBodyLimit.read` like every production route that reads a body.
final class LocalAPIWireProbe: Sendable {

    let port: UInt16
    private let server: HTTPServer
    private let runTask: Task<Void, Never>

    private init(port: UInt16, server: HTTPServer, runTask: Task<Void, Never>) {
        self.port = port
        self.server = server
        self.runTask = runTask
    }

    enum ProbeError: Error {
        case noPort
    }

    static func start(
        headReadDeadline: TimeInterval = LocalAPIRequestHeadLimit.readDeadline
    ) async throws -> LocalAPIWireProbe {
        let address = try sockaddr_in.inet(ip4: "127.0.0.1", port: 0)
        let server = HTTPServer(
            config: LocalAPIServer.serverConfiguration(address: address, headReadDeadline: headReadDeadline)
        )
        await server.appendRoute("GET /health") { (_: HTTPRequest) -> HTTPResponse in
            HTTPResponse(statusCode: .ok, body: Data("ok".utf8))
        }
        await server.appendRoute("POST /echo") { (request: HTTPRequest) async -> HTTPResponse in
            switch await LocalAPIBodyLimit.read(request) {
            case .body(let data):
                return HTTPResponse(statusCode: .ok, body: data)
            case .rejected(let response):
                return response
            }
        }

        let runTask = Task<Void, Never> {
            try? await server.run()
        }
        do {
            try await server.waitUntilListening()
            guard let bound = await server.listeningAddress, case .ip4(_, let port) = bound, port > 0 else {
                throw ProbeError.noPort
            }
            return LocalAPIWireProbe(port: port, server: server, runTask: runTask)
        } catch {
            await server.stop(timeout: 0)
            runTask.cancel()
            throw error
        }
    }

    func stop() async {
        await server.stop(timeout: 1)
        runTask.cancel()
    }
}
