//
//  CrashRecoveryManager.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import CoreData
import AVFoundation
import Darwin  // sysctl(KERN_PROC_PID) for this process's kernel start time

/// One `.incomplete_*.wav` file found by the orphan-WAV directory scan.
///
/// Plain `Sendable` value type so the scan can run off the main actor and hand
/// its results back without any Core Data object crossing the boundary. It is
/// declared at file scope rather than nested inside `CrashRecoveryManager` so it
/// does not pick up that type's `@MainActor` isolation.
struct CrashRecoveryWAVCandidate: Sendable {
    /// The file's URL. Its `path` is derived at the use sites rather than stored:
    /// `URL.path` is a pure, non-blocking derivation, so a second stored copy
    /// would only be a field that has to agree with this one.
    let url: URL
    /// Raw filesystem creation date; `nil` when it can't be read. The caller
    /// applies the "unreadable ⇒ treat as brand new" fallback.
    let creationDate: Date?
}

/// Recovers incomplete recordings from app crashes
///
/// **Purpose:**
/// When the app crashes during recording, the RecordingSession entity persists in Core Data
/// with endTime = nil, but the audio file may be partially written. This manager finds
/// these "orphaned" sessions, validates the audio files, and prepares them for manual
/// transcription by the user.
///
/// **Recovery Flow:**
/// 1. Query Core Data for sessions with endTime == nil (never finished)
/// 2. For each session:
///    - Check if audio file exists and is a WAV file (current format)
///    - Delete sessions pointing to old CAF files (deprecated format)
///    - Validate file has actual audio data (length > 0)
///    - Use WAV directly if < 25MB, convert to M4A if larger
///    - Update session with endTime and audio metadata
/// 3. Batch save all changes (performance optimization)
/// 4. User can manually transcribe recovered sessions from the History page
///
/// **File Format History:**
/// - Pre-November 2025: Used CAF files from AVAudioEngine tap
/// - November 2025+: Uses WAV files from AVAudioRecorder (16kHz mono)
/// - Old CAF files are automatically deleted as they use a deprecated format
///
/// **When Called:**
/// During app initialization in hyperwhisperApp.swift, after Core Data loads.
/// Runs async to not block startup.
///
/// **Dependencies:**
/// - AudioFileConverter: For optional WAV to M4A conversion (large files only)
/// - PersistenceController: For Core Data operations
///
/// **Thread Safety:**
/// Methods are @MainActor for Core Data safety. The two filesystem probes on the
/// launch path are the exceptions: `scanForUnclaimedWAVCandidates` (directory
/// listing + per-file creation date) and `isRecoverableWAV` (existence,
/// readability, size, `AVAudioFile` open) are `nonisolated static` and run their
/// blocking work inside a detached task, so a slow or cloud-synced recordings
/// volume can't stall the main thread.
///
/// **Known follow-up (HYPERWHISPER-VM is not fully closed):** the rest of the
/// recovery loop's file I/O still runs on the main actor — inside
/// `recoverAndConvertRecording` (`attributesOfItem`, `createDirectory`, a second
/// `AVAudioFile(forReading:)`, `moveItem`) and the `FileManager.removeItem` calls
/// in the delete paths of `recoverOrphanedRecordings`. A slow volume can still
/// hang launch from those frames until they move off the main actor too.
@MainActor
class CrashRecoveryManager {

    // MARK: - Dependencies

    /// Audio file converter for WAV to M4A conversion (large files only)
    private let audioFileConverter: AudioFileConverter

    /// Settings manager for resolving the configured recordings folder.
    /// Injected via `configure(settingsManager:)` after initialization.
    private weak var settingsManager: SettingsManager?

    /// Size threshold for WAV to M4A conversion (25MB)
    private let wavToM4AThreshold: Int64 = 25 * 1024 * 1024

    /// True while a `recoverOrphanedRecordings` pass is in flight.
    ///
    /// The recovery pass is NOT re-entrant. It is invoked from
    /// `handleMainWindowAppear` in `hyperwhisperApp.swift` with no once-flag of its
    /// own, and the main window can appear more than once per process (menu-bar
    /// mode closes the window and `MainAppView.openMainWindow` re-opens it), so two
    /// passes really can be started while the first is still suspended on one of
    /// its `await`s.
    ///
    /// Two overlapping passes corrupt each other in two ways:
    ///  - **Silent data loss.** Pass A suspends on the `isRecoverableWAV` probe;
    ///    pass B finishes recovering the same session (moves the file to its final
    ///    name and sets `endTime`); A's probe then fails against the now-moved
    ///    `.incomplete_` path and A deletes the row B just recovered. The audio
    ///    file survives but has lost its `.incomplete_` prefix, so no future sweep
    ///    can ever see it again.
    ///  - **Lost attempt counts.** `attemptCounts` is snapshotted from UserDefaults,
    ///    mutated in memory across the loop's suspension points, then written back
    ///    whole — so the later writer clobbers the earlier one's increments and the
    ///    3-strike quarantine never engages.
    ///
    /// Because this class is `@MainActor`, the check-and-set below the entry point
    /// is atomic as long as it happens before the first suspension point — so it
    /// must stay at the very top of `recoverOrphanedRecordings`.
    private var isRecovering = false

