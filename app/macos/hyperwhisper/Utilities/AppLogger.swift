//
//  AppLogger.swift
//  hyperwhisper
//
//  Created by AI Assistant on 21/08/2025.
//

import Foundation
import os.log
import AppKit
import UniformTypeIdentifiers

/// UNIFIED APPLICATION LOGGING SYSTEM
/// This class provides comprehensive logging for the entire HyperWhisper application
/// using Apple's os.log system. It complements UpdateLogger (which handles file-based
/// logging for Sparkle updates) with system-integrated logging for all app components.
///
/// Key Features:
/// - Native os.log integration (viewable in Console.app)
/// - Privacy-preserving (automatic PII redaction)
/// - Category-based organization
/// - Minimal performance impact
/// - Survives app crashes
/// - Integration with crash reports and sysdiagnose
///
/// Privacy Note:
/// Transcription content is NEVER logged to protect user privacy.
/// Only metadata about transcription operations is logged.
final class AppLogger {
    
    // MARK: - Subsystem
    
    /// The reverse DNS identifier for our app's logging subsystem
    /// This allows filtering in Console.app: subsystem:"com.hyperwhisper.app"
    private static let subsystem = "com.hyperwhisper.app"
    
    // MARK: - Category Loggers
    
    /// Audio recording and device management
    /// Logs: device changes, format issues, recording start/stop, buffer errors
    static let audio = Logger(subsystem: subsystem, category: "audio")
    
    /// Transcription operations (privacy-safe)
    /// Logs: model loading, API calls, processing time, errors
    /// NEVER logs: actual transcribed text or audio content
    static let transcription = Logger(subsystem: subsystem, category: "transcription")
    
    /// Core Data operations
    /// Logs: saves, fetches, migration, conflicts
    static let coreData = Logger(subsystem: subsystem, category: "coredata")
    
    /// Network operations
    /// Logs: API requests, responses, connectivity issues
    static let network = Logger(subsystem: subsystem, category: "network")
    
    /// UI and user interactions
    /// Logs: view lifecycle, user actions, navigation
    static let ui = Logger(subsystem: subsystem, category: "ui")
    
    /// Sparkle update operations (mirrors to UpdateLogger)
    /// Logs: update checks, downloads, installations
    static let updates = Logger(subsystem: subsystem, category: "updates")
    
    /// Licensing and validation
    /// Logs: license checks, validation, trial status
    static let license = Logger(subsystem: subsystem, category: "license")
    
    /// Settings and configuration
    /// Logs: preference changes, migrations, exports
    static let settings = Logger(subsystem: subsystem, category: "settings")
    
    /// Model download and management
    /// Logs: downloads, extractions, validations
    static let models = Logger(subsystem: subsystem, category: "models")
    
    /// Accessibility and permissions
    /// Logs: permission requests, accessibility API usage
    static let accessibility = Logger(subsystem: subsystem, category: "accessibility")

    /// History view and transcript selection
    /// Logs: selection changes, detail view rendering, performance diagnostics
    static let history = Logger(subsystem: subsystem, category: "history")

    /// Model residency and process memory
    /// Logs: model load (cold vs cache-hit) + release, phys_footprint samples,
    /// co-residence, inter-use idle gaps, and memory-pressure eviction events.
    /// Used by `ModelResidencyRegistry` / `MemoryPressureMonitor`.
    static let memory = Logger(subsystem: subsystem, category: "memory")
    
    // MARK: - Convenience Methods
    
    /// Logs an audio error with context
    static func logAudioError(_ message: String, error: Error? = nil, metadata: [String: Any] = [:]) {
        if let error = error {
            if isExpectedMicrophoneUnavailable(error) {
                // Expected user state: microphone access not granted, or no input
                // device connected. Not an app bug, so don't report it to Sentry.
                audio.warning("\(message, privacy: .public): \(error.localizedDescription, privacy: .public)")
            } else {
                audio.error("\(message, privacy: .public): \(error, privacy: .public)")
                // Mirror to Sentry (no transcript content included) if user enabled logging
                if isErrorLoggingEnabled {
                    SentryService.capture(error: error, message: message, extras: metadata, tags: ["category": "audio"])
                }
            }
        } else {
            audio.error("\(message, privacy: .public)")
        }
        logMetadata(to: audio, metadata: metadata)
    }
    
