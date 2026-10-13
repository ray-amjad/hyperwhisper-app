//
//  CustomEndpointTestProbeTests.swift
//  hyperwhisperTests
//
//  Issue #935: the Add/Edit sheet's "Test Connection" ran a private copy of
//  the probe that recorded no verdict, so `lastTestSuccess` stayed nil and the
//  Model Library tick never drew. The sheet now runs
//  `CustomPostProcessingManager.testEndpoint(_:apiKey:session:)`, and these
//  tests drive it through a stubbed `URLSession`. That probe records nothing:
//  the sheet hands what it tested to the save, which records the verdict only
//  when the saved URL, model and key are the ones tested.
//
//  No test here writes a key: the test host has no Keychain access, so every
//  saved endpoint is keyless and a typed key is what makes a mismatch.
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
    private static var sent: [String: [URLRequest]] = [:]

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
        lock.withLock { sent[host] ?? [] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let host = request.url?.host ?? ""
        let reply = Self.lock.withLock { () -> Reply? in
            Self.sent[host, default: []].append(request)
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

    private static let helloBody = #"{"choices":[{"message":{"content":"Hello"}}]}"#

    private func url(_ host: String) -> String { "https://\(host)/v1/chat/completions" }

    private func tested(_ endpoint: CustomPostProcessingEndpoint, apiKey: String = "", success: Bool)
        -> CustomEndpointTestedConfiguration {
        CustomEndpointTestedConfiguration(
            endpointURL: endpoint.endpointURL, modelName: endpoint.modelName, apiKey: apiKey, success: success)
    }

    @Test func aSavedEndpointTestSucceedsAndRecordsTheVerdict() async throws {
        try await withStoredEndpointsRestored {
            let host = ProbeStubURLProtocol.register(.init(status: 200, body: Self.helloBody))
            let manager = CustomPostProcessingManager()
            let endpoint = try manager.addEndpoint(name: "Probe", endpointURL: url(host), modelName: "llama3")

            let result = await manager.testEndpoint(id: endpoint.id, session: session)

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
            let endpoint = try manager.addEndpoint(name: "Probe", endpointURL: url(host), modelName: "llama3")

            let result = await manager.testEndpoint(id: endpoint.id, session: session)

            #expect(failureMessage(result) == "Invalid response format - expected OpenAI-compatible response")
            #expect(manager.getEndpoint(id: endpoint.id)?.lastTestSuccess == false)
        }
    }

    /// A sheet test the user then cancels must not touch the stored endpoint.
    @Test func theSheetProbeRecordsNothingOnTheStoredEndpoint() async throws {
        try await withStoredEndpointsRestored {
            let host = ProbeStubURLProtocol.register(.init(
                status: 401, body: #"{"error":{"message":"Incorrect API key provided"}}"#))
            let manager = CustomPostProcessingManager()
            let stored = try manager.addEndpoint(name: "Probe", endpointURL: url(host), modelName: "llama3")
            try manager.updateEndpoint(id: stored.id, testedConfiguration: tested(stored, success: true))

            let result = await manager.testEndpoint(stored, apiKey: "sk-mistyped", session: session)

            #expect(failureMessage(result) == "HTTP 401: Incorrect API key provided")
            #expect(manager.getEndpoint(id: stored.id)?.lastTestSuccess == true)
        }
    }

    @Test func addingTheTestedConfigurationRecordsItsVerdict() async throws {
        try await withStoredEndpointsRestored {
            let host = ProbeStubURLProtocol.register(.init(status: 200, body: Self.helloBody))
            let manager = CustomPostProcessingManager()
            let draft = CustomPostProcessingEndpoint(name: "Probe", endpointURL: url(host), modelName: "llama3")
            let result = await manager.testEndpoint(draft, apiKey: "", session: session)
            guard case .success = result else {
                Issue.record("expected success, got \(result)")
                return
            }

            // The sheet's draft has its own UUID; the save makes another.
            let saved = try manager.addEndpoint(
                name: "Probe", endpointURL: " \(url(host)) ", modelName: "llama3",
                testedConfiguration: tested(draft, success: true))

            #expect(saved.id != draft.id)
            #expect(manager.getEndpoint(id: saved.id)?.lastTestSuccess == true)
            #expect(CustomPostProcessingManager().getEndpoint(id: saved.id)?.lastTestSuccess == true)
        }
    }

    @Test func addingADifferentConfigurationRecordsNoVerdict() async throws {
        try await withStoredEndpointsRestored {
            let manager = CustomPostProcessingManager()
            let draft = CustomPostProcessingEndpoint(
                name: "Probe", endpointURL: "https://a.example.com/v1/chat/completions", modelName: "llama3")

            let otherModel = try manager.addEndpoint(
                name: "Probe", endpointURL: draft.endpointURL, modelName: "llama3.2",
                testedConfiguration: tested(draft, success: true))
            // Tested with a typed key, saved with none.
            let otherKey = try manager.addEndpoint(
                name: "Probe", endpointURL: draft.endpointURL, modelName: "llama3",
                testedConfiguration: tested(draft, apiKey: "sk-typed", success: true))

            #expect(manager.getEndpoint(id: otherModel.id)?.lastTestSuccess == nil)
            #expect(manager.getEndpoint(id: otherKey.id)?.lastTestSuccess == nil)
        }
    }

    @Test func savingATestedNewURLRecordsItsVerdict() async throws {
        try await withStoredEndpointsRestored {
            let manager = CustomPostProcessingManager()
            let stored = try manager.addEndpoint(
                name: "Probe", endpointURL: "https://old.example.com/v1/chat/completions", modelName: "llama3")
            try manager.updateEndpoint(id: stored.id, testedConfiguration: tested(stored, success: false))
            var edited = stored
            edited.endpointURL = "https://new.example.com/v1/chat/completions"

            try manager.updateEndpoint(
                id: stored.id, endpointURL: edited.endpointURL, modelName: edited.modelName,
                testedConfiguration: tested(edited, success: true))

            #expect(manager.getEndpoint(id: stored.id)?.lastTestSuccess == true)
        }
    }

    @Test func savingAnUntestedModelClearsTheOldVerdict() async throws {
        try await withStoredEndpointsRestored {
            let manager = CustomPostProcessingManager()
            let stored = try manager.addEndpoint(
                name: "Probe", endpointURL: "https://a.example.com/v1/chat/completions", modelName: "llama3")
            try manager.updateEndpoint(id: stored.id, testedConfiguration: tested(stored, success: true))
            var edited = stored
            edited.modelName = "no-such-model"

            // The sheet's last test was of a third model, not the one saved.
            var other = stored
            other.modelName = "llama3.2"
            try manager.updateEndpoint(
                id: stored.id, endpointURL: edited.endpointURL, modelName: edited.modelName,
                testedConfiguration: tested(other, success: true))

            #expect(manager.getEndpoint(id: stored.id)?.lastTestSuccess == nil)
        }
    }

    @Test func savingWithoutChangesKeepsTheVerdict() async throws {
        try await withStoredEndpointsRestored {
            let manager = CustomPostProcessingManager()
            let stored = try manager.addEndpoint(
                name: "Probe", endpointURL: "https://a.example.com/v1/chat/completions", modelName: "llama3")
            try manager.updateEndpoint(id: stored.id, testedConfiguration: tested(stored, success: true))

            // A rename, with a test of a typed key that is not saved.
            try manager.updateEndpoint(
                id: stored.id, name: "Renamed", endpointURL: stored.endpointURL, modelName: stored.modelName,
                testedConfiguration: tested(stored, apiKey: "sk-typed", success: false))

            #expect(manager.getEndpoint(id: stored.id)?.lastTestSuccess == true)
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
}
