//
//  SentryService+Lifecycle.swift
//  hyperwhisper
//
//  SDK start and stop, and the on-disk queue the stop deletes.
//  Part of `SentryService`; see SentryService.swift.
//

import Foundation
#if canImport(Sentry)
@_spi(Private) import Sentry
#endif

extension SentryService {

    // MARK: - On-disk state

    /// Where the SDK keeps its on-disk state: queued envelopes, release-health
    /// sessions, the pending app-hang event, and raw crash reports.
    ///
    /// The SDK defaults `cacheDirectoryPath` to `NSCachesDirectory`, and this app
    /// is not sandboxed (see `hyperwhisper-release.entitlements`), so that is the
    /// SHARED `~/Library/Caches` — every Sentry-using Mac app writes into the same
    /// `io.sentry` folder there. Turning error logging off has to delete our
    /// queue, and deleting a folder we share with other vendors' apps is not on,
    /// so `initialize` points the SDK at a directory of our own and `shutdown`
    /// deletes that instead.
    ///
    /// nil only if the user has no Caches directory, in which case there is
    /// nothing to purge either.
    // internal (not private): read by ErrorLoggingToggleTests.
    static var cacheDirectory: URL? {
        guard let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first else {
            return nil
        }
        let bundleID = Bundle.main.bundleIdentifier ?? "com.hyperwhisper.hyperwhisper"
        return caches
            .appendingPathComponent(bundleID, isDirectory: true)
            .appendingPathComponent("Sentry", isDirectory: true)
    }

    /// Delete everything Sentry has queued on disk.
    ///
    /// Called only from `shutdown()`. Deliberately not "flush then delete": a
    /// flush is an upload, and the whole point of the toggle is that nothing more
    /// is uploaded.
    private static func purgeQueuedReports() {
        guard let directory = cacheDirectory else { return }
        try? FileManager.default.removeItem(at: directory)
    }

    // MARK: - Initialization

    /// Initialize Sentry using DSN from Info.plist or provided string.
    static func initialize() {
        // Read DSN and environment from Info.plist
        let dsn = Bundle.main.object(forInfoDictionaryKey: "SentryDSN") as? String
        let env = Bundle.main.object(forInfoDictionaryKey: "SentryEnvironment") as? String
        initialize(dsn: dsn, environment: env)
    }

    /// Initialize with explicit DSN (no-op if empty).
    /// Configures:
    /// - Performance tracing at 100% sample rate
    /// - Release health / session tracking
    /// - App hang detection (10s threshold)
    /// - Device/system tags for filtering
    static func initialize(dsn: String?, environment: String? = nil) {
        guard let dsn, !dsn.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        #if canImport(Sentry)
        // Already running: starting a second time would replace the hub and leak
        // the first one's integrations. Matches Windows SentryService.Initialize.
        guard !SentrySDK.isEnabled else { return }

        let release = (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "unknown"
        let build = (Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String) ?? "?"
        let resolvedEnv: String = {
            if let environment, !environment.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return environment }
            #if DEBUG
            return "development"
            #else
            return "production"
            #endif
        }()

        #if arch(arm64)
        let architecture = "apple_silicon"
        #else
        let architecture = "intel"
        #endif
        let deviceTags: [String: String] = [
            "macos_version": ProcessInfo.processInfo.operatingSystemVersionString,
            "build_number": build,
            "architecture": architecture,
            "cpu_cores": String(ProcessInfo.processInfo.processorCount)
        ]
        logScopeState.reset(tags: deviceTags)

        SentrySDK.start { options in
            options.dsn = dsn
            options.environment = resolvedEnv
            options.releaseName = "hyperwhisper@\(release)"

            // ON-DISK STATE
            // Keep it in a HyperWhisper-owned directory rather than the shared
            // ~/Library/Caches default, so turning error logging off can delete
            // the queue without touching another app's. See `cacheDirectory`.
            if let directory = Self.cacheDirectory {
                options.cacheDirectoryPath = directory.path
            }

            // PERFORMANCE TRACING
            // Sample 100% of transactions to capture all performance data
            // This lets us see slow transcriptions, API calls, and UI operations
            // For a small user base, 100% is fine; reduce if Sentry quota becomes an issue
            options.tracesSampleRate = 1.0

            // PROFILING
            // CPU profiling for slow operations - helps identify code bottlenecks
            // Attached to sampled transactions
            options.profilesSampleRate = 1.0

            // RELEASE HEALTH
            // Tracks crash-free sessions per release
            // Enables "Release Health" dashboard in Sentry showing:
            // - Crash-free session % (e.g., "2.10 has 99.2% crash-free")
            // - Adoption rate (how many users upgraded)
            // - Session count per release
            options.enableAutoSessionTracking = true
            options.sessionTrackingIntervalMillis = 30000  // 30 seconds

            // Follow docs: include IP (PII); gated by enableErrorLogging
            options.sendDefaultPii = true

            // STRUCTURED LOGS
            // The store behind `captureMessage` for a non-error level. A log is
            // billed against the logs quota, not the error quota, and it is
            // never grouped into an Issue, so a diagnostic that repeats does
            // not push a real crash down the Issues list.
            //
            // Warning: leave this on. `SentrySDK.logger` builds its batcher once,
            // at client start, and only when this flag was already true. With the
            // flag off every `logger` call is dropped in silence, so the info and
            // warning diagnostics in `captureMessage` would go nowhere at all.
            options.experimental.enableLogs = true

            // HANG DETECTION CONFIGURATION
            // Increase AppHang timeout from default 2s to 10s to reduce false positives
            // from normal modal dialogs (NSAlert, NSOpenPanel) that wait for user input.
            // Modal dialogs block the main thread legitimately - not actual app freezes.
            options.enableAppHangTracking = true
            options.appHangTimeoutInterval = 10.0  // seconds (was 2.0 by default)

            // EXCLUDE LOCALHOST FROM AUTO-CAPTURED FAILED HTTP REQUESTS
            // SentryNetworkTrackingIntegration swizzles URLSessionTask and reports every
            // 5xx response as an HTTPClientError. The local llama.cpp runtime returns 503
            // on GET http://127.0.0.1:<port>/health during model warmup - this is expected
            // and already handled by LlamaServerController.waitForReadiness() which polls
            // every 250ms for up to 25s. Default failedRequestTargets = [".*"] was capturing
            // every one of those 503s, flooding Sentry with 7000+ events across 80+ users
            // (HYPERWHISPER-EW). Keep auto-capture on for remote hosts (HyperWhisper Cloud,
            // license server) but skip loopback URLs.
            options.failedRequestTargets = [
                #"^(?!https?://(127\.0\.0\.1|localhost|\[?::1\]?)(:|/|$)).*"#
            ]

            // SHUTDOWN FLUSH
            // shutdownTimeInterval is only read by SentrySDK.close(), and the
            // only thing that closes this SDK is the user opting out. Waiting
            // two seconds on the main thread to upload more of what they just
            // refused is the wrong trade in both directions, so don't wait.
            options.shutdownTimeInterval = 0

            // Scrub potentially sensitive data from error events
            options.beforeSend = Self.sanitize

            // The Logs pipeline does not use `beforeSend`. Check the same
            // privacy preference in its own last hook so work that started
            // before opt-out cannot add a new log to the SDK batch afterwards.
            options.beforeSendLog = Self.sanitize
        }

        // DEVICE/SYSTEM TAGS
        // Set global tags for filtering issues by hardware/software configuration
        // These tags appear on every event, making it easy to filter in Sentry UI
        SentrySDK.configureScope { scope in
            for (key, value) in deviceTags {
                scope.setTag(value: value, key: key)
            }
        }
        #endif
    }

