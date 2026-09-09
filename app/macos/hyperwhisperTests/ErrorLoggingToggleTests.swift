//
//  ErrorLoggingToggleTests.swift
//  hyperwhisperTests
//
//  Issue #551 — Settings → General → Error logging is the privacy opt-out the
//  data-privacy page points at. Turning it OFF has to stop Sentry immediately,
//  not at the next launch, and turning it back ON has to start it again without
//  a launch either.
//
//  Two suites. The first never starts the SDK and runs in CI: it drives the real
//  Settings property and asserts on the queue Sentry keeps on disk, which is
//  enough to fail if the `didSet` disable arm is removed again. The second one
//  runs the SDK for real and is opt-in — see its own comment for why.
//
//  Note for anyone running these on their own Mac: the test host shares the
//  installed app's bundle identifier, so the purge clears
//  ~/Library/Caches/<bundle id>/Sentry for the copy of HyperWhisper you have
//  installed too. That is only Sentry's own queue, and only for this app.
//

import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Helpers shared by both suites

enum ErrorLoggingToggleFixture {

    static let settingKey = "enableErrorLogging"

    /// A syntactically valid DSN whose host is the discard port on loopback: the
    /// SDK starts for real and builds real envelopes, but no request can reach
    /// anything and nothing leaves the machine.
    static let localDSN = "http://0123456789abcdef0123456789abcdef@127.0.0.1:9/1"

    /// Leave no SDK running and no queue on disk for the next test, whichever
    /// way the one that just ran exited.
    static func withCleanSentry(_ body: () throws -> Void) rethrows {
        let original = UserDefaults.standard.object(forKey: settingKey)
        UserDefaults.standard.set(true, forKey: settingKey)
        SentryService.shutdown()
        defer {
            SentryService.shutdown()
            if let original {
                UserDefaults.standard.set(original, forKey: settingKey)
            } else {
                UserDefaults.standard.removeObject(forKey: settingKey)
            }
        }
        try body()
    }

    /// Write a file where Sentry queues its envelopes, standing in for a crash
    /// report or an upload that failed while the machine was offline.
    enum FixtureError: Error { case noCacheDirectory }

    @discardableResult
    static func plantAQueuedReport() throws -> URL {
        guard let cacheDirectory = SentryService.cacheDirectory else {
            throw FixtureError.noCacheDirectory
        }
        let envelopes = cacheDirectory
            .appendingPathComponent("io.sentry", isDirectory: true)
            .appendingPathComponent("deadbeef", isDirectory: true)
            .appendingPathComponent("envelopes", isDirectory: true)
        try FileManager.default.createDirectory(at: envelopes, withIntermediateDirectories: true)
        let envelope = envelopes.appendingPathComponent("1.envelope")
        try Data("queued".utf8).write(to: envelope)
        return envelope
    }

    /// Poll for the cache directory to appear or disappear. The SDK writes it
    /// from a background queue, so both directions need a small window rather
    /// than one instant read.
    @MainActor
    static func waitForCacheDirectory(toExist shouldExist: Bool, timeout: TimeInterval = 3) -> Bool {
        guard let path = SentryService.cacheDirectory?.path else { return !shouldExist }
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if FileManager.default.fileExists(atPath: path) == shouldExist { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        } while Date() < deadline
        return FileManager.default.fileExists(atPath: path) == shouldExist
    }
}

// MARK: - Always on

/// Serialized and `@MainActor`: every test drives one process-wide service and
/// one directory on disk.
@MainActor
@Suite("Error logging toggle", .serialized)
struct ErrorLoggingToggleTests {

    /// The regression test proper. It drives the real Settings property rather
    /// than `SentryService`, so removing the `didSet` disable arm — the whole of
    /// issue #551 — fails here.
    ///
    /// The queue on disk is the observable: before the fix, unticking the box
    /// wrote a preference and did nothing else.
    @Test func theSettingsToggleItselfStopsReportingAndClearsTheQueue() throws {
        try ErrorLoggingToggleFixture.withCleanSentry {
            let settings = GeneralSettingsManager()
            #expect(settings.enableErrorLogging)
            let envelope = try ErrorLoggingToggleFixture.plantAQueuedReport()
            #expect(FileManager.default.fileExists(atPath: envelope.path))

            settings.enableErrorLogging = false

            #expect(SentryService.isReportingEnabled == false)
            #expect(ErrorLoggingToggleFixture.waitForCacheDirectory(toExist: false))
        }
    }

    /// The purge runs even when the SDK was never started, so a queue left by an
    /// earlier session cannot outlive the opt-out. A crash report that flushes
    /// on the next launch is the same leak one launch later.
    @Test func shutdownPurgesAQueueLeftBehindByAnEarlierSession() throws {
        try ErrorLoggingToggleFixture.withCleanSentry {
            try ErrorLoggingToggleFixture.plantAQueuedReport()

            #expect(SentryService.isSDKRunning == false)
            SentryService.shutdown()

            #expect(ErrorLoggingToggleFixture.waitForCacheDirectory(toExist: false))
        }
    }

