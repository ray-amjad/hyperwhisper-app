//
//  RecordingSessionManager.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import CoreData

/// Manages Core Data recording session lifecycle
///
/// **Purpose:**
/// Handles the creation and updating of RecordingSession entities in Core Data.
/// Each recording session tracks metadata like device info, audio format, duration,
/// and file paths for crash recovery and history management.
///
/// **Responsibilities:**
/// - Create new RecordingSession entities once the recorder is live
/// - Update sessions with final audio file paths and durations when recording stops
/// - Update session format metadata after AAC conversion
/// - Track current recording session for cleanup and recovery
///
/// **Core Data Integration:**
/// Session creation saves on a background context to avoid blocking the main thread.
/// Updates and deletions use the viewContext since the managed object is already there.
///
/// **Session Lifecycle:**
/// 1. **Start Recording**: scheduleRecordingSessionCreation() → saves after the start transaction
/// 2. **Stop Recording**: updateRecordingSessionOnStop() → updates with final data
/// 3. **Crash**: Session remains with endTime = nil → recovered by CrashRecoveryManager
///
/// **Thread Safety:**
/// All methods run on main actor for Core Data consistency.
@MainActor
class RecordingSessionManager {

    // MARK: - Properties

    /// Current active recording session (nil when not recording)
    private(set) var currentRecordingSession: RecordingSession?

    /// Session creation is scheduled after the recorder is live so the
    /// Recording Start transaction does not include a Core Data insert.
    private var pendingRecordingSessionCreation: Task<Void, Never>?

    // MARK: - Session Creation

    /// Schedule a new recording session in Core Data
    ///
    /// **What This Does:**
    /// 1. Creates a RecordingSession entity in Core Data after recorder startup
    /// 2. Sets metadata: device info, audio format, start time
    /// 3. Saves after startup instrumentation for crash recovery
    /// 4. Stores reference in currentRecordingSession
    ///
    /// **Why Deferred Save:**
    /// If the app crashes during recording, we need the session entity to exist
    /// so CrashRecoveryManager can find and recover the orphaned recording. The
    /// save is deferred until after the Sentry Recording Start transaction so
    /// Core Data does not report an insert on the main-thread startup path.
    ///
    /// **Parameters:**
    /// - `deviceId`: UID of the audio input device
    /// - `deviceName`: Display name of the device (e.g., "MacBook Pro Microphone")
    /// - `sampleRate`: Hardware sample rate (e.g., 48000.0)
    /// - `channelCount`: Number of channels (1 = mono, 2 = stereo)
    /// - `audioFormat`: Format string (e.g., "m4a", "caf")
    ///
    /// **Returns:**
    /// The newly created RecordingSession entity (registered on the viewContext), or nil
    /// when persistence failed and recording should continue without crash recovery.
    func scheduleRecordingSessionCreation(
        deviceId: String?,
        deviceName: String,
        sampleRate: Double,
        channelCount: Int,
        audioFormat: String,
        audioFilePath: String? = nil,
        startTime: Date
    ) {
        pendingRecordingSessionCreation?.cancel()
        currentRecordingSession = nil

        pendingRecordingSessionCreation = Task { [weak self] in
            guard let self else { return }
            _ = await self.createRecordingSession(
                deviceId: deviceId,
                deviceName: deviceName,
                sampleRate: sampleRate,
                channelCount: channelCount,
                audioFormat: audioFormat,
                audioFilePath: audioFilePath,
                startTime: startTime
            )
        }
    }

    /// Return the active session, awaiting deferred creation when a rapid
    /// stop/cancel arrives before the background save has completed.
    func resolveCurrentSession() async -> RecordingSession? {
        if let pendingRecordingSessionCreation {
            await pendingRecordingSessionCreation.value
            self.pendingRecordingSessionCreation = nil
        }

        return currentRecordingSession
    }

    private func createRecordingSession(
        deviceId: String?,
        deviceName: String,
        sampleRate: Double,
        channelCount: Int,
        audioFormat: String,
        audioFilePath: String? = nil,
        startTime: Date
    ) async -> RecordingSession? {
        let container = PersistenceController.shared.container
        let backgroundContext = container.newBackgroundContext()
        let sessionId = UUID()

        let objectID: NSManagedObjectID? = await backgroundContext.perform {
            let session = RecordingSession(context: backgroundContext)
            session.id = sessionId
            session.startTime = startTime
            session.deviceId = deviceId
            session.deviceName = deviceName
            session.sampleRate = sampleRate
            session.channelCount = Int16(channelCount)
            session.audioFormat = audioFormat
            session.audioFilePath = audioFilePath

            do {
                try backgroundContext.obtainPermanentIDs(for: [session])
                try backgroundContext.save()
            } catch {
                AppLogger.coreData.error("Failed to save recording session on background context: \(error.localizedDescription)")
                return nil
            }
            return session.objectID
        }

        guard let objectID, let session = container.viewContext.object(with: objectID) as? RecordingSession else {
            AppLogger.coreData.error("Recording session background save failed; continuing without session tracking")
            currentRecordingSession = nil
            return nil
        }

        currentRecordingSession = session

        AppLogger.audio.info("📝 Created recording session: \(sessionId.uuidString)")

        return session
    }

