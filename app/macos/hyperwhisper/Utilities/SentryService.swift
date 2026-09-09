//
//  SentryService.swift
//  hyperwhisper
//
//  Lightweight wrapper around Sentry SDK with privacy-safe defaults.
//  Compiles away when Sentry is not present.
//
//  FEATURES:
//  - Performance tracing (100% sampling) for identifying slow operations
//  - Release health tracking for crash-free session monitoring
//  - Custom spans for instrumenting transcription pipeline
//  - Device/system tags for filtering issues by hardware
//  - User action breadcrumbs for understanding flows before errors
//  - Custom metrics for KPI dashboards
//

import Foundation
#if canImport(Sentry)
import Sentry
#endif

// MARK: - SentryService

enum SentryService {

    /// Rebuild an error with identifiers only. Error descriptions and NSError
    /// userInfo can contain paths, provider bodies, URLs, or credentials.
    static func identifierOnlyError(_ error: Error) -> Error {
        let nsError = error as NSError
        return NSError(domain: nsError.domain, code: nsError.code, userInfo: nil)
    }

    // MARK: - Enablement

    /// The SDK's own view of whether it is running: it has a hub with a client
    /// bound to it. False before `initialize()` and again after `shutdown()`.
    ///
    /// `isReportingEnabled` is the gate the send paths below check. This is the
    /// raw state underneath it, exposed so a test can prove the Settings toggle
    /// really stopped the SDK rather than only flipping a flag of ours.
    // internal (not private): read by ErrorLoggingToggleTests.
    static var isSDKRunning: Bool {
        #if canImport(Sentry)
        return SentrySDK.isEnabled
        #else
        return false
        #endif
    }

