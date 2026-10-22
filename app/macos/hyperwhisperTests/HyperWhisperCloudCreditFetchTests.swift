//
//  HyperWhisperCloudCreditFetchTests.swift
//  hyperwhisperTests
//
//  #687: the credit fetch maps every reply from the usage endpoint to one
//  `HyperWhisperCloudError`, and `refreshServerLicenseCache` must reach the
//  server even when a fetch is cached. These drive a real
//  `HyperWhisperCloudManager` through a scripted transport, so each rule runs
//  in production code. The identifier is a dummy: in production it is the
//  account key, a paid-service bearer credential.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct HyperWhisperCloudCreditFetchTests {

    private static let identifier = "test-identifier"

    @Test func non200WithMessageThrowsThatMessage() async {
        let server = ScriptedCreditServer([.reply(402, #"{"message":"Out of credits"}"#)])
        let error = await fetchError(server)
        #expect(serverErrorMessage(error) == "Out of credits")
    }

    @Test func non200WithOnlyErrorKeyFallsBackToIt() async {
        let server = ScriptedCreditServer([.reply(402, #"{"error":"not entitled"}"#)])
        let error = await fetchError(server)
        #expect(serverErrorMessage(error) == "not entitled")
    }

    @Test func non200WithNonJSONBodyThrowsHTTPStatus() async {
        let server = ScriptedCreditServer([.reply(503, "<html>Service Unavailable</html>")])
        let error = await fetchError(server)
        #expect(serverErrorMessage(error) == "HTTP 503")
    }

    @Test func urlErrorThrowsTransientNetwork() async {
        let server = ScriptedCreditServer([.fail(URLError(.timedOut))])
        let error = await fetchError(server)
        guard case .transientNetwork = error else {
            Issue.record("expected transientNetwork, got \(String(describing: error))")
            return
        }
    }

    @Test func undecodable200ThrowsInvalidResponse() async {
        let server = ScriptedCreditServer([.reply(200, #"{"credits_remaining":"lots"}"#)])
        let error = await fetchError(server)
        guard case .invalidResponse = error else {
            Issue.record("expected invalidResponse, got \(String(describing: error))")
            return
        }
    }

    @Test func licenseCacheRefreshBypassesWarmCacheAndRejectsUnlicensedAccount() async throws {
        let server = ScriptedCreditServer([
            .reply(200, Self.creditsJSON(isLicensed: false)),
            .reply(200, Self.creditsJSON(isLicensed: false)),
        ])
        let manager = makeManager(server)

        // Warm the 60 s cache, and show it is warm: a second plain fetch is
        // answered from it without reaching the server.
        _ = try await manager.fetchCredits()
        _ = try await manager.fetchCredits()
        #expect(server.requests.count == 1)

        var thrown: Error?
        do {
            try await manager.refreshServerLicenseCache(for: Self.identifier)
        } catch {
            thrown = error
        }

        #expect(server.requests.count == 2)
        let refresh = try #require(server.requests.last?.url)
        let query = URLComponents(url: refresh, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(query.contains(URLQueryItem(name: "identifier", value: Self.identifier)))
        #expect(query.contains(URLQueryItem(name: "force_refresh", value: "true")))

        guard case .invalidResponse(let message) = thrown as? HyperWhisperCloudError else {
            Issue.record("expected invalidResponse, got \(String(describing: thrown))")
            return
        }
        #expect(message == "Server license-cache refresh did not confirm a licensed account")
    }

    // MARK: - Helpers

    private func makeManager(_ server: ScriptedCreditServer) -> HyperWhisperCloudManager {
        HyperWhisperCloudManager(
            licenseManager: LicenseManager(
                networkService: NoLicenseNetwork(),
                loadStoredLicenseOnInit: false,
                notificationCenter: NotificationCenter()
            ),
            transport: { try await server.send($0) }
        )
    }

    private func fetchError(_ server: ScriptedCreditServer) async -> HyperWhisperCloudError? {
        let manager = makeManager(server)
        do {
            _ = try await manager.fetchCredits(forceRefresh: true)
            Issue.record("fetchCredits returned instead of throwing")
            return nil
        } catch let error as HyperWhisperCloudError {
            #expect(server.requests.count == 1)
            return error
        } catch {
            Issue.record("expected HyperWhisperCloudError, got \(error)")
            return nil
        }
    }

    private func serverErrorMessage(_ error: HyperWhisperCloudError?) -> String? {
        guard case .serverError(let message) = error else {
            Issue.record("expected serverError, got \(String(describing: error))")
            return nil
        }
        return message
    }

    private static func creditsJSON(isLicensed: Bool) -> String {
        """
        {"credits_remaining":120.5,"minutes_remaining":12,"credits_per_minute":10,\
        "is_licensed":\(isLicensed),"is_anonymous":\(!isLicensed)}
        """
    }
}

/// Answers each credit request with the next scripted reply, and records it.
@MainActor
private final class ScriptedCreditServer {
    enum Reply {
        case reply(Int, String)
        case fail(Error)
    }

    private var replies: [Reply]
    private(set) var requests: [URLRequest] = []

    init(_ replies: [Reply]) {
        self.replies = replies
    }

    func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        requests.append(request)
        guard !replies.isEmpty else {
            throw URLError(.resourceUnavailable)
        }
        switch replies.removeFirst() {
        case .fail(let error):
            throw error
        case .reply(let status, let body):
            let url = request.url ?? URL(string: "https://example.invalid")!
            let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil)!
            return (Data(body.utf8), response)
        }
    }
}

/// A licence network with no stored key, so the default identity is the
/// anonymous device id and nothing reaches the licensing API.
private final class NoLicenseNetwork: LicenseNetworkServing {
    private let none = LicenseValidationResult(
        isValid: false,
        status: .invalid,
        customerId: nil,
        customerEmail: nil,
        customerName: nil,
        errorMessage: nil
    )

    func activateLicense(_ licenseKey: String) async -> LicenseValidationResult { none }
    func deactivateLicense() async -> (success: Bool, error: String?) { (true, nil) }
    func validateLicense(
        _ licenseKey: String,
        isLaunchValidation: Bool,
        expectedStoredLicenseKey: String?
    ) async -> LicenseValidationResult { none }
    func probeLicense(_ licenseKey: String) async -> LicenseValidationResult { none }
    func shouldRevalidateLicense() -> Bool { false }
    func getCachedLicenseStatus() -> LicenseStatus? { nil }
    func getStoredLicenseKey() -> String? { nil }
    func readStoredLicenseKey(retryAfterFailure: Bool) -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func readStoredLicenseKeyFromSecureStore() async -> RustLicenseStore.StoredLicenseKeyRead { .missing }
    func clearStoredLicense() -> Bool { true }
    func replaceStoredLicenseKeyForImport(_ licenseKey: String) -> Bool { true }
}
