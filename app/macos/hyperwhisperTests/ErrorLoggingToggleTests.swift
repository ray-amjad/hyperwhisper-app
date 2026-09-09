//
//  ErrorLoggingToggleTests.swift
//  hyperwhisperTests
//
//  Issue #551 — Settings → General → Error logging is the privacy opt-out the
//  data-privacy page points at. Turning it OFF has to stop Sentry immediately,
//  not at the next launch, and turning it back ON has to start it again without
//  a launch either.
//
//  Two suites. The first runs in CI and touches neither the SDK nor SwiftUI: it
//  asserts on the gate and on the queue Sentry keeps on disk. The second drives
//  the real Settings property and the real SDK, and is opt-in — see its own
//  comment for why it cannot live in the CI gate.
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
    /// way the one that just ran exited. Deliberately does NOT touch the
    /// `enableErrorLogging` preference — see `withErrorLoggingOn`.
    static func withCleanSentry(_ body: () throws -> Void) rethrows {
        SentryService.shutdown()
        defer { SentryService.shutdown() }
        try body()
    }

    /// The same, plus the preference pinned on and restored afterwards.
    ///
    /// Writing a NEW value to `enableErrorLogging` is what confines this to the
    /// opt-in suite: the running test host is the app, its live
    /// `GeneralSettingsManager` holds an `@AppStorage` on that key, and driving
    /// SwiftUI's update machinery from outside a view ends the host.
    static func withErrorLoggingOn(_ body: () throws -> Void) rethrows {
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

    /// Whether Sentry has anything on disk right now. `shutdown()` deletes it
    /// synchronously, so the always-on suite reads this directly.
    static var cacheDirectoryExists: Bool {
        guard let path = SentryService.cacheDirectory?.path else { return false }
        return FileManager.default.fileExists(atPath: path)
    }

    /// Poll, for the opt-in suite only: the running SDK writes its cache from a
    /// background queue, so its appearance needs a small window.
    static func waitForCacheDirectory(toExist shouldExist: Bool, timeout: TimeInterval = 3) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if cacheDirectoryExists == shouldExist { return true }
            Thread.sleep(forTimeInterval: 0.05)
        } while Date() < deadline
        return cacheDirectoryExists == shouldExist
    }
}

// MARK: - Always on

/// Serialized and `@MainActor`: every test drives one process-wide service and
/// one directory on disk.
@MainActor
@Suite("Error logging toggle", .serialized)
struct ErrorLoggingToggleTests {

    /// What the disable arm has to do: drop everything Sentry has queued on
    /// disk. A crash report, or an envelope that failed to upload while the
    /// machine was offline, is sent the next time the SDK starts — the same leak
    /// one launch later. It runs even when the SDK was never started, so a queue
    /// an earlier session left cannot outlive the opt-out either.
    @Test func shutdownPurgesEverythingQueuedOnDisk() throws {
        try ErrorLoggingToggleFixture.withCleanSentry {
            let envelope = try ErrorLoggingToggleFixture.plantAQueuedReport()
            #expect(FileManager.default.fileExists(atPath: envelope.path))
            #expect(SentryService.isSDKRunning == false)

            SentryService.shutdown()

            #expect(ErrorLoggingToggleFixture.cacheDirectoryExists == false)
        }
    }

    /// With the SDK down, every send path is closed and inert. `capture` and
    /// friends are called from error handlers, so "inert" has to mean returning
    /// quietly rather than trapping.
    ///
    /// The other half of the gate — the preference closing these paths while the
    /// SDK is still up — is asserted in the opt-in suite below, because writing a
    /// new value to that preference ends the test host.
    @Test func everySendPathIsClosedWhileTheSDKIsDown() {
        ErrorLoggingToggleFixture.withCleanSentry {
            #expect(SentryService.isSDKRunning == false)
            #expect(SentryService.isReportingEnabled == false)
            #expect(SentryService.startTransaction(name: "must not start", operation: "test") == nil)
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

/// The same claim, asserted against the two things the CI suite above cannot
/// touch: `SentrySDK.isEnabled` — the SDK's own client state — and the real
/// `@AppStorage` Settings property. Both destabilise the *test host*, which
/// XCTest reports as a 0.000s failure whose name moves between runs and takes
/// every other suite in flight down with it:
///
/// - `SentrySDK.close()` runs `SentryDependencyContainer.reset()`, and
///   `sentrycrashbic_startCache` registers dyld add/remove-image callbacks it
///   never unregisters, so starting and closing the SDK inside one process is
///   not something the SDK supports being done repeatedly. The app does it once,
///   when the user opts out.
/// - Writing a new value to the `enableErrorLogging` preference ends the test
///   host outright, with no crash report at all. The host IS the app: its live
///   `GeneralSettingsManager` holds an `@AppStorage` on that key, so the write
///   drives SwiftUI's update machinery from outside a view. That is a property
///   of the test host, not of the fix — the same write from the real toggle,
///   inside a real view, is what the app does every day.
///
/// `macos-ci.yml`'s crash-report step looks for `hyperwhisper*.ips`, which does
/// not match `HyperWhisper`, so neither report ever reaches the log — which is
/// what makes these expensive to debug in CI and cheap to run deliberately:
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

    /// Issue #551 itself: the Settings property, not `SentryService`. Removing
    /// the `didSet` disable arm fails this.
    @Test func theSettingsPropertyItselfStopsTheSDK() {
        ErrorLoggingToggleFixture.withErrorLoggingOn {
            let settings = GeneralSettingsManager()
            #expect(settings.enableErrorLogging)
            SentryService.initialize(
                dsn: ErrorLoggingToggleFixture.localDSN,
                environment: "test"
            )
            #expect(SentryService.isSDKRunning)

            settings.enableErrorLogging = false

            #expect(SentryService.isSDKRunning == false)
            #expect(SentryService.isReportingEnabled == false)
            #expect(ErrorLoggingToggleFixture.waitForCacheDirectory(toExist: false))
        }
    }

    /// One test, one lifecycle: off must stop the SDK in the same session, and
    /// on must start it again in the same session.
    @Test func theSDKStopsAndStartsAgainWithoutAnAppRestart() {
        ErrorLoggingToggleFixture.withErrorLoggingOn {
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
