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

import XCTest
@testable import hyperwhisper

final class ErrorLoggingToggleTests: XCTestCase {

    /// A syntactically valid DSN whose host is the discard port on loopback:
    /// the SDK starts for real and builds real envelopes, but no request can
    /// reach anything, and nothing leaves the machine.
    private let localDSN = "http://0123456789abcdef0123456789abcdef@127.0.0.1:9/1"

    private var settingKey: String { "enableErrorLogging" }
    private var originalSetting: Any?

    override func setUp() {
        super.setUp()
        originalSetting = UserDefaults.standard.object(forKey: settingKey)
        // The gate reads the user setting on every call, so pin it on for the
        // tests that are about the SDK rather than about the setting.
        UserDefaults.standard.set(true, forKey: settingKey)
        SentryService.shutdown()
    }

    override func tearDown() {
        SentryService.shutdown()
        if let originalSetting {
            UserDefaults.standard.set(originalSetting, forKey: settingKey)
        } else {
            UserDefaults.standard.removeObject(forKey: settingKey)
        }
        super.tearDown()
    }

    // MARK: - The bug in #551

    /// Turning the setting off must stop the SDK there and then. Before the fix
    /// the `didSet` had an enable arm and no disable arm, so the SDK stayed up —
    /// with its crash handler, app-hang detection, failed-request reporting and
    /// release-health sessions all still running — until the app was restarted.
    func testShutdownStopsTheSDKWithoutARestart() {
        SentryService.initialize(dsn: localDSN, environment: "test")
        XCTAssertTrue(SentryService.isSDKRunning, "Sentry should be running after initialize")
        XCTAssertTrue(SentryService.isReportingEnabled, "Reporting should be open while the setting is on")

        SentryService.shutdown()

        XCTAssertFalse(SentryService.isSDKRunning, "shutdown() must close the SDK, not just gate our own calls")
        XCTAssertFalse(SentryService.isReportingEnabled, "Nothing may be sent once the user has opted out")
    }

    /// The reverse direction has to work without a restart too, otherwise the
    /// fix would trade one restart for another.
    func testTurningItBackOnRestartsTheSDKWithoutARestart() {
        SentryService.initialize(dsn: localDSN, environment: "test")
        SentryService.shutdown()
        XCTAssertFalse(SentryService.isSDKRunning)

        SentryService.initialize(dsn: localDSN, environment: "test")

        XCTAssertTrue(SentryService.isSDKRunning, "Re-enabling must start the SDK again in the same session")
        XCTAssertTrue(SentryService.isReportingEnabled)
    }

    /// `capture` must be inert after the opt-out — and must not quietly restart
    /// anything or write a new envelope to disk.
    func testCaptureIsInertAfterShutdown() {
        SentryService.initialize(dsn: localDSN, environment: "test")
        SentryService.shutdown()

        SentryService.capture(
            error: NSError(domain: "hyperwhisper.test.551", code: 1),
            message: "must not be captured",
            includeRecentLogs: false
        )
        SentryService.captureMessage("must not be captured", includeRecentLogs: false)
        SentryService.addBreadcrumb(message: "must not be recorded", category: "test")
        XCTAssertNil(SentryService.startTransaction(name: "must not start", operation: "test"))

        XCTAssertFalse(SentryService.isSDKRunning, "A capture after the opt-out must not bring the SDK back")
        assertCacheDirectoryIsGone()
    }

    /// The second door: even with the SDK somehow still up, the user's setting
    /// alone closes the send paths. Most call sites already check
    /// `AppLogger.isErrorLoggingEnabled` by hand; this proves the one that
    /// forgets is covered too.
    func testTheSettingAloneGatesReportingWhileTheSDKIsUp() {
        SentryService.initialize(dsn: localDSN, environment: "test")
        XCTAssertTrue(SentryService.isReportingEnabled)

        UserDefaults.standard.set(false, forKey: settingKey)

        XCTAssertTrue(SentryService.isSDKRunning, "precondition: the SDK is still up in this test")
        XCTAssertFalse(SentryService.isReportingEnabled, "The user setting must gate reporting on its own")
        XCTAssertNil(SentryService.startTransaction(name: "must not start", operation: "test"))
    }