    // MARK: - Shutdown

    /// Stop error reporting now, and drop whatever is still queued.
    ///
    /// Called when the user turns Settings → General → Error logging off. That
    /// switch is the opt-out the data-privacy page points at, so "stop" has to
    /// mean three separate things:
    ///
    /// 1. **Nothing new is collected.** `SentrySDK.close()` uninstalls every
    ///    integration — the crash handler, app-hang detection, the URLSession
    ///    swizzle that reports failed requests, and release-health sessions.
    ///    None of those go through `capture` (SentryService+Diagnostics.swift), so gating `capture` alone
    ///    would have left them all running.
    /// 2. **Nothing already collected is sent.** `SentrySDK.close()` calls
    ///    `flush(shutdownTimeInterval)` on its way out (sentry-cocoa
    ///    `SentryClient.close`), which drains the on-disk queue to the network —
    ///    the exact opposite of what an opt-out should do. So the queue is
    ///    deleted *before* the close, leaving that flush nothing to send. It
    ///    also makes the close fast: the flush returns as soon as there is
    ///    nothing cached, instead of waiting out its 2-second timeout on the
    ///    main thread.
    /// 3. **Nothing survives to the next launch.** A raw crash report, or an
    ///    envelope that failed to upload while the machine was offline, sits on
    ///    disk and is sent the next time the SDK starts. A crash report that
    ///    flushes on the next launch is the same leak, one launch later, so the
    ///    directory goes again once nothing is writing to it.
    ///
    /// Safe to call when Sentry was never started; it then just clears any queue
    /// an earlier session left behind.
    ///
    /// What this cannot stop: `close()` uninstalls the release-health
    /// integration by *ending* the session, and ending a session enqueues one
    /// last session envelope, which the close may then upload. sentry-cocoa has
    /// no hook to suppress it — `beforeSend` sees events, not sessions, and
    /// there is no `beforeSendEnvelope` in 8.x. That envelope carries no error,
    /// no audio and no text: session id, install id, start time, duration and
    /// the status `exited`. It is the one thing that can still leave after the
    /// tick, and it is the shutdown itself, not a diagnostic.
    static func shutdown() {
        #if canImport(Sentry)
        // `close()` flushes the in-memory Logs batch before it closes the
        // client. Release that batch first: an opt-out must discard it, not
        // turn the SDK shutdown into its final upload.
        SentrySDK.clearLogger()

        // Before the close, because the close flushes (see 2 above).
        purgeQueuedReports()

        if SentrySDK.isEnabled {
            SentrySDK.close()
        }

        // And again with the SDK stopped: the close can still write on the way
        // out, and the raw crash reports are only turned into envelopes at the
        // next start, which is what makes deleting them here the fix for (3).
        purgeQueuedReports()
        #endif
    }
}
