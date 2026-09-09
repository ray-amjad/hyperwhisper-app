//
//  ErrorLoggingToggleTests.swift
//  hyperwhisperTests
//
//  Issue #551 — Settings → General → Error logging is the privacy opt-out the
//  data-privacy page points at. Turning it OFF has to stop Sentry immediately,
//  not at the next launch, and turning it back ON has to start it again without
//  a launch either.
//
//  These assert on the SDK's own state (`SentrySDK.isEnabled`, surfaced as
//  `SentryService.isSDKRunning`) and on the queue it keeps on disk, rather than
//  on a flag of ours, so a change that only flipped a boolean would fail here.
//
//  Serialized and @MainActor, and the whole suite is: every test drives one
//  process-wide SDK and one directory on disk, and sentry-cocoa only installs
//  its hub synchronously when start() is called from the main thread (off the
//  main thread it dispatches the install async, so `isSDKRunning` would read
//  false right after `initialize`).
//
//  Note for anyone running these on their own Mac: the test host shares the
//  installed app's bundle identifier, so the purge clears
//  ~/Library/Caches/<bundle id>/Sentry for the copy of HyperWhisper you have
//  installed too. That is only Sentry's own queue, and only for this app.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("Error logging toggle", .serialized)
struct ErrorLoggingToggleTests {

    /// A syntactically valid DSN whose host is the discard port on loopback: the
    /// SDK starts for real and builds real envelopes, but no request can reach
    /// anything and nothing leaves the machine.
    private static let localDSN = "http://0123456789abcdef0123456789abcdef@127.0.0.1:9/1"

    private static let settingKey = "enableErrorLogging"

    /// Leave no SDK running and no queue on disk for the next test, whichever
    /// way the one that just ran exited.
    private func withCleanSentry(_ body: () throws -> Void) rethrows {
        let original = UserDefaults.standard.object(forKey: Self.settingKey)
        UserDefaults.standard.set(true, forKey: Self.settingKey)
        SentryService.shutdown()
        defer {
            SentryService.shutdown()
            if let original {
                UserDefaults.standard.set(original, forKey: Self.settingKey)
            } else {
                UserDefaults.standard.removeObject(forKey: Self.settingKey)
            }
        }
        try body()
    }

    // MARK: - The bug in #551

    /// Turning the setting off must stop the SDK there and then. Before the fix
    /// the `didSet` had an enable arm and no disable arm, so the SDK stayed up —
    /// crash handler, app-hang detection, failed-request reporting and
    /// release-health sessions all still running — until the app was restarted.
    @Test func shutdownStopsTheSDKWithoutARestart() {
        withCleanSentry {
            SentryService.initialize(dsn: Self.localDSN, environment: "test")
            #expect(SentryService.isSDKRunning)
            #expect(SentryService.isReportingEnabled)

            SentryService.shutdown()

            #expect(SentryService.isSDKRunning == false)
            #expect(SentryService.isReportingEnabled == false)
        }
    }

    /// The regression test proper: drive the Settings toggle itself rather than
    /// `SentryService`, so that deleting the `didSet` disable arm — the whole of
    /// issue #551 — fails here rather than passing on the service API alone.
    @Test func theSettingsToggleItselfStopsTheSDK() {
        withCleanSentry {
            let settings = GeneralSettingsManager()
            #expect(settings.enableErrorLogging)
            SentryService.initialize(dsn: Self.localDSN, environment: "test")
            #expect(SentryService.isSDKRunning)

            settings.enableErrorLogging = false

            #expect(SentryService.isSDKRunning == false)
            #expect(SentryService.isReportingEnabled == false)
            #expect(Self.waitForCacheDirectory(toExist: false))
        }
    }

    /// The reverse direction has to work without a restart too, or the fix would
    /// only trade one restart for another.
    @Test func turningItBackOnRestartsTheSDKWithoutARestart() {
        withCleanSentry {
            SentryService.initialize(dsn: Self.localDSN, environment: "test")
            SentryService.shutdown()
            #expect(SentryService.isSDKRunning == false)

            SentryService.initialize(dsn: Self.localDSN, environment: "test")

            #expect(SentryService.isSDKRunning)
            #expect(SentryService.isReportingEnabled)
        }
    }

