//
//  AutoDeleteCleanupService.swift
//  hyperwhisper
//
//  AUTO-DELETE CLEANUP SERVICE
//  Handles automatic deletion of old recordings and transcripts based on user settings.
//
//  RESPONSIBILITIES:
//  - Perform cleanup on app launch
//  - Run periodic cleanup based on timer
//  - Delete transcripts older than the configured duration
//  - Delete associated audio files (original and trimmed)
//
//  CLEANUP FLOW:
//  1. Check if auto-delete is enabled in settings
//  2. Calculate the cutoff date based on configured time unit and value
//  3. Fetch all transcripts older than the cutoff date
//  4. For each transcript:
//     a. Delete the original audio file from disk (if exists)
//     b. Delete the trimmed audio file from disk (if exists)
//     c. Delete the transcript from Core Data
//  5. Save changes to Core Data
//
//  SCHEDULING:
//  - Runs immediately on app launch (if enabled)
//  - Runs periodically based on the configured time unit:
//    - Minutes: checks every 1 minute
//    - Hours: checks every 5 minutes
//    - Days: checks every 1 hour
//  - Can be triggered manually via performCleanup()
//

import Foundation
import CoreData
import Combine
import os

// MARK: - Auto-Delete Cleanup Service

/// Service responsible for automatically deleting old recordings based on user settings
///
/// USAGE:
/// ```swift
/// let service = AutoDeleteCleanupService(
///     settingsManager: autoDeleteSettings,
///     persistenceController: PersistenceController.shared
/// )
/// service.startPeriodicCleanup()
/// ```
///
/// THREAD SAFETY:
/// - All Core Data operations happen on the main thread via @MainActor
/// - File system operations happen on background threads
/// - Timer fires on main thread to coordinate with Core Data
@MainActor
class AutoDeleteCleanupService: ObservableObject {

    // MARK: - Logger

