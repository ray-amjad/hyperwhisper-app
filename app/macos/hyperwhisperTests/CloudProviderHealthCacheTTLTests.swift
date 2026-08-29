//
//  CloudProviderHealthCacheTTLTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
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
private final class CountingHealthCheckClient: HealthCheckHTTPClient {
    private let lock = NSLock()
    private var count = 0

    var sendCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return count
    }

    func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        lock.lock()
        count += 1
        lock.unlock()

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
    private let lock = NSLock()
    private var postProcessingReads = 0

    var postProcessingReadCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return postProcessingReads
    }

    func apiKey(for provider: CloudProvider) -> String {
        "sk-test-key-0123456789"
    }

    func postProcessingAPIKey(for provider: PostProcessingProvider) -> String {
        lock.lock()
        postProcessingReads += 1
        lock.unlock()
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
}
