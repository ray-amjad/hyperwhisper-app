//
//  LicenseNetworkResilienceTests.swift
//  hyperwhisperTests
//
//  HYPERWHISPER-F4: launch-time license validation must self-heal from a
//  transient network blip (short bounded retry) and, once retries are
//  exhausted, fall back to the last cached SERVER verdict rather than
//  surfacing a hard error to a paying user. These tests cover the pure,
//  synchronously-testable pieces of that fix:
//    - RetryConfiguration.licenseLaunchValidation stays short and bounded
//      (tighter than the general-purpose `.cloud` budget).
//    - NetworkConfig.licenseLaunchValidationTimeout (the per-request timeout
//      for the same call) is also much shorter than the normal
//      `licenseValidationTimeout`, so the retry preset's short backoff isn't
//      undermined by each individual attempt still hanging for the full 10s.
//    - LicenseNetworkService.isNetworkFailure classifies connectivity errors
//      (timeout/no connection/DNS/connection refused) distinctly from other
//      errors, so the offline diagnostic signal can tell them apart.
//    - A cached Expired/Invalid verdict within the 7-day grace is a real cache
//      hit (`licenseCachedStatusWithinGrace` returns non-nil) even though it's
//      not an "active" verdict (`licenseOfflineFallbackOutcome(...).isValid`
//      is false) — this is the exact distinction `validateLicense`'s offline
//      branch must make to avoid firing the `offline_no_cache` Sentry signal
//      for a genuinely-cached, merely-non-active verdict.
//
//  The end-to-end retry → cache-fallback flow itself lives in
//  `LicenseNetworkService.validateLicense` and is exercised indirectly via the
//  Rust `hw-license::cache` golden tests (offline_fallback_uses_cache_within_grace
//  / offline_fallback_invalid_after_grace), since that's where the cache
//  semantics are the single source of truth for all platforms.
//
//  REVIEW ROUND 2 additions:
//    - `LicenseNetworkService.requestPolicy(isLaunchValidation:)` pairs the
//      per-request timeout and retry preset from a single lookup so they can't
//      drift out of sync (previously two independent ternaries).
//    - `NetworkConfig.licenseLaunchValidationRetrySoonDelay` backs a short,
//      one-shot background retry `LicenseManager` schedules after a launch-time
//      validation falls back to cache specifically due to a network failure —
//      so a merely-slow-but-live network doesn't ride a stale cached verdict
//      for up to a week. `LicenseValidationResult.networkFailureFallback` is
//      the signal that triggers it.
//

import Testing
import Foundation
@testable import HyperWhisper

/// Minimal in-memory `KeyValueStore` (the `hw-license` core's callback
/// interface) for exercising the cache/grace/offline-fallback core functions
/// directly, without touching `RustLicenseStore`'s real UserDefaults +
/// one-shot Core Data usage seed (irrelevant here and unnecessary coupling
/// for a pure cache-semantics test).
private final class FakeKeyValueStore: KeyValueStore {
    private var storage: [String: String] = [:]

    func get(key: String) -> String? { storage[key] }
    func set(key: String, value: String) { storage[key] = value }
    func delete(key: String) { storage.removeValue(forKey: key) }
}

struct LicenseNetworkResilienceTests {

    // MARK: - RetryConfiguration.licenseLaunchValidation

    @Test func launchValidationRetryIsShortAndBounded() {
        let config = RetryConfiguration.licenseLaunchValidation
        #expect(config.maxAttempts == 3)

        // Total backoff sleep across all retryable attempts must stay a few
        // seconds at most — this is what lets a transient blip at launch
        // self-heal without leaving a paying user looking unlicensed for long.
        let totalBackoff = (1..<config.maxAttempts).reduce(0.0) { sum, attempt in
            sum + config.delay(for: attempt)
        }
        #expect(totalBackoff < 5.0)

        // Every single delay (even accounting for jitter) stays well under the
        // `.cloud` budget's own per-attempt ceiling.
        for attempt in 1..<config.maxAttempts {
            #expect(config.delay(for: attempt) <= config.maxDelay)
        }
    }

    @Test func launchValidationRetryIsTighterThanCloudDefault() {
        // The launch-time budget is deliberately tighter than `.cloud` (used for
        // explicit, user-triggered activation) — a silent background check
        // should not make a paying user wait anywhere near as long as an
        // explicit "Activate" button press already implies.
        let launch = RetryConfiguration.licenseLaunchValidation
        let cloud = RetryConfiguration.cloud

        #expect(launch.initialDelay < cloud.initialDelay)
        #expect(launch.maxDelay < cloud.maxDelay)
        #expect(launch.delay(for: 1) < cloud.delay(for: 1))
    }

    // MARK: - LicenseNetworkService.isNetworkFailure