    /// UserDefaults key for tracking recovery attempt counts per session UUID
    private static let attemptCountsKey = "crashRecovery.attemptCounts"

    /// Maximum recovery attempts before quarantining a session
    private static let maxRecoveryAttempts = 3

    /// Wall-clock time at which this process launched.
    ///
    /// Any orphaned session whose `startTime` predates this is guaranteed to be from
    /// a *previous* process instance (i.e. a crashed run), so it can never be a live
    /// in-progress recording and is always safe to recover — even if it started only
    /// a few seconds before the crash. Derived from the kernel's process start time so
    /// it survives clock drift relative to `Date()` better than a captured `Date()`.
    /// Nil means preserve the original wall-clock staleness cutoff.
    private static let processLaunchDate: Date? = CrashRecoveryManager.kernelProcessStartDate()

    /// Read this process's start time from the kernel via `sysctl(KERN_PROC_PID)`.
    /// Returns nil if the lookup fails for any reason.
    private static func kernelProcessStartDate() -> Date? {
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        let result = sysctl(&mib, UInt32(mib.count), &info, &size, nil, 0)
        guard result == 0, size >= MemoryLayout<kinfo_proc>.stride else { return nil }
        let startTime = info.kp_proc.p_starttime
        let seconds = TimeInterval(startTime.tv_sec) + TimeInterval(startTime.tv_usec) / 1_000_000
        guard seconds > 0 else { return nil }
        return Date(timeIntervalSince1970: seconds)
    }

    // MARK: - Initialization

    init(audioFileConverter: AudioFileConverter) {
        self.audioFileConverter = audioFileConverter
    }

    /// Configure with settings manager after initialization.
    /// Mirrors `RecordingLifecycle.configure(settingsManager:)` so recovered
    /// recordings honor the same configured destination as normal recordings.
    func configure(settingsManager: SettingsManager?) {
        self.settingsManager = settingsManager
    }

    /// Recordings directory URL — same resolution as `RecordingLifecycle`.
    /// Honors `settingsManager.recordingsFolder` when set, otherwise falls back
    /// to the default `~/Documents/Recordings`.
    private var recordingsDirectory: URL {
        if let path = settingsManager?.recordingsFolder, !path.isEmpty {
            return URL(fileURLWithPath: path, isDirectory: true)
        }

        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Recordings", isDirectory: true)
    }

    // MARK: - Attempt Tracking

    /// Get the current attempt counts dictionary from UserDefaults
    private func getAttemptCounts() -> [String: Int] {
        UserDefaults.standard.dictionary(forKey: Self.attemptCountsKey) as? [String: Int] ?? [:]
    }

    /// Save the attempt counts dictionary to UserDefaults
    private func saveAttemptCounts(_ counts: [String: Int]) {
        UserDefaults.standard.set(counts, forKey: Self.attemptCountsKey)
    }

    /// Increment the attempt count in-memory and return the new count
    private func incrementAttemptCount(for sessionId: String, in counts: inout [String: Int]) -> Int {
        let newCount = (counts[sessionId] ?? 0) + 1
        counts[sessionId] = newCount
        return newCount
    }

    /// Remove the attempt count in-memory (on success, deletion, or quarantine)
    private func removeAttemptCount(for sessionId: String, from counts: inout [String: Int]) {
        counts.removeValue(forKey: sessionId)
    }

    // MARK: - Recovery

