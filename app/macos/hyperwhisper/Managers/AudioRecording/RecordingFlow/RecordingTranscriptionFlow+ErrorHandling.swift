//
//  RecordingTranscriptionFlow+ErrorHandling.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import CoreData
import Foundation
import KeyboardShortcuts

/// The CoreAudio-derived half of `recordingStartFailureMetadata(error:)`, gathered in one
/// hop off the main actor.
///
/// File scope, not nested in `RecordingTranscriptionFlow`: that type is `@MainActor`, and
/// a nested type would inherit that isolation and could not be produced from a detached
/// block. Same rationale as `AudioDeviceScanSnapshot` in `AudioDeviceManager`.
///
/// `nil` on the optional fields means "do not write this key at all", preserving exactly
/// which keys the synchronous version emitted.
private struct RecordingStartDeviceProbe: Sendable {
    let selectedDeviceTransportType: String?
    let activeDeviceTransportType: String?
    let inputStreamFormat: CoreAudioDeviceHelper.AudioStreamFormatInfo?
    let availableDeviceSummaries: [String]
}

/// Read every CoreAudio property `recordingStartFailureMetadata(error:)` needs, off the
/// main actor.
///
/// **Why this is not "already off the main actor" (Sentry HYPERWHISPER-F7):**
/// `RecordingTranscriptionFlow` is `@MainActor`, and these are blocking `mach_msg` round
/// trips to `coreaudiod` — the device summary alone is N x (1 + N) of them for N devices,
/// because it resolves an `AudioDeviceID` per device and then reads a transport type per
/// ID. This runs from the recording-start failure handler, which fires *precisely* when
/// `coreaudiod` is unresponsive: the user gets `noMicrophoneAvailable` after the 3.2 s
/// retry schedule and the failure handler then blocks the main thread again on the same
/// sick daemon. Reporting a hang must not extend it.
///
/// All inputs are captured on the main actor by the caller and are `Sendable`.
private func probeRecordingStartDeviceMetadata(
    selectedUID: String?,
    activeUID: String?,
    availableDevices: [AudioDevice],
    maxDevices: Int
) async -> RecordingStartDeviceProbe {
    await offMainActor { () -> RecordingStartDeviceProbe in
        // Only emitted when the UID resolves to a live device, matching the previous
        // behaviour: an unresolvable selection writes no key, a resolvable device with an
        // unreadable transport type writes "unknown".
        let selectedTransport: String? = selectedUID
            .flatMap { CoreAudioDeviceHelper.findAudioDeviceID(byUID: $0) }
            .map { CoreAudioDeviceHelper.transportTypeString(for: $0) ?? "unknown" }

        let activeDeviceID = activeUID
            .flatMap { CoreAudioDeviceHelper.findAudioDeviceID(byUID: $0) }
            ?? CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()

        let summaries = availableDevices.prefix(maxDevices).map { device -> String in
            let transport: String
            if let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: device.uid),
               let transportType = CoreAudioDeviceHelper.transportTypeString(for: deviceID) {
                transport = transportType
            } else {
                transport = "unknown"
            }
            return "\(device.name) (\(transport))"
        }

        return RecordingStartDeviceProbe(
            selectedDeviceTransportType: selectedTransport,
            activeDeviceTransportType: activeDeviceID.flatMap { CoreAudioDeviceHelper.transportTypeString(for: $0) },
            inputStreamFormat: activeDeviceID.flatMap { CoreAudioDeviceHelper.copyInputStreamFormat(for: $0) },
            availableDeviceSummaries: summaries
        )
    }
}

extension RecordingTranscriptionFlow {

    // MARK: - Error Handling

    /// Retry transcription using a previously recorded audio file that failed before transcription started
    func retryPendingFile() {
        toggleTask?.cancel()
        toggleTask = Task {
            await retryTranscriptionFromPendingPath()
        }
    }

    private func retryTranscriptionFromPendingPath() async {
        guard
            let appState = appState,
            let path = appState.pendingRetryAudioPath
        else { return }

        let audioURL = URL(fileURLWithPath: path)
        let exists = FileManager.default.fileExists(atPath: audioURL.path)
        let readable = FileManager.default.isReadableFile(atPath: audioURL.path)

        guard exists && readable else {
            await MainActor.run {
                appState.pendingRetryAudioPath = nil
                appState.recordingState = .idle
                appState.lastTranscription = "Error: \("recording.retry.failed.missing".localized)"
                appState.showRecordingDialog = true
            }
            return
        }

        let actualMode = activeSessionModeName
        let transcriptionMode = await PersistenceController.shared.resolveTranscriptionModeInBackground(
            id: activeSessionModeId,
            fallbackName: actualMode
        )

        await MainActor.run {
            appState.recordingState = .transcribing
            appState.showRecordingDialog = true
        }

        do {
            guard let transcriptionMgr = transcriptionPipeline else {
                throw AudioError.noTranscriptionPipeline
            }

            let transcriptionResult = try await transcriptionMgr.transcribeWithDetails(
                audioURL: audioURL,
                mode: transcriptionMode,
                recordingSession: nil,
                applicationContext: capturedApplicationContext
            )

            await MainActor.run {
                appState.lastTranscription = transcriptionResult.text
                appState.recordingState = .idle
                appState.pendingRetryAudioPath = nil
            }
            clearActiveSessionMode()
        } catch {
            await MainActor.run {
                appState.recordingState = .idle
                appState.lastTranscription = "Error: \(error.localizedDescription)"
                appState.showRecordingDialog = true
            }
        }
    }

