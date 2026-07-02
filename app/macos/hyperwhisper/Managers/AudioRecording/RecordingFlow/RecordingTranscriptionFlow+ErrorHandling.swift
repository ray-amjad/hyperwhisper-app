//
//  RecordingTranscriptionFlow+ErrorHandling.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import CoreData
import Foundation
import KeyboardShortcuts

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
                appState.lastTranscription = "Error: Audio file missing for retry"
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
            let metadata = recordingStartFailureMetadata(error: error)
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
        quickCaptureContext = nil
    }

    private func recordingStartFailureMetadata(error: Error) -> [String: Any] {
        var metadata: [String: Any] = [:]

        metadata["recordingAttemptId"] = currentRecordingAttemptId ?? "none"
        metadata["recordingTriggerSource"] = currentRecordingTriggerSource.rawValue
        metadata["permissionStatus"] = permissionManager.currentAuthorizationStatusString()
        metadata["hasMicrophonePermission"] = permissionManager.hasMicrophonePermission
        metadata["recordingLifecycleHasPermission"] = recordingLifecycle.hasMicrophonePermission

        let selectedDevice = recordingLifecycle.deviceManager.selectedDevice
        metadata["selectedDeviceName"] = selectedDevice?.name ?? "system_default"
        metadata["selectedDeviceUID"] = selectedDevice?.uid ?? "system_default"
        if let uid = selectedDevice?.uid,
           let deviceID = CoreAudioDeviceHelper.findAudioDeviceID(byUID: uid) {
            metadata["selectedDeviceTransportType"] = CoreAudioDeviceHelper.transportTypeString(for: deviceID) ?? "unknown"
        }

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

        let activeDeviceID = activeUID
            .flatMap { CoreAudioDeviceHelper.findAudioDeviceID(byUID: $0) }
            ?? CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()
        if let activeDeviceID = activeDeviceID {
            if let transport = CoreAudioDeviceHelper.transportTypeString(for: activeDeviceID) {
                metadata["activeDeviceTransportType"] = transport
            }
            if let format = CoreAudioDeviceHelper.copyInputStreamFormat(for: activeDeviceID) {
                metadata["inputSampleRateHz"] = format.sampleRate
                metadata["inputChannelCount"] = format.channels
                metadata["inputBitDepth"] = format.bitDepth
                metadata["inputIsFloat"] = format.isFloat
            }
        }

        let availableDevices = deviceManager.availableDevices
        metadata["availableInputDeviceCount"] = availableDevices.count
        metadata["availableInputDevices"] = summarizeAvailableDevices(availableDevices, maxDevices: 20)

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

    private func summarizeAvailableDevices(_ devices: [AudioDevice], maxDevices: Int) -> [String] {
        let trimmed = devices.prefix(maxDevices)
        return trimmed.map { device in
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
