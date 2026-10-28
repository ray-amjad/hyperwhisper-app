//
//  LocalModelFetcherTests.swift
//  hyperwhisperTests
//
//  Issue #937: `LocalModelFetcher` decides which message the custom endpoint
//  sheet shows when an Ollama or LM Studio model list fails, and it had no
//  test because both fetch methods called `URLSession.shared`. They now take a
//  `session:` parameter, and these tests drive them through a stubbed one.
//

import Foundation
import Testing
@testable import HyperWhisper

/// Answers each request with the outcome registered for its host, and records
/// the requests per host. Keyed by host so each test owns its outcome even when
/// Swift Testing runs tests in parallel.
private final class FetcherStubURLProtocol: URLProtocol {
    enum Outcome {
        case reply(status: Int, body: String)
        case fail(URLError.Code)
    }

    private static let lock = NSLock()
    private static var outcomes: [String: Outcome] = [:]
    private static var sent: [String: [URLRequest]] = [:]

    static func makeSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [FetcherStubURLProtocol.self]
        return URLSession(configuration: config)
    }

    /// Register `outcome` for a fresh host and return that host.
    static func register(_ outcome: Outcome) -> String {
        let host = "fetch-\(UUID().uuidString.lowercased()).example.com"
        lock.withLock { outcomes[host] = outcome }
        return host
    }

    static func requests(to host: String) -> [URLRequest] {
        lock.withLock { sent[host] ?? [] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let host = request.url?.host ?? ""
        let outcome = Self.lock.withLock { () -> Outcome? in
            Self.sent[host, default: []].append(request)
            return Self.outcomes[host]
        }
        switch outcome {
        case let .reply(status, body):
            guard let url = request.url,
                  let response = HTTPURLResponse(
                    url: url,
                    statusCode: status,
                    httpVersion: "HTTP/1.1",
                    headerFields: ["Content-Type": "application/json"]
                  ) else {
                client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
                return
            }
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        case let .fail(code):
            client?.urlProtocol(self, didFailWithError: URLError(code))
        case nil:
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
        }
    }

    override func stopLoading() {}
}

struct LocalModelFetcherTests {
    private let session = FetcherStubURLProtocol.makeSession()
    private let fetcher = LocalModelFetcher()

    /// The `FetchError` an Ollama fetch against `host` throws, or nil when it returns.
    private func ollamaError(_ host: String) async -> LocalModelFetcher.FetchError? {
        do {
            _ = try await fetcher.fetchOllamaModels(baseURL: "http://\(host)", session: session)
            return nil
        } catch let error as LocalModelFetcher.FetchError {
            return error
        } catch {
            Issue.record("expected a FetchError, got \(error)")
            return nil
        }
    }

    private func isServerNotRunning(_ error: LocalModelFetcher.FetchError?) -> Bool {
        if case .serverNotRunning = error { return true }
        return false
    }

    // MARK: 1. URLError classification

    @Test(arguments: [URLError.Code.timedOut, .cannotConnectToHost])
    func aTimeoutOrRefusedConnectionReportsServerNotRunning(_ code: URLError.Code) async {
        let host = FetcherStubURLProtocol.register(.fail(code))

        let error = await ollamaError(host)

        #expect(isServerNotRunning(error))
        #expect(error?.errorDescription == "Server is not running or unreachable")
    }

    @Test func anyOtherURLErrorPassesTheSystemStringThrough() async {
        let host = FetcherStubURLProtocol.register(.fail(.badServerResponse))

        let error = await ollamaError(host)

        guard case let .networkError(message) = error else {
            Issue.record("expected .networkError, got \(String(describing: error))")
            return
        }
        #expect(message == URLError(.badServerResponse).localizedDescription)
        #expect(message != "Server is not running or unreachable")
    }

    // MARK: 2. Status ladder

    @Test func a404ReportsTheStatusNotServerNotRunning() async {
        let host = FetcherStubURLProtocol.register(.reply(status: 404, body: "not found"))

        let error = await ollamaError(host)

        guard case let .invalidResponse(statusCode) = error else {
            Issue.record("expected .invalidResponse, got \(String(describing: error))")
            return
        }
        #expect(statusCode == 404)
    }

    // MARK: 3. Body shape

    @Test func a200WithTheWrongShapeReportsDecodingFailed() async {
        let host = FetcherStubURLProtocol.register(.reply(status: 200, body: #"{"data":[{"id":"x"}]}"#))

        let error = await ollamaError(host)

        guard case .decodingFailed = error else {
            Issue.record("expected .decodingFailed, got \(String(describing: error))")
            return
        }
    }

    // MARK: 4. URL normalisation and paths

    @Test func aTrailingSlashReachesApiTagsOnceWithNoDoubledSlash() async throws {
        let host = FetcherStubURLProtocol.register(.reply(
            status: 200, body: #"{"models":[{"name":"llama3:8b"},{"name":"qwen2.5"}]}"#))

        let names = try await fetcher.fetchOllamaModels(baseURL: "http://\(host)/", session: session)

        #expect(names == ["llama3:8b", "qwen2.5"])
        let sent = FetcherStubURLProtocol.requests(to: host)
        #expect(sent.count == 1)
        #expect(sent.first?.url?.absoluteString == "http://\(host)/api/tags")
        #expect(sent.first?.httpMethod == "GET")
    }

    @Test func lmStudioDecodesItsOwnShapeNotOllamas() async {
        let host = FetcherStubURLProtocol.register(.reply(status: 200, body: #"{"models":[{"name":"llama3"}]}"#))

        do {
            _ = try await fetcher.fetchLMStudioModels(baseURL: "http://\(host)/v1", session: session)
            Issue.record("expected .decodingFailed, got a model list")
        } catch let error as LocalModelFetcher.FetchError {
            guard case .decodingFailed = error else {
                Issue.record("expected .decodingFailed, got \(error)")
                return
            }
        } catch {
            Issue.record("expected a FetchError, got \(error)")
        }
    }

    @Test func lmStudioReachesModelsUnderTheV1Base() async throws {
        let host = FetcherStubURLProtocol.register(.reply(
            status: 200, body: #"{"data":[{"id":"qwen2.5-7b-instruct"}]}"#))

        let ids = try await fetcher.fetchLMStudioModels(baseURL: "http://\(host)/v1/", session: session)

        #expect(ids == ["qwen2.5-7b-instruct"])
        let sent = FetcherStubURLProtocol.requests(to: host)
        #expect(sent.count == 1)
        #expect(sent.first?.url?.absoluteString == "http://\(host)/v1/models")
    }
}