    // MARK: - Session Updates

    /// Update recording session with final audio file and duration
    ///
    /// **What This Does:**
    /// 1. Updates the session with final M4A file path
    /// 2. Sets duration and end time
    /// 3. Saves to Core Data
    /// 4. Clears currentRecordingSession reference
    ///
    /// **When to Call:**
    /// After recording stops and M4A conversion completes successfully.
    ///
    /// **Parameters:**
    /// - `session`: The recording session to update
    /// - `audioFilePath`: Full path to the final M4A file
    /// - `duration`: Recording duration in seconds
    func updateRecordingSessionOnStop(
        session: RecordingSession,
        audioFilePath: String,
        duration: TimeInterval
    ) {
        // Update session with final data
        session.audioFilePath = audioFilePath
        session.durationInSeconds = duration
        session.endTime = Date()

        // Save changes
        PersistenceController.shared.save()

        // Clear reference
        currentRecordingSession = nil

        AppLogger.audio.info("✅ Updated recording session: duration=\(String(format: "%.1f", duration))s, path=\(audioFilePath)")
    }

    /// Update session format metadata after conversion
    ///
    /// **What This Does:**
    /// Helper method to update audio format metadata if needed after
    /// AAC conversion. Useful when format info wasn't available at creation.
    ///
    /// **Parameters:**
    /// - `session`: The session to update
    /// - `url`: Audio file URL to extract format metadata from
    func updateSessionFormat(session: RecordingSession, url: URL) {
        // This is a helper method for future use
        // Currently format metadata is set during creation
        // Could be extended to read actual file format if needed
        AppLogger.audio.debug("Session format update (currently no-op)")
    }

    // MARK: - Cleanup

    /// Clear current session reference without saving
    ///
    /// **What This Does:**
    /// Clears the currentRecordingSession reference. Used for cleanup
    /// when recording fails or is cancelled.
    ///
    /// **When to Call:**
    /// - Recording start fails after session creation
    /// - Recording is cancelled before completion
    func clearCurrentSession() {
        currentRecordingSession = nil
        AppLogger.audio.debug("Cleared current recording session reference")
    }

    /// Delete the current recording session from Core Data
    ///
    /// **What This Does:**
    /// 1. Deletes the incomplete RecordingSession entity from Core Data
    /// 2. Removes associated audio file from disk if it exists
    /// 3. Saves changes immediately
    /// 4. Clears the currentRecordingSession reference
    ///
    /// **When to Call:**
    /// When recording start fails after session creation. This prevents orphaned
    /// Core Data entities and temporary audio files from accumulating.
    ///
    /// **Why Delete:**
    /// A failed recording attempt creates a session entity and temp file before
    /// the engine starts. If startup fails, we need to clean up these resources
    /// to avoid triggering crash recovery on next launch.
    func deleteCurrentSession() async {
        guard let session = await resolveCurrentSession() else {
            AppLogger.audio.debug("No current session to delete")
            return
        }

        deleteSession(session)
    }

    /// Delete a specific recording session from Core Data.
    ///
    /// Use this when the caller already resolved the session before clearing
    /// `currentRecordingSession`, such as the too-short discard path.
    func deleteSession(_ session: RecordingSession, deleteAudioFile: Bool = true) {
        let context = PersistenceController.shared.container.viewContext

        // Delete associated audio file if it exists
        if deleteAudioFile, let filePath = session.audioFilePath {
            do {
                try FileManager.default.removeItem(atPath: filePath)
                AppLogger.audio.debug("Deleted incomplete audio file: \(filePath)")
            } catch {
                AppLogger.audio.debug("Could not delete incomplete audio file (may not exist): \(error.localizedDescription)")
            }
        }

        // Delete the session entity
        context.delete(session)

        // Save changes
        PersistenceController.shared.save()

        // Clear reference
        if currentRecordingSession?.objectID == session.objectID {
            currentRecordingSession = nil
        }

        AppLogger.audio.info("🗑️ Deleted incomplete recording session")
    }
}