    /// Whether an event may leave this machine right now.
    ///
    /// Both doors have to be open:
    ///
    /// - `isSDKRunning` — the door that matters most, because the automatic
    ///   integrations (crash reporting, app-hang detection, the URLSession
    ///   swizzle that reports failed requests, release-health sessions) never
    ///   come through this type at all. Only closing the SDK stops those, so a
    ///   flag on its own could never have fixed issue #551.
    /// - `AppLogger.isErrorLoggingEnabled` — the user's Settings → General →
    ///   Error logging toggle, read fresh on every call. Most call sites already
    ///   check it by hand; checking it here too means the one that forgets still
    ///   cannot send something the user has opted out of.
    static var isReportingEnabled: Bool {
        isSDKRunning && AppLogger.isErrorLoggingEnabled
    }

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
            options.beforeSend = { event in
                // LAST GATE BEFORE THE NETWORK
                // The user can turn error logging off at any moment, including
                // while an event the SDK built for itself is already in flight —
                // a failed request, an app hang, a crash report recovered from
                // the last session. None of those pass through capture() below,
                // and shutdown() cannot reach one that is mid-air. beforeSend
                // runs on the way out of every event, so it is the last place to
                // drop it. Issue #551.
                guard AppLogger.isErrorLoggingEnabled else { return nil }

                // Remove breadcrumbs to avoid leaking text content via logs
                // Note: We still collect breadcrumbs locally for debugging flow,
                // but strip them before sending to Sentry for privacy
                event.breadcrumbs = nil
                // Drop any suspicious extras
                var sanitized = event.extra ?? [:]
                for key in sanitized.keys {
                    if Self.isRedactedExtraKey(key) {
                        sanitized[key] = "[redacted]"
                    }
                }
                event.extra = sanitized
                return event
            }
        }

        // DEVICE/SYSTEM TAGS
        // Set global tags for filtering issues by hardware/software configuration
        // These tags appear on every event, making it easy to filter in Sentry UI
        SentrySDK.configureScope { scope in
            // macOS version (e.g., "14.2.1")
            scope.setTag(value: ProcessInfo.processInfo.operatingSystemVersionString, key: "macos_version")

            // Build number for precise version tracking
            scope.setTag(value: build, key: "build_number")

            // CPU architecture - helps identify Apple Silicon vs Intel issues
            #if arch(arm64)
            scope.setTag(value: "apple_silicon", key: "architecture")
            #else
            scope.setTag(value: "intel", key: "architecture")
            #endif

            // Processor count - helps identify performance issues on low-core machines
            scope.setTag(value: String(ProcessInfo.processInfo.processorCount), key: "cpu_cores")
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
    ///    None of those go through `capture` below, so gating `capture` alone
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

    /// Return whether an extra key can identify user speech or prompt content.
    static func isRedactedExtraKey(_ key: String) -> Bool {
        let lower = key.lowercased()
        return lower.contains("transcript") || lower.contains("text") || lower.contains("prompt")
    }

    // MARK: - Breadcrumbs

    /// Add a breadcrumb for debugging flow before errors occur.
    /// Note: Breadcrumbs are stripped before sending to Sentry for privacy,
    /// but are useful for local debugging and understanding user flows.
    static func addBreadcrumb(message: String, category: String, level: SentryLevel = .info, data: [String: Any] = [:]) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }
        let crumb = Breadcrumb(level: level, category: category)
        crumb.message = message
        crumb.data = data
        SentrySDK.addBreadcrumb(crumb)
        #endif
    }

    // MARK: - Error Capture

    /// Capture an error with optional message and extra context.
    /// - Parameters:
    ///   - error: The error to capture
    ///   - message: Optional descriptive message (improves grouping and readability)
    ///   - extras: Additional context (will NOT affect grouping)
    ///   - tags: Tags for filtering (will NOT affect grouping)
    ///   - fingerprint: Optional custom fingerprint for grouping (defaults to [message, error.localizedDescription])
    ///   - includeRecentLogs: Whether to attach recent sanitized logs for debugging context (default: true)
    static func capture(
        error: Error,
        message: String? = nil,
        extras: [String: Any] = [:],
        tags: [String: String] = [:],
        fingerprint: [String]? = nil,
        includeRecentLogs: Bool = true
    ) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }

        // Capture error with ALL context in a SINGLE event
        // This prevents creating separate INFO-level message events
        let event = Event(error: error)
        event.level = .error

        // Set message for better issue titles in Sentry UI
        if let message {
            event.message = SentryMessage(formatted: message)
        }

        // Add tags (use local scope, not global configureScope)
        // Initialize tags dictionary if nil - optional chaining does nothing on nil
        if event.tags == nil {
            event.tags = [:]
        }
        for (k, v) in tags {
            event.tags?[k] = v
        }

        // Add extras
        // Initialize extras dictionary if nil - optional chaining does nothing on nil
        if event.extra == nil {
            event.extra = [:]
        }
        for (k, v) in extras {
            event.extra?[k] = v
        }

        // DIAGNOSTIC LOGS ATTACHMENT
        // Attach recent sanitized logs for debugging context.
        // Logs are fetched from os.log and sanitized to remove PII before sending.
        // The beforeSend hook provides additional sanitization as a safety net.
        if includeRecentLogs {
            // Fetch last 5 minutes of logs, max 100 lines
            // This runs synchronously but is fast (< 100ms typically)
            if let recentLogs = AppLogger.getRecentLogs(minutes: 5, maxLines: 100) {
                event.extra?["recent_logs"] = recentLogs
            }
        }

        // Set custom fingerprint for proper grouping
        // Without this, Sentry groups by stack trace which can merge unrelated errors
        if let fingerprint {
            event.fingerprint = fingerprint
        } else if let message {
            // Default: group by message + error type
            let errorType = String(describing: type(of: error))
            event.fingerprint = ["{{ default }}", message, errorType]
        }

        SentrySDK.capture(event: event)
        #else
        // No-op when Sentry SDK is not linked
        _ = (error, message, extras, tags, fingerprint, includeRecentLogs)
        #endif
    }

    /// Capture a non-error diagnostic event with structured context.
    /// Useful for slow-path warnings that succeeded but still need production visibility.
    static func captureMessage(
        _ message: String,
        level: SentryLevel = .info,
        extras: [String: Any] = [:],
        tags: [String: String] = [:],
        includeRecentLogs: Bool = true
    ) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }

        let event = Event()
        event.level = level
        event.message = SentryMessage(formatted: message)

        if event.tags == nil {
            event.tags = [:]
        }
        for (k, v) in tags {
            event.tags?[k] = v
        }

        if event.extra == nil {
            event.extra = [:]
        }
        for (k, v) in extras {
            event.extra?[k] = v
        }

        if includeRecentLogs, let recentLogs = AppLogger.getRecentLogs(minutes: 5, maxLines: 100) {
            event.extra?["recent_logs"] = recentLogs
        }

        SentrySDK.capture(event: event)
        #endif
    }

    // MARK: - Tags

    /// Programmatic tag setter (safe when Sentry absent).
    /// Tags are indexed and searchable in Sentry - use for filterable dimensions.
    static func setTag(_ key: String, _ value: String) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }
        SentrySDK.configureScope { $0.setTag(value: value, key: key) }
        #else
        _ = (key, value)
        #endif
    }

    /// Set multiple scope extras at once. Unlike breadcrumbs (which `beforeSend`
    /// strips for privacy), scope extras survive and ride along with the next
    /// captured event — use for lightweight diagnostics like per-stage timings.
    static func setExtras(_ extras: [String: Any]) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }
        SentrySDK.configureScope { scope in
            for (key, value) in extras {
                scope.setExtra(value: value, key: key)
            }
        }
        #endif
    }

    // MARK: - Performance Spans

    /// Start a new transaction for a user-facing operation.
    /// Transactions are the top-level performance unit in Sentry.
    /// Use for major operations like "Transcribe Audio" or "Export Diagnostics".
    /// - Parameters:
    ///   - name: Human-readable name (e.g., "Transcribe Audio")
    ///   - operation: Category (e.g., "transcription", "ui", "export")
    /// - Returns: A span that must be finished when the operation completes
    @discardableResult
    static func startTransaction(name: String, operation: String) -> SpanProtocol? {
        #if canImport(Sentry)
        // A finished transaction is an event like any other, so it goes through
        // the same gate. Callers already handle a nil span.
        guard isReportingEnabled else { return nil }
        return SentrySDK.startTransaction(name: name, operation: operation, bindToScope: true)
        #else
        return nil
        #endif
    }

    /// Start a child span under the current transaction.
    /// Use for sub-operations within a transaction (e.g., "API Call", "Audio Conversion").
    /// - Parameters:
    ///   - operation: Category (e.g., "http", "file", "process")
    ///   - description: Human-readable description (e.g., "POST /transcribe")
    /// - Returns: A span that must be finished when the sub-operation completes
    @discardableResult
    static func startSpan(operation: String, description: String) -> SpanProtocol? {
        #if canImport(Sentry)
        return SentrySDK.span?.startChild(operation: operation, description: description)
        #else
        return nil
        #endif
    }

    /// Finish a span, recording its duration.
    /// Call this when the operation represented by the span completes.
    static func finishSpan(_ span: SpanProtocol?, status: SpanStatus = .ok) {
        #if canImport(Sentry)
        span?.status = status
        span?.finish()
        #endif
    }

    /// Measure an async operation and record it as a span.
    /// Automatically starts and finishes the span around the work closure.
    /// - Parameters:
    ///   - operation: Category (e.g., "transcription", "http")
    ///   - description: Human-readable description
    ///   - work: The async work to measure
    /// - Returns: The result of the work closure
    static func measureAsync<T>(
        operation: String,
        description: String,
        work: () async throws -> T
    ) async rethrows -> T {
        #if canImport(Sentry)
        let span = SentrySDK.span?.startChild(operation: operation, description: description)
        do {
            let result = try await work()
            span?.status = .ok
            span?.finish()
            return result
        } catch {
            span?.status = .internalError
            span?.finish()
            throw error
        }
        #else
        return try await work()
        #endif
    }

    /// Measure a synchronous operation and record it as a span.
    static func measure<T>(
        operation: String,
        description: String,
        work: () throws -> T
    ) rethrows -> T {
        #if canImport(Sentry)
        let span = SentrySDK.span?.startChild(operation: operation, description: description)
        do {
            let result = try work()
            span?.status = .ok
            span?.finish()
            return result
        } catch {
            span?.status = .internalError
            span?.finish()
            throw error
        }
        #else
        return try work()
        #endif
    }

    // MARK: - Custom Metrics
    // NOTE: Sentry metrics API (SentrySDK.metrics) requires explicit enablement
    // and may have limited availability. These functions are stubs that log locally
    // until metrics are configured. To enable, add to options:
    //   options.enableMetrics = true (if available in your SDK version)

}

// MARK: - SpanProtocol Extension

#if canImport(Sentry)
/// Protocol to abstract Sentry's Span type for easier testing and type erasure.
/// The actual Sentry Span conforms to this via the SDK.
public typealias SpanProtocol = Span
public typealias SpanStatus = SentrySpanStatus
#else
/// Stub protocol when Sentry is not available.
public protocol SpanProtocol {
    var status: SpanStatus { get set }
    func finish()
    func startChild(operation: String, description: String) -> SpanProtocol
}
public enum SpanStatus {
    case ok
    case internalError
    /// Work that was abandoned on purpose rather than succeeding or failing — e.g. a
    /// recording start superseded by a newer one. Mirrors `kSentrySpanStatusCancelled`.
    case cancelled
}
#endif