    /// Logs a transcription operation (privacy-safe)
    /// IMPORTANT: Never pass actual transcribed text to this method
    static func logTranscription(_ event: TranscriptionEvent, metadata: [String: Any] = [:]) {
        switch event {
        case .started(let mode, let duration):
            transcription.info("Transcription started - mode: \(mode, privacy: .public), duration: \(duration, privacy: .public)s")
        case .completed(let wordCount, let processingTime):
            transcription.info("Transcription completed - words: \(wordCount, privacy: .public), time: \(processingTime, privacy: .public)s")
        case .failed(let error):
            transcription.error("Transcription failed: \(error, privacy: .public)")
            // Mirror to Sentry if enabled (privacy: do not attach transcript text anywhere)
            if isErrorLoggingEnabled {
                SentryService.capture(error: error, message: "Transcription failed", tags: ["category": "transcription"])        
            }
        case .modelLoaded(let model):
            transcription.info("Model loaded: \(model, privacy: .public)")
        case .apiCall(let endpoint, let status):
            transcription.debug("API call to \(endpoint, privacy: .public) - status: \(status, privacy: .public)")
        }
        logMetadata(to: transcription, metadata: metadata)
    }
    
    /// Transcription events that can be logged (privacy-safe)
    enum TranscriptionEvent {
        case started(mode: String, duration: Double)
        case completed(wordCount: Int, processingTime: Double)
        case failed(Error)
        case modelLoaded(String)
        case apiCall(endpoint: String, status: Int)
    }
    
    /// Logs Core Data operations
    ///
    /// SENTRY HYPERWHISPER-VC / HYPERWHISPER-VB ("Core Data save failed"): the
    /// save report used to carry two tags and a raw `\(error)` dump. Two things
    /// were wrong with that.
    ///
    /// 1. It could not be diagnosed. `operation=save` does not say which entity,
    ///    which attribute, which Cocoa code, or which of the eight save sites
    ///    failed, so 19 identical-looking events described a fault nobody could
    ///    name. `CoreDataSaveDiagnostics` supplies all four, as tags, so they
    ///    are searchable and groupable.
    /// 2. `\(error)` inlines `NSValidationErrorObject` — the failing row's own
    ///    attribute values, transcript text included — into the unified log, and
    ///    from there into the `recent_logs` Sentry extra. The line sanitiser
    ///    works one line at a time and an `NSError` description is many lines,
    ///    so the dump survived it. The raw interpolation is gone.
    ///
    /// - Parameters:
    ///   - operation: which Core Data operation; for `.save`, which call site and
    ///     which context.
    ///   - error: the failure, if there was one.
    ///   - metadata: extra privacy-safe context, e.g. `contextShape`.
    static func logCoreData(_ operation: CoreDataOperation, error: Error? = nil, metadata: [String: Any] = [:]) {
        switch operation {
        case .save(let site, let contextKey):
            guard let error = error else {
                coreData.debug("Context saved successfully · site=\(site, privacy: .public)")
                CoreDataSaveDiagnostics.recordSuccess(contextKey: contextKey)
                return
            }
            let nsError = error as NSError
            let streak = CoreDataSaveDiagnostics.recordFailure(contextKey: contextKey)
            let summary = CoreDataSaveDiagnostics.summary(for: nsError)
            // The pending counts go in the LOCAL line too. They are the signal
            // that separates a poisoned context from one bad write, and a
            // support engineer reading `log show` has no Sentry to fall back on
            // — nor does a user who left error reporting switched off.
            let shape = metadata
                .sorted { $0.key < $1.key }
                .map { "\($0.key)=\($0.value)" }
                .joined(separator: " ")
            coreData.error("Failed to save context · site=\(site, privacy: .public) · context=\(contextKey, privacy: .public) · \(summary, privacy: .public) · consecutiveFailures=\(streak.failureCount, privacy: .public) · sinceFirstFailureMs=\(streak.sinceFirstFailureMs, privacy: .public) · \(shape, privacy: .public)")
            guard isErrorLoggingEnabled else { return }
            var extras = CoreDataSaveDiagnostics.metadata(for: nsError)
            extras["save_site"] = site
            extras["save_context"] = contextKey
            extras["consecutive_failures"] = streak.failureCount
            extras["since_first_failure_ms"] = streak.sinceFirstFailureMs
            for (key, value) in metadata { extras[key] = value }
            SentryService.capture(
                error: error,
                message: "Core Data save failed",
                extras: extras,
                tags: coreDataTags(site: site, operation: "save", error: nsError, metadata: extras),
                // GROUPING: the default fingerprint is `{{ default }}` + message,
                // and `{{ default }}` is the stack trace. That split one fault
                // across HYPERWHISPER-VB and HYPERWHISPER-VC while merging
                // genuinely different validation failures into each. Group by
                // what the fault IS instead.
                fingerprint: [
                    "core-data-save-failed",
                    site,
                    CoreDataSaveDiagnostics.codeName(nsError.code),
                    extras["coredata_entity"] as? String ?? "unknown"
                ],
                // The structured extras above ARE the diagnosis now. Dropping the
                // blob removes the only path by which a row's values reached
                // Sentry, and takes a blocking `log show` subprocess off a save
                // that may be failing on the main thread (HYPERWHISPER-F7).
                includeRecentLogs: false
            )
        case .fetch(let entity, let count):
            coreData.debug("Fetched \(count, privacy: .public) \(entity, privacy: .public) objects")
        case .delete(let entity):
            // A delete that FAILED used to log "Deleted <entity>" at info: the
            // error argument was accepted and then dropped on the floor, so the
            // unified log recorded a failure as a success.
            if let error = error {
                let summary = CoreDataSaveDiagnostics.summary(for: error as NSError)
                coreData.error("Failed to delete \(entity, privacy: .public) · \(summary, privacy: .public)")
            } else {
                coreData.info("Deleted \(entity, privacy: .public)")
            }
        case .migration(let from, let to):
            coreData.info("Migrating from v\(from, privacy: .public) to v\(to, privacy: .public)")
        case .storeLoad:
            if let error = error {
                let nsError = error as NSError
                // Same dump as the save path. A store-load `NSError` carries the
                // store URL, which is under the user's home directory.
                let summary = CoreDataSaveDiagnostics.summary(for: nsError)
                coreData.fault("Failed to load persistent store · \(summary, privacy: .public)")
                // Always send critical store load errors to Sentry
                var extras = CoreDataSaveDiagnostics.metadata(for: nsError)
                for (key, value) in metadata { extras[key] = value }
                SentryService.capture(
                    error: error,
                    message: "Failed to load persistent store",
                    extras: extras,
                    tags: coreDataTags(site: "store_load", operation: "storeLoad", error: nsError, metadata: extras)
                        .merging(["severity": "critical"]) { current, _ in current },
                    includeRecentLogs: false
                )
            } else {
                coreData.info("Persistent store loaded")
            }
        }
    }