    /// A capture after the opt-out must be inert, and must not quietly bring the
    /// SDK back or write a new envelope to disk.
    @Test func captureIsInertAfterShutdown() {
        withCleanSentry {
            SentryService.initialize(dsn: Self.localDSN, environment: "test")
            SentryService.shutdown()

            SentryService.capture(
                error: NSError(domain: "hyperwhisper.test.551", code: 1),
                message: "must not be captured",
                includeRecentLogs: false
            )
            SentryService.captureMessage("must not be captured", includeRecentLogs: false)
            SentryService.addBreadcrumb(message: "must not be recorded", category: "test")
            #expect(SentryService.startTransaction(name: "must not start", operation: "test") == nil)

            #expect(SentryService.isSDKRunning == false)
            #expect(Self.waitForCacheDirectory(toExist: false))
        }
    }

    /// The second door: even with the SDK still up, the user's setting alone
    /// closes the send paths. Most call sites already check
    /// `AppLogger.isErrorLoggingEnabled` by hand; this covers the one that
    /// forgets.
    @Test func theSettingAloneGatesReportingWhileTheSDKIsUp() {
        withCleanSentry {
            SentryService.initialize(dsn: Self.localDSN, environment: "test")
            #expect(SentryService.isReportingEnabled)

            UserDefaults.standard.set(false, forKey: Self.settingKey)

            #expect(SentryService.isSDKRunning)
            #expect(SentryService.isReportingEnabled == false)
            #expect(SentryService.startTransaction(name: "must not start", operation: "test") == nil)
        }
    }

    // MARK: - What is already queued on disk

    /// Starting the SDK creates its cache directory, and turning the setting off
    /// deletes it. A crash report, or an envelope that failed to upload while
    /// the machine was offline, sits there and is sent the next time the SDK
    /// starts — the same leak one launch later.
    @Test func shutdownDeletesTheQueueTheSDKKeepsOnDisk() throws {
        try withCleanSentry {
            let cacheDirectory = try #require(SentryService.cacheDirectory)

            SentryService.initialize(dsn: Self.localDSN, environment: "test")
            // If this fails, the cacheDirectoryPath override in initialize() no
            // longer matches where sentry-cocoa actually writes, and shutdown()
            // is deleting the wrong directory.
            #expect(
                Self.waitForCacheDirectory(toExist: true),
                "Sentry should be writing its state under \(cacheDirectory.path)"
            )

            SentryService.shutdown()

            #expect(
                Self.waitForCacheDirectory(toExist: false),
                "shutdown() must delete Sentry's on-disk queue at \(cacheDirectory.path)"
            )
        }
    }

    /// The purge runs even when the SDK was never started, so a queue left by an
    /// earlier session cannot outlive the opt-out.
    @Test func shutdownPurgesAQueueLeftBehindByAnEarlierSession() throws {
        try withCleanSentry {
            let cacheDirectory = try #require(SentryService.cacheDirectory)
            let leftover = cacheDirectory
                .appendingPathComponent("io.sentry", isDirectory: true)
                .appendingPathComponent("deadbeef", isDirectory: true)
                .appendingPathComponent("envelopes", isDirectory: true)
            try FileManager.default.createDirectory(at: leftover, withIntermediateDirectories: true)
            try Data("queued".utf8).write(to: leftover.appendingPathComponent("1.envelope"))

            #expect(SentryService.isSDKRunning == false)
            SentryService.shutdown()

            #expect(Self.waitForCacheDirectory(toExist: false))
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

    // MARK: - Helpers

    /// Poll for the cache directory to appear or disappear. The SDK writes it
    /// from a background queue, so both directions need a small window rather
    /// than one instant read.
    @MainActor
    private static func waitForCacheDirectory(
        toExist shouldExist: Bool,
        timeout: TimeInterval = 3
    ) -> Bool {
        guard let path = cacheDirectoryPath else { return !shouldExist }
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if FileManager.default.fileExists(atPath: path) == shouldExist { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        } while Date() < deadline
        return FileManager.default.fileExists(atPath: path) == shouldExist
    }

    private static var cacheDirectoryPath: String? { SentryService.cacheDirectory?.path }
}