    /// Recover incomplete recordings from previous app crashes
    ///
    /// **What This Does:**
    /// 1. Queries Core Data for RecordingSessions with endTime == nil (never finished)
    /// 2. Checks if the audioFilePath still exists and is readable
    /// 3. Validates the CAF file isn't corrupted
    /// 4. Converts to M4A and updates the session to "processing" status
    /// 5. Triggers transcription for successfully recovered sessions
    /// 6. Deletes sessions that have no recoverable audio
    ///
    /// **When to Call:**
    /// During app initialization to recover any crashed recordings
    ///
    /// **Concurrency:**
    /// Not re-entrant, and callers do not guarantee a single invocation per
    /// process. A second call made while a pass is still in flight returns
    /// immediately (see `isRecovering`) rather than running a parallel pass.
    /// Dropping the second call — rather than queueing it — is safe because
    /// recovery is idempotent and repeated: anything the dropped call would have
    /// picked up is still an orphan afterwards, and the next `.onAppear` or the
    /// next launch sweeps it. The early return is logged so it stays observable.
    ///
    /// **Performance:**
    /// - Batch saves instead of per-session saves
    /// - Runs async to not block UI
    /// - Validation is sequential — one awaited filesystem probe per orphan — but
    ///   each probe runs off the main actor (`isRecoverableWAV`), as does the
    ///   directory scan (`scanForUnclaimedWAVCandidates`), so the blocking I/O
    ///   never lands on the main thread. There is no fan-out (no `TaskGroup`,
    ///   `async let`, or `concurrentPerform`): the loop mutates `@MainActor` Core
    ///   Data objects and a shared `attemptCounts` dictionary in between probes,
    ///   which parallel checks would race.
    func recoverOrphanedRecordings(currentSessionID: UUID? = nil) async {
        // IN-FLIGHT GUARD. Must stay above every `await` in this function: the
        // class is `@MainActor`, so this check-and-set is atomic only while no
        // suspension point can separate the read from the write.
        guard !isRecovering else {
            AppLogger.audio.info("Orphaned recording recovery already in progress; skipping duplicate pass")
            return
        }
        isRecovering = true
        defer { isRecovering = false }

        let recoveryStart = Date()
        // Sessions older than this 60s wall-clock window are always safe to recover.
        // The window only exists to avoid racing an *active* recording (see below).
        let wallClockCutoff = recoveryStart.addingTimeInterval(-60)
        // A session that started before THIS process launched cannot belong to the
        // current process, so it can never be a live in-progress recording — it is
        // always safe to recover regardless of how recently it started. This lets a
        // short recording that crashed and was relaunched immediately recover on the
        // first pass instead of waiting for a later recovery invocation. Taking the
        // later of the two cutoffs preserves the anti-race guarantee for sessions that
        // *were* started within this process (which is also covered by currentSessionID).
        let staleSessionCutoff = Self.processLaunchDate.map { max(wallClockCutoff, $0) } ?? wallClockCutoff
        AppLogger.audio.info("Starting orphaned recording recovery scan")

        let context = PersistenceController.shared.container.viewContext

        // STEP 1: Find incomplete sessions (endTime == nil means recording never finished)
        let request = RecordingSession.fetchRequest()
        request.predicate = NSPredicate(format: "endTime == nil")

        guard var orphans = try? await context.perform({ try context.fetch(request) }) else {
            AppLogger.audio.warning("Failed to fetch orphaned sessions")
            return
        }

        // STEP 1b: ORPHAN-WAV SWEEP. The record-start session insert is deferred
        // off the record-start hot path, so a crash in the first ~100ms of a
        // recording can leave an `.incomplete_*.wav` on disk with NO session row
        // — invisible to the fetch above and never recovered or cleaned up.
        // Synthesize a stub session for each unclaimed, stale incomplete WAV and
        // let the existing validation/recovery/quarantine loop below handle it
        // unchanged.
        orphans += await synthesizeStubSessionsForUnclaimedWAVs(
            existingOrphans: orphans,
            context: context,
            staleSessionCutoff: staleSessionCutoff
        )

        guard !orphans.isEmpty else {
            AppLogger.audio.info("No orphaned recordings found")
            return
        }

        AppLogger.audio.info("Found \(orphans.count) orphaned session(s)")

        // Track successfully recovered sessions for transcription
        var recoveredSessions: [(session: RecordingSession, audioURL: URL)] = []

        // Load attempt counts once, mutate in-memory, save once at the end.
        //
        // This snapshot opens a read-modify-write that spans every suspension
        // point in the loop below and is closed by the unconditional whole-
        // dictionary write in STEP 4. It is safe ONLY because the in-flight guard
        // at the top of this function makes passes mutually exclusive — two
        // overlapping passes would both snapshot the same counts and the later
        // writer would silently discard the earlier one's increments, so a session
        // that fails forever would never reach the 3-strike quarantine. Do not
        // remove that guard without replacing this with a merge on write.
        var attemptCounts = getAttemptCounts()

        // STEP 2: Attempt to recover each session
        for session in orphans {
            let attemptStart = Date()
            let sessionId = session.id?.uuidString ?? "unknown"
            var outcome = "skipped"
            defer {
                let elapsedMs = Int(Date().timeIntervalSince(attemptStart) * 1000)
                if elapsedMs > 750 {
                    AppLogger.audio.warning("⚠️ Recovery attempt for session \(sessionId) \(outcome) in \(elapsedMs)ms")
                    if AppLogger.isErrorLoggingEnabled {
                        SentryService.addBreadcrumb(
                            message: "Slow crash recovery attempt",
                            category: "audio.recovery",
                            level: .warning,
                            data: [
                                "sessionId": sessionId,
                                "durationMs": elapsedMs,
                                "outcome": outcome
                            ]
                        )
                    }
                } else {
                    AppLogger.audio.debug("Recovery attempt for session \(sessionId) \(outcome) in \(elapsedMs)ms")
                }
            }

            // RACE CONDITION FIX: Skip the currently active recording session.
            // Without this, the recovery manager can move the .incomplete_ WAV file
            // out from under a live AVAudioRecorder, causing finalization to fail with
            // "Raw audio file does not exist" when the recording stops.
            if session.id == currentSessionID {
                outcome = "skipped_active_session"
                AppLogger.audio.debug("Skipping crash recovery for active recording session \(sessionId)")
                continue
            }

            // STALENESS FILTER: Skip sessions that may still be a live, in-process
            // recording. A session is only "live" if it started AFTER this process
            // launched and within the last 60 seconds — that's what `staleSessionCutoff`
            // (the later of the 60s wall-clock window and this process's launch time)
            // encodes. Sessions from a previous (crashed) process always predate the
            // launch time, so a short recording that crashed and relaunched immediately
            // is recovered on the first pass instead of being deferred. This guards
            // against the query picking up a genuinely active session that started
            // between the query fetch and the iteration reaching it.
            guard let startTime = session.startTime, startTime <= staleSessionCutoff else {
                outcome = "skipped_recent_session"
                AppLogger.audio.debug("Skipping crash recovery for recent session \(sessionId)")
                continue
            }

            // QUARANTINE CHECK: Skip sessions that have failed recovery too many times.
            // Instead of retrying forever (which triggers the polling loop on every launch),
            // quarantine the session by setting endTime so it's no longer an "orphan".
            if let priorAttempts = attemptCounts[sessionId], priorAttempts >= Self.maxRecoveryAttempts {
                AppLogger.audio.warning("Quarantining session \(sessionId) after \(priorAttempts) failed recovery attempts")
                session.endTime = Date()
                removeAttemptCount(for: sessionId, from: &attemptCounts)
                outcome = "quarantined"
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Quarantined unrecoverable orphaned session",
                        category: "audio.recovery",
                        level: .warning,
                        data: [
                            "sessionId": sessionId,
                            "attempts": priorAttempts
                        ]
                    )
                }
                continue
            }