    /// Tags for a Core Data failure. Every value is a fixed slug, an entity name
    /// or an attribute name — all of them identifiers from the app's own model,
    /// so cardinality stays bounded and nothing user-written is indexed.
    private static func coreDataTags(
        site: String,
        operation: String,
        error: NSError,
        metadata: [String: Any]
    ) -> [String: String] {
        var tags = [
            "category": "coredata",
            "operation": operation,
            "save_site": site,
            "coredata_code": String(error.code),
            "coredata_error": CoreDataSaveDiagnostics.codeName(error.code)
        ]
        if let contextKey = metadata["save_context"] as? String {
            tags["save_context"] = contextKey
        }
        if let entity = metadata["coredata_entity"] as? String {
            tags["coredata_entity"] = entity
        }
        if let attribute = metadata["coredata_attribute"] as? String {
            tags["coredata_attribute"] = attribute
        }
        return tags
    }

    /// Core Data operations
    enum CoreDataOperation {
        /// `site` names the call site as a fixed slug — eight of them funnelled
        /// into one Sentry issue with no way to tell them apart. `contextKey`
        /// names the context they save, because seven of those eight sites
        /// commit the SAME `viewContext` and the failure streak belongs to the
        /// context, not to the caller.
        case save(site: String, contextKey: String)
        case fetch(entity: String, count: Int)
        case delete(entity: String)
        case migration(from: String, to: String)
        case storeLoad
    }
    
    // MARK: - Helper Methods
    
    /// Logs metadata dictionary to a specific logger
    private static func logMetadata(to logger: Logger, metadata: [String: Any]) {
        guard !metadata.isEmpty else { return }
        
        // Convert metadata to string for logging
        let metadataString = metadata
            .map { "\($0.key)=\($0.value)" }
            .joined(separator: ", ")
        
        logger.debug("Metadata: \(metadataString, privacy: .public)")
    }
    
