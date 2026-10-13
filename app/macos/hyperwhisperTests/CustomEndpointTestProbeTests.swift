//
//  CustomEndpointTestProbeTests.swift
//  hyperwhisperTests
//
//  Issue #935: the Add/Edit sheet's "Test Connection" ran a private copy of
//  the probe that recorded no verdict, so `lastTestSuccess` stayed nil and the
//  Model Library tick never drew. The sheet now runs
//  `CustomPostProcessingManager.testEndpoint(_:apiKey:session:)`, and these
//  tests drive it through a stubbed `URLSession`.
//

import Foundation
import Testing
@testable import HyperWhisper

/// Answers each request with the reply registered for its host, and counts the
/// requests per host. Keyed by host so each test owns its replies even when
/// Swift Testing runs suites in parallel.
private final class ProbeStubURLProtocol: URLProtocol {
    struct Reply {
        let status: Int
        let body: String
    }

    private static let lock = NSLock()
    private static var replies: [String: Reply] = [:]
    private static var requests: [String: [URLRequest]] = [:]

    static func makeSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ProbeStubURLProtocol.self]
        return URLSession(configuration: config)
    }

    /// Register `reply` for a fresh host and return that host.
    static func register(_ reply: Reply) -> String {
        let host = "probe-\(UUID().uuidString.lowercased()).example.com"
        lock.withLock { replies[host] = reply }
        return host
    }

    static func requests(to host: String) -> [URLRequest] {
        lock.withLock { requests[host] ?? [] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let host = request.url?.host ?? ""
        let reply = Self.lock.withLock { () -> Reply? in
            Self.requests[host, default: []].append(request)
            return Self.replies[host]
        }
        guard let url = request.url, let reply,
              let response = HTTPURLResponse(
                url: url,
                statusCode: reply.status,
                httpVersion: "HTTP/1.1",
                headerFields: ["Content-Type": "application/json"]
              ) else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// Serialized because the manager persists to the real
/// `UserDefaults.standard` key, which each test snapshots and restores.
@MainActor
@Suite(.serialized)
struct CustomEndpointTestProbeTests {
    private static let storageKey = "customPostProcessingEndpoints"

    private let session = ProbeStubURLProtocol.makeSession()

    private func failureMessage(_ result: CustomEndpointTestResult) -> String? {
        if case let .failure(error) = result { return error }
        return nil
    }

    /// Run `body` with the stored endpoints restored afterwards.
    private func withStoredEndpointsRestored(_ body: () async throws -> Void) async rethrows {
        let saved = UserDefaults.standard.data(forKey: Self.storageKey)
        defer {
            if let saved {
                UserDefaults.standard.set(saved, forKey: Self.storageKey)
            } else {
                UserDefaults.standard.removeObject(forKey: Self.storageKey)
            }
        }
        try await body()
    }

    @Test func a401ReportsTheProvidersMessageNotTheBareStatus() async {
        let host = ProbeStubURLProtocol.register(.init(
            status: 401, body: #"{"error":{"message":"Incorrect API key provided"}}"#))
        let endpoint = CustomPostProcessingEndpoint(
            name: "Probe", endpointURL: "https://\(host)/v1/chat/completions", modelName: "llama3")

        let result = await CustomPostProcessingManager()
            .testEndpoint(endpoint, apiKey: "sk-typed", session: session)

        #expect(failureMessage(result) == "HTTP 401: Incorrect API key provided")
        // The sheet's TYPED key is what goes on the wire.
        let sent = ProbeStubURLProtocol.requests(to: host)
        #expect(sent.count == 1)
        #expect(sent.first?.value(forHTTPHeaderField: "Authorization") == "Bearer sk-typed")
    }

    @Test func a503WithANonOpenAIBodyFallsBackToTheStatusLine() async {
        let host = ProbeStubURLProtocol.register(.init(status: 503, body: #"{"detail":"busy"}"#))
        let endpoint = CustomPostProcessingEndpoint(
            name: "Probe", endpointURL: "https://\(host)/v1/chat/completions", modelName: "llama3")

        let result = await CustomPostProcessingManager()
            .testEndpoint(endpoint, apiKey: "", session: session)

        #expect(failureMessage(result) == "HTTP 503: Unknown error")
        #expect(ProbeStubURLProtocol.requests(to: host).first?
            .value(forHTTPHeaderField: "Authorization") == nil)
    }

    @Test func aCompletionSucceedsAndRecordsTheVerdict() async throws {
        try await withStoredEndpointsRestored {
            let host = ProbeStubURLProtocol.register(.init(
                status: 200, body: #"{"choices":[{"message":{"content":"Hello"}}]}"#))
            let manager = CustomPostProcessingManager()
            let endpoint = try manager.addEndpoint(
                name: "Probe", endpointURL: "https://\(host)/v1/chat/completions", modelName: "llama3")

            let result = await manager.testEndpoint(endpoint, apiKey: "", session: session)

            guard case let .success(preview) = result else {
                Issue.record("expected success, got \(result)")
                return
            }
            #expect(preview == "Hello")
            #expect(manager.getEndpoint(id: endpoint.id)?.lastTestSuccess == true)
            // Persisted, so the tick survives a relaunch.
            #expect(CustomPostProcessingManager().getEndpoint(id: endpoint.id)?.lastTestSuccess == true)
        }
    }

    @Test func aNonJSON200ReportsTheShapeErrorAndRecordsAFailure() async throws {
        try await withStoredEndpointsRestored {
            let host = ProbeStubURLProtocol.register(.init(status: 200, body: "<html>not json</html>"))
            let manager = CustomPostProcessingManager()
            let endpoint = try manager.addEndpoint(
                name: "Probe", endpointURL: "https://\(host)/v1/chat/completions", modelName: "llama3")

            let result = await manager.testEndpoint(endpoint, apiKey: "", session: session)

            #expect(failureMessage(result) == "Invalid response format - expected OpenAI-compatible response")
            #expect(manager.getEndpoint(id: endpoint.id)?.lastTestSuccess == false)
        }
    }

    @Test func aCorrectableURLReturnsTheSuggestionAndSendsNothing() async {
        let host = ProbeStubURLProtocol.register(.init(
            status: 200, body: #"{"choices":[{"message":{"content":"Hello"}}]}"#))
        let endpoint = CustomPostProcessingEndpoint(
            name: "Probe",
            endpointURL: "https://user:secret@\(host)/v1/chat/completions",
            modelName: "llama3")

        let result = await CustomPostProcessingManager()
            .testEndpoint(endpoint, apiKey: "", session: session)

        #expect(failureMessage(result)
            == "the endpoint URL must not contain a username or password — did you mean https://\(host)/v1/chat/completions?")
        #expect(ProbeStubURLProtocol.requests(to: host).isEmpty)
    }

    @Test func testingUnsavedEditsLeavesTheStoredVerdictAlone() async throws {
        try await withStoredEndpointsRestored {
            let savedHost = ProbeStubURLProtocol.register(.init(status: 500, body: ""))
            let editedHost = ProbeStubURLProtocol.register(.init(
                status: 200, body: #"{"choices":[{"message":{"content":"Hello"}}]}"#))
            let manager = CustomPostProcessingManager()
            let stored = try manager.addEndpoint(
                name: "Probe", endpointURL: "https://\(savedHost)/v1/chat/completions", modelName: "llama3")
            var edited = stored
            edited.endpointURL = "https://\(editedHost)/v1/chat/completions"

            let result = await manager.testEndpoint(edited, apiKey: "", session: session)

            guard case .success = result else {
                Issue.record("expected success, got \(result)")
                return
            }
            #expect(manager.getEndpoint(id: stored.id)?.lastTestSuccess == nil)
        }
    }
}