    // MARK: - What is already queued on disk

    /// Starting the SDK creates its cache directory, and turning the setting off
    /// deletes it. A crash report or an envelope that failed to upload sits
    /// there and is sent the next time the SDK starts, so leaving it would be
    /// the same leak one launch later.
    func testShutdownDeletesTheQueueTheSDKKeepsOnDisk() throws {
        let cacheDirectory = try XCTUnwrap(SentryService.cacheDirectory)

        SentryService.initialize(dsn: localDSN, environment: "test")
        XCTAssertTrue(
            waitForFile(at: cacheDirectory, toExist: true),
            "Sentry should be writing its state under \(cacheDirectory.path) — if it is not, the cacheDirectoryPath override in initialize() no longer matches where the SDK actually writes, and shutdown() is deleting the wrong directory"
        )

        SentryService.shutdown()

        XCTAssertTrue(
            waitForFile(at: cacheDirectory, toExist: false),
            "shutdown() must delete Sentry's on-disk queue at \(cacheDirectory.path)"
        )
    }

    /// The purge runs even when the SDK was never started, so a queue left by an
    /// earlier session cannot outlive the opt-out.
    func testShutdownPurgesAQueueLeftBehindByAnEarlierSession() throws {
        let cacheDirectory = try XCTUnwrap(SentryService.cacheDirectory)
        let leftover = cacheDirectory
            .appendingPathComponent("io.sentry", isDirectory: true)
            .appendingPathComponent("deadbeef", isDirectory: true)
            .appendingPathComponent("envelopes", isDirectory: true)
        try FileManager.default.createDirectory(at: leftover, withIntermediateDirectories: true)
        try Data("queued".utf8).write(to: leftover.appendingPathComponent("1.envelope"))

        XCTAssertFalse(SentryService.isSDKRunning, "precondition: nothing running")
        SentryService.shutdown()

        assertCacheDirectoryIsGone()
    }

    /// This app is not sandboxed, so the SDK's default cache location is the
    /// SHARED `~/Library/Caches` — every Sentry-using Mac app writes into the
    /// same `io.sentry` folder there. The purge above is only safe because
    /// `initialize` moves our state into a directory of our own; if that ever
    /// regresses, `shutdown()` would be deleting other apps' crash queues.
    func testTheCacheDirectoryIsPrivateToThisApp() throws {
        let cacheDirectory = try XCTUnwrap(SentryService.cacheDirectory)
        let sharedCaches = try XCTUnwrap(
            FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
        )

        XCTAssertNotEqual(cacheDirectory.standardizedFileURL, sharedCaches.standardizedFileURL)
        XCTAssertNotEqual(
            cacheDirectory.standardizedFileURL,
            sharedCaches.appendingPathComponent("io.sentry").standardizedFileURL,
            "shutdown() deletes this directory whole — it must never be a folder shared with other vendors' apps"
        )
        let bundleID = try XCTUnwrap(Bundle.main.bundleIdentifier)
        XCTAssertTrue(
            cacheDirectory.path.contains(bundleID),
            "The Sentry cache directory should be scoped to this app's bundle identifier, got \(cacheDirectory.path)"
        )
    }

    // MARK: - Helpers

    private func assertCacheDirectoryIsGone(file: StaticString = #filePath, line: UInt = #line) {
        guard let cacheDirectory = SentryService.cacheDirectory else { return }
        XCTAssertTrue(
            waitForFile(at: cacheDirectory, toExist: false),
            "Sentry's on-disk state should be gone at \(cacheDirectory.path)",
            file: file,
            line: line
        )
    }

    /// Poll for a file to appear or disappear. The SDK writes its cache from a
    /// background queue, so both directions need a small window rather than an
    /// instant read.
    private func waitForFile(at url: URL, toExist shouldExist: Bool, timeout: TimeInterval = 5) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if FileManager.default.fileExists(atPath: url.path) == shouldExist { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        } while Date() < deadline
        return FileManager.default.fileExists(atPath: url.path) == shouldExist
    }
}