    /// Detects expected microphone errors — permission not granted, or no audio
    /// input device connected — so we avoid reporting these user-environment
    /// conditions to Sentry as crashes.
    private static func isExpectedMicrophoneUnavailable(_ error: Error) -> Bool {
        if let audioError = error as? AudioError {
            switch audioError {
            case .noPermission, .noMicrophoneAvailable:
                return true
            default:
                break
            }
        }

        let nsError = error as NSError
        // AudioError bridges to NSError with code == case declaration index:
        // permissionDenied == 1, noMicrophoneAvailable == 14.
        if nsError.domain == "HyperWhisper.AudioError" && (nsError.code == 1 || nsError.code == 14) {
            return true
        }

        return false
    }
    
    // MARK: - Log Export
    
    /// Exports system logs for diagnostic purposes
    /// Combines os.log entries with UpdateLogger files
    static func exportDiagnostics(completion: @escaping (URL?) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            do {
                let exportDir = FileManager.default.temporaryDirectory
                    .appendingPathComponent("hyperwhisper-diagnostics-\(Date().timeIntervalSince1970)")
                try FileManager.default.createDirectory(at: exportDir, withIntermediateDirectories: true)
                
                // EXPORT OS.LOG ENTRIES
                // Use log show command to export last 24 hours of our app's logs
                let logFile = exportDir.appendingPathComponent("system-logs.txt")
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/log")
                process.arguments = [
                    "show",
                    "--predicate", "subsystem == '\(subsystem)'",
                    "--last", "24h",
                    "--style", "json"
                ]
                
                let outputPipe = Pipe()
                process.standardOutput = outputPipe
                
                try process.run()
                process.waitUntilExit()
                
                let data = outputPipe.fileHandleForReading.readDataToEndOfFile()
                try data.write(to: logFile)
                
                // COPY UPDATE LOGS
                let updateLogPath = UpdateLogger.shared.currentLogPath
                if FileManager.default.fileExists(atPath: updateLogPath) {
                    let updateLogURL = URL(fileURLWithPath: updateLogPath)
                    let destURL = exportDir.appendingPathComponent("update-logs.json")
                    try FileManager.default.copyItem(at: updateLogURL, to: destURL)
                }
                
                // ADD SYSTEM INFO
                let systemInfo = exportDir.appendingPathComponent("system-info.txt")
                let info = """
                HyperWhisper Diagnostic Report
                ===============================
                Date: \(Date())
                App Version: \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") ?? "Unknown")
                Build: \(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") ?? "Unknown")
                macOS Version: \(ProcessInfo.processInfo.operatingSystemVersionString)
                Device: \(getDeviceInfo())
                
                Log Categories:
                - audio: Audio recording and devices
                - transcription: Transcription operations (privacy-safe)
                - coredata: Database operations
                - network: API and network calls
                - ui: User interface events
                - updates: App updates (Sparkle)
                - license: License validation
                - settings: Configuration changes
                - models: Model downloads
                - accessibility: Permissions and accessibility
                
                Note: Transcription content is never logged for privacy.
                """
                try info.write(to: systemInfo, atomically: true, encoding: .utf8)
                
                // CREATE ZIP ARCHIVE
                let zipURL = FileManager.default.temporaryDirectory
                    .appendingPathComponent("hyperwhisper-diagnostics-\(Date().timeIntervalSince1970).zip")
                
                let zipProcess = Process()
                zipProcess.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
                zipProcess.arguments = ["-r", zipURL.path, exportDir.lastPathComponent]
                zipProcess.currentDirectoryURL = exportDir.deletingLastPathComponent()
                
                try zipProcess.run()
                zipProcess.waitUntilExit()
                
                // Clean up temp directory
                try? FileManager.default.removeItem(at: exportDir)
                
                DispatchQueue.main.async {
                    completion(zipURL)
                }
                
            } catch {
                audio.error("Failed to export diagnostics: \(error, privacy: .public)")
                DispatchQueue.main.async {
                    completion(nil)
                }
            }
        }
    }
    
    /// Gets device information for diagnostics
    private static func getDeviceInfo() -> String {
        var size = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var model = [CChar](repeating: 0, count: size)
        sysctlbyname("hw.model", &model, &size, nil, 0)
        return String(cString: model)
    }
    
    // MARK: - Console.app Helper
    
