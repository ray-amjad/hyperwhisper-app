//
//  CloudProviderHealthCacheTTLTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
import os
@testable import HyperWhisper

// MARK: - Test doubles

/// A clock the test moves by hand. `CloudProviderHealthManager` reads its
/// injected `now` closure for every timestamp it takes, so moving this box
/// moves the whole 60 s `cacheTTL` window at once.
///
/// Before the `now:` parameter existed, `refresh(_:force:)` compared
/// `Date().timeIntervalSince(record.timestamp)` against `cacheTTL` directly.
/// The only way to observe the expiry was to wait 60 real seconds, so none of
/// the tests below could be written.
private final class TestClock {
    private(set) var now = Date(timeIntervalSince1970: 1_700_000_000)

    func advance(_ seconds: TimeInterval) {
        now = now.addingTimeInterval(seconds)
    }
}

/// A `HealthCheckHTTPClient` that never touches the network and counts the
/// probes the manager sends. The count is what separates a cache hit (no probe)
/// from a cache miss (one more probe).
///
/// `send` is an `async` requirement, so the counter uses
/// `OSAllocatedUnfairLock` rather than `NSLock`: `NSLock.lock()` is marked
/// unavailable from an asynchronous context and warns today, then fails to
/// compile under the Swift 6 language mode.
private final class CountingHealthCheckClient: HealthCheckHTTPClient {
    private let count = OSAllocatedUnfairLock(initialState: 0)

    var sendCount: Int {
        count.withLock { $0 }
    }

    func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        count.withLock { $0 += 1 }

        let response = HTTPURLResponse(
            url: request.url ?? URL(string: "https://api.anthropic.com/v1/models")!,
            statusCode: 200,
            httpVersion: nil,
            headerFields: nil
        )!
        return (Data(), response)
    }
}

/// Supplies a fixed key so the manager gets past its missing-key gate without
/// reading the Keychain or `UserDefaults` of the test host application.
///
/// The manager holds `apiKeyProvider` weakly, so each test keeps its own
/// instance alive for the whole test body.
private final class FixedKeyProvider: CloudProviderAPIKeyProviding {
    private let postProcessingReads = OSAllocatedUnfairLock(initialState: 0)

    var postProcessingReadCount: Int {
        postProcessingReads.withLock { $0 }
    }

    func apiKey(for provider: CloudProvider) -> String {
        "sk-test-key-0123456789"
    }

    func postProcessingAPIKey(for provider: PostProcessingProvider) -> String {
        postProcessingReads.withLock { $0 += 1 }
        return "sk-test-key-0123456789"
    }
}

// MARK: - Tests

@MainActor
struct CloudProviderHealthCacheTTLTests {
    /// Anthropic is the probe used throughout: its post-processing health check
    /// is the branch that goes through the injectable `httpClient`, so a stub
    /// client sees every request the manager makes.
    private static let provider: PostProcessingProvider = .anthropic

    /// Google Chirp 3 is the STT provider used by the failure-override tests
    /// below, for two reasons. It is the provider from issue #379 — the one that
    /// kept reporting `"status":"healthy","reachable":true` while
    /// `POST /transcribe {"engine":"googlespeech"}` failed. And it is the only
    /// `CloudProvider` whose probe is hermetic: `performHealthCheck(for:force:)`
    /// short-circuits the HW-Cloud-routed providers to `.healthy` with no network
    /// call and no API key, so "the probe says healthy" is a fact of the code
    /// rather than something a stub has to fake. Every BYOK provider's probe goes
    /// through the non-injectable `rustHealthSession` and would hit the network.
    private static let cloudProvider: CloudProvider = .googleSpeech

    /// The definitive provider-down error: `RustRetry.swift` maps
    /// `HwTranscriptionError.ProviderUnavailable` onto exactly this shape.
    private static let providerDownError = TranscriptionError.serverError(
        statusCode: 503,
        message: "Provider unavailable"
    )

    private static func makeManager(
        client: CountingHealthCheckClient,
        keys: FixedKeyProvider,
        clock: TestClock
    ) -> CloudProviderHealthManager {
        let manager = CloudProviderHealthManager(httpClient: client, now: { clock.now })
        manager.configure(apiKeyProvider: keys)
        return manager
    }

    @Test func aRefreshInsideTheTTLServesTheCacheAndSendsNoSecondProbe() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        // First probe. This stamps the cache record at the clock's start value.
        let first = await manager.ensureHealthy(Self.provider)
        #expect(first == .healthy)
        #expect(client.sendCount == 1)

        // 30 s later the record is still inside the 60 s TTL, so `refresh` must
        // publish the cached status and return without scheduling any work.
        clock.advance(30)
        manager.refresh(Self.provider)