    /// Handle recording start failures
    func handleRecordingStartFailure(_ error: Error) async {
        let (message, microphoneInUse) = messageForRecordingStartError(error)

        if error is CancellationError {
            AppLogger.audio.info("Recording start cancelled: \(error.localizedDescription)")
        } else if microphoneInUse {
            AppLogger.audio.warning("Recording start blocked: microphone busy · error: \(error.localizedDescription)")
        } else {
            let metadata = await recordingStartFailureMetadata(error: error)
            AppLogger.logAudioError("Failed to start recording", error: error, metadata: metadata)
        }

        powerActivityManager.endPowerActivity()
        AccessibilityHelper.shared.endRecordingSession()
        await cleanupFailedRecordingAttempt()
        clearActiveSessionMode()

        appState?.recordingState = .idle
        appState?.showRecordingDialog = false
        appState?.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag
        appState?.showError(message)
        currentRecordingAttemptId = nil
        currentRecordingTriggerSource = .unknown
        sessionStartedWithTextDeliverySuppressed = false
        quickCaptureContext = nil
    }

    /// Build the Sentry metadata for a recording-start failure.
    ///
    /// `async` because the CoreAudio half of it is gathered by
    /// `probeRecordingStartDeviceMetadata(...)` off the main actor — see that function for
    /// why. Everything read from `self` here is main-actor state and stays on the main
    /// actor; only resolved UIDs and the device roster cross over.
    private func recordingStartFailureMetadata(error: Error) async -> [String: Any] {
        var metadata: [String: Any] = [:]

        metadata["recordingAttemptId"] = currentRecordingAttemptId ?? "none"
        metadata["recordingTriggerSource"] = currentRecordingTriggerSource.rawValue
        metadata["permissionStatus"] = permissionManager.currentAuthorizationStatusString()
        metadata["hasMicrophonePermission"] = permissionManager.hasMicrophonePermission
        metadata["recordingLifecycleHasPermission"] = recordingLifecycle.hasMicrophonePermission

        let selectedDevice = recordingLifecycle.deviceManager.selectedDevice
        metadata["selectedDeviceName"] = selectedDevice?.name ?? "system_default"
        metadata["selectedDeviceUID"] = selectedDevice?.uid ?? "system_default"

        let recordingsFolder = settingsManager?.recordingsFolder ?? ""
        metadata["recordingsFolder"] = recordingsFolder
        if recordingsFolder.isEmpty {
            metadata["recordingsFolderWritable"] = false
            metadata["recordingsFolderExists"] = false
        } else {
            metadata["recordingsFolderWritable"] = FileManager.default.isWritableFile(atPath: recordingsFolder)
            var isDir: ObjCBool = false
            let exists = FileManager.default.fileExists(atPath: recordingsFolder, isDirectory: &isDir)
            metadata["recordingsFolderExists"] = exists
            metadata["recordingsFolderIsDirectory"] = isDir.boolValue
            if let attrs = try? FileManager.default.attributesOfFileSystem(forPath: recordingsFolder),
               let freeBytes = attrs[.systemFreeSize] as? NSNumber {
                metadata["recordingsFolderFreeBytes"] = freeBytes.int64Value
            }
        }

        metadata["isStreamingShortcutTriggered"] = appState?.isStreamingShortcutTriggered ?? false
        metadata["recordingLifecycleIsRecording"] = recordingLifecycle.isRecording
        metadata["toggleTaskCancelled"] = toggleTask?.isCancelled ?? false
        metadata["recordingState"] = safeRecordingStateLabel(appState?.recordingState)

        let mediaControlMode = settingsManager?.audio.mediaControlMode.rawValue ?? "unknown"
        metadata["mediaControlMode"] = mediaControlMode
        metadata["autoIncreaseMicVolume"] = settingsManager?.autoIncreaseMicVolume ?? false

        let deviceManager = recordingLifecycle.deviceManager
        let systemDefaultUID = deviceManager.systemDefaultDeviceUID
        let activeUID = deviceManager.activeInputDeviceIdentifier ?? selectedDevice?.uid ?? systemDefaultUID
        metadata["systemDefaultDeviceUID"] = systemDefaultUID ?? "unknown"
        metadata["activeDeviceName"] = deviceManager.activeInputDeviceName
        metadata["activeDeviceUID"] = activeUID ?? "unknown"
        metadata["activeDeviceIsDefault"] = (activeUID != nil && activeUID == systemDefaultUID)

        let availableDevices = deviceManager.availableDevices
        metadata["availableInputDeviceCount"] = availableDevices.count

        // The blocking CoreAudio reads, in one hop off the main actor. This handler runs
        // when coreaudiod is already unresponsive, so gathering diagnostics must not add
        // another main-thread stall to the hang it is describing.
        let deviceProbe = await probeRecordingStartDeviceMetadata(
            selectedUID: selectedDevice?.uid,
            activeUID: activeUID,
            availableDevices: availableDevices,
            maxDevices: 20
        )

        if let selectedTransport = deviceProbe.selectedDeviceTransportType {
            metadata["selectedDeviceTransportType"] = selectedTransport
        }
        if let transport = deviceProbe.activeDeviceTransportType {
            metadata["activeDeviceTransportType"] = transport
        }
        if let format = deviceProbe.inputStreamFormat {
            metadata["inputSampleRateHz"] = format.sampleRate
            metadata["inputChannelCount"] = format.channels
            metadata["inputBitDepth"] = format.bitDepth
            metadata["inputIsFloat"] = format.isFloat
        }
        metadata["availableInputDevices"] = deviceProbe.availableDeviceSummaries

        metadata["recordingFailureStage"] = recordingFailureStage(for: error)

        let nsError = error as NSError
        metadata["errorDomain"] = nsError.domain
        metadata["errorCode"] = nsError.code
        metadata["errorDescription"] = nsError.localizedDescription
        if let failureReason = nsError.userInfo[NSLocalizedFailureReasonErrorKey] as? String {
            metadata["errorFailureReason"] = failureReason
        }

        return metadata
    }

    private func recordingFailureStage(for error: Error) -> String {
        if error is CancellationError {
            return "cancelled"
        }

        if let audioError = error as? AudioError {
            switch audioError {
            case .noMicrophoneAvailable:
                return "no_microphone"
            case .recordingFailed(let reason):
                if reason == "Failed to start recording" {
                    return "record_start_failed"
                }
                return "recorder_init_failed"
            default:
                return "audio_error"
            }
        }

        return "unknown"
    }

    private func safeRecordingStateLabel(_ state: RecordingState?) -> String {
        guard let state else { return "unknown" }
        switch state {
        case .idle:
            return "idle"
        case .recording:
            return "recording"
        case .processing:
            return "processing"
        case .transcribing:
            return "transcribing"
        case .postProcessing:
            return "post_processing"
        case .complete:
            return "complete"
        case .error:
            return "error"
        }
    }

    /// Clean up after failed recording start
    ///
    /// **What This Does:**
    /// Removes all resources created during failed recording attempt:
    /// 1. Delete the RecordingSession entity from Core Data
    /// 2. Delete the incomplete .caf file from disk
    /// 3. Restore previous system default input device
    /// 4. Clear transient state (app context, PIDs)
    /// 5. Disable cancel keyboard shortcut
    ///
    /// **Why This Matters:**
    /// A failed recording start still creates a RecordingSession in Core Data
    /// and writes a temp .caf file before the engine starts. Without cleanup:
    /// - Orphaned Core Data entities trigger false crash recovery
    /// - Temp files accumulate on disk
    /// - Device override persists incorrectly
    /// - Cancel shortcut stays active when idle
    private func cleanupFailedRecordingAttempt() async {
        // STEP 1: Delete incomplete recording session from Core Data
        // This also removes the associated .incomplete_*.caf file
        await recordingLifecycle.sessionManager.deleteCurrentSession()
        recordingLifecycle.cleanupFailedStartArtifacts()

        // STEP 2: Clear transient state
        capturedApplicationContext = nil
        previousFrontmostPID = nil
        previousFrontmostBundleID = nil

        // STEP 3: Disable cancel shortcut
        KeyboardShortcuts.disable(.cancelRecording)
        appState?.showCancelConfirmation = false

        AppLogger.audio.debug("🧹 Cleaned up failed recording attempt")
    }

    /// Map errors to user-friendly messages
    private func messageForRecordingStartError(_ error: Error) -> (message: String, microphoneInUse: Bool) {
        if let audioError = error as? AudioError {
            return (audioError.localizedDescription, false)
        }

        let nsError = error as NSError
        let domain = nsError.domain

        if domain == NSOSStatusErrorDomain ||
            domain == NSPOSIXErrorDomain ||
            domain == "com.apple.coreaudio.avfaudio" ||
            domain == "AVAudioSessionErrorDomain" {
            return ("audio.error.microphoneInUse".localized, true)
        }

        return (error.localizedDescription, false)
    }