    @Test func classifiesConnectivityErrorsAsNetworkFailures() {
        #expect(LicenseNetworkService.isNetworkFailure(URLError(.timedOut)))
        #expect(LicenseNetworkService.isNetworkFailure(URLError(.notConnectedToInternet)))
        #expect(LicenseNetworkService.isNetworkFailure(URLError(.cannotFindHost)))
        #expect(LicenseNetworkService.isNetworkFailure(URLError(.cannotConnectToHost)))
        #expect(LicenseNetworkService.isNetworkFailure(URLError(.dnsLookupFailed)))
        #expect(LicenseNetworkService.isNetworkFailure(URLError(.networkConnectionLost)))
    }

    @Test func doesNotClassifyServerVerdictsAsNetworkFailures() {
        // A deliberately-thrown "retry this transient server error" URLError
        // (constructed from a raw 500/429 status code, see validateLicense) must
        // NOT be misclassified as a connectivity failure — it's a server-side
        // condition, not "the network isn't up yet".
        #expect(!LicenseNetworkService.isNetworkFailure(URLError(URLError.Code(rawValue: 500))))
        #expect(!LicenseNetworkService.isNetworkFailure(URLError(URLError.Code(rawValue: 429))))
    }

    @Test func doesNotClassifyUnrelatedErrorsAsNetworkFailures() {
        let unrelated = NSError(domain: "SomeOtherDomain", code: 1, userInfo: nil)
        #expect(!LicenseNetworkService.isNetworkFailure(unrelated))
    }

    // MARK: - NetworkConfig.licenseLaunchValidationTimeout

    @Test func launchValidationPerRequestTimeoutIsMuchShorterThanDefault() {
        // The retry preset's short backoff is pointless if each individual
        // attempt still hangs for the full 10s `licenseValidationTimeout` — the
        // per-request timeout for launch-time validation must also be tightened
        // (see `LicenseNetworkService.validateLicense`'s `requestTimeout`
        // selection) so the worst case (all attempts time out) stays a
        // single-digit-second budget instead of ~30s.
        #expect(NetworkConfig.licenseLaunchValidationTimeout < NetworkConfig.licenseValidationTimeout)
        #expect(NetworkConfig.licenseLaunchValidationTimeout <= 3.0)

        // Sanity bound on the actual worst case this enables: 3 attempts at the
        // shorter per-request timeout plus the launch retry preset's total
        // backoff sleep should land in the single digits, nowhere near the old
        // ~30s (3 × 10s) worst case.
        let launch = RetryConfiguration.licenseLaunchValidation
        let totalBackoff = (1..<launch.maxAttempts).reduce(0.0) { $0 + launch.delay(for: $1) }
        let worstCase = Double(launch.maxAttempts) * NetworkConfig.licenseLaunchValidationTimeout + totalBackoff
        #expect(worstCase < 10.0)
    }

    // MARK: - Offline fallback: cached-but-non-active verdicts are still a cache hit

    @Test func cachedExpiredVerdictWithinGraceIsARealCacheHit() {
        // Regression test for the mislabeled `offline_no_cache` signal: a cached
        // Expired verdict within the 7-day grace is a genuine cache hit — just
        // not an ACTIVE one. `outcome.isValid` alone (`status == .active`) must
        // NOT be used to decide whether to fire the "no cached fallback"
        // diagnostic; `licenseCachedStatusWithinGrace` is the correct signal for
        // "is there a cached verdict at all."
        let store = FakeKeyValueStore()
        let now = RustLicenseTime.nowUTC()

        licensePersistValidationVerdict(store: store, status: .active, attemptedKey: "KEY-1", nowUnixSecs: now)
        // Re-validating the same key later comes back Expired, still within grace.
        licensePersistValidationVerdict(store: store, status: .expired, attemptedKey: "KEY-1", nowUnixSecs: now)

        let outcome = licenseOfflineFallbackOutcome(store: store, nowUnixSecs: now)
        #expect(outcome.status == .expired)
        #expect(!outcome.isValid) // Expired ≠ Active, so isValid is false here...

        // ...but there IS a real cached verdict to fall back on — the
        // offline_no_cache alarm must NOT fire in this case.
        let hasCachedVerdict = licenseCachedStatusWithinGrace(store: store, nowUnixSecs: now) != nil
        #expect(hasCachedVerdict)
    }

    @Test func cachedInvalidVerdictWithinGraceIsARealCacheHit() {
        let store = FakeKeyValueStore()
        let now = RustLicenseTime.nowUTC()

        licensePersistValidationVerdict(store: store, status: .active, attemptedKey: "KEY-1", nowUnixSecs: now)
        licensePersistValidationVerdict(store: store, status: .invalid, attemptedKey: "KEY-1", nowUnixSecs: now)

        let outcome = licenseOfflineFallbackOutcome(store: store, nowUnixSecs: now)
        #expect(outcome.status == .invalid)
        #expect(!outcome.isValid)

        let hasCachedVerdict = licenseCachedStatusWithinGrace(store: store, nowUnixSecs: now) != nil
        #expect(hasCachedVerdict)
    }

