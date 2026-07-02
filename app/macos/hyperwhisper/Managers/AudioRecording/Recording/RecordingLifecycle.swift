//
//  RecordingLifecycle.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import AVFoundation
import CoreData

/// Manages core recording start/stop lifecycle
///
/// **Purpose:**
/// Coordinates the complete recording flow from start to finish:
/// - SimpleRecorder-based audio capture (AVAudioRecorder)
/// - Audio level monitoring for visualization
/// - M4A conversion after recording stops (if file exceeds 25MB)
/// - File management and cleanup
/// - Duration tracking with timer
///
/// **Recording Architecture:**
/// ```
/// Start → SimpleRecorder (AVAudioRecorder) → write 16kHz mono WAV
///   ↓
/// Recording... (WAV written to .incomplete_<sessionID>.wav)
///   ↓
/// Stop → SimpleRecorder.stop() → convert to M4A if large → cleanup
///   ↓
/// Return final audio URL + duration
/// ```
///
/// **Crash Recovery Integration:**
/// - Uses session-tagged temp files: `.incomplete_<sessionID>.wav`
/// - Stores file path in RecordingSession after the startup transaction finishes
/// - If app crashes, CrashRecoveryManager finds incomplete sessions
/// - Converts recovered WAV files to M4A and triggers transcription
///
/// **Dependencies:**
/// - SimpleRecorder: AVAudioRecorder-based audio capture at 16kHz mono
/// - AudioFileConverter: WAV to M4A conversion
/// - AudioDeviceManager: Device selection and restoration
/// - RecordingSessionManager: Core Data session tracking
///
/// **Thread Safety:**
/// All methods run on main actor for UI consistency and Core Data safety.
@MainActor
class RecordingLifecycle {

    // MARK: - Dependencies

    private let simpleRecorder: SimpleRecorder

    /// Audio file converter for format conversions
    /// Internal access for RecordingTranscriptionFlow's M4A conversion of large trimmed files
    /// nonisolated(unsafe) because this is a let constant assigned once in init — allows nonisolated methods to access it without an actor hop
    nonisolated(unsafe) internal let audioFileConverter: AudioFileConverter

    // Internal access for cleanup operations
    internal let deviceManager: AudioDeviceManager
    internal let sessionManager: RecordingSessionManager

    /// Manages audio environment (system volume, media players) during recording
    private let audioSessionManager: AudioSessionManager

    private weak var settingsManager: SettingsManager?

    // MARK: - State Properties

    /// Current recording state
    @Published var isRecording = false

    /// Real-time audio level (0.0 to 1.0) for visualization
    @Published var audioLevel: Float = 0

    /// Recording duration in seconds (updated by timer)
    @Published var recordingDuration: TimeInterval = 0

    /// Last successfully recorded file (for retry)
    @Published var lastRecordingURL: URL?

    // MARK: - File References

    /// Temporary raw PCM file (CAF format)
    private var rawURL: URL?

    /// Final output file (M4A format)
    private var finalURL: URL?

    // MARK: - Recording State

    /// Recording start timestamp
    private var recordingStartTime: Date?

    /// Timer for updating duration display
    private var recordingTimer: Timer?

    /// Microphone permission status
    var hasMicrophonePermission: Bool = false

    /// Saved audio environment state for restoration after recording
    /// Stores original system volume and paused media players
    private var recordingAudioEnvironmentState: AudioEnvironmentState?

    /// Original microphone input volume for restoration after recording
    /// Used by the "Automatically increase microphone volume" feature.
    /// Stores the volume level (0.0 to 1.0) before it was increased to max.
    private var originalMicVolume: Float?

    /// AudioDeviceID whose volume was increased, captured alongside `originalMicVolume`.
    /// Restoration must target this exact device — the system default at stop time can
    /// differ (failed default switch, mid-recording device change), which would write
    /// the saved volume to the wrong device (issue #235).
    private var originalMicVolumeDeviceID: AudioDeviceID?

    /// Recordings directory URL
    private var recordingsDirectory: URL {
        if let path = settingsManager?.recordingsFolder, !path.isEmpty {
            return URL(fileURLWithPath: path, isDirectory: true)
        }

        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Recordings", isDirectory: true)
    }

    // MARK: - Initialization

    init(
        simpleRecorder: SimpleRecorder,
        audioFileConverter: AudioFileConverter,
        deviceManager: AudioDeviceManager,
        sessionManager: RecordingSessionManager,
        audioSessionManager: AudioSessionManager
    ) {
        self.simpleRecorder = simpleRecorder
        self.audioFileConverter = audioFileConverter
        self.deviceManager = deviceManager
        self.sessionManager = sessionManager
        self.audioSessionManager = audioSessionManager

        // Bind SimpleRecorder's audioLevel to our published property
        simpleRecorder.$audioLevel
            .assign(to: &$audioLevel)
    }

    /// Configure with settings manager after initialization
    func configure(settingsManager: SettingsManager?) {
        self.settingsManager = settingsManager
    }

    // MARK: - Media Control

    /// Apply media control settings (mute audio or pause media).
    /// Called AFTER recording starts and AFTER the start sound plays,
    /// so the sound remains audible.
    func applyMediaControl() {
        let mediaControlMode = settingsManager?.audio.mediaControlMode ?? .off
        switch mediaControlMode {
        case .off:
            AppLogger.audio.debug("Media control mode: off - no audio changes")
        case .muteAudio:
            let audioEnvironmentState = audioSessionManager.prepareAudioEnvironment()
            if audioEnvironmentState != nil {
                self.recordingAudioEnvironmentState = audioEnvironmentState
                AppLogger.audio.info("Audio environment prepared (muted) for recording")
            } else {
                AppLogger.audio.warning("Failed to mute audio environment, continuing recording")
            }
        }
    }

    // MARK: - Recording Control