    /// Handle transcription errors with appropriate UI updates.
    ///
    /// UI state is updated on the main actor FIRST (mirroring the success path) so
    /// the error surfaces immediately even when the serial writer is busy; the
    /// failed-status write then goes to the background writer via the transcript's
    /// object ID. For the retry reference we resolve the now-failed transcript on
    /// the view context AFTER awaiting the writer (auto-merge has applied the
    /// failed status by then).
    func handleTranscriptionError(_ error: Error, processingTranscriptID: NSManagedObjectID?, mode: String, duration: TimeInterval, audioURL: URL) {
        // HYPERWHISPER-EX: `TranscriptionPipeline` deliberately excludes
        // `.noSpeechDetected` from Sentry capture as "user-recoverable" — which
        // also hides the case where the mic auto-boost silently failed (fire-and-
        // forget task, see `RecordingLifecycle.lastMicBoostFailed`) and the
        // resulting quiet-but-not-silent recording got misclassified as no-speech.
        // That's a capture-quality defect, not the user simply staying silent, so
        // report it distinctly here (this call site isn't covered by the
        // pipeline's blanket exclusion — no duplicate event for the common case).
        if AppLogger.isErrorLoggingEnabled,
           let te = error as? TranscriptionError, case .noSpeechDetected = te,
           recordingLifecycle.lastMicBoostFailed {
            SentryService.capture(
                error: te,
                message: "No speech detected after mic auto-boost failure",
                extras: ["mode": mode, "durationSeconds": duration],
                tags: ["category": "audio", "kind": "no_speech_after_boost_failure"]
            )
        }

        let isNetworkOutage: Bool
        if let transcriptionError = error as? TranscriptionError, case .transientNetwork = transcriptionError {
            isNetworkOutage = true
        } else if let cloudError = error as? HyperWhisperCloudError, case .transientNetwork = cloudError {
            isNetworkOutage = true
        } else if let urlError = error as? URLError {
            switch urlError.code {
            case .notConnectedToInternet, .networkConnectionLost, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed:
                isNetworkOutage = true
            default:
                isNetworkOutage = false
            }
        } else {
            isNetworkOutage = false
        }

        // Special case: streaming interrupted - keep partial text
        if let te = error as? TranscriptionError, case .streamingInterrupted = te {
            Task {
                await MainActor.run {
                    appState?.recordingState = .idle

                    // CRITICAL: Disable cancel shortcut on error
                    KeyboardShortcuts.disable(.cancelRecording)
                    clearActiveSessionMode()

                    powerActivityManager.endPowerActivity()
                }
                if let processingTranscriptID {
                    await PersistenceController.shared.markTranscriptFailedInBackground(
                        transcriptID: processingTranscriptID,
                        failedReason: te.localizedDescription,
                        errorText: "Transcription failed: \(te.localizedDescription)"
                    )
                }
            }
            AppLogger.audio.warning("⚠️ Streaming interrupted; kept partial text on screen")
        } else {
            // Handle generic transcription failure
            Task {
                await MainActor.run {
                    if isNetworkOutage {
                        appState?.errorMessage = ""
                        appState?.showErrorAlert = false
                    } else {
                        appState?.showError(error.localizedDescription)
                    }
                    appState?.recordingState = .idle
                    appState?.lastTranscription = "Error: \(error.localizedDescription)"

                    // CRITICAL: Disable cancel shortcut on error
                    KeyboardShortcuts.disable(.cancelRecording)
                    clearActiveSessionMode()

                    // Sentry capture handled in TranscriptionPipeline to avoid duplicates.

                    powerActivityManager.endPowerActivity()
                }
                if let processingTranscriptID {
                    await PersistenceController.shared.markTranscriptFailedInBackground(
                        transcriptID: processingTranscriptID,
                        failedReason: error.localizedDescription,
                        errorText: "Transcription failed: \(error.localizedDescription)"
                    )
                }
                if !isNetworkOutage, let processingTranscriptID {
                    await MainActor.run {
                        // Store reference to failed transcript for retry.
                        // Resolve on the view context AFTER awaiting the writer, so
                        // auto-merge has applied the failed status by now.
                        // NOTE: `lastFailedTranscript` currently has no readers — the
                        // Retry button uses `pendingRetryAudioPath` — but it's kept
                        // honest for the existing AppState contract.
                        if let failed = (try? PersistenceController.shared.container.viewContext.existingObject(with: processingTranscriptID)) as? Transcript {
                            appState?.lastFailedTranscript = failed
                        }
                    }
                }
            }
            AppLogger.audio.error("❌ Transcription error: \(error)")
        }
    }
}