            guard let path = session.audioFilePath else {
                // No file path stored, delete the session
                await MainActor.run {
                    context.delete(session)
                }
                removeAttemptCount(for: sessionId, from: &attemptCounts)
                outcome = "deleted_missing_path"
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Deleted orphaned session without audio path",
                        category: "audio.recovery",
                        level: .warning,
                        data: [
                            "sessionId": session.id?.uuidString ?? "unknown"
                        ]
                    )
                }
                continue
            }

            let url = URL(fileURLWithPath: path)
            let fileExtension = url.pathExtension.lowercased()

            // STEP 2a: Check for deprecated CAF format - auto-delete these
            // CAF files are from the old AVAudioEngine tap implementation (pre-November 2025)
            // They often have format issues and should not be recovered
            if fileExtension == "caf" {
                AppLogger.audio.info("Session \(sessionId) uses deprecated CAF format, deleting")
                // Delete both the session and the file
                try? FileManager.default.removeItem(at: url)
                await MainActor.run {
                    context.delete(session)
                }
                removeAttemptCount(for: sessionId, from: &attemptCounts)
                outcome = "deleted_deprecated_caf"
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Deleted orphaned session with deprecated CAF format",
                        category: "audio.recovery",
                        level: .info,
                        data: [
                            "sessionId": sessionId,
                            "path": path
                        ]
                    )
                }
                continue
            }

            // STEP 2b: Only recover WAV files (current format)
            guard fileExtension == "wav" else {
                AppLogger.audio.warning("Session \(sessionId) has unsupported format: \(fileExtension), deleting")
                try? FileManager.default.removeItem(at: url)
                await MainActor.run {
                    context.delete(session)
                }
                removeAttemptCount(for: sessionId, from: &attemptCounts)
                outcome = "deleted_unsupported_format"
                continue
            }

            // Check if WAV file exists and is recoverable. The probe is blocking
            // file I/O (stat + `AVAudioFile` open), so it runs on a detached task
            // rather than inline on this MainActor-isolated loop.
            guard await Self.isRecoverableWAV(url) else {
                // File missing or corrupted: delete the session AND the file —
                // leaving the file behind accumulates junk `.incomplete_` WAVs
                // (and the orphan-WAV sweep would re-synthesize a stub for it
                // on every launch).
                AppLogger.audio.warning("Session \(sessionId) has unrecoverable audio, deleting")
                try? FileManager.default.removeItem(at: url)
                await MainActor.run {
                    context.delete(session)
                }
                removeAttemptCount(for: sessionId, from: &attemptCounts)
                outcome = "deleted_unrecoverable"
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Deleted orphaned session with unrecoverable audio",
                        category: "audio.recovery",
                        level: .warning,
                        data: [
                            "sessionId": sessionId,
                            "path": path
                        ]
                    )
                }
                continue
            }

            // Attempt recovery and conversion
            AppLogger.audio.info("Attempting to recover session \(sessionId, privacy: .public)")
            if let recoveredURL = await recoverAndConvertRecording(session: session, rawURL: url) {
                recoveredSessions.append((session: session, audioURL: recoveredURL))
                removeAttemptCount(for: sessionId, from: &attemptCounts)
                outcome = "recovered"
            } else {
                let newCount = incrementAttemptCount(for: sessionId, in: &attemptCounts)
                outcome = "conversion_failed"
                AppLogger.audio.warning("Recovery failed for session \(sessionId) (attempt \(newCount)/\(Self.maxRecoveryAttempts))")
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Recovery conversion failed",
                        category: "audio.recovery",
                        level: .error,
                        data: [
                            "sessionId": sessionId,
                            "rawPath": path,
                            "attemptNumber": newCount,
                            "willQuarantineNext": newCount >= Self.maxRecoveryAttempts
                        ]
                    )
                }
            }
        }

        // STEP 3: BATCH SAVE - Save all changes at once instead of per-session
        await MainActor.run {
            PersistenceController.shared.save()
        }

        let totalDurationMs = Int(Date().timeIntervalSince(recoveryStart) * 1000)
        AppLogger.audio.info("Orphaned recording recovery complete: \(recoveredSessions.count) recovered, \(orphans.count - recoveredSessions.count) failed/deleted in \(totalDurationMs)ms")
        if totalDurationMs > 2000, AppLogger.isErrorLoggingEnabled {
            SentryService.addBreadcrumb(
                message: "Slow orphan recovery scan",
                category: "audio.recovery",
                level: .warning,
                data: [
                    "durationMs": totalDurationMs,
                    "recovered": recoveredSessions.count,
                    "failed": orphans.count - recoveredSessions.count
                ]
            )
        }

        // STEP 4: Prune attempt counts for sessions that no longer exist in Core Data, then flush
        let orphanIds = Set(orphans.compactMap { $0.id?.uuidString })
        let staleKeys = attemptCounts.keys.filter { !orphanIds.contains($0) }
        if !staleKeys.isEmpty {
            for key in staleKeys {
                attemptCounts.removeValue(forKey: key)
            }
            AppLogger.audio.debug("Pruned \(staleKeys.count) stale recovery attempt count(s)")
        }
        // Closes the read-modify-write opened by `getAttemptCounts()` above: a
        // whole-dictionary overwrite with no merge, which also makes the prune
        // above effective. Correct only under the in-flight guard, which is what
        // guarantees no other pass wrote to this key since the snapshot.
        saveAttemptCounts(attemptCounts)

        // NOTE: Transcription is NOT auto-triggered here.
        // Users can manually transcribe recovered sessions from the History page.
    }

    // MARK: - Orphan-WAV Sweep

    /// Synthesize stub `RecordingSession` rows for `.incomplete_*.wav` files in
    /// the recordings directory that no session row claims.
    ///
    /// **Why:** the record-start session insert is intentionally asynchronous
    /// (kept off the record-start hot path — do NOT make it synchronous again),
    /// which opens a small crash window where the recorder has created the WAV
    /// but the row doesn't exist yet. Those files were previously unrecoverable
    /// AND uncollectable junk.
    ///
    /// **Safety:** a file is only claimed if (a) no session row (orphaned or
    /// completed) references its path, and (b) its creation date predates
    /// `staleSessionCutoff`. Together those cover every live recording whose
    /// record-start row exists; see the RESIDUAL GAP note in the loop body for the
    /// one case they do not cover (a >60s live recording whose row insert failed).
    /// No sidecar marker files are needed: the WAV filename embeds the
    /// session UUID, which the stub reuses so recovery attempt-counting stays
    /// stable across launches.
    private func synthesizeStubSessionsForUnclaimedWAVs(
        existingOrphans: [RecordingSession],
        context: NSManagedObjectContext,
        staleSessionCutoff: Date
    ) async -> [RecordingSession] {
        // `recordingsDirectory` is a MainActor-isolated computed property (it reads
        // `settingsManager`), so resolve it here and hand only the plain URL to the
        // detached scan.
        let directory = recordingsDirectory
        let candidates = await Self.scanForUnclaimedWAVCandidates(in: directory)
        guard !candidates.isEmpty else { return [] }

        let claimedPaths = Set(existingOrphans.compactMap { $0.audioFilePath })
        var stubs: [RecordingSession] = []

        for candidate in candidates {
            let url = candidate.url
            let path = url.path
            if claimedPaths.contains(path) { continue }

            // Never touch a file that could belong to this process's live recording.
            // An unreadable creation date falls back to "now", i.e. treated as too
            // new to touch.
            //
            // Ordering: this is pure arithmetic on values the scan already returned,
            // so it runs BEFORE the `count(for:)` query below — a too-new candidate
            // must not pay for a main-thread SQLite round-trip on the launch path.
            //
            // What actually protects a live recording here:
            //  - a WAV recorded by *this* process is always newer than
            //    `staleSessionCutoff` while its session row is still missing.
            //    `staleSessionCutoff` is `max(now - 60s, processLaunchDate)`, so it
            //    is never *earlier* than launch (line ~189) — when the process has
            //    been up under 60s the cutoff IS the launch time and any file this
            //    process created is newer; once it has been up longer the cutoff is
            //    `now - 60s`, and a file in the deferred-insert window is only
            //    milliseconds old, so it is still newer. Either way it is skipped.
            //  - a longer-lived live recording (older than the cutoff) is covered by
            //    the `claimedPaths` set and the `count(for:)` query below ONLY IF
            //    the deferred record-start insert succeeded. When it does,
            //    `RecordingLifecycle.persistSessionForActiveRecording` passes the
            //    same `.incomplete_<uuid>.wav` path as `audioFilePath`
            //    (RecordingLifecycle.swift:426/485), so the row claims this exact
            //    path and both checks hit it.
            //
            // RESIDUAL GAP (known, not closed here): the insert is allowed to fail.
            // `RecordingSessionManager.createRecordingSession` returns nil and
            // clears `currentRecordingSession` when the background save or
            // `obtainPermanentIDs` fails — by design, "continuing without session
            // tracking" (RecordingSessionManager.swift:139-153) — and recording
            // keeps going. On that path a live recording has NO row at all, so
            // `claimedPaths`, the `count(for:)` query, and the recovery loop's
            // `currentSessionID` check (which reads the same nil-ed
            // `currentRecordingSession`) all miss it. Once such a recording runs
            // past `staleSessionCutoff` — i.e. longer than 60s, with the process up
            // longer than 60s — the staleness guard stops protecting it too, and
            // the sweep can synthesize a stub for a file still being written and
            // move it out from under the live `AVAudioRecorder`. This requires a
            // Core Data write failure first, so it is rare, but it is reachable.
            //
            // The scan above is a suspension point but can't widen this race:
            // `staleSessionCutoff` was computed before it, so any delay only makes
            // candidates newer relative to the cutoff, never older.
            //
            // Note `currentSessionID` (checked in the recovery loop) is NOT a second
            // guard for these stubs: a stub's `id` is parsed out of the
            // `.incomplete_<uuid>.wav` filename, while a live session row's `id` is
            // an independent `UUID()`, so the two can never compare equal.
            let creationDate = candidate.creationDate ?? Date()
            guard creationDate <= staleSessionCutoff else { continue }

            // A non-orphaned row may still claim this file — cheap existence check.
            let claimCheck = RecordingSession.fetchRequest()
            claimCheck.predicate = NSPredicate(format: "audioFilePath == %@", path)
            claimCheck.fetchLimit = 1
            if let count = try? context.count(for: claimCheck), count > 0 { continue }

            // Filename is ".incomplete_<sessionUUID>.wav" — reuse that UUID.
            let stem = url.deletingPathExtension().lastPathComponent
                .replacingOccurrences(of: ".incomplete_", with: "")
            let sessionId = UUID(uuidString: stem) ?? UUID()

            // Fixed recorder format metadata: SimpleRecorder always records
            // 16kHz mono WAV (same constants as persistSessionForActiveRecording).
            let stub = RecordingSession(context: context)
            stub.id = sessionId
            stub.startTime = creationDate
            stub.audioFilePath = path
            stub.sampleRate = 16000
            stub.channelCount = 1
            stub.audioFormat = "WAV PCM 16000Hz 1ch"
            stub.endTime = nil
            // `deviceId`/`deviceName` are non-optional on RecordingSession (no default
            // value), but the crashed process that recorded this file never got a
            // chance to persist them — the row we're synthesizing here never went
            // through the normal record-start path that populates them from the
            // selected input device. Leaving them nil causes the batch save in STEP 3
            // to fail validation for every session in the batch with
            // NSCocoaErrorDomain code 1570 "deviceId is a required value", silently
            // dropping the whole recovery pass (see HYPERWHISPER-V3/V4). The real
            // device is unrecoverable at this point, so fall back to a sentinel —
            // consistent with the "default" fallback already used elsewhere when no
            // input device resolves (see RecordingLifecycle.swift:459-460).
            stub.deviceId = "default"
            stub.deviceName = "audio.device.default".localized
            stubs.append(stub)

            AppLogger.audio.info("🩹 Synthesized stub session \(sessionId.uuidString, privacy: .public) for unclaimed incomplete WAV: \(url.lastPathComponent, privacy: .public)")
        }

        if !stubs.isEmpty, AppLogger.isErrorLoggingEnabled {
            SentryService.addBreadcrumb(
                message: "Synthesized stub sessions for unclaimed incomplete WAVs",
                category: "audio.recovery",
                data: ["count": stubs.count]
            )
        }
        return stubs
    }

    /// List the `.incomplete_*.wav` files in `directory`, off the main actor.
    ///
    /// **Why detached:** `contentsOfDirectory` and `resourceValues` are blocking
    /// filesystem syscalls. When the recordings directory lives on a cloud-synced
    /// or network-backed volume they can take many seconds, and running them on
    /// the main actor hung the app at launch (HYPERWHISPER-VM, "App hanging for at
    /// least 10000 ms") — they must not execute on the main actor, whatever the
    /// caller. Being `nonisolated async` is what takes this work off the caller's
    /// actor; the `Task.detached` on top of that pins the blocking work to a fixed
    /// `.userInitiated` priority instead of inheriting the caller's, and matches
    /// the established shape in
    /// `RecordingTranscriptionFlow.isAudioFileReadable`.
    ///
    /// Returns an empty list if the directory can't be read (missing, unreadable).
    nonisolated static func scanForUnclaimedWAVCandidates(
        in directory: URL
    ) async -> [CrashRecoveryWAVCandidate] {
        await Task.detached(priority: .userInitiated) { () -> [CrashRecoveryWAVCandidate] in
            let fm = FileManager.default
            // options: [] (NOT .skipsHiddenFiles) — the incomplete files are
            // dot-prefixed and would otherwise be invisible to the sweep.
            guard let entries = try? fm.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: [.creationDateKey],
                options: []
            ) else {
                return []
            }

            return entries
                .filter {
                    $0.lastPathComponent.hasPrefix(".incomplete_") && $0.pathExtension.lowercased() == "wav"
                }
                .map { url in
                    CrashRecoveryWAVCandidate(
                        url: url,
                        creationDate: (try? url.resourceValues(forKeys: [.creationDateKey]))?.creationDate
                    )
                }
        }.value
    }

    // MARK: - Validation

    /// Check if a WAV file is recoverable
    ///
    /// **What This Does:**
    /// Validates that a WAV file:
    /// 1. Exists on disk
    /// 2. Is readable
    /// 3. Has audio data (fileSize > header size, ~44 bytes for WAV)
    /// 4. Can be opened by AVAudioFile
    ///
    /// **Why nonisolated + detached:**
    /// Every check here is a blocking filesystem call (`stat`, `open`, plus the
    /// header decode inside `AVAudioFile`). The only caller is the `@MainActor`
    /// recovery loop, so running them inline would put one round of blocking I/O
    /// per orphan on the main thread — on a cloud-synced volume that is the
    /// expensive half of the launch hang this method's sibling scan was moved off
    /// for (HYPERWHISPER-VM). Same shape as
    /// `RecordingTranscriptionFlow.isAudioFileReadable`: touches no Core Data or
    /// UI state, so it is `nonisolated static` and does its work on a detached
    /// task at `.userInitiated`.
    ///
    /// **Parameters:**
    /// - `url`: Path to the WAV file
    ///
    /// **Returns:**
    /// true if file can be recovered, false otherwise
    nonisolated static func isRecoverableWAV(_ url: URL) async -> Bool {
        await Task.detached(priority: .userInitiated) { () -> Bool in
            let fm = FileManager.default

            // Check existence
            guard fm.fileExists(atPath: url.path) else {
                return false
            }

            // Check readability
            guard fm.isReadableFile(atPath: url.path) else {
                return false
            }

            // Check file size (must have data beyond WAV header ~44 bytes)
            guard let attrs = try? fm.attributesOfItem(atPath: url.path),
                  let fileSize = attrs[.size] as? Int64,
                  fileSize > 100 else { // At least 100 bytes to have some audio data
                return false
            }

            // Try opening with AVAudioFile (validates format)
            guard let _ = try? AVAudioFile(forReading: url) else {
                return false
            }

            return true
        }.value
    }

    // MARK: - Recovery

    /// Recover an orphaned WAV recording
    ///
    /// **What This Does:**
    /// 1. Check WAV file size
    /// 2. If < 25MB: Rename to final location (WAV is efficient for short recordings)
    /// 3. If >= 25MB: Convert to M4A for space efficiency
    /// 4. Update session with new path and duration
    /// 5. Delete the incomplete file
    ///
    /// **File Size Strategy:**
    /// WAV files under 25MB are kept as-is since:
    /// - Conversion overhead isn't worth it for small files
    /// - WAV is more reliable (no encoding failures)
    /// - Matches the normal recording flow behavior
    ///
    /// **Parameters:**
    /// - `session`: The orphaned RecordingSession
    /// - `rawURL`: URL of the incomplete WAV file
    ///
    /// **Returns:**
    /// URL of the recovered audio file, or nil if recovery failed
    private func recoverAndConvertRecording(
        session: RecordingSession,
        rawURL: URL
    ) async -> URL? {
        let fm = FileManager.default
        let sessionID = session.id?.uuidString ?? UUID().uuidString

        // Get file size to decide WAV vs M4A
        guard let attrs = try? fm.attributesOfItem(atPath: rawURL.path),
              let fileSize = attrs[.size] as? Int64 else {
            AppLogger.audio.error("Cannot get file size for recovery: \(rawURL.lastPathComponent)")
            return nil
        }

        // Generate destination path (honors settingsManager.recordingsFolder)
        let recordingsDir = recordingsDirectory

        // Ensure recordings directory exists
        try? fm.createDirectory(at: recordingsDir, withIntermediateDirectories: true)

        do {
            // Get audio info from source file
            guard let audioFile = try? AVAudioFile(forReading: rawURL) else {
                AppLogger.audio.error("Cannot read WAV file for recovery: \(rawURL.lastPathComponent)")
                return nil
            }

            let sampleRate = audioFile.processingFormat.sampleRate
            let channels = audioFile.processingFormat.channelCount
            let frameCount = Double(audioFile.length)
            let duration = frameCount / sampleRate

            let finalURL: URL
            let finalFormat: String

            if fileSize < wavToM4AThreshold {
                // Small file: Keep as WAV
                finalURL = recordingsDir.appendingPathComponent("\(sessionID).wav")
                finalFormat = "wav"

                // Move/rename the file
                if rawURL != finalURL {
                    try? fm.removeItem(at: finalURL) // Remove if exists
                    try fm.moveItem(at: rawURL, to: finalURL)
                }

                AppLogger.audio.info("✅ Recovered WAV: \(sessionID) (\(String(format: "%.1f", duration))s, \(fileSize / 1024)KB)")
            } else {
                // Large file: Convert to M4A
                finalURL = recordingsDir.appendingPathComponent("\(sessionID).m4a")
                finalFormat = "m4a"

                _ = try await audioFileConverter.convertAudioToAAC(
                    from: rawURL,
                    to: finalURL
                )

                // Delete original WAV file to save space
                try? fm.removeItem(at: rawURL)

                AppLogger.audio.info("✅ Recovered M4A: \(sessionID) (\(String(format: "%.1f", duration))s, converted from \(fileSize / 1024)KB WAV)")
            }

            // Update session with recovered data
            session.audioFilePath = finalURL.path
            session.durationInSeconds = duration
            session.endTime = Date() // Mark as complete
            session.sampleRate = sampleRate
            session.channelCount = Int16(channels)
            session.audioFormat = finalFormat

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Recovered recording",
                    category: "audio.recovery",
                    data: [
                        "sessionId": sessionID,
                        "rawPath": rawURL.path,
                        "finalPath": finalURL.path,
                        "format": finalFormat,
                        "durationSec": duration
                    ]
                )
            }

            return finalURL

        } catch {
            AppLogger.audio.error("Failed to recover WAV: \(error.localizedDescription)")
            if AppLogger.isErrorLoggingEnabled {
                let nsError = error as NSError
                SentryService.addBreadcrumb(
                    message: "Recovery failed",
                    category: "audio.recovery",
                    level: .error,
                    data: [
                        "sessionId": sessionID,
                        "rawPath": rawURL.path,
                        "errorDomain": nsError.domain,
                        "errorCode": nsError.code
                    ]
                )
            }
            return nil
        }
    }
}
