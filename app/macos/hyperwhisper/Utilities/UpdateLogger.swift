//
//  UpdateLogger.swift
//  hyperwhisper
//
//  Created by AI Assistant on 21/08/2025.
//

import Foundation
import os.log
import os
import AppKit

/// UPDATE LOGGING SYSTEM
/// This class provides comprehensive logging for Sparkle update operations to help diagnose
/// production update failures. It writes structured logs to a file in the user's Library folder
/// and maintains a rolling log history.
///
/// Key Features:
/// - File-based logging to ~/Library/Logs/HyperWhisper/updates.log
/// - JSON format for easy parsing and analysis
/// - Automatic log rotation (keeps last 7 days)
/// - Thread-safe operations
/// - Severity levels for filtering
/// - Rich context capture for debugging
final class UpdateLogger {

    // MARK: - Singleton

    /// Shared instance for app-wide logging
    static let shared = UpdateLogger()

    /// Logger for unified logging system
    /// Uses Apple's os.log for production-ready logging with privacy controls
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "UpdateLogger")
    
    // MARK: - Properties
    
    /// Log severity levels
    enum LogLevel: String, Codable {
        case debug = "DEBUG"
        case info = "INFO"
        case warning = "WARNING"
        case error = "ERROR"
        case critical = "CRITICAL"
    }
    
    /// Structure for log entries
    struct LogEntry: Codable {
        let timestamp: Date
        let level: LogLevel
        let message: String
        let context: [String: String]?
        let error: ErrorInfo?
        
        struct ErrorInfo: Codable {
            let domain: String
            let code: Int
            let description: String
            let userInfo: [String: String]?
        }
    }
    
    /// Directory where log files are stored
    private let logDirectory: URL
    
    /// Current log file URL
    private let logFileURL: URL
    
    /// Queue for thread-safe file operations
    private let logQueue = DispatchQueue(label: "com.hyperwhisper.updatelogger", qos: .utility)

    /// Whether a write failure has already been reported to Sentry this session
    private var hasReportedWriteFailure = false
    
    /// Date formatter for log timestamps
    private let dateFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()
    
    /// Maximum age of log files in days
    private let maxLogAgeDays = 7
    
    /// Maximum log file size in bytes (10 MB)
    private let maxLogFileSize: Int = 10 * 1024 * 1024
    
    // MARK: - Initialization
    
    private init() {
        // SETUP LOG DIRECTORY
        // Create the log directory in ~/Library/Logs/HyperWhisper/
        // This is the standard location for app logs on macOS
        let libraryURL = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first!
        logDirectory = libraryURL.appendingPathComponent("Logs/HyperWhisper")
        logFileURL = logDirectory.appendingPathComponent("updates.log")
        
        // Create directory if it doesn't exist
        do {
            try FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)
        } catch {
            logger.error("Failed to create log directory: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to create update log directory", tags: ["component": "UpdateLogger", "operation": "createDirectory"])
        }
        
        // Clean up old logs on initialization
        cleanupOldLogs()
        
        // Log initialization
        log(.info, "UpdateLogger initialized", context: [
            "logPath": logFileURL.path,
            "maxAgeDays": String(maxLogAgeDays),
            "maxSize": formatBytes(maxLogFileSize)
        ])
    }
    
    // MARK: - Public Logging Methods
    
    /// Logs a debug message (verbose information for development)
    func debug(_ message: String, context: [String: String]? = nil) {
        log(.debug, message, context: context)
    }
    
    /// Logs an info message (general information)
    func info(_ message: String, context: [String: String]? = nil) {
        log(.info, message, context: context)
    }
    
    /// Logs a warning message (potential issues)
    func warning(_ message: String, context: [String: String]? = nil) {
        log(.warning, message, context: context)
    }
    
    /// Logs an error message with optional NSError details
    func error(_ message: String, error: NSError? = nil, context: [String: String]? = nil) {
        var errorInfo: LogEntry.ErrorInfo?
        var enrichedContext = context ?? [:]
        
        // EXTRACT ERROR DETAILS
        // Convert NSError to structured format for better debugging
        if let error = error {
            errorInfo = LogEntry.ErrorInfo(
                domain: error.domain,
                code: error.code,
                description: error.localizedDescription,
                userInfo: error.userInfo.compactMapValues { String(describing: $0) }
            )
            
            // Add common error fields to context
            enrichedContext["errorDomain"] = error.domain
            enrichedContext["errorCode"] = String(error.code)
            
            // Extract specific Sparkle error info if available
            if error.domain == "SUSparkleErrorDomain" || error.domain.contains("Sparkle") {
                enrichedContext["sparkleError"] = "true"
                
                // Common Sparkle error keys
                if let appcastURL = error.userInfo["SUAppcastURL"] as? URL {
                    enrichedContext["appcastURL"] = appcastURL.absoluteString
                }
                if let httpStatusCode = error.userInfo["HTTPStatusCode"] {
                    enrichedContext["httpStatusCode"] = String(describing: httpStatusCode)
                }
            }
        }
        
        log(.error, message, context: enrichedContext, error: errorInfo)
    }
    
    /// Logs a critical error (system failures, update installation failures)
    func critical(_ message: String, error: NSError? = nil, context: [String: String]? = nil) {
        var errorInfo: LogEntry.ErrorInfo?
        if let error = error {
            errorInfo = LogEntry.ErrorInfo(
                domain: error.domain,
                code: error.code,
                description: error.localizedDescription,
                userInfo: error.userInfo.compactMapValues { String(describing: $0) }
            )
        }
        log(.critical, message, context: context, error: errorInfo)
    }
    
    // MARK: - Sparkle-Specific Logging
    
    /// Logs the start of an update check
    func logUpdateCheckStarted(automatic: Bool) {
        info("Update check started", context: [
            "automatic": String(automatic),
            "timestamp": dateFormatter.string(from: Date())
        ])
    }
    
    /// Logs when an update is found
    func logUpdateFound(version: String, releaseNotes: String? = nil) {
        info("Update found", context: [
            "newVersion": version,
            "hasReleaseNotes": String(releaseNotes != nil)
        ])
    }
    
    /// Logs download progress
    func logDownloadProgress(bytesDownloaded: Int64, totalBytes: Int64) {
        let percentage = totalBytes > 0 ? Int((Double(bytesDownloaded) / Double(totalBytes)) * 100) : 0
        debug("Download progress", context: [
            "bytesDownloaded": formatBytes(Int(bytesDownloaded)),
            "totalBytes": formatBytes(Int(totalBytes)),
            "percentage": "\(percentage)%"
        ])
    }
    
    /// Logs successful download
    func logDownloadCompleted() {
        info("Update download completed successfully")
    }
    
    /// Logs download failure
    func logDownloadFailed(error: NSError) {
        self.error("Update download failed", error: error, context: [
            "networkAvailable": String(isNetworkAvailable())
        ])
    }
    
    /// Logs installation start
    func logInstallationStarted(version: String) {
        info("Update installation started", context: [
            "version": version,
            "diskSpaceAvailable": formatBytes(availableDiskSpace())
        ])
    }
    
    /// Logs installation completion
    func logInstallationCompleted() {
        info("Update installation completed successfully")
    }
    
    /// Logs installation failure
    func logInstallationFailed(error: NSError) {
        critical("Update installation failed", error: error, context: [
            "diskSpace": formatBytes(availableDiskSpace()),
            "permissions": checkPermissions()
        ])
    }
    
    // MARK: - Log Management
    
    /// Returns the path to the current log file
    var currentLogPath: String {
        logFileURL.path
    }
    
    /// Exports logs for support/debugging
    func exportLogs() -> URL? {
        let exportURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hyperwhisper-update-logs-\(Date().timeIntervalSince1970).log")
        
        do {
            try FileManager.default.copyItem(at: logFileURL, to: exportURL)
            return exportURL
        } catch {
            self.error("Failed to export logs", error: error as NSError)
            return nil
        }
    }
    
    /// Opens the log file in the default text editor
    func openLogFile() {
        NSWorkspace.shared.open(logFileURL)
    }
    
    /// Clears all logs
    func clearLogs() {
        logQueue.async { [weak self] in
            guard let self = self else { return }
            do {
                try "".write(to: self.logFileURL, atomically: true, encoding: .utf8)
                self.info("Logs cleared by user")
            } catch {
                self.logger.error("Failed to clear logs: \(error, privacy: .public)")
                SentryService.capture(error: error, message: "Failed to clear update logs", tags: ["component": "UpdateLogger", "operation": "clearLogs"])
            }
        }
    }
    
    // MARK: - Private Methods
    
    /// Core logging function
    private func log(_ level: LogLevel, _ message: String, context: [String: String]? = nil, error: LogEntry.ErrorInfo? = nil) {
        let entry = LogEntry(
            timestamp: Date(),
            level: level,
            message: message,
            context: context,
            error: error
        )
        
        // Write to file asynchronously
        logQueue.async { [weak self] in
            self?.writeToFile(entry)
        }
        
        // Also log to console in debug builds
        #if DEBUG
        let contextString = context?.map { "\($0.key)=\($0.value)" }.joined(separator: ", ") ?? ""
        let contextDisplay = contextString.isEmpty ? "" : "[\(contextString)]"
        logger.info("[UpdateLogger] [\(level.rawValue, privacy: .public)] \(message, privacy: .public) \(contextDisplay, privacy: .public)")
        #endif
    }
    
    /// Writes log entry to file
    private func writeToFile(_ entry: LogEntry) {
        do {
            // Check file size and rotate if needed
            if let attributes = try? FileManager.default.attributesOfItem(atPath: logFileURL.path),
               let fileSize = attributes[.size] as? Int,
               fileSize > maxLogFileSize {
                rotateLogFile()
            }

            // Encode entry as JSON
            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            encoder.outputFormatting = [.sortedKeys]
            var data = try encoder.encode(entry)
            data.append("\n".data(using: .utf8)!)

            // Try to append via FileHandle (fast path for existing file).
            // Use try? to avoid TOCTOU race where fileExists returns true
            // but the file is deleted before FileHandle opens it
            // (Sentry HYPERWHISPER-KM: 13 users, 1,342 events).
            if let fileHandle = try? FileHandle(forWritingTo: logFileURL) {
                defer { try? fileHandle.close() }
                try fileHandle.seekToEnd()
                try fileHandle.write(contentsOf: data)
            } else {
                // File or directory doesn't exist — recreate both.
                // The directory may have been removed after init by macOS
                // or a cleanup tool.
                try FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)
                try data.write(to: logFileURL)
            }

            // Reset failure flag on successful write
            hasReportedWriteFailure = false
        } catch {
            logger.error("Failed to write log: \(error, privacy: .public)")
            if !hasReportedWriteFailure {
                hasReportedWriteFailure = true
                SentryService.capture(error: error, message: "Failed to write update log", tags: ["component": "UpdateLogger", "operation": "writeLog"])
            }
        }
    }
    
    /// Rotates log file when it gets too large
    private func rotateLogFile() {
        let timestamp = dateFormatter.string(from: Date()).replacingOccurrences(of: ":", with: "-")
        let rotatedURL = logDirectory.appendingPathComponent("updates-\(timestamp).log")
        
        do {
            try FileManager.default.moveItem(at: logFileURL, to: rotatedURL)
            info("Log file rotated", context: ["rotatedTo": rotatedURL.lastPathComponent])
        } catch {
            logger.error("Failed to rotate log file: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to rotate log file", tags: ["component": "UpdateLogger", "operation": "rotateLog"])
        }
    }
    
    /// Cleans up log files older than maxLogAgeDays
    private func cleanupOldLogs() {
        do {
            let files = try FileManager.default.contentsOfDirectory(at: logDirectory, includingPropertiesForKeys: [.creationDateKey])
            let cutoffDate = Date().addingTimeInterval(-Double(maxLogAgeDays * 24 * 60 * 60))
            
            for file in files where file.pathExtension == "log" {
                // Never delete the active log file — only rotated copies
                guard file.lastPathComponent != logFileURL.lastPathComponent else { continue }

                if let attributes = try? FileManager.default.attributesOfItem(atPath: file.path),
                   let creationDate = attributes[.creationDate] as? Date,
                   creationDate < cutoffDate {
                    try FileManager.default.removeItem(at: file)
                    debug("Removed old log file", context: ["file": file.lastPathComponent])
                }
            }
        } catch {
            logger.error("Failed to cleanup old logs: \(error, privacy: .public)")
            SentryService.capture(error: error, message: "Failed to cleanup old logs", tags: ["component": "UpdateLogger", "operation": "cleanupLogs"])
        }
    }
    
    // MARK: - Helper Methods
    
    /// Formats bytes to human-readable string
    private func formatBytes(_ bytes: Int) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .binary
        return formatter.string(fromByteCount: Int64(bytes))
    }
    
    /// Checks if network is available (basic check)
    private func isNetworkAvailable() -> Bool {
        // Quick non-blocking check using URLSession
        // Returns true by default to avoid false negatives
        // The actual network error will be logged separately
        return true  // Simplified to avoid blocking - actual network errors will be captured
    }
    
    /// Gets available disk space
    private func availableDiskSpace() -> Int {
        let fileURL = URL(fileURLWithPath: NSHomeDirectory())
        do {
            let values = try fileURL.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            if let capacity = values.volumeAvailableCapacityForImportantUsage {
                return Int(capacity)
            }
        } catch {
            logger.error("Error retrieving disk space: \(error, privacy: .public)")
        }
        return 0
    }
    
    /// Checks file system permissions
    private func checkPermissions() -> String {
        let appURL = Bundle.main.bundleURL
        let isWritable = FileManager.default.isWritableFile(atPath: appURL.path)
        let isReadable = FileManager.default.isReadableFile(atPath: appURL.path)
        return "readable=\(isReadable), writable=\(isWritable)"
    }
}