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
/// Methods are @MainActor for Core Data safety, except isRecoverableWAV
/// which is nonisolated for background validation.
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
    /// **Performance:**
    /// - Batch saves instead of per-session saves
    /// - Runs async to not block UI
    /// - Uses nonisolated validation for parallel checks
    func recoverOrphanedRecordings(currentSessionID: UUID? = nil) async {
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
        orphans += synthesizeStubSessionsForUnclaimedWAVs(
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

        // Load attempt counts once, mutate in-memory, save once at the end
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

            // Check if WAV file exists and is recoverable
            // Note: isRecoverableWAV is nonisolated so can be called directly
            guard isRecoverableWAV(url) else {
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
    /// `staleSessionCutoff` — so the live recording of this process is never
    /// touched. No sidecar marker files are needed: the WAV filename embeds the
    /// session UUID, which the stub reuses so recovery attempt-counting stays
    /// stable across launches.
    private func synthesizeStubSessionsForUnclaimedWAVs(
        existingOrphans: [RecordingSession],
        context: NSManagedObjectContext,
        staleSessionCutoff: Date
    ) -> [RecordingSession] {
        let fm = FileManager.default
        // options: [] (NOT .skipsHiddenFiles) — the incomplete files are
        // dot-prefixed and would otherwise be invisible to the sweep.
        guard let entries = try? fm.contentsOfDirectory(
            at: recordingsDirectory,
            includingPropertiesForKeys: [.creationDateKey],
            options: []
        ) else {
            return []
        }

        let candidates = entries.filter {
            $0.lastPathComponent.hasPrefix(".incomplete_") && $0.pathExtension.lowercased() == "wav"
        }
        guard !candidates.isEmpty else { return [] }

        let claimedPaths = Set(existingOrphans.compactMap { $0.audioFilePath })
        var stubs: [RecordingSession] = []

        for url in candidates {
            let path = url.path
            if claimedPaths.contains(path) { continue }

            // A non-orphaned row may still claim this file — cheap existence check.
            let claimCheck = RecordingSession.fetchRequest()
            claimCheck.predicate = NSPredicate(format: "audioFilePath == %@", path)
            claimCheck.fetchLimit = 1
            if let count = try? context.count(for: claimCheck), count > 0 { continue }

            // Never touch a file that could belong to this process's live recording.
            let creationDate = (try? url.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? Date()
            guard creationDate <= staleSessionCutoff else { continue }

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
    /// **Why Nonisolated:**
    /// This method only does file I/O and doesn't touch Core Data or UI state.
    /// Making it nonisolated allows it to be called from background threads
    /// for parallel validation of multiple files.
    ///
    /// **Parameters:**
    /// - `url`: Path to the WAV file
    ///
    /// **Returns:**
    /// true if file can be recovered, false otherwise
    nonisolated func isRecoverableWAV(_ url: URL) -> Bool {
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