        #expect(manager.status(for: Self.provider) == .healthy)
        #expect(client.sendCount == 1)
        #expect(keys.postProcessingReadCount == 1)
    }

    @Test func aRefreshPastTheTTLProbesAgain() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        let first = await manager.ensureHealthy(Self.provider)
        #expect(first == .healthy)
        #expect(client.sendCount == 1)

        // 61 s later the record is stale. `refresh` publishes `.checking`
        // synchronously — that alone is the miss — and schedules a new probe.
        clock.advance(61)
        manager.refresh(Self.provider)
        #expect(manager.status(for: Self.provider) == .checking)

        // Join the scheduled probe and confirm it really reached the client.
        let second = await manager.ensureHealthy(Self.provider)
        #expect(second == .healthy)
        #expect(client.sendCount == 2)
        #expect(keys.postProcessingReadCount == 2)
    }

    /// The TTL gate is a strict `<`, so exactly 60 s is already expired. This
    /// pins the boundary a wall-clock test could never land on.
    @Test func theTTLBoundaryIsExclusive() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        _ = await manager.ensureHealthy(Self.provider)
        #expect(client.sendCount == 1)

        clock.advance(59.9)
        manager.refresh(Self.provider)
        #expect(manager.status(for: Self.provider) == .healthy)
        #expect(client.sendCount == 1)

        clock.advance(0.1)
        manager.refresh(Self.provider)
        #expect(manager.status(for: Self.provider) == .checking)

        _ = await manager.ensureHealthy(Self.provider)
        #expect(client.sendCount == 2)
        #expect(keys.postProcessingReadCount == 2)
    }

    /// `healthSnapshot()` reads the same clock, so the Local API `/health`
    /// timestamp is now pinnable too.
    @Test func theHealthSnapshotTimestampComesFromTheInjectedClock() {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        clock.advance(1234)
        let snapshot = manager.healthSnapshot()

        #expect(snapshot.timestamp == Date(timeIntervalSince1970: 1_700_000_000 + 1234))
        #expect(keys.postProcessingReadCount == 0)
    }

    // MARK: - Transcription-failure override (issue #379)

    /// (a) The defect itself. A real transcription that came back with a 5xx must
    /// stop `/health` reporting the provider healthy — even though the probe,
    /// which never touches the transcription endpoint, keeps saying it is.
    ///
    /// The override belongs only to `healthSnapshot()`. The probe-derived
    /// accessor and published dictionary feed app gates and badges, so they must
    /// stay healthy after the failed request.
    @Test func aRecordedProviderDownFailureOutranksAHealthyProbe() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        let probed = await manager.ensureHealthy(Self.cloudProvider)
        #expect(probed == .healthy)
        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "healthy")

        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: manager.captureTranscriptionCredentialGeneration(),
            error: Self.providerDownError
        )

        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.statuses[Self.cloudProvider] == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unreachable")

        // The app gate serves its existing healthy probe without forcing a new
        // health check. Only the Local API snapshot sees the failed outcome.
        let gate = await manager.ensureHealthy(Self.cloudProvider)
        #expect(gate == .healthy)
        #expect(gate.shouldBlockTranscription == false)
        #expect(manager.statuses[Self.cloudProvider] == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unreachable")

        // The whole exchange was hermetic: no injectable-client traffic at all.
        #expect(client.sendCount == 0)
    }

    /// (b) The override is a cooldown, not a latch. Once `failureOverrideTTL`
    /// passes, whatever the probe last published shows through again. The gate is
    /// a strict `<`, so exactly 60 s is already expired — the same boundary rule
    /// as `cacheTTL`.
    @Test func theFailureOverrideExpiresAfterItsTTL() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        _ = await manager.ensureHealthy(Self.cloudProvider)
        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: manager.captureTranscriptionCredentialGeneration(),
            error: Self.providerDownError
        )

        #expect(manager.status(for: Self.cloudProvider) == .healthy)

        clock.advance(59.9)
        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unreachable")

        clock.advance(0.1)
        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "healthy")
    }

    /// (c) A real success is stronger evidence than any probe, so it clears the
    /// override immediately rather than waiting out the window. Without this a
    /// provider that recovered on the very next attempt would still be reported
    /// unreachable for the remaining ~59 s while demonstrably working.
    @Test func aRecordedSuccessClearsTheOverrideImmediately() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        _ = await manager.ensureHealthy(Self.cloudProvider)
        let credentialGeneration = manager.captureTranscriptionCredentialGeneration()
        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: credentialGeneration,
            error: Self.providerDownError
        )
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unreachable")

        // No clock movement at all — the success alone must clear it.
        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: credentialGeneration,
            error: nil
        )

        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "healthy")
    }

    @Test func aRecordedSuccessRefreshesAStaleProbeVerdictToHealthy() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        manager.setCachedTranscriptionStatusForTests(.unreachable, for: Self.cloudProvider)
        clock.advance(61)
        let generation = manager.captureTranscriptionCredentialGeneration()

        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: generation,
            error: nil
        )

        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "healthy")
        clock.advance(59)
        manager.refresh(Self.cloudProvider)
        #expect(manager.status(for: Self.cloudProvider) == .healthy)
    }

    @Test func anOldCredentialGenerationCannotOverwriteTheNewUnauthorizedVerdict() {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        let oldGeneration = manager.captureTranscriptionCredentialGeneration()
        manager.registerAPIKeyChange(for: Self.cloudProvider, newValue: "short-new-key")
        manager.setCachedTranscriptionStatusForTests(.unauthorized, for: Self.cloudProvider)

        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: oldGeneration,
            error: nil
        )

        #expect(manager.status(for: Self.cloudProvider) == .unauthorized)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unauthorized")
    }

    /// (d) The verdict is keyed off the CLASSIFIED error, never off "a
    /// transcription failed". Marking a provider unreachable because the user's
    /// Wi-Fi dropped, their card expired, or they recorded silence would be a
    /// worse bug than the stale verdict the override exists to fix.
    ///
    /// The 404 case matters most: it proves the check is on the 5xx RANGE, not on
    /// `.serverError` as a case.
    @Test func nonDefinitiveFailuresDoNotSetTheOverride() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        _ = await manager.ensureHealthy(Self.cloudProvider)
        #expect(manager.status(for: Self.cloudProvider) == .healthy)

        let harmless: [Error] = [
            TranscriptionError.unauthorized(provider: "Google Chirp 3", statusCode: 401),
            TranscriptionError.quotaExceeded(provider: "Google Chirp 3", message: nil),
            TranscriptionError.insufficientCredits(remaining: 0, required: 10),
            TranscriptionError.rateLimited(retryAfter: 5),
            TranscriptionError.transientNetwork(details: "No internet connection"),
            TranscriptionError.noSpeechDetected,
            TranscriptionError.invalidRequest,
            TranscriptionError.invalidResponse(details: "bad json"),
            TranscriptionError.serverError(statusCode: 404, message: "Not Found"),
            StreamingError.connectionTimeout,
            StreamingError.serverError("Connection lost after multiple retries"),
            StreamingError.audioEngineError("microphone unavailable"),
            StreamingError.insufficientCredits,
            StreamingError.unauthorized,
            CancellationError()
        ]

        for error in harmless {
            manager.recordTranscriptionOutcome(
                for: Self.cloudProvider,
                credentialGeneration: manager.captureTranscriptionCredentialGeneration(),
                error: error
            )
            #expect(manager.status(for: Self.cloudProvider) == .healthy)
            #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "healthy")
            #expect(CloudProviderHealthManager.isDefinitiveProviderDownVerdict(error) == false)
        }

        // …and the two that DO count, for contrast.
        #expect(CloudProviderHealthManager.isDefinitiveProviderDownVerdict(Self.providerDownError))
        #expect(CloudProviderHealthManager.isDefinitiveProviderDownVerdict(
            TranscriptionError.providerNotAvailable(provider: "Google Chirp 3", reason: "down")
        ))
    }

    /// (e) THE NO-DEGRADATION GUARANTEE — the most important case here.
    ///
    /// The override applies only to `/health`. `status(for:)`, the published
    /// dictionary, and `ensureHealthy` feed menus, badges and preflight. They
    /// must all retain the raw healthy probe and must not force a new probe.
    @Test func theOverrideDoesNotDegradeAppGatesOrBadges() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        _ = await manager.ensureHealthy(Self.cloudProvider)
        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: manager.captureTranscriptionCredentialGeneration(),
            error: Self.providerDownError
        )
        #expect(manager.status(for: Self.cloudProvider) == .healthy)
        #expect(manager.statuses[Self.cloudProvider] == .healthy)

        let gate = await manager.ensureHealthy(Self.cloudProvider)
        #expect(gate == .healthy)
        #expect(gate.shouldBlockTranscription == false)
        #expect(manager.statuses[Self.cloudProvider] == .healthy)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unreachable")
        #expect(client.sendCount == 0)
    }

    /// Re-pasting an API key during an outage must not leave the user staring at
    /// `unreachable` and concluding the new key is bad.
    @Test func anAPIKeyChangeClearsTheFailureOverride() async {
        let clock = TestClock()
        let client = CountingHealthCheckClient()
        let keys = FixedKeyProvider()
        let manager = Self.makeManager(client: client, keys: keys, clock: clock)

        _ = await manager.ensureHealthy(Self.cloudProvider)
        manager.recordTranscriptionOutcome(
            for: Self.cloudProvider,
            credentialGeneration: manager.captureTranscriptionCredentialGeneration(),
            error: Self.providerDownError
        )
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unreachable")

        // An empty value takes the early-return branch, so no probe is scheduled
        // and the published status resets to `.unknown` — not `.unreachable`.
        manager.registerAPIKeyChange(for: Self.cloudProvider, newValue: "")
        #expect(manager.status(for: Self.cloudProvider) == .unknown)
        #expect(manager.healthSnapshot().cloud[Self.cloudProvider.rawValue] == "unknown")
    }
}
