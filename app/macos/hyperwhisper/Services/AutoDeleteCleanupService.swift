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
//  3. In ONE uninterrupted main-actor block: fetch all transcripts older than the
//     cutoff date, collect every audio file path to remove (original + trimmed),
//     delete those transcripts from Core Data, and save
//  4. If the save did not commit, roll back and stop — nothing is unlinked
//  5. Delete those files from disk in one batch, off the main actor
//  6. Back on the main actor: tally the stats, log them, record them
//
//  WHY THE CORE DATA WORK COMES FIRST:
//  Step 5 is a suspension point. Anything read from a `Transcript` BEFORE it and
//  used AFTER it is a snapshot that other MAIN-ACTOR work can invalidate while we
//  are away — a retry's path rewrite merging in, the history UI deleting the row,
//  the debounced `refreshAllObjects()` maintenance pass re-faulting every object
//  we hold. All of those need the main actor, so all of them are excluded by
//  running the entire Core Data half to completion first, with no `await`
//  anywhere inside it, and letting only plain `[String]` paths cross the hop.
//  (It does NOT exclude `PersistenceController.writerContext`, which runs on its
//  own private queue — see the STEP 1 comment in `performCleanup()` for what
//  remains open there.)
//
//  THE TRADE-OFF, DELIBERATELY ACCEPTED:
//  A pass interrupted between the save and the unlink — quit, crash, force-kill —
//  leaves audio files on disk that no Core Data row references, and NOTHING in
//  the app sweeps them up. `CrashRecoveryManager.scanForUnclaimedWAVCandidates`
//  is not that sweep: it matches only `.incomplete_*.wav` files and only
//  synthesizes stub `RecordingSession` rows, freeing no disk. Auto-delete removes
//  only finalized files — `recording_<uuid>.wav`, converted `.m4a`,
//  `<name>_trimmed.<ext>` — none of which carry that prefix. So those bytes leak
//  permanently. The pre-reorder order was self-healing here: the unlink came
//  first and the single trailing save meant an interrupted pass discarded its
//  pending deletes, and the next tick redid the work.
//
//  We take the leak anyway. The other order corrupts the user's data instead —
//  a surviving row pointing at a file that is already gone, which the History UI
//  renders as a playable recording that silently fails — and it reopens the
//  snapshot races above. Leaked bytes are invisible and recoverable; a dead
//  History row is neither. A persisted, retryable deletion queue would close both
//  and is deliberately out of scope for this change.
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
/// - File system operations happen on background threads: the blocking deletes
///   run in `FileDeletion.deleteFiles(at:)`, which does its work on a detached
///   task (HYPERWHISPER-HF)
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

    /// Performs a cleanup operation based on current settings.
    ///
    /// See the CLEANUP FLOW note at the top of this file for the ordering and why
    /// the Core Data work has to finish before the off-actor file deletion.
    ///
    /// - Returns: The cleanup statistics, or `nil` when no cleanup happened —
    ///   auto-delete is disabled, a pass is already running, no cutoff date could
    ///   be calculated, or the Core Data save did not commit (in which case the
    ///   pending deletes are rolled back and no file is touched).
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
        // Placed immediately AFTER the flag is set, never at the top of the
        // function: a top-level defer would also fire on the `guard
        // !isCleanupInProgress` early return above and clear the flag out from
        // under the cleanup that is actually running. This body now has a
        // suspension point (the file-deletion hop), so every exit path has to
        // clear the flag exactly once.
        defer { isCleanupInProgress = false }
        let startTime = CFAbsoluteTimeGetCurrent()

        logger.info("Starting auto-delete cleanup. Cutoff date: \(cutoffDate, privacy: .public)")

        // Fetch transcripts older than the cutoff date
        let transcriptsToDelete = fetchTranscriptsOlderThan(cutoffDate)

        guard !transcriptsToDelete.isEmpty else {
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

        // ---------------------------------------------------------------------
        // STEP 1 (main actor, NO suspension point anywhere in this block):
        // read the paths off the managed objects, delete the rows, and save.
        //
        // This runs to completion before the `await` below, so no other
        // MAIN-ACTOR work can delete a row or re-fault the objects underneath
        // us, and no merge from a background write can land on the `viewContext`
        // mid-block. That is what makes `transcriptsToDelete.count` an honest
        // count of what was deleted. Do not introduce an `await` between the
        // fetch and `save()`.
        //
        // What this does NOT give us is atomicity against the rest of the app.
        // The absence of a suspension point only excludes work that needs the
        // main actor. `PersistenceController.writerContext` is a
        // `newBackgroundContext()` that runs every write via `context.perform`
        // on a private queue, and `updateTranscriptAudioFilePathInBackground`
        // performs the `.wav` -> `.m4a` rewrite from an un-awaited detached
        // `Task`. That write can commit in genuine parallel with this block, so
        // a path collected here can already be stale by the time we save: the
        // reorder NARROWS the orphan-a-newly-converted-file window from "the
        // whole pass, including the off-actor deletion hop" down to the
        // fetch->save span; it does not close it. Closing it needs the fetch and
        // the delete to run on the writer itself, which is a larger change than
        // this fix.
        // ---------------------------------------------------------------------

        // Every `Transcript` access happens inside STEP 1 — `Transcript` is an
        // `NSManagedObject` and must never cross into the detached task.
        // The list is deliberately NOT de-duplicated: if a transcript's original
        // and trimmed paths are the same string, the first entry deletes and
        // counts the file and the second finds it already gone, exactly as the
        // sequential per-file version behaved.
        var paths: [String] = []
        for transcript in transcriptsToDelete {
            // Original audio file
            if let audioPath = transcript.audioFilePath {
                paths.append(audioPath)
            }

            // Trimmed audio file (VAD-processed version)
            if let trimmedPath = transcript.value(forKey: "trimmedAudioFilePath") as? String {
                paths.append(trimmedPath)
            }
        }

        let viewContext = persistenceController.container.viewContext
        for transcript in transcriptsToDelete {
            viewContext.delete(transcript)
        }

        // Save Core Data changes. From here on the rows are gone; the files they
        // referenced are unlinked below. A pass interrupted in between orphans
        // files on disk that nothing sweeps up — see the trade-off note in the
        // file header.
        persistenceController.save()

        // The save may not have taken. `PersistenceController.save()` is a
        // non-throwing `Void` function: it logs the error and reports it to
        // Sentry, but it neither rethrows nor rolls back, so a failure leaves the
        // pending deletes sitting on the context and returns silently. A
        // disk-full `NSFileWriteOutOfSpaceError` — the very condition
        // auto-delete exists to prevent — is one way to get there; any unrelated
        // invalid pending edit anywhere on this app-wide `viewContext` is
        // another.
        //
        // `hasChanges` is the only signal available at this call site, and it is
        // a sound one here: a successful `save()` clears it, and there is no
        // suspension point between the `save()` and this check, so nothing else
        // on the main actor can dirty the context in between.
        guard !viewContext.hasChanges else {
            // Do NOT unlink anything. The rows are still there, so every path we
            // collected is still referenced by a live History row — deleting the
            // files now would produce exactly the broken-playback failure this
            // ordering exists to prevent.
            //
            // Roll back so the context is not left dirty for every later
            // operation. This also discards any unrelated pending edit on the
            // `viewContext`, which is the intended behaviour rather than a side
            // effect: something on this context is unsavable, and leaving our
            // deletes pending means the next `save()` from anywhere in the app
            // commits them at an arbitrary later moment with the audio files
            // still on disk and no cleanup pass aware of it.
            viewContext.rollback()

            logger.error("""
                Auto-delete aborted: Core Data save did not commit. \
                Rolled back \(transcriptsToDelete.count, privacy: .public) pending transcript deletion(s); \
                no audio files were deleted.
                """)
            SentryService.captureMessage(
                "Auto-delete aborted: Core Data save did not commit",
                level: .error,
                extras: ["transcriptsPendingDeletion": transcriptsToDelete.count],
                tags: ["component": "AutoDeleteCleanupService"]
            )

            // Return `nil` rather than a zero-filled `CleanupStats`, and leave
            // `lastCleanupStats` / `lastCleanupDate` untouched: `nil` already
            // means "no cleanup was performed" everywhere else in this function,
            // and a zeroed stats object is indistinguishable from a genuine
            // empty-backlog pass — it would show the user "No recordings to
            // delete" for a backlog that is still entirely there. The `defer`
            // above still clears `isCleanupInProgress` on this path.
            return nil
        }

        // STEP 2 (off the main actor): the blocking filesystem work.
        let results = await FileDeletion.deleteFiles(at: paths)

        // STEP 3 (back on the main actor): tally and log. `deleteFiles` returns
        // one result per input path, in order, so `zip` pairs them up.
        var audioFilesDeleted = 0
        var bytesFreed: Int64 = 0
        var failedDeletionCount = 0
        for (path, result) in zip(paths, results) {
            if result.deleted {
                audioFilesDeleted += 1
                bytesFreed += result.bytesFreed
                logger.debug("Deleted audio file: \(path, privacy: .public)")
            } else if let failureDescription = result.failureDescription {
                // Log the path, not just the description. The Core Data row is
                // already committed as deleted by this point, so this line is
                // the only surviving record that the file exists — and
                // `failureDescription` is a bare `localizedDescription`, which
                // for POSIX-domain errors ("Permission denied") names neither
                // the file nor its directory.
                failedDeletionCount += 1
                logger.error("Failed to delete audio file: \(path, privacy: .public) — \(failureDescription, privacy: .public)")
            }
        }

        // One event per pass, not one per file: a failing volume fails every
        // path in the batch, and this is the only signal that auto-delete is
        // silently leaking disk in production. Counts only — the paths stay in
        // the local log, since they contain the user's home directory and the
        // recording filenames.
        if failedDeletionCount > 0 {
            SentryService.captureMessage(
                "Auto-delete could not remove some audio files",
                level: .warning,
                extras: [
                    "failedDeletions": failedDeletionCount,
                    "pathsAttempted": paths.count
                ],
                tags: ["component": "AutoDeleteCleanupService"]
            )
        }

        let duration = CFAbsoluteTimeGetCurrent() - startTime
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

}