    /// Logger for cleanup operations
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "AutoDeleteCleanup")

    // MARK: - Dependencies

    /// Settings manager that holds auto-delete configuration
    private let settingsManager: AutoDeleteSettingsManager

    /// Core Data persistence controller for transcript operations
    private let persistenceController: PersistenceController

    // MARK: - State

    /// Timer for periodic cleanup
    private var cleanupTimer: Timer?

    /// Combine cancellables for observing settings changes
    private var cancellables = Set<AnyCancellable>()

    /// Tracks the last time unit to detect changes and reschedule timer
    private var lastTimeUnit: AutoDeleteTimeUnit?

    /// Whether a cleanup operation is currently in progress
    @Published private(set) var isCleanupInProgress: Bool = false

    /// Statistics from the last cleanup operation
    @Published private(set) var lastCleanupStats: CleanupStats?

    /// Date of the last cleanup operation
    @Published private(set) var lastCleanupDate: Date?

    /// Date of the next scheduled cleanup operation
    @Published private(set) var nextCleanupDate: Date?

    // MARK: - Cleanup Statistics

    /// Statistics from a cleanup operation
    struct CleanupStats {
        /// Number of transcripts deleted
        let transcriptsDeleted: Int
        /// Number of audio files deleted
        let audioFilesDeleted: Int
        /// Total bytes freed from disk
        let bytesFreed: Int64
        /// Duration of the cleanup operation
        let durationSeconds: TimeInterval

        /// Human-readable summary of the cleanup
        var summary: String {
            if transcriptsDeleted == 0 {
                return NSLocalizedString(
                    "history.autoDelete.cleanup.noItems",
                    value: "No recordings to delete",
                    comment: ""
                )
            }

            let format = NSLocalizedString(
                "history.autoDelete.cleanup.summary",
                value: "Deleted %d recording(s) and freed %@",
                comment: "Format: Deleted [count] recording(s) and freed [size]"
            )
            return String(format: format, transcriptsDeleted, ByteCountFormatter.string(fromByteCount: bytesFreed, countStyle: .file))
        }
    }

    // MARK: - Initialization

    /// Creates a new cleanup service
    ///
    /// - Parameters:
    ///   - settingsManager: The auto-delete settings manager
    ///   - persistenceController: The Core Data persistence controller
    init(settingsManager: AutoDeleteSettingsManager, persistenceController: PersistenceController = .shared) {
        self.settingsManager = settingsManager
        self.persistenceController = persistenceController

        // OBSERVE TIME UNIT CHANGES:
        // When the user changes the time unit (minutes/hours/days), we need to
        // reschedule the cleanup timer to use the appropriate interval.
        // This ensures cleanup frequency matches user expectations.
        settingsManager.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                self?.checkAndRescheduleIfNeeded()
            }
            .store(in: &cancellables)

        logger.info("AutoDeleteCleanupService initialized")
    }

    /// Checks if the time unit has changed and reschedules the timer if needed
    private func checkAndRescheduleIfNeeded() {
        let currentUnit = settingsManager.autoDeleteTimeUnit

        // Only reschedule if time unit changed and timer is active
        if let lastUnit = lastTimeUnit, lastUnit != currentUnit, cleanupTimer != nil {
            logger.info("Time unit changed from \(lastUnit.rawValue, privacy: .public) to \(currentUnit.rawValue, privacy: .public), rescheduling timer")
            scheduleCleanupTimer()
        }

        lastTimeUnit = currentUnit
    }

    deinit {
        cleanupTimer?.invalidate()
    }

    // MARK: - Public Methods

    /// Starts periodic cleanup with the configured interval
    /// Cleanup runs immediately on start, then at intervals based on the time unit setting
    func startPeriodicCleanup() {
        // Track current time unit for change detection
        lastTimeUnit = settingsManager.autoDeleteTimeUnit

        // Run cleanup immediately on start
        Task {
            await performCleanup()
        }

        // Schedule cleanup based on time unit setting
        scheduleCleanupTimer()

        logger.info("Periodic cleanup started (interval: \(self.cleanupIntervalDescription, privacy: .public))")
    }

    /// Reschedules the cleanup timer based on current settings
    /// Call this when the user changes the time unit setting
    func rescheduleCleanupTimer() {
        scheduleCleanupTimer()
        logger.info("Cleanup timer rescheduled (interval: \(self.cleanupIntervalDescription, privacy: .public))")
    }

    /// Schedules the cleanup timer based on the current time unit setting
    ///
    /// INTERVAL LOGIC:
    /// - Minutes: check every 1 minute (60s) - for quick deletion needs
    /// - Hours: check every 5 minutes (300s) - reasonable responsiveness
    /// - Days: check every 1 hour (3600s) - no need for frequent checks
    private func scheduleCleanupTimer() {
        cleanupTimer?.invalidate()

        let interval = cleanupInterval
        nextCleanupDate = Date().addingTimeInterval(interval)

        cleanupTimer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            Task { @MainActor in
                await self?.performCleanup()
                // Update next cleanup date after each run
                if let self = self {
                    self.nextCleanupDate = Date().addingTimeInterval(self.cleanupInterval)
                }
            }
        }
    }

    /// Returns the appropriate cleanup interval in seconds based on the time unit setting
    private var cleanupInterval: TimeInterval {
        switch settingsManager.autoDeleteTimeUnit {
        case .minutes:
            return 60       // Check every 1 minute
        case .hours:
            return 300      // Check every 5 minutes
        case .days:
            return 3600     // Check every 1 hour
        }
    }

    /// Human-readable description of the cleanup interval for logging
    private var cleanupIntervalDescription: String {
        switch settingsManager.autoDeleteTimeUnit {
        case .minutes:
            return "every 1 minute"
        case .hours:
            return "every 5 minutes"
        case .days:
            return "every 1 hour"
        }
    }

    /// Stops periodic cleanup
    func stopPeriodicCleanup() {
        cleanupTimer?.invalidate()
        cleanupTimer = nil
        logger.info("Periodic cleanup stopped")
    }

    /// Performs a cleanup operation based on current settings
    ///
    /// CLEANUP STEPS:
    /// 1. Check if auto-delete is enabled
    /// 2. Calculate cutoff date
    /// 3. Fetch transcripts older than cutoff
    /// 4. Delete audio files and transcripts
    /// 5. Update statistics
    ///
    /// - Returns: The cleanup statistics, or nil if auto-delete is disabled
    @discardableResult
    func performCleanup() async -> CleanupStats? {
        // Early exit if disabled or already running
        guard settingsManager.autoDeleteEnabled else {
            logger.debug("Auto-delete is disabled, skipping cleanup")
            return nil
        }

        guard !isCleanupInProgress else {
            logger.warning("Cleanup already in progress, skipping")
            return nil
        }

        // Get the cutoff date
        guard let cutoffDate = settingsManager.deletionCutoffDate else {
            logger.warning("Could not calculate cutoff date, skipping cleanup")
            return nil
        }

        isCleanupInProgress = true
        let startTime = CFAbsoluteTimeGetCurrent()

        logger.info("Starting auto-delete cleanup. Cutoff date: \(cutoffDate, privacy: .public)")

        // Fetch transcripts older than the cutoff date
        let transcriptsToDelete = fetchTranscriptsOlderThan(cutoffDate)

        guard !transcriptsToDelete.isEmpty else {
            isCleanupInProgress = false
            lastCleanupDate = Date()

            let stats = CleanupStats(
                transcriptsDeleted: 0,
                audioFilesDeleted: 0,
                bytesFreed: 0,
                durationSeconds: CFAbsoluteTimeGetCurrent() - startTime
            )
            lastCleanupStats = stats

            logger.info("No transcripts older than cutoff date found")
            return stats
        }

        logger.info("Found \(transcriptsToDelete.count, privacy: .public) transcripts to delete")

        // Delete transcripts and their audio files
        var audioFilesDeleted = 0
        var bytesFreed: Int64 = 0

        for transcript in transcriptsToDelete {
            // Delete original audio file
            if let audioPath = transcript.audioFilePath {
                let (deleted, bytes) = deleteFileIfExists(at: audioPath)
                if deleted {
                    audioFilesDeleted += 1
                    bytesFreed += bytes
                }
            }

            // Delete trimmed audio file (VAD-processed version)
            if let trimmedPath = transcript.value(forKey: "trimmedAudioFilePath") as? String {
                let (deleted, bytes) = deleteFileIfExists(at: trimmedPath)
                if deleted {
                    audioFilesDeleted += 1
                    bytesFreed += bytes
                }
            }

            // Delete the transcript from Core Data
            persistenceController.container.viewContext.delete(transcript)
        }

        // Save Core Data changes
        persistenceController.save()

        let duration = CFAbsoluteTimeGetCurrent() - startTime
        isCleanupInProgress = false
        lastCleanupDate = Date()

        let stats = CleanupStats(
            transcriptsDeleted: transcriptsToDelete.count,
            audioFilesDeleted: audioFilesDeleted,
            bytesFreed: bytesFreed,
            durationSeconds: duration
        )
        lastCleanupStats = stats

        logger.info("""
            Auto-delete cleanup complete:
            - Transcripts deleted: \(stats.transcriptsDeleted, privacy: .public)
            - Audio files deleted: \(stats.audioFilesDeleted, privacy: .public)
            - Bytes freed: \(stats.bytesFreed, privacy: .public)
            - Duration: \(stats.durationSeconds, format: .fixed(precision: 2))s
            """)

        // Report to Sentry for diagnostics (non-error, just breadcrumb)
        if AppLogger.isErrorLoggingEnabled {
            SentryService.addBreadcrumb(
                message: "Auto-delete cleanup completed",
                category: "auto-delete",
                data: [
                    "transcriptsDeleted": stats.transcriptsDeleted,
                    "audioFilesDeleted": stats.audioFilesDeleted,
                    "bytesFreed": stats.bytesFreed
                ]
            )
        }

        return stats
    }

    /// Forces a cleanup operation regardless of the timer schedule
    /// Useful for testing or user-initiated cleanup
    func forceCleanup() async -> CleanupStats? {
        logger.info("Force cleanup requested")
        return await performCleanup()
    }

    // MARK: - Private Methods

    /// Fetches all transcripts with a date older than the specified cutoff
    ///
    /// - Parameter cutoffDate: The date threshold for deletion
    /// - Returns: Array of transcripts to delete
    private func fetchTranscriptsOlderThan(_ cutoffDate: Date) -> [Transcript] {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()

        // Fetch transcripts where date is older than (less than) the cutoff
        request.predicate = NSPredicate(format: "date < %@", cutoffDate as NSDate)

        // Sort by date ascending (oldest first) for predictable deletion order
        request.sortDescriptors = [NSSortDescriptor(keyPath: \Transcript.date, ascending: true)]

        do {
            return try persistenceController.container.viewContext.fetch(request)
        } catch {
            logger.error("Failed to fetch transcripts for auto-delete: \(error.localizedDescription, privacy: .public)")
            SentryService.capture(
                error: error,
                message: "Failed to fetch transcripts for auto-delete",
                tags: ["component": "AutoDeleteCleanupService"]
            )
            return []
        }
    }

    /// Deletes a file at the specified path if it exists
    ///
    /// - Parameter path: The file path to delete
    /// - Returns: Tuple of (wasDeleted, bytesFreed)
    private func deleteFileIfExists(at path: String) -> (deleted: Bool, bytes: Int64) {
        let fileManager = FileManager.default

        guard fileManager.fileExists(atPath: path) else {
            return (false, 0)
        }

        // Get file size before deletion
        var fileSize: Int64 = 0
        if let attrs = try? fileManager.attributesOfItem(atPath: path),
           let size = attrs[.size] as? Int64 {
            fileSize = size
        }

        do {
            try fileManager.removeItem(atPath: path)
            logger.debug("Deleted audio file: \(path, privacy: .public)")
            return (true, fileSize)
        } catch {
            logger.error("Failed to delete audio file: \(error.localizedDescription, privacy: .public)")
            return (false, 0)
        }
    }
}