    /// Start recording audio with live level metering
    ///
    /// **What This Does:**
    /// 1. Validates permissions and prepares recordings folder
    /// 2. Clears stale file references from previous recordings
    /// 3. Stops any existing recording
    /// 4. Applies selected audio input device
    /// 5. Creates AVAudioEngine with validated hardware format and prepares file URLs
    /// 6. Creates RecordingSession in Core Data
    /// 7. Installs tap for audio writing and level monitoring
    /// 8. Starts engine and duration timer
    /// 9. Returns final M4A URL (created after conversion)
    ///
    /// **Architecture:**
    /// AVAudioEngine → tap() → write raw PCM + audio level → stop() → convert to m4a
    ///
    /// **File Collision Fix:**
    /// Always clears file references to ensure each recording has unique URLs
    /// based on current timestamp. Prevents FileWatcher from monitoring wrong files.
    ///
    /// **Hardware Format Validation:**
    /// Some audio interfaces report invalid formats (sampleRate = 0) on first query.
    /// If detected, we reset and retry with a new engine instance.
    ///
    /// **Crash Recovery:**
    /// Uses session-tagged temp file: `.incomplete_<sessionID>.caf`
    /// Stores path in RecordingSession immediately for recovery after crash.
    ///
    /// **Returns:**
    /// URL where final M4A file will be written (after conversion)
    ///
    /// **Throws:**
    /// - `AudioError.noPermission`: No microphone permission
    /// - `AudioError.permissionDenied`: Can't access recordings folder
    /// - `AudioError.invalidHardwareFormat`: Audio interface format error
    /// - `AudioError.fileCreationFailed`: Can't create audio file
    @discardableResult
    func startRecording() async throws -> URL {
        // STEP 1: Prepare environment
        // Ensure recordings folder is accessible before starting
        if let settings = settingsManager {
            let storageSpan = SentryService.startSpan(operation: "audio.storage", description: "prepare recordings folder")
            let ready = await settings.prepareRecordingsFolderIfNeededAsync()
            SentryService.finishSpan(storageSpan, status: ready ? .ok : .internalError)
            if !ready {
                // Try recovery prompt with manual selection as fallback
                let chose = await MainActor.run { settings.presentStorageRecoveryPrompt() }
                if !chose {
                    throw AudioError.permissionDenied(reason: "recording.error.documentsAccess".localized)
                }
            }
        }

        // STEP 2: FILE COLLISION FIX
        // Clear stale file references from previous recordings
        // Problem: If a new recording starts before the previous one fully completes,
        // the FileWatcher could try to monitor the wrong file
        // Solution: Always clear these references to ensure each recording session
        // has fresh, unique file URLs based on current timestamp
        self.finalURL = nil
        self.rawURL = nil

        // STEP 3: Stop any existing recording
        if isRecording {
            _ = await stopRecording()
            try await Task.sleep(nanoseconds: 100_000_000) // 0.1 seconds
        }

        // STEP 3.5: Verify audio input devices exist before attempting to record.
        // This avoids the misleading "recording too short" path on machines with no
        // connected microphone or other usable input device.
        //
        // CoreAudio can transiently report 0 input devices during audio route changes
        // (Bluetooth disconnect, USB audio removal, AirPods reconnect, wake-from-sleep,
        // etc.) even on Macs with built-in microphones. Retry with exponential backoff
        // before giving up — real "no microphone" machines return instantly; transient
        // route changes typically recover within ~1–2 s but can take longer on wake.
        //
        // History: Original fix (HYPERWHISPER-NF, 7 users) used 2×250ms = 500ms total —
        // too short. Regressed on v2.33.1 to 9 users. Extending to ~3.2s total with
        // per-attempt breadcrumbs so we can see recovery patterns in Sentry.
        let deviceRetryDelaysMs: [UInt64] = [150, 250, 400, 600, 800, 1000]
        let deviceRetryStart = Date()
        var inputDevices = CoreAudioDeviceHelper.fetchCoreAudioInputDevices()
        if inputDevices.isEmpty {
            for (index, delayMs) in deviceRetryDelaysMs.enumerated() {
                let attempt = index + 1
                AppLogger.audio.warning("No audio input devices on attempt \(attempt, privacy: .public) - retrying in \(delayMs, privacy: .public)ms")
                try await Task.sleep(nanoseconds: delayMs * 1_000_000)
                inputDevices = CoreAudioDeviceHelper.fetchCoreAudioInputDevices()
                if !inputDevices.isEmpty {
                    let elapsedMs = Int(Date().timeIntervalSince(deviceRetryStart) * 1_000)
                    AppLogger.audio.notice("Audio input devices recovered on attempt \(attempt, privacy: .public) after \(elapsedMs, privacy: .public)ms (count=\(inputDevices.count, privacy: .public))")
                    if AppLogger.isErrorLoggingEnabled {
                        SentryService.addBreadcrumb(
                            message: "Recording start - input devices recovered after retry",
                            category: "audio.recording",
                            level: .info,
                            data: [
                                "retryAttempt": attempt,
                                "elapsedMs": elapsedMs,
                                "recoveredDeviceCount": inputDevices.count
                            ]
                        )
                    }
                    break
                }
            }
        }
        if inputDevices.isEmpty {
            let elapsedMs = Int(Date().timeIntervalSince(deviceRetryStart) * 1_000)
            var diagnostics = collectInputDeviceDiagnostics(availableDevices: inputDevices)
            diagnostics["deviceRetryElapsedMs"] = elapsedMs
            diagnostics["deviceRetryAttempts"] = deviceRetryDelaysMs.count
            AppLogger.audio.error("No audio input devices detected after \(deviceRetryDelaysMs.count, privacy: .public) retries (\(elapsedMs, privacy: .public)ms) - cannot start recording")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Recording start blocked - no audio input devices after retries",
                    category: "audio.recording",
                    level: .warning,
                    data: diagnostics
                )
            }

