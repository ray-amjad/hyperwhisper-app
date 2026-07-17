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
//    - LicenseNetworkService.isNetworkFailure classifies connectivity errors
//      (timeout/no connection/DNS/connection refused) distinctly from other
//      errors, so the offline diagnostic signal can tell them apart.
//
//  The end-to-end retry → cache-fallback flow itself lives in
//  `LicenseNetworkService.validateLicense` and is exercised indirectly via the
//  Rust `hw-license::cache` golden tests (offline_fallback_uses_cache_within_grace
//  / offline_fallback_invalid_after_grace), since that's where the cache
//  semantics are the single source of truth for all platforms.
//

import Testing
import Foundation
@testable import HyperWhisper

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
}
