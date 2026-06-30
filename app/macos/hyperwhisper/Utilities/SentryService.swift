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

            // Scrub potentially sensitive data from error events
            options.beforeSend = { event in
                // Remove breadcrumbs to avoid leaking text content via logs
                // Note: We still collect breadcrumbs locally for debugging flow,
                // but strip them before sending to Sentry for privacy
                event.breadcrumbs = nil
                // Drop any suspicious extras
                var sanitized = event.extra ?? [:]
                for key in sanitized.keys {
                    let lower = key.lowercased()
                    if lower.contains("transcript") || lower.contains("text") || lower.contains("prompt") {
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

    // MARK: - Breadcrumbs

    /// Add a breadcrumb for debugging flow before errors occur.
    /// Note: Breadcrumbs are stripped before sending to Sentry for privacy,
    /// but are useful for local debugging and understanding user flows.
    static func addBreadcrumb(message: String, category: String, level: SentryLevel = .info, data: [String: Any] = [:]) {
        #if canImport(Sentry)
        let crumb = Breadcrumb(level: level, category: category)
        crumb.message = message
        crumb.data = data
        SentrySDK.addBreadcrumb(crumb)
        #endif
    }

    /// Track a user action as a breadcrumb.
    /// Use this for key user interactions to understand the flow before an error.
    /// Examples: "started_recording", "transcription_complete", "mode_selected"
    static func trackUserAction(_ action: String, data: [String: Any] = [:]) {
        #if canImport(Sentry)
        let crumb = Breadcrumb(level: .info, category: "user_action")
        crumb.message = action
        crumb.data = data
        crumb.timestamp = Date()
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
        SentrySDK.configureScope { $0.setTag(value: value, key: key) }
        #else
        _ = (key, value)
        #endif
    }

    /// Set multiple tags at once for efficiency.
    static func setTags(_ tags: [String: String]) {
        #if canImport(Sentry)
        SentrySDK.configureScope { scope in
            for (key, value) in tags {
                scope.setTag(value: value, key: key)
            }
        }
        #endif
    }

    // MARK: - Transcription Context

    /// Set transcription-specific context for better error filtering.
    /// Call this before starting a transcription to tag all subsequent events.
    /// - Parameters:
    ///   - provider: The transcription provider ("local", "hyperwhisper_cloud", "openai")
    ///   - mode: The transcription mode name (e.g., "Default", "Meeting Notes")
    ///   - audioLengthSeconds: Duration of the audio being transcribed
    static func setTranscriptionContext(provider: String, mode: String, audioLengthSeconds: TimeInterval? = nil) {
        #if canImport(Sentry)
        SentrySDK.configureScope { scope in
            scope.setTag(value: provider, key: "transcription_provider")
            scope.setTag(value: mode, key: "transcription_mode")
            if let audioLengthSeconds {
                // Round to nearest second for cleaner grouping
                scope.setExtra(value: Int(audioLengthSeconds), key: "audio_length_seconds")
            }
        }
        #endif
    }

    /// Clear transcription context after transcription completes.
    /// Prevents stale context from appearing on unrelated errors.
    static func clearTranscriptionContext() {
        #if canImport(Sentry)
        SentrySDK.configureScope { scope in
            scope.removeTag(key: "transcription_provider")
            scope.removeTag(key: "transcription_mode")
            scope.removeExtra(key: "audio_length_seconds")
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

    /// Record a distribution metric (for timing, sizes, etc.).
    /// Use for values where you want to see percentiles (p50, p95, p99).
    /// Examples: transcription_latency_ms, audio_length_seconds
    static func recordDistribution(key: String, value: Double, unit: String = "none", tags: [String: String] = [:]) {
        // Metrics recorded as breadcrumbs until Sentry metrics API is enabled
        #if canImport(Sentry)
        let crumb = Breadcrumb(level: .debug, category: "metric.distribution")
        crumb.message = "\(key): \(value) \(unit)"
        var data: [String: Any] = ["value": value, "unit": unit]
        for (k, v) in tags { data[k] = v }
        crumb.data = data
        SentrySDK.addBreadcrumb(crumb)
        #endif
    }

    /// Increment a counter metric.
    /// Use for counting occurrences of events.
    /// Examples: transcription_started, paste_success, paste_failure
    static func incrementCounter(key: String, by value: Double = 1.0, tags: [String: String] = [:]) {
        #if canImport(Sentry)
        let crumb = Breadcrumb(level: .debug, category: "metric.counter")
        crumb.message = "\(key): +\(value)"
        var data: [String: Any] = ["increment": value]
        for (k, v) in tags { data[k] = v }
        crumb.data = data
        SentrySDK.addBreadcrumb(crumb)
        #endif
    }

    /// Record a gauge metric (for current values).
    /// Use for values that can go up and down.
    /// Examples: active_recordings, queue_depth
    static func recordGauge(key: String, value: Double, unit: String = "none", tags: [String: String] = [:]) {
        #if canImport(Sentry)
        let crumb = Breadcrumb(level: .debug, category: "metric.gauge")
        crumb.message = "\(key): \(value) \(unit)"
        var data: [String: Any] = ["value": value, "unit": unit]
        for (k, v) in tags { data[k] = v }
        crumb.data = data
        SentrySDK.addBreadcrumb(crumb)
        #endif
    }

    /// Record a set metric (for counting unique values).
    /// Use for counting unique items.
    /// Examples: unique_users, unique_modes_used
    static func recordSet(key: String, value: String, tags: [String: String] = [:]) {
        #if canImport(Sentry)
        let crumb = Breadcrumb(level: .debug, category: "metric.set")
        crumb.message = "\(key): \(value)"
        var data: [String: Any] = ["value": value]
        for (k, v) in tags { data[k] = v }
        crumb.data = data
        SentrySDK.addBreadcrumb(crumb)
        #endif
    }
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
}
#endif