// MARK: - Recent Logs for Error Context

    /// Retrieves recent log entries as sanitized text for attaching to error reports.
    /// Uses `log show` command to fetch last N minutes of logs from our subsystem.
    ///
    /// Privacy: Sanitizes output to remove any potential PII that might have leaked:
    /// - Redacts file paths containing user names
    /// - Redacts IP addresses
    /// - Redacts anything that looks like transcript content
    ///
    /// - Parameters:
    ///   - minutes: How many minutes of logs to retrieve (default: 5)
    ///   - maxLines: Maximum number of log lines to return (default: 100)
    /// - Returns: Sanitized log text, or nil if retrieval failed
    static func getRecentLogs(minutes: Int = 5, maxLines: Int = 100) -> String? {
        // HOW LONG THIS BLOCKS IS ITSELF A DIAGNOSTIC (Sentry HYPERWHISPER-F7).
        // `log show` is a subprocess and `waitUntilExit()` blocks the CALLING
        // thread. 55 of the 56 `SentryService.capture` sites leave
        // `includeRecentLogs` at its default of `true`, so any of them reached
        // from the main thread parks it here for as long as the subprocess
        // takes. Nothing measured that until now, so a hang report could not
        // say whether the app was stalled inside its own error reporting.
        //
        // The `running` state is published BEFORE the subprocess starts, so an
        // AppHang raised while the fetch is in flight carries it.
        let fetchStart = Date()
        let onMainThread = Thread.isMainThread
        publishDiagnosticLogState([
            "diagnostic_logs_state": "running",
            "diagnostic_logs_on_main_thread": onMainThread
        ])

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/log")
        process.arguments = [
            "show",
            "--predicate", "subsystem == '\(subsystem)'",
            "--last", "\(minutes)m",
            "--style", "compact"
        ]

        let outputPipe = Pipe()
        let errorPipe = Pipe()
        process.standardOutput = outputPipe
        process.standardError = errorPipe

        do {
            try process.run()
            process.waitUntilExit()

            let data = outputPipe.fileHandleForReading.readDataToEndOfFile()
            let fetchMs = Int((Date().timeIntervalSince(fetchStart) * 1_000).rounded())

            guard let output = String(data: data, encoding: .utf8), !output.isEmpty else {
                // Was silent before. An empty result and a slow empty result are
                // different faults, and only the timing tells them apart.
                finishDiagnosticLogState(
                    outcome: "empty",
                    fetchMs: fetchMs,
                    onMainThread: onMainThread,
                    lineCount: 0,
                    exitCode: Int(process.terminationStatus)
                )
                return nil
            }

            // Split into lines and take only the last maxLines
            var lines = output.components(separatedBy: .newlines)
            if lines.count > maxLines {
                lines = Array(lines.suffix(maxLines))
            }

            // Sanitize each line
            let sanitizedLines = lines.map { sanitizeLogLine($0) }

            finishDiagnosticLogState(
                outcome: "ok",
                fetchMs: fetchMs,
                onMainThread: onMainThread,
                lineCount: lines.count,
                exitCode: Int(process.terminationStatus)
            )

            return sanitizedLines.joined(separator: "\n")

        } catch {
            // Was reported under the `audio` category, which hid it from anyone
            // filtering the unified log for a diagnostics fault.
            let fetchMs = Int((Date().timeIntervalSince(fetchStart) * 1_000).rounded())
            settings.error("Failed to retrieve recent logs after \(fetchMs, privacy: .public)ms: \(error, privacy: .public)")
            finishDiagnosticLogState(
                outcome: "launch_failed",
                fetchMs: fetchMs,
                onMainThread: onMainThread,
                lineCount: 0,
                exitCode: nil
            )
            return nil
        }
    }

    /// Milliseconds above which a `log show` fetch is reported as a stall
    /// rather than as normal cost. The app-hang threshold is 10 000 ms, so a
    /// fetch past this point is already a large part of one.
    private static let slowDiagnosticLogFetchMs = 2_000

    /// Publish the state of the `log show` fetch as Sentry SCOPE extras.
    /// Scope extras survive `SentryService.beforeSend`, which nils breadcrumbs.
    private static func publishDiagnosticLogState(_ extras: [String: Any]) {
        guard isErrorLoggingEnabled else { return }
        SentryService.setExtras(extras)
    }

    private static func finishDiagnosticLogState(
        outcome: String,
        fetchMs: Int,
        onMainThread: Bool,
        lineCount: Int,
        exitCode: Int?
    ) {
        if fetchMs >= slowDiagnosticLogFetchMs {
            settings.error("⏱️ Recent-log fetch stalled the caller for \(fetchMs, privacy: .public)ms · onMainThread=\(onMainThread, privacy: .public) · outcome=\(outcome, privacy: .public)")
        } else {
            settings.debug("Recent-log fetch took \(fetchMs, privacy: .public)ms · outcome=\(outcome, privacy: .public)")
        }

        var extras: [String: Any] = [
            "diagnostic_logs_state": "finished",
            "diagnostic_logs_outcome": outcome,
            "diagnostic_logs_fetch_ms": fetchMs,
            "diagnostic_logs_on_main_thread": onMainThread,
            "diagnostic_logs_line_count": lineCount,
            "diagnostic_logs_was_slow": fetchMs >= slowDiagnosticLogFetchMs
        ]
        if let exitCode {
            extras["diagnostic_logs_exit_code"] = exitCode
        }
        publishDiagnosticLogState(extras)
    }

    /// Sanitizes a single log line to remove potential PII.
    /// This is a defense-in-depth measure - logs shouldn't contain PII,
    /// but this catches anything that might have slipped through.
    private static func sanitizeLogLine(_ line: String) -> String {
        var result = line

        // REDACT USER HOME PATHS
        // Pattern: /Users/username/... → /Users/[REDACTED]/...
        let homePathPattern = #"/Users/[^/\s]+/"#
        if let regex = try? NSRegularExpression(pattern: homePathPattern, options: []) {
            result = regex.stringByReplacingMatches(
                in: result,
                options: [],
                range: NSRange(result.startIndex..., in: result),
                withTemplate: "/Users/[REDACTED]/"
            )
        }

        // REDACT IP ADDRESSES
        // Pattern: xxx.xxx.xxx.xxx
        let ipPattern = #"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"#
        if let regex = try? NSRegularExpression(pattern: ipPattern, options: []) {
            result = regex.stringByReplacingMatches(
                in: result,
                options: [],
                range: NSRange(result.startIndex..., in: result),
                withTemplate: "[IP_REDACTED]"
            )
        }

        // REDACT POTENTIAL TRANSCRIPT CONTENT
        // Look for patterns like: "text: ...", "transcript=...", "content: ..."
        //
        // Two separators, two key lists. Free-text messages use `key: value`,
        // while this app's os.log lines use `key=value`. `message=` is left out
        // of the `=` list on purpose: 15 error lines end in
        // `message=\(error.localizedDescription)`, which is allowed metadata and
        // the most useful part of the line. The word boundary keeps `context=`
        // and `hasContext=` out of both matches.
        let contentPatterns = [
            #"(?i)\b(text|transcript|content|prompt|message)\b:\s*[^\n\r]{20,}"#,  // Long content after these keys
            #"(?i)\b(text|transcript|content|prompt)\b=\s*[^\n\r]{20,}"#,          // Same, in key=value log lines
            #"(?i)Full text:\s*.*"#,     // Old logging format (now removed, but catch any remnants)
            #"(?i)Text preview:\s*.*"#   // Old logging format (now removed, but catch any remnants)
        ]

        for pattern in contentPatterns {
            if let regex = try? NSRegularExpression(pattern: pattern, options: []) {
                result = regex.stringByReplacingMatches(
                    in: result,
                    options: [],
                    range: NSRange(result.startIndex..., in: result),
                    withTemplate: "[CONTENT_REDACTED]"
                )
            }
        }

        // REDACT EMAIL-LIKE PATTERNS
        let emailPattern = #"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"#
        if let regex = try? NSRegularExpression(pattern: emailPattern, options: []) {
            result = regex.stringByReplacingMatches(
                in: result,
                options: [],
                range: NSRange(result.startIndex..., in: result),
                withTemplate: "[EMAIL_REDACTED]"
            )
        }

        return result
    }

}

// MARK: - Settings Integration

extension AppLogger {
    /// Checks if error logging is enabled in settings
    /// This respects the user's privacy preferences
    static var isErrorLoggingEnabled: Bool {
        UserDefaults.standard.bool(forKey: "enableErrorLogging")
    }
    
}
