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
@_spi(Private) import Sentry
#endif

// MARK: - SentryService

enum SentryService {

    /// Sentry Cocoa 8.x applies OS/device contexts to Logs, but it does not
    /// apply the scope's tags or extras. Mirror only the context this wrapper
    /// writes so the Log path carries the same app context as the Event path.
    // internal (not private): read by `initialize` in
    // SentryService+Lifecycle.swift and `mergedLogAttributes` in
    // SentryService+Diagnostics.swift.
    final class LogScopeState: @unchecked Sendable {
        private let lock = NSLock()
        private var tags: [String: String] = [:]
        private var extras: [String: Any] = [:]

        func reset(tags: [String: String]) {
            lock.lock()
            self.tags = tags
            extras = [:]
            lock.unlock()
        }

        func setTag(_ key: String, _ value: String) {
            lock.lock()
            tags[key] = value
            lock.unlock()
        }

        func setExtras(_ values: [String: Any]) {
            lock.lock()
            for (key, value) in values { extras[key] = value }
            lock.unlock()
        }

        func snapshot() -> (tags: [String: String], extras: [String: Any]) {
            lock.lock()
            defer { lock.unlock() }
            return (tags, extras)
        }
    }

    static let logScopeState = LogScopeState()

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
    /// `isReportingEnabled` is the gate the send paths check. This is the
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

    // MARK: - Tags

    /// Programmatic tag setter (safe when Sentry absent).
    /// Tags are indexed and searchable in Sentry - use for filterable dimensions.
    static func setTag(_ key: String, _ value: String) {
        #if canImport(Sentry)
        guard isReportingEnabled else { return }
        logScopeState.setTag(key, value)
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
        logScopeState.setExtras(extras)
        SentrySDK.configureScope { scope in
            for (key, value) in extras {
                scope.setExtra(value: value, key: key)
            }
        }
        #endif
    }

}