            throw AudioError.noMicrophoneAvailable
        }

        // STEP 4: Apply selected audio input device
        // This temporarily changes system default if user selected specific device
        let deviceApplySpan = SentryService.startSpan(operation: "audio.device", description: "apply selected input device")
        deviceManager.applySelectedInputDeviceIfNeeded()
        SentryService.finishSpan(deviceApplySpan, status: .ok)

        // STEP 4.5: Auto-increase microphone volume if enabled
        // Sets the active input device's volume to maximum for optimal recording levels.
        // Works with both system default and explicitly selected devices.
        // The original volume is saved and will be restored after recording stops.
        //
        // FIRE-AND-FORGET FIX FOR MAIN THREAD BLOCKING
        // ---------------------------------------------------------------------------------
        // Problem: CoreAudio API calls (AudioObjectGetPropertyData/AudioObjectSetPropertyData)
        // can hang for 10+ seconds with Bluetooth, USB, or driver-problematic audio devices.
        // This was causing 12-second UI freezes during recording startup.
        //
        // Solution: Move CoreAudio operations to a detached background task.
        // Recording starts immediately; volume adjustment happens asynchronously.
        // If the task completes before recording stops, the original volume will be restored.
        // If recording stops first, the volume may not be restored (acceptable trade-off
        // since the feature is "nice to have" and shouldn't block the core recording function).
        if settingsManager?.autoIncreaseMicVolume == true {
            // Capture device UID before launching background task (if a specific device is selected)
            let selectedDeviceUID = deviceManager.selectedDevice?.uid

            Task.detached(priority: .userInitiated) { [weak self] in
                AppLogger.audio.info("🎚️ [ASYNC] Auto-increase mic volume task STARTED on background thread")

                guard self != nil else {
                    AppLogger.audio.warning("🎚️ [ASYNC] Task cancelled - self was deallocated")
                    return
                }

                // Get device ID - either from selected device or system default
                let deviceID: AudioDeviceID?
                if let uid = selectedDeviceUID {
                    // User selected a specific device - find its ID by UID
                    deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: uid)
                    if deviceID == nil {
                        AppLogger.audio.warning("🎚️ [ASYNC] Failed - unable to find device with UID: \(uid, privacy: .public)")
                        return
                    }
                } else {
                    // Using system default
                    deviceID = CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()
                    if deviceID == nil {
                        AppLogger.audio.warning("🎚️ [ASYNC] Failed - unable to get system default device ID")
                        return
                    }
                }

                guard let deviceID = deviceID else { return }

                // Log device info for debugging
                let deviceName = CoreAudioDeviceHelper.copyDeviceName(for: deviceID) ?? "unknown"
                let deviceType = selectedDeviceUID != nil ? "selected" : "system default"
                AppLogger.audio.info("🎚️ [ASYNC] Target device (\(deviceType, privacy: .public)): \(deviceName, privacy: .public) (ID: \(deviceID))")

                // Save original volume for restoration
                let originalVolume = CoreAudioDeviceHelper.readInputVolumeScalar(for: deviceID)
                let originalStr = originalVolume.map { String(format: "%.0f%%", $0 * 100) } ?? "nil (unsupported)"
                AppLogger.audio.info("🎚️ [ASYNC] Current volume: \(originalStr, privacy: .public)")

                // GUARD: if we can't read the current volume, we can't restore it later.
                // setInputVolumeScalar is an independent CoreAudio write that can still
                // succeed (e.g. via the channel-1 fallback) even when the read failed,
                // which would pin the device at 90% for every other app with no way back.
                // Refuse the irreversible write instead.
                guard let originalVolume = originalVolume else {
                    AppLogger.audio.warning("🎚️ [ASYNC] SKIPPED - device does not report a readable volume; not raising volume (would be unrestorable)")
                    AppLogger.audio.info("🎚️ [ASYNC] Auto-increase mic volume task COMPLETED")
                    return
                }

                // Set to 90% (0.9) to avoid potential clipping at max volume
                AppLogger.audio.info("🎚️ [ASYNC] Attempting to set volume to 90%...")
                let success = CoreAudioDeviceHelper.setInputVolumeScalar(for: deviceID, volume: 0.9)

                if success {
                    // Verify the change actually took effect
                    let newVolume = CoreAudioDeviceHelper.readInputVolumeScalar(for: deviceID)
                    let newStr = newVolume.map { String(format: "%.0f%%", $0 * 100) } ?? "nil"
                    AppLogger.audio.info("🎚️ [ASYNC] SUCCESS - Volume changed from \(originalStr, privacy: .public) → \(newStr, privacy: .public)")

                    // Store original volume + device ID on main actor for restoration after recording
                    await MainActor.run { [weak self] in
                        self?.originalMicVolume = originalVolume
                        self?.originalMicVolumeDeviceID = deviceID
                        AppLogger.audio.info("🎚️ [ASYNC] Stored original volume for restoration")
                    }
                } else {
                    AppLogger.audio.warning("🎚️ [ASYNC] FAILED - CoreAudio setInputVolumeScalar returned false (device may not support software volume control)")
                }

                AppLogger.audio.info("🎚️ [ASYNC] Auto-increase mic volume task COMPLETED")
            }
        }

        // STEP 5: Generate file paths
        // Record directly to WAV at 16kHz mono (Whisper-optimized format)
        // No intermediate CAF or real-time format conversion needed
        let timestamp = Date().timeIntervalSince1970
        let sessionID = UUID()

        // Create session-tagged temp WAV file for crash recovery
        let wavURL = recordingsDirectory.appendingPathComponent(".incomplete_\(sessionID.uuidString).wav")
        self.rawURL = wavURL

        // Final URL will be determined after recording (WAV if small, M4A if large)
        // UUID-based path — avoid second-resolution collisions on rapid retrigger (issue #236)
        let finalURL = recordingsDirectory.appendingPathComponent("recording_\(sessionID.uuidString).wav")
        self.finalURL = finalURL

        // DEFENSIVE: Ensure recordings directory exists before creating audio file
        do {
            try FileManager.default.createDirectory(
                at: self.recordingsDirectory,
                withIntermediateDirectories: true,
                attributes: nil
            )
            AppLogger.audio.debug("Recordings directory ready: \(self.recordingsDirectory.path, privacy: .public)")
        } catch {
            AppLogger.audio.error("Failed to create recordings directory: \(error.localizedDescription)")
        }

        // STEP 6: Start recording with SimpleRecorder
        // AVAudioRecorder handles all buffer management and format conversion internally
        let recorderStartSpan = SentryService.startSpan(operation: "audio.recorder", description: "start AVAudioRecorder")
        do {
            try simpleRecorder.startRecording(to: wavURL)
            SentryService.finishSpan(recorderStartSpan, status: .ok)
        } catch {
            SentryService.finishSpan(recorderStartSpan, status: .internalError)
            throw error
        }

        // STEP 7: Update state and start timer
        isRecording = true
        recordingStartTime = Date()
        startDurationTimer()

        AppLogger.audio.info("Started recording to file: \(wavURL.lastPathComponent, privacy: .public)")

        return finalURL
    }

    /// Schedule Core Data session persistence after the Sentry Recording Start
    /// transaction has finished. The recorder is already live at this point, so
    /// stop/cancel paths await the pending creation before updating or deleting it.
    func persistSessionForActiveRecording() {
        guard let rawURL else {
            AppLogger.audio.error("Cannot create recording session: missing raw recording URL")
            return
        }

        // SimpleRecorder always records at 16kHz mono - this is fixed.
        let deviceId = deviceManager.selectedDevice?.uid ?? "default"
        let deviceName = deviceManager.selectedDevice?.name ?? "audio.device.default".localized
        sessionManager.scheduleRecordingSessionCreation(
            deviceId: deviceId,
            deviceName: deviceName,
            sampleRate: 16000,
            channelCount: 1,
            audioFormat: "WAV PCM 16000Hz 1ch",
            audioFilePath: rawURL.path,
            startTime: recordingStartTime ?? Date()
        )
    }

    /// Remove raw files created before a deferred RecordingSession exists.
    /// This covers recorder-start failures where AVAudioRecorder created the
    /// `.incomplete_*.wav` file but `record()` did not successfully start.
    func cleanupFailedStartArtifacts() {
        if let rawURL, FileManager.default.fileExists(atPath: rawURL.path) {
            do {
                try FileManager.default.removeItem(at: rawURL)
                AppLogger.audio.info("Deleted raw recording file after failed start: \(rawURL.lastPathComponent, privacy: .public)")
            } catch {
                AppLogger.audio.warning("Failed to delete raw recording file after failed start: \(error.localizedDescription)")
            }
        }

        rawURL = nil
        finalURL = nil
        recordingStartTime = nil
        recordingTimer?.invalidate()
        recordingTimer = nil
    }

    /// Stop recording and optionally convert to M4A
    ///
    /// **What This Does:**
    /// 1. Stops SimpleRecorder
    /// 2. Stops duration timer
    /// 3. Renames temp WAV to final name (or converts to M4A if large)
    /// 4. Updates RecordingSession with final file path and duration
    /// 5. Cleans up temporary files and references
    /// 6. Returns final audio URL and duration
    ///
    /// **Architecture:**
    /// Stop SimpleRecorder → Rename WAV (or convert to M4A if >25MB) → Return URL
    ///
    /// **Conversion Strategy:**
    /// - Small files (<25MB): Keep as WAV for reliability
    /// - Large files (>=25MB): Convert to M4A AAC for upload compatibility
    ///
    /// **Returns:**
    /// Optional tuple containing:
    /// - `url`: Final audio file URL (WAV or M4A)
    /// - `duration`: Recording length in seconds
    /// - `conversionWarning`: Optional warning message if compression failed
    /// - `recordingSession`: Core Data session updated for this stop, if available
    /// Returns nil if not currently recording
    func stopRecording(cancelled: Bool = false) async -> (url: URL?, duration: TimeInterval, conversionWarning: String?, recordingSession: RecordingSession?)? {
        guard isRecording else { return nil }

        // Capture the duration before resetting
        let duration = recordingDuration

        // Track any conversion warning to surface to the user
        var conversionWarning: String?

        // STEP 1: Stop SimpleRecorder
        simpleRecorder.stopRecording()
        self.isRecording = false

        // STEP 3: Stop duration timer
        recordingTimer?.invalidate()
        recordingTimer = nil

        // STEP 3.5: Restore audio environment if we muted system audio
        if let state = recordingAudioEnvironmentState {
            audioSessionManager.restoreAudioEnvironment(state)
            AppLogger.audio.info("Audio environment restored after recording")
            self.recordingAudioEnvironmentState = nil
        }

        // STEP 3.6: Restore microphone input volume if we increased it
        // This restores the original volume level that was saved in startRecording()
        // when "Automatically increase microphone volume" was enabled.
        if let volume = originalMicVolume {
            deviceManager.restoreInputVolume(volume, deviceID: originalMicVolumeDeviceID)
            originalMicVolume = nil
            originalMicVolumeDeviceID = nil
        }

        // Update UI state immediately
        await MainActor.run {
            self.audioLevel = 0
            self.recordingDuration = 0
        }

        AppLogger.audio.info("Stopped recording - Duration: \(duration, privacy: .public)s")

        // STEP 4: FILE FINALIZATION
        // When the recording was cancelled (e.g., validation failure like "offline + cloud mode"),
        // skip finalization entirely. The raw file contains no useful audio (just a WAV header),
        // so polling it 40 times and reporting "Audio finalization failed" to Sentry is wasteful.
        // The caller (cancelRecordingWithError) will delete the file and session anyway.
        if cancelled {
            // Clean up the raw file — it has no useful audio data
            if let rawURL = rawURL {
                try? FileManager.default.removeItem(at: rawURL)
            }
            self.finalURL = nil
            rawURL = nil
            return (url: nil, duration: duration, conversionWarning: nil, recordingSession: nil)
        }

        // SimpleRecorder already recorded directly to WAV at 16kHz mono
        // Check file size to decide: keep as WAV or convert to M4A
        //
        // Flow:
        // 1. Check WAV file size
        //    - < 25 MB: Rename from .incomplete to final name, use WAV for transcription
        //    - >= 25 MB: Convert WAV → M4A (retry up to 5 times)
        // 2. Background M4A conversion happens AFTER transcription (in RecordingTranscriptionFlow)
        //    if user has storeAsM4A setting enabled
        if let rawURL = rawURL, let dstURL = finalURL {
            do {
                // Wait for WAV file to be ready (SimpleRecorder just stopped)
                // RECORDING TOO SHORT FIX:
                // If the file is too small (< 5KB), it means recording was stopped before
                // any meaningful audio data was written. This typically happens when:
                // 1. User pressed record and immediately pressed stop
                // 2. Audio device wasn't delivering audio (disconnected, etc.)
                // 3. AVAudioRecorder buffer hadn't flushed yet
                guard await waitForRawFileReady(rawURL) else {
                    // Get detailed file info for diagnostics
                    let fileExists = FileManager.default.fileExists(atPath: rawURL.path)
                    let fileSize = (try? FileManager.default.attributesOfItem(atPath: rawURL.path)[.size] as? Int64) ?? -1

                    AppLogger.audio.error("Raw WAV file not ready or too small: \(rawURL.lastPathComponent, privacy: .public) (exists: \(fileExists), size: \(fileSize) bytes)")

                    if AppLogger.isErrorLoggingEnabled {
                        SentryService.addBreadcrumb(
                            message: "Recording too short - WAV file not ready",
                            category: "audio.recording",
                            level: .warning,
                            data: [
                                "rawPath": rawURL.lastPathComponent,
                                "fileExists": fileExists,
                                "fileSizeBytes": fileSize,
                                "minimumSizeBytes": 5000,
                                "recordingDurationMs": Int(duration * 1000),
                                "sessionId": sessionManager.currentRecordingSession?.id?.uuidString ?? "nil"
                            ]
                        )
                    }

                    throw AudioError.recordingTooShort
                }

                try validateRawWAV(rawURL)

                if AppLogger.isErrorLoggingEnabled {
                    var breadcrumbData: [String: Any] = [
                        "rawPath": rawURL.path,
                        "destPath": dstURL.path,
                        "recordingDurationMs": Int(duration * 1_000)
                    ]
                    if let sessionId = sessionManager.currentRecordingSession?.id?.uuidString {
                        breadcrumbData["sessionId"] = sessionId
                    }
                    SentryService.addBreadcrumb(
                        message: "Processing recorded WAV file",
                        category: "audio.recording",
                        data: breadcrumbData
                    )
                }

                // STEP 4a: Check WAV file size to decide if M4A conversion is needed
                let maxWAVSizeForUpload: Int64 = 25 * 1024 * 1024  // 25 MB
                let wavSize = getFileSize(at: rawURL) ?? 0

                if wavSize < maxWAVSizeForUpload {
                    // WAV is small enough for direct upload - rename from .incomplete to final name
                    // Background M4A conversion will happen after transcription if storeAsM4A is enabled
                    let finalWavURL = dstURL.deletingPathExtension().appendingPathExtension("wav")
                    try FileManager.default.moveItem(at: rawURL, to: finalWavURL)
                    self.finalURL = finalWavURL
                    AppLogger.audio.info("WAV file size (\(wavSize / 1024)KB) is under 25MB - using WAV: \(finalWavURL.lastPathComponent, privacy: .public)")

                    if AppLogger.isErrorLoggingEnabled {
                        SentryService.addBreadcrumb(
                            message: "Using WAV directly (under 25MB threshold)",
                            category: "audio.recording",
                            data: [
                                "wavPath": finalWavURL.path,
                                "wavSizeBytes": wavSize,
                                "sampleRate": 16000,
                                "channels": 1
                            ]
                        )
                    }
                } else {
                    // WAV is too large - must convert to M4A for upload
                    // RELIABILITY FIX: Use two-stage fallback to prevent data loss
                    // Stage 1: Try AVAssetReader/Writer (primary, more control)
                    // Stage 2: Try AVAssetExportSession (fallback, different code path)
                    // Stage 3: Keep WAV as-is (last resort, file may be too large for some APIs)
                    AppLogger.audio.info("WAV file size (\(wavSize / (1024 * 1024))MB) exceeds 25MB - converting to M4A")

                    let m4aURL = dstURL.deletingPathExtension().appendingPathExtension("m4a")
                    let m4aResult = await performM4AConversionWithRetries(from: rawURL, to: m4aURL)

                    if m4aResult.success {
                        self.finalURL = m4aURL
                        // Delete WAV since M4A was created successfully
                        try? FileManager.default.removeItem(at: rawURL)

                        if let metrics = m4aResult.metrics {
                            let kbps = metrics.2 / 1000
                            AppLogger.audio.info(
                                "Compressed to M4A \(Int(metrics.0))Hz \(metrics.1)ch @ \(kbps) kbps (attempt \(m4aResult.attempts)): \(m4aURL.lastPathComponent, privacy: .public)"
                            )
                        }

                        if AppLogger.isErrorLoggingEnabled {
                            SentryService.addBreadcrumb(
                                message: "Converted large WAV to M4A",
                                category: "audio.recording",
                                data: [
                                    "m4aPath": m4aURL.path,
                                    "originalWavSizeBytes": wavSize,
                                    "attempts": m4aResult.attempts
                                ]
                            )
                        }
                    } else {
                        // PRIMARY CONVERSION FAILED - Try AVAssetExportSession fallback
                        // This uses a different code path that may succeed where the primary fails
                        AppLogger.audio.warning("Primary M4A conversion failed after \(m4aResult.attempts) attempts - trying AVAssetExportSession fallback")

                        let exportSessionResult = await performExportSessionFallback(from: rawURL, to: m4aURL)

                        if exportSessionResult.success {
                            // AVAssetExportSession succeeded!
                            self.finalURL = m4aURL
                            try? FileManager.default.removeItem(at: rawURL)

                            AppLogger.audio.info("AVAssetExportSession fallback succeeded: \(m4aURL.lastPathComponent, privacy: .public)")

                            if AppLogger.isErrorLoggingEnabled {
                                SentryService.addBreadcrumb(
                                    message: "AVAssetExportSession fallback succeeded for large WAV",
                                    category: "audio.recording",
                                    data: [
                                        "m4aPath": m4aURL.path,
                                        "originalWavSizeBytes": wavSize,
                                        "primaryAttempts": m4aResult.attempts
                                    ]
                                )
                            }
                        } else {
                            // BOTH CONVERSION METHODS FAILED - Keep WAV to prevent data loss
                            // The WAV may be too large for some APIs, but losing the recording is worse
                            // RecordingTranscriptionFlow will handle the oversized file appropriately
                            AppLogger.audio.warning("All M4A conversion methods failed - keeping oversized WAV to prevent data loss")

                            // Rename the temp WAV to final name
                            let finalWavURL = dstURL.deletingPathExtension().appendingPathExtension("wav")
                            try? FileManager.default.removeItem(at: finalWavURL)
                            try? FileManager.default.moveItem(at: rawURL, to: finalWavURL)
                            self.finalURL = finalWavURL

                            // Set warning to surface to user - the file is oversized and may cause issues
                            let sizeMB = wavSize / (1024 * 1024)
                            conversionWarning = "recording.warning.oversizedWAV".localized(arguments: String(sizeMB))

                            if AppLogger.isErrorLoggingEnabled {
                                SentryService.addBreadcrumb(
                                    message: "All M4A conversions failed - keeping oversized WAV",
                                    category: "audio.recording",
                                    level: .warning,
                                    data: [
                                        "wavPath": finalWavURL.path,
                                        "wavSizeBytes": wavSize,
                                        "primaryAttempts": m4aResult.attempts,
                                        "primaryError": m4aResult.lastError?.localizedDescription ?? "unknown",
                                        "exportSessionError": exportSessionResult.error?.localizedDescription ?? "unknown"
                                    ]
                                )
                            }
                        }
                    }
                }

            } catch {
                // Clear finalURL first — only re-set if a fallback file actually exists.
                // Without this, stopRecording() returns a stale URL pointing to a non-existent file,
                // causing downstream "Audio file missing" errors.
                self.finalURL = nil

                var metadata = collectDiagnostics(rawURL: rawURL, dstURL: dstURL, duration: duration)
                metadata["phase"] = "wav_finalization_error"
                metadata["errorType"] = String(describing: type(of: error))
                metadata["errorCode"] = (error as NSError).code
                metadata["errorDescription"] = (error as NSError).localizedDescription
                metadata["recordingDurationMs"] = Int(duration * 1000)

                // ACCIDENTAL-TAP FILTER (HYPERWHISPER-F1):
                // When the user presses and releases record instantly, finalization
                // throws .recordingTooShort because the raw WAV never grew past its
                // ~4KB header. These recordings are already silently discarded
                // downstream by the 1.0s minimumRecordingDuration filter in
                // RecordingTranscriptionFlow+StopRecording.swift, so capturing them
                // in Sentry is pure noise. Skip Sentry only when the full signature
                // matches (sub-second duration AND header-only WAV); any longer
                // recording that still collapses to header-only is a real bug
                // (device dropout, buffer flush failure) and must remain visible.
                var isAccidentalTap = false
                if let audioError = error as? AudioError {
                    switch audioError {
                    case .recordingTooShort:
                        let rawSize = (metadata["rawSizeBytes"] as? Int64) ?? Int64.max
                        let durationMs = Int(duration * 1000)
                        if durationMs < 1000 && rawSize < 5000 {
                            isAccidentalTap = true
                            metadata["phase"] = "wav_file_not_ready_accidental_tap"
                        } else {
                            metadata["phase"] = "wav_file_not_ready"
                        }
                    case .noMicrophoneAvailable:
                        metadata["phase"] = "no_microphone_available"
                    default:
                        break
                    }
                }

                if isAccidentalTap {
                    // Log locally at warning level; skip Sentry capture.
                    AppLogger.audio.warning("Audio finalization: recording too short (accidental tap): \(error.localizedDescription, privacy: .public)")
                } else {
                    AppLogger.logAudioError(
                        "Audio finalization failed",
                        error: error,
                        metadata: metadata
                    )
                }

                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Audio finalization error",
                        category: "audio.recording",
                        level: isAccidentalTap ? .info : .error,
                        data: metadata
                    )
                }

                // Last resort: use the raw WAV if it still exists
                if FileManager.default.fileExists(atPath: rawURL.path) {
                    let finalWavURL = dstURL.deletingPathExtension().appendingPathExtension("wav")
                    do {
                        if finalWavURL != rawURL {
                            try? FileManager.default.removeItem(at: finalWavURL)
                            try FileManager.default.moveItem(at: rawURL, to: finalWavURL)
                            AppLogger.audio.warning("Using raw WAV as last resort: \(finalWavURL.lastPathComponent, privacy: .public)")
                            self.finalURL = finalWavURL
                        } else {
                            self.finalURL = rawURL
                        }
                    } catch {
                        AppLogger.audio.error("Failed to move WAV fallback file: \(error.localizedDescription)")
                        self.finalURL = rawURL
                    }
                }
            }
        }

        // STEP 5: Clear references
        rawURL = nil

        // STEP 6: Resolve the session even when finalization failed. Otherwise
        // a deferred create can leave an orphaned row pointing at an incomplete file.
        var stoppedRecordingSession: RecordingSession?
        if let session = await sessionManager.resolveCurrentSession() {
            if let fileURL = finalURL {
                stoppedRecordingSession = session

                // Compute the human-readable audio format label for the final output.
                let formatLabel: String
                if let (sr, ch) = audioFileConverter.getAudioFormatInfo(url: fileURL) {
                    let ext = fileURL.pathExtension.lowercased()
                    let base: String
                    switch ext {
                    case "m4a":
                        base = "M4A AAC"
                    case "wav":
                        base = "WAV PCM"
                    case "caf":
                        base = "CAF PCM"
                    default:
                        base = ext.uppercased()
                    }
                    formatLabel = "\(base) \(Int(sr))Hz \(ch)ch"
                } else {
                    let ext = fileURL.pathExtension.uppercased()
                    formatLabel = ext.isEmpty ? "Audio (unknown)" : "\(ext) (unknown)"
                }

                // ONE serial write: final path + duration + endTime + audioFormat.
                await PersistenceController.shared.updateRecordingSessionOnStopInBackground(
                    sessionID: session.objectID,
                    audioFilePath: fileURL.path,
                    duration: duration,
                    audioFormat: formatLabel
                )
                sessionManager.clearCurrentSession()
                AppLogger.audio.info("✅ Updated recording session: duration=\(String(format: "%.1f", duration))s, path=\(fileURL.path)")
            } else {
                await sessionManager.deleteSession(session)
            }
        }

        // STEP 7: Store for retry functionality and return
        // DEFENSIVE FIX: Capture return value before clearing state
        let result: (url: URL?, duration: TimeInterval, conversionWarning: String?)?
        if let fileURL = finalURL {
            lastRecordingURL = fileURL
            result = (url: fileURL, duration: duration, conversionWarning: conversionWarning)
        } else {
            result = nil
        }

        // STEP 8: Clear state to prevent stale URLs on next recording
        // This fixes the bug where 2nd/3rd recordings would return old M4A paths
        self.finalURL = nil
        // rawURL already cleared at line 445

        guard let result else {
            return nil
        }

        return (
            url: result.url,
            duration: result.duration,
            conversionWarning: result.conversionWarning,
            recordingSession: stoppedRecordingSession
        )
    }

    // MARK: - Conversion Helpers

    private struct ConversionRetryResult {
        let success: Bool
        let attempts: Int
        let metrics: (Double, Int, Int)?
        let lastError: Error?
    }

    /// Ensure the WAV file is readable and contains audio data before processing.
    private func validateRawWAV(_ url: URL) throws {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { throw AudioError.fileNotReadable }
        guard fm.isReadableFile(atPath: url.path) else { throw AudioError.fileNotReadable }

        let attributes = try fm.attributesOfItem(atPath: url.path)
        if let size = attributes[.size] as? NSNumber, size.int64Value == 0 {
            throw AudioError.fileNotReadable
        }

        do {
            let audioFile = try AVAudioFile(forReading: url)
            guard audioFile.length > 0 else { throw AudioError.fileNotReadable }
            let format = audioFile.processingFormat
            guard format.channelCount > 0, format.sampleRate > 0 else { throw AudioError.fileNotReadable }
        } catch {
            throw AudioError.fileNotReadable
        }
    }

    /// Collect filesystem diagnostics for telemetry logging.
    private func collectDiagnostics(rawURL: URL, dstURL: URL, duration: TimeInterval) -> [String: Any] {
        let fm = FileManager.default
        let rawExists = fm.fileExists(atPath: rawURL.path)
        let rawReadable = fm.isReadableFile(atPath: rawURL.path)
        let dstExists = fm.fileExists(atPath: dstURL.path)
        let rawSize = (try? fm.attributesOfItem(atPath: rawURL.path)[.size] as? Int64) ?? -1

        var freeBytes: Int64 = -1
        if let values = try? rawURL.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]),
           let capacity = values.volumeAvailableCapacityForImportantUsage {
            freeBytes = capacity
        }

        var diagnostics: [String: Any] = [
            "rawURL": rawURL.path,
            "dstURL": dstURL.path,
            "rawExists": rawExists,
            "rawReadable": rawReadable,
            "dstExists": dstExists,
            "rawSizeBytes": rawSize,
            "volumeFreeBytes": freeBytes,
            "recordingDurationMs": Int(duration * 1_000),
            "sessionId": sessionManager.currentRecordingSession?.id?.uuidString ?? "nil"
        ]

        for (key, value) in collectInputDeviceDiagnostics() {
            diagnostics[key] = value
        }

        return diagnostics
    }

    private func collectInputDeviceDiagnostics(availableDevices: [AudioDevice]? = nil) -> [String: Any] {
        let devices = availableDevices ?? CoreAudioDeviceHelper.fetchCoreAudioInputDevices()
        let selectedDevice = deviceManager.selectedDevice
        let activeDeviceUID = deviceManager.activeInputDeviceIdentifier ?? selectedDevice?.uid ?? deviceManager.systemDefaultDeviceUID

        var diagnostics: [String: Any] = [
            "availableInputDeviceCount": devices.count,
            "availableInputDevices": summarizeDevices(devices, maxDevices: 20),
            "selectedInputDeviceName": selectedDevice?.name ?? "system_default",
            "selectedInputDeviceUID": selectedDevice?.uid ?? "system_default",
            "activeInputDeviceName": deviceManager.activeInputDeviceName,
            "activeInputDeviceUID": activeDeviceUID ?? "unknown",
            "systemDefaultInputDeviceUID": deviceManager.systemDefaultDeviceUID ?? "unknown",
            "inputVolumeScalar": deviceManager.inputVolumeScalar ?? -1
        ]

        if let selectedDevice,
           let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: selectedDevice.uid) {
            diagnostics["selectedInputDeviceTransportType"] = CoreAudioDeviceHelper.transportTypeString(for: deviceID) ?? "unknown"
        }

        if let activeDeviceUID,
           let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: activeDeviceUID) {
            diagnostics["activeInputDeviceTransportType"] = CoreAudioDeviceHelper.transportTypeString(for: deviceID) ?? "unknown"
        }

        return diagnostics
    }

    private func summarizeDevices(_ devices: [AudioDevice], maxDevices: Int) -> [String] {
        devices.prefix(maxDevices).map { device in
            let transport: String
            if let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: device.uid),
               let transportType = CoreAudioDeviceHelper.transportTypeString(for: deviceID) {
                transport = transportType
            } else {
                transport = "unknown"
            }

            return "\(device.name) (\(transport))"
        }
    }

    /// Polls until the raw WAV file exists and has meaningful audio content.
    ///
    /// **Minimum Size Validation:**
    /// A 16kHz mono 16-bit WAV file requires:
    /// - 44 bytes for standard WAV header
    /// - 32,000 bytes per second of audio (16,000 samples × 2 bytes)
    ///
    /// We require at least 5,000 bytes (~0.15 seconds of audio) to ensure
    /// the file contains actual audio data, not just the header.
    ///
    /// **Why This Matters:**
    /// AVAudioRecorder creates the file immediately with header/buffer (often 4096 bytes),
    /// but audio data is written asynchronously. If stopRecording() is called too quickly,
    /// the file may exist but contain no audio frames.
    private func waitForRawFileReady(_ url: URL) async -> Bool {
        let fm = FileManager.default

        // Minimum file size to contain meaningful audio
        // 44 bytes header + ~0.15 seconds of 16kHz mono 16-bit audio
        let minimumAudioFileSize: Int64 = 5_000

        // Tiered backoff: 1-10 at 25ms, 11-20 at 100ms, 21-30 at 250ms
        // Total max wait ~3.75s with fewer polls than the old 40x50ms approach
        let maxAttempts = 30
        // Wall-time bound for the stalled-size early exit: if the file has a
        // nonzero but sub-threshold size that stops growing, bail once we've
        // waited this long rather than polling all the way to ~3.75s.
        let stalledExitWallMs = 400
        var totalWaitMs = 0
        var lastSize: Int64 = -1
        // Consecutive polls where the size held steady. A single stalled read can
        // just be a buffer that hasn't flushed yet, so we require 2 in a row
        // before bailing (worst-case exit ~650ms instead of ~450ms — still far
        // under the old ~3.75s full wait).
        var stalledStableReads = 0
        var loggedTooSmall = false

        for attempt in 1...maxAttempts {
            if fm.fileExists(atPath: url.path),
               let attributes = try? fm.attributesOfItem(atPath: url.path),
               let sizeNumber = attributes[.size] as? NSNumber {
                let fileSize = sizeNumber.int64Value

                if fileSize >= minimumAudioFileSize {
                    if attempt > 1 {
                        AppLogger.audio.debug("Raw audio file ready after \(attempt) checks, \(totalWaitMs)ms (\(fileSize) bytes): \(url.lastPathComponent, privacy: .public)")
                    }
                    return true
                }

                // Log "too small" warning once when first detected
                if !loggedTooSmall && attempt >= 10 {
                    loggedTooSmall = true
                    AppLogger.audio.warning("Raw audio file still too small at check \(attempt): \(fileSize) bytes (minimum: \(minimumAudioFileSize))")
                }

                // Stalled-size early exit (wall-time bound): if the file has a
                // nonzero but sub-threshold size that hasn't grown for 2
                // consecutive polls, and we've already waited past ~400ms, it
                // isn't going to grow — bail now.
                if fileSize > 0, fileSize == lastSize {
                    stalledStableReads += 1
                } else {
                    stalledStableReads = 0
                }
                if stalledStableReads >= 2, totalWaitMs >= stalledExitWallMs {
                    AppLogger.audio.warning("Raw audio file size stalled at \(fileSize) bytes past \(totalWaitMs)ms, giving up after \(attempt) attempts")
                    break
                }
                lastSize = fileSize
            }

            let sleepMs: Int
            switch attempt {
            case 1...10: sleepMs = 25
            case 11...20: sleepMs = 100
            default: sleepMs = 250
            }
            totalWaitMs += sleepMs
            try? await Task.sleep(nanoseconds: UInt64(sleepMs) * 1_000_000)
        }

        // Log final file state for diagnostics
        let fileExists = fm.fileExists(atPath: url.path)
        let finalSize: Int64
        if let attributes = try? fm.attributesOfItem(atPath: url.path),
           let sizeNumber = attributes[.size] as? NSNumber {
            finalSize = sizeNumber.int64Value
            AppLogger.audio.error("Raw audio file failed size check after \(totalWaitMs)ms: \(finalSize) bytes (minimum: \(minimumAudioFileSize))")
        } else {
            finalSize = -1
            AppLogger.audio.error("Raw audio file does not exist or unreadable: \(url.lastPathComponent, privacy: .public)")
        }

        // Add Sentry breadcrumb with detailed failure info
        if AppLogger.isErrorLoggingEnabled {
            SentryService.addBreadcrumb(
                message: "waitForRawFileReady failed",
                category: "audio.recording",
                level: .warning,
                data: [
                    "fileName": url.lastPathComponent,
                    "fileExists": fileExists,
                    "finalSizeBytes": finalSize,
                    "minimumSizeBytes": minimumAudioFileSize,
                    "attemptsUsed": maxAttempts,
                    "totalWaitMs": totalWaitMs
                ]
            )
        }

        return false
    }

    /// Get file size in bytes for a given URL
    ///
    /// - Parameter url: The file URL to check
    /// - Returns: File size in bytes, or nil if unable to determine
    private func getFileSize(at url: URL) -> Int64? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber else {
            return nil
        }
        return size.int64Value
    }

    /// Attempt M4A conversion from WAV source with up to 5 retries
    ///
    /// This is used when a WAV file exceeds 25MB and needs to be compressed
    /// for upload to transcription APIs. Uses exponential backoff between retries.
    ///
    /// - Parameters:
    ///   - sourceURL: The WAV file to convert
    ///   - destinationURL: Where to save the M4A file
    /// - Returns: ConversionRetryResult with success status and metrics
    private nonisolated func performM4AConversionWithRetries(
        from sourceURL: URL,
        to destinationURL: URL
    ) async -> ConversionRetryResult {
        let maxAttempts = 5
        var lastError: Error?

        for attempt in 1...maxAttempts {
            do {
                AppLogger.audio.info("M4A conversion attempt \(attempt)/\(maxAttempts)")

                // Remove any existing destination file
                try? FileManager.default.removeItem(at: destinationURL)

                let result = try await audioFileConverter.convertAudioToAAC(
                    from: sourceURL,
                    to: destinationURL
                )

                return ConversionRetryResult(
                    success: true,
                    attempts: attempt,
                    metrics: result,
                    lastError: nil
                )
            } catch {
                lastError = error
                AppLogger.audio.warning("M4A conversion attempt \(attempt) failed: \(error.localizedDescription, privacy: .public)")

                if attempt < maxAttempts {
                    // Exponential backoff: 100ms, 200ms, 400ms, 800ms
                    let delayMs = 100 * (1 << (attempt - 1))
                    try? await Task.sleep(nanoseconds: UInt64(delayMs) * 1_000_000)
                }
            }
        }

        AppLogger.audio.error("M4A conversion failed after \(maxAttempts) attempts")
        return ConversionRetryResult(
            success: false,
            attempts: maxAttempts,
            metrics: nil,
            lastError: lastError
        )
    }

    /// Result type for AVAssetExportSession fallback conversion
    private struct ExportSessionFallbackResult {
        let success: Bool
        let error: Error?
    }

    /// Attempt M4A conversion using AVAssetExportSession as a fallback
    ///
    /// **Purpose:**
    /// Provides a second chance for M4A conversion when the primary AVAssetReader/Writer
    /// approach fails. AVAssetExportSession uses different internal code paths and may
    /// succeed in cases where the lower-level approach fails.
    ///
    /// **Why This Fallback:**
    /// Some audio configurations can cause AVAssetWriter to fail even with valid audio.
    /// Having two independent conversion methods significantly increases reliability.
    ///
    /// - Parameters:
    ///   - sourceURL: The WAV file to convert
    ///   - destinationURL: Where to save the M4A file
    /// - Returns: ExportSessionFallbackResult with success status
    private nonisolated func performExportSessionFallback(
        from sourceURL: URL,
        to destinationURL: URL
    ) async -> ExportSessionFallbackResult {
        do {
            AppLogger.audio.info("Attempting AVAssetExportSession fallback conversion")

            // Remove any partial file from previous attempt
            try? FileManager.default.removeItem(at: destinationURL)

            _ = try await audioFileConverter.convertAudioToM4AWithExportSession(
                from: sourceURL,
                to: destinationURL
            )

            // Verify output file exists
            guard FileManager.default.fileExists(atPath: destinationURL.path) else {
                return ExportSessionFallbackResult(success: false, error: AudioError.exportFailed)
            }

            return ExportSessionFallbackResult(success: true, error: nil)
        } catch {
            AppLogger.audio.warning("AVAssetExportSession fallback failed: \(error.localizedDescription, privacy: .public)")
            return ExportSessionFallbackResult(success: false, error: error)
        }
    }

    // MARK: - Duration Timer

    /// Start timer to update recording duration
    ///
    /// **What This Does:**
    /// Creates a 0.1 second timer that updates recordingDuration based on
    /// elapsed time since recordingStartTime.
    ///
    /// **Why 0.1 seconds:**
    /// Provides smooth updates for UI without excessive timer overhead.
    private func startDurationTimer() {
        recordingTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            guard let self = self, let start = self.recordingStartTime else { return }
            self.recordingDuration = Date().timeIntervalSince(start)
        }
    }

    // MARK: - Background M4A Conversion

    /// Perform background WAV→M4A conversion after successful transcription
    ///
    /// **Purpose:**
    /// Converts WAV files to M4A for storage efficiency AFTER transcription completes.
    /// This ensures that transcription succeeds first (using the reliable WAV file),
    /// then we optimize storage by converting to compressed M4A.
    ///
    /// **Flow:**
    /// 1. Check if file is WAV (skip if already M4A or other format)
    /// 2. Convert WAV → M4A using AAC codec
    /// 3. On success: Update transcript's audioFilePath in Core Data, delete WAV
    /// 4. On failure: Keep WAV file (transcription already succeeded, so no data loss)
    ///
    /// **Important:**
    /// This is called from RecordingTranscriptionFlow AFTER transcription succeeds.
    /// It runs in a detached task to avoid blocking the UI.
    ///
    /// - Parameters:
    ///   - transcriptID: The Transcript object ID to update with the new file path
    ///   - wavURL: The WAV file to convert
    nonisolated func performBackgroundWAVToM4AConversion(transcriptID: NSManagedObjectID, wavURL: URL) async {
        // Only process WAV files
        guard wavURL.pathExtension.lowercased() == "wav" else {
            AppLogger.audio.debug("Skipping background M4A conversion - file is not WAV: \(wavURL.lastPathComponent, privacy: .public)")
            return
        }

        let m4aURL = wavURL.deletingPathExtension().appendingPathExtension("m4a")

        // RELIABILITY FIX: Use two-stage conversion with fallback
        // Stage 1: Try primary AVAssetReader/Writer (more control, usually works)
        // Stage 2: Try AVAssetExportSession fallback (different code path)
        // This mirrors the large-file conversion flow for consistency

        AppLogger.audio.info("Starting background WAV→M4A conversion: \(wavURL.lastPathComponent, privacy: .public)")

        // Remove any existing M4A file at destination
        try? FileManager.default.removeItem(at: m4aURL)

        // Stage 1: Try primary converter
        do {
            let (sampleRate, channels, bitrate) = try await audioFileConverter.convertAudioToAAC(
                from: wavURL,
                to: m4aURL
            )

            // Verify M4A was created successfully
            guard FileManager.default.fileExists(atPath: m4aURL.path) else {
                throw AudioError.exportFailed
            }

            // Update transcript with new path on the serial background writer.
            await PersistenceController.shared.updateTranscriptAudioFilePathInBackground(transcriptID: transcriptID, newPath: m4aURL.path)

            // Delete the WAV file since M4A was created successfully
            try? FileManager.default.removeItem(at: wavURL)

            let kbps = bitrate / 1000
            AppLogger.audio.info("Background WAV→M4A conversion succeeded: \(Int(sampleRate))Hz \(channels)ch @ \(kbps) kbps → \(m4aURL.lastPathComponent, privacy: .public)")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Background WAV→M4A conversion succeeded",
                    category: "audio.recording",
                    data: [
                        "m4aPath": m4aURL.path,
                        "sampleRate": sampleRate,
                        "channels": channels,
                        "bitrate": bitrate
                    ]
                )
            }
            return // Success - exit early

        } catch {
            AppLogger.audio.warning("Background primary M4A conversion failed: \(error.localizedDescription, privacy: .public) - trying fallback")
        }

        // Stage 2: Try AVAssetExportSession fallback
        do {
            // Remove any partial file from failed attempt
            try? FileManager.default.removeItem(at: m4aURL)

            _ = try await audioFileConverter.convertAudioToM4AWithExportSession(
                from: wavURL,
                to: m4aURL
            )

            // Verify M4A was created successfully
            guard FileManager.default.fileExists(atPath: m4aURL.path) else {
                throw AudioError.exportFailed
            }

            // Update transcript with new path on the serial background writer.
            await PersistenceController.shared.updateTranscriptAudioFilePathInBackground(transcriptID: transcriptID, newPath: m4aURL.path)

            // Delete the WAV file since M4A was created successfully
            try? FileManager.default.removeItem(at: wavURL)

            AppLogger.audio.info("Background WAV→M4A conversion succeeded via fallback: \(m4aURL.lastPathComponent, privacy: .public)")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Background WAV→M4A conversion succeeded (fallback)",
                    category: "audio.recording",
                    data: ["m4aPath": m4aURL.path]
                )
            }

        } catch {
            // Both methods failed - keep WAV file, transcription already succeeded
            AppLogger.audio.warning("Background M4A conversion failed (both methods), keeping WAV: \(error.localizedDescription, privacy: .public)")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Background WAV→M4A conversion failed (both methods)",
                    category: "audio.recording",
                    level: .warning,
                    data: [
                        "wavPath": wavURL.path,
                        "fallbackError": error.localizedDescription
                    ]
                )
            }
        }
    }
}