    /// The second door: the user's setting closes every send path on its own,
    /// whatever the SDK is doing. Most call sites already check
    /// `AppLogger.isErrorLoggingEnabled` by hand; this covers the one that
    /// forgets.
    @Test func theSettingAloneClosesEverySendPath() {
        ErrorLoggingToggleFixture.withCleanSentry {
            UserDefaults.standard.set(false, forKey: ErrorLoggingToggleFixture.settingKey)

            #expect(SentryService.isReportingEnabled == false)
            #expect(SentryService.startTransaction(name: "must not start", operation: "test") == nil)
            // Inert rather than throwing: these are called from error paths.
            SentryService.capture(
                error: NSError(domain: "hyperwhisper.test.551", code: 1),
                message: "must not be captured",
                includeRecentLogs: false
            )
            SentryService.captureMessage("must not be captured", includeRecentLogs: false)
            SentryService.addBreadcrumb(message: "must not be recorded", category: "test")
            SentryService.setTag("must_not", "be_set")
            SentryService.setExtras(["must_not": "be_set"])
        }
    }

    /// This app is not sandboxed, so the SDK's default cache location is the
    /// SHARED `~/Library/Caches` — every Sentry-using Mac app writes into the
    /// same `io.sentry` folder there, and sentry-cocoa puts raw crash reports in
    /// a sibling `SentryCrash/`. The purge is only safe because `initialize`
    /// moves our state somewhere of our own; if that regresses, `shutdown()`
    /// would be deleting another vendor's crash queue.
    @Test func theCacheDirectoryIsPrivateToThisApp() throws {
        let cacheDirectory = try #require(SentryService.cacheDirectory)
        let sharedCaches = try #require(
            FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
        )
        let bundleID = try #require(Bundle.main.bundleIdentifier)

        #expect(cacheDirectory.standardizedFileURL != sharedCaches.standardizedFileURL)
        #expect(
            cacheDirectory.standardizedFileURL
                != sharedCaches.appendingPathComponent("io.sentry").standardizedFileURL
        )
        #expect(
            cacheDirectory.standardizedFileURL
                != sharedCaches.appendingPathComponent("SentryCrash").standardizedFileURL
        )
        #expect(cacheDirectory.path.contains(bundleID))
    }
}

// MARK: - Opt-in: the real SDK

/// The same claim, asserted against `SentrySDK.isEnabled` — the SDK's own client
/// state — instead of against the queue on disk. This is the strongest form of
/// the proof, and it is opt-in rather than part of the CI gate:
///
/// `SentrySDK.close()` runs `SentryDependencyContainer.reset()`, and
/// `sentrycrashbic_startCache` registers dyld add/remove-image callbacks that it
/// never unregisters. Starting and closing the SDK repeatedly inside one process
/// therefore destabilises the *test host*, which XCTest reports as a 0.000s
/// failure whose name moves between runs — and `macos-ci.yml`'s crash-report
/// step looks for `hyperwhisper*.ips`, which does not match `HyperWhisper`, so
/// the report never even reaches the log. The app closes the SDK once, when the
/// user opts out, and never in a loop.
///
/// Run it deliberately:
///
///     HW_SENTRY_LIFECYCLE_TESTS=1 xcodebuild test … \
///       -only-testing:hyperwhisperTests/ErrorLoggingSDKLifecycleTests
@MainActor
@Suite(
    "Error logging toggle — real SDK",
    .serialized,
    .enabled(if: ProcessInfo.processInfo.environment["HW_SENTRY_LIFECYCLE_TESTS"] == "1")
)
struct ErrorLoggingSDKLifecycleTests {

    /// One test, one lifecycle: off must stop the SDK in the same session, and
    /// on must start it again in the same session.
    @Test func theSDKStopsAndStartsAgainWithoutAnAppRestart() {
        ErrorLoggingToggleFixture.withCleanSentry {
            SentryService.initialize(
                dsn: ErrorLoggingToggleFixture.localDSN,
                environment: "test"
            )
            #expect(SentryService.isSDKRunning)
            #expect(SentryService.isReportingEnabled)
            #expect(ErrorLoggingToggleFixture.waitForCacheDirectory(toExist: true))

            SentryService.shutdown()

            #expect(SentryService.isSDKRunning == false)
            #expect(SentryService.isReportingEnabled == false)
            #expect(ErrorLoggingToggleFixture.waitForCacheDirectory(toExist: false))

            SentryService.initialize(
                dsn: ErrorLoggingToggleFixture.localDSN,
                environment: "test"
            )

            #expect(SentryService.isSDKRunning)
            #expect(SentryService.isReportingEnabled)
        }
    }
}
