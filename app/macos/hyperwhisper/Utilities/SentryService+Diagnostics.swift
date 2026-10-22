//
//  SentryService+Diagnostics.swift
//  hyperwhisper
//
//  Breadcrumbs, error capture, and where a diagnostic goes (log or Issue).
//  Part of `SentryService`; see SentryService.swift.
//

import Foundation
#if canImport(Sentry)
@_spi(Private) import Sentry
#endif

extension SentryService {

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

        // Set custom fingerprint for proper grouping
        // Without this, Sentry groups by stack trace which can merge unrelated errors
        if let fingerprint {
            event.fingerprint = fingerprint
        } else if let message {
            // Default: group by message + error type
            let errorType = String(describing: type(of: error))
            event.fingerprint = ["{{ default }}", message, errorType]
        }

        // DIAGNOSTIC LOGS ATTACHMENT
        // Attach recent sanitized logs for debugging context.
        // Logs are fetched from os.log and sanitized to remove PII before sending.
        // The beforeSend hook provides additional sanitization as a safety net.
        // Never fetched on the main thread; see `withRecentLogs`.
        withRecentLogs(includeRecentLogs) { recentLogExtras in
            for (k, v) in recentLogExtras {
                event.extra?[k] = v
            }
            SentrySDK.capture(event: event)
        }
        #else
        // No-op when Sentry SDK is not linked
        _ = (error, message, extras, tags, fingerprint, includeRecentLogs)
        #endif
    }

    // MARK: - Where a diagnostic goes

    /// The 2 stores a diagnostic can land in. They have different prices.
    enum DiagnosticStore: Equatable {
        /// The logs quota. Never grouped, never shown in the Issues list.
        case log
        /// The error quota. Grouped into an Issue somebody is meant to open.
        case issue
    }

    /// Sentry's severity names as a type of our own.
    ///
    /// It exists so the routing table below can be pinned by a test. The test
    /// target does not link the Sentry package, so a test cannot name
    /// `SentryLevel` at all.
    enum DiagnosticSeverity: String, CaseIterable {
        case debug, info, warning, error, fatal
    }

    /// THE ROUTING POLICY, as one function with no side effects.
    ///
    /// If you change which severities are Issues, change it here, and the test
    /// that pins this table will tell you what moved.
    static func store(for severity: DiagnosticSeverity) -> DiagnosticStore {
        switch severity {
        case .debug, .info, .warning: return .log
        case .error, .fatal: return .issue
        }
    }

    /// A log attribute carries a scalar. The transport keeps String, Bool and
    /// the number types; anything else would be dropped without a word, so an
    /// array or a struct is turned into its description here instead.
    private static func logAttributeValue(_ value: Any) -> Any {
        switch value {
        case is String, is Bool, is NSNumber: return value
        default: return String(describing: value)
        }
    }

    /// Flatten scope and call-site context with the same precedence as an
    /// Event: local values override inherited values, and searchable tags win
    /// over extras when both use one key. Apply the Event path's redaction gate
    /// because Sentry Logs do not pass through `beforeSend`.
    static func mergeLogAttributes(
        scopeExtras: [String: Any],
        scopeTags: [String: String],
        extras: [String: Any],
        tags: [String: String]
    ) -> [String: Any] {
        var attributes: [String: Any] = [:]
        for (key, value) in scopeExtras { attributes[key] = logAttributeValue(value) }
        for (key, value) in scopeTags { attributes[key] = value }
        for (key, value) in extras { attributes[key] = logAttributeValue(value) }
        for (key, value) in tags { attributes[key] = value }
        for (key, value) in attributes {
            attributes[key] = redactedValue(forKey: key, value: value)
        }
        return attributes
    }

    #if canImport(Sentry)
    /// Sentry's level as our severity. Nil for `.none`, the SDK's absent value:
    /// nobody chose it, so it takes the conservative path and stays an Issue.
    private static func severity(of level: SentryLevel) -> DiagnosticSeverity? {
        switch level {
        case .debug: return .debug
        case .info: return .info
        case .warning: return .warning
        case .error: return .error
        case .fatal: return .fatal
        default: return nil
        }
    }

    /// The log function for a level, or nil when the level must stay an Issue.
    private static func logWriter(
        for level: SentryLevel
    ) -> ((String, [String: Any]) -> Void)? {
        guard let severity = severity(of: level), store(for: severity) == .log else {
            return nil
        }
        let logger = SentrySDK.logger
        switch severity {
        case .debug: return { logger.debug($0, attributes: $1) }
        case .warning: return { logger.warn($0, attributes: $1) }
        default: return { logger.info($0, attributes: $1) }
        }
    }

    /// Add the scope context that Sentry Cocoa 8.x does not apply to Logs.
    private static func mergedLogAttributes(
        extras: [String: Any],
        tags: [String: String]
    ) -> [String: Any] {
        let scope = logScopeState.snapshot()
        return mergeLogAttributes(
            scopeExtras: scope.extras,
            scopeTags: scope.tags,
            extras: extras,
            tags: tags
        )
    }
    #endif

    /// Capture through the routing table without exposing the Sentry SDK's
    /// level type. Call sites can pin their real severity in ordinary tests.
    static func captureDiagnosticMessage(
        _ message: String,
        severity: DiagnosticSeverity,
        extras: [String: Any] = [:],
        tags: [String: String] = [:],
        includeRecentLogs: Bool = true
    ) {
        #if canImport(Sentry)
        let level: SentryLevel
        switch severity {
        case .debug: level = .debug
        case .info: level = .info
        case .warning: level = .warning
        case .error: level = .error
        case .fatal: level = .fatal
        }
        captureMessage(
            message,
            level: level,
            extras: extras,
            tags: tags,
            includeRecentLogs: includeRecentLogs
        )
        #else
        _ = (message, severity, extras, tags, includeRecentLogs)
        #endif
    }

    /// Capture a diagnostic with structured context. THE LEVEL PICKS THE STORE.
    ///
    ///   .debug .info .warning   ->  a Sentry LOG. Logs quota. Not an Issue.
    ///   .error .fatal           ->  a Sentry EVENT. Error quota. An Issue.
    ///
    /// The rule that decides the level is not "how loud is this". It is:
    /// WOULD A PERSON OPEN THIS ONE OCCURRENCE AND DO SOMETHING? A cancelled
    /// model load and a file the cleanup could not delete are things we want to
    /// count and read in aggregate, so they are logs. They used to be Issues,
    /// which is why an expected outcome could outrank a crash in the Issues list.
    ///
    /// Warning: do not raise a level to make a diagnostic easier to find. That
    /// moves it back onto the error quota and back into the Issues list.
    ///
    /// `tags` and `extras` both become flat log attributes on the log path,
    /// because a log has one attribute bag and no indexed/unindexed split. Keep
    /// tag keys stable: they are what a log query filters on.
    ///
    /// `includeRecentLogs` applies to the EVENT path only. A 100-line log dump
    /// is an attachment for something a person will open, and nobody opens a
    /// single log line.
    static func captureMessage(
        _ message: String,
        level: SentryLevel = .info,
        extras: [String: Any] = [:],
        tags: [String: String] = [:],
        includeRecentLogs: Bool = true
    ) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }

        if let write = Self.logWriter(for: level) {
            let attributes = Self.mergedLogAttributes(extras: extras, tags: tags)
            write(message, attributes)
            return
        }

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

        withRecentLogs(includeRecentLogs) { recentLogExtras in
            for (k, v) in recentLogExtras {
                event.extra?[k] = v
            }
            SentrySDK.capture(event: event)
        }
        #endif
    }

    // MARK: - Recent logs, never on the main thread

    /// The recent-log extras for an Event, handed to `send` (#991).
    ///
    /// `send` always runs inline, on the CALLER's thread, before this returns,
    /// so `SentrySDK.capture` inside it keeps the call site's stack.
    ///
    /// The fetch shells out to `log show` and blocks until it exits, which can
    /// take seconds on a busy log. Off the main thread it runs inline, as it
    /// always did, and `send` gets `recent_logs`. On the main thread it never
    /// runs, so error reporting cannot park the UI: `send` gets
    /// `recent_logs_skipped = "main_thread"` instead, so the gap is visible on
    /// the event.
    ///
    /// With `include` false nothing is fetched and `send` gets no extras.
    static func withRecentLogs(
        _ include: Bool,
        fetch: () -> String? = { AppLogger.getRecentLogs(minutes: 5, maxLines: 100) },
        send: (_ recentLogExtras: [String: Any]) -> Void
    ) {
        guard include else {
            send([:])
            return
        }
        guard !Thread.isMainThread else {
            send(["recent_logs_skipped": "main_thread"])
            return
        }
        if let recentLogs = fetch() {
            send(["recent_logs": recentLogs])
        } else {
            send([:])
        }
    }
}