    @Test func noCachedVerdictAtAllIsCorrectlyDetectedAsNoCache() {
        // The complementary case: nothing was ever validated, so there is truly
        // no cached verdict — THIS is the case that should fire the
        // offline_no_cache signal.
        let store = FakeKeyValueStore()
        let now = RustLicenseTime.nowUTC()

        let outcome = licenseOfflineFallbackOutcome(store: store, nowUnixSecs: now)
        #expect(!outcome.isValid)

        let hasCachedVerdict = licenseCachedStatusWithinGrace(store: store, nowUnixSecs: now) != nil
        #expect(!hasCachedVerdict)
    }

    // MARK: - RequestPolicy: single lookup pairing timeout + retry preset

    @Test func requestPolicyPairsTighterSettingsForLaunchValidation() {
        // Regression test for the review-round-2 fix: the per-request timeout
        // and retry preset used to be selected via two independent ternaries on
        // the same `isLaunchValidation` bool ~18 lines apart. Now both come from
        // one lookup — assert they stay paired correctly for the launch case.
        let policy = LicenseNetworkService.requestPolicy(isLaunchValidation: true)
        #expect(policy.requestTimeout == NetworkConfig.licenseLaunchValidationTimeout)
        #expect(policy.retryConfig.maxAttempts == RetryConfiguration.licenseLaunchValidation.maxAttempts)
        #expect(policy.retryConfig.initialDelay == RetryConfiguration.licenseLaunchValidation.initialDelay)
        #expect(policy.retryConfig.maxDelay == RetryConfiguration.licenseLaunchValidation.maxDelay)
    }

    @Test func requestPolicyPairsNormalSettingsForExplicitActivation() {
        let policy = LicenseNetworkService.requestPolicy(isLaunchValidation: false)
        #expect(policy.requestTimeout == NetworkConfig.licenseValidationTimeout)
        #expect(policy.retryConfig.maxAttempts == RetryConfiguration.cloud.maxAttempts)
        #expect(policy.retryConfig.initialDelay == RetryConfiguration.cloud.initialDelay)
        #expect(policy.retryConfig.maxDelay == RetryConfiguration.cloud.maxDelay)
    }

    // MARK: - networkFailureFallback signal (drives the launch-time retry-soon)

    @Test func licenseValidationResultDefaultsNetworkFailureFallbackToFalse() {
        let result = LicenseValidationResult(
            isValid: true,
            status: .active,
            customerId: nil,
            customerEmail: nil,
            customerName: nil,
            errorMessage: nil
        )
        #expect(!result.networkFailureFallback)
    }

    @Test func adaptPropagatesNetworkFailureFallbackFlagWhenSet() {
        // `adapt(_:networkFailureFallback:)` is what `validateLicense`'s offline
        // catch-branch uses to tag a result as "served from cache because we
        // couldn't reach the network" — the signal `LicenseManager` checks to
        // decide whether to schedule the short background retry-soon.
        let outcome = ValidationOutcome(
            isValid: true,
            status: .active,
            customerId: "cust_1",
            customerEmail: "person@example.com",
            expiresAt: nil,
            errorMessage: "Using cached license (offline)"
        )

        let fellBackDueToNetwork = LicenseNetworkService.adapt(outcome, networkFailureFallback: true)
        #expect(fellBackDueToNetwork.networkFailureFallback)
        #expect(fellBackDueToNetwork.isValid) // adapt() doesn't touch the underlying verdict

        // Default (omitted) stays false — every non-offline-fallback call site
        // (200 success, terminal HTTP error, `getCachedLicenseStatus`) must not
        // be misclassified as a network-failure fallback.
        let normal = LicenseNetworkService.adapt(outcome)
        #expect(!normal.networkFailureFallback)
    }

    // MARK: - Retry-soon delay: prompt, and nowhere close to the cache cycle it shortcuts

    @Test func retrySoonDelayIsPromptAndFarShorterThanTheCacheCycle() {
        // "Soon" means within roughly the next minute, not anywhere near the
        // 24h validation cache or the 7-day offline grace it exists to shortcut.
        #expect(NetworkConfig.licenseLaunchValidationRetrySoonDelay > 0)
        #expect(NetworkConfig.licenseLaunchValidationRetrySoonDelay <= 120)
        #expect(NetworkConfig.licenseLaunchValidationRetrySoonDelay < NetworkConfig.validationCacheDuration)
        #expect(NetworkConfig.licenseLaunchValidationRetrySoonDelay < NetworkConfig.offlineGracePeriod)
    }

    @Test func cachedVerdictPastGraceIsCorrectlyDetectedAsNoCache() {
        // A cached verdict that has fallen out of the 7-day grace window is, for
        // this purpose, equivalent to "no cache" — it's no longer a usable
        // safety net.
        let store = FakeKeyValueStore()
        let t0 = RustLicenseTime.nowUTC()
        licensePersistValidationVerdict(store: store, status: .active, attemptedKey: "KEY-1", nowUnixSecs: t0)

        let farFuture = t0 + 604_800 + 10 // just past the 7-day grace period
        let outcome = licenseOfflineFallbackOutcome(store: store, nowUnixSecs: farFuture)
        #expect(!outcome.isValid)

        let hasCachedVerdict = licenseCachedStatusWithinGrace(store: store, nowUnixSecs: farFuture) != nil
        #expect(!hasCachedVerdict)
    }
}
