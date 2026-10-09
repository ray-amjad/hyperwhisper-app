//
//  RecordingTranscriptionFlow+ErrorHandling.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import CoreData
import Foundation
import KeyboardShortcuts

/// The mode the user picked for a pending-file retry, after the mode that made
/// the recording was deleted (#1617). Plain values, not the Core Data `Mode`,
/// so the pick can cross into the retry's task.
struct PendingRetryModeChoice: Equatable, Sendable {
    let id: String
    let name: String
}

extension RecordingTranscriptionFlow {

    // MARK: - Error Handling

    /// Retry transcription using a previously recorded audio file that failed before transcription started
    /// - Parameter pickedMode: the mode the user chose in the recording dialog's
    ///   picker after the session's mode turned out to be deleted (#1617);
    ///   `nil` retries with the session's own mode, as before.
    func retryPendingFile(with pickedMode: PendingRetryModeChoice? = nil) {
        toggleTask?.cancel()
        // A retry is a session of its own: an older retry still in flight is
        // superseded by this one and writes nothing when it ends (#1276).
        guard let appState = appState else { return }
        appState.pendingRetryNeedsModePick = false
        let identity = PendingRetryIdentity(sessionGeneration: appState.beginTranscriptionSession())
        toggleTask = Task {
            await retryTranscriptionFromPendingPath(identity: identity, pickedMode: pickedMode)
        }
    }

    /// Whether a newer flow (a dictation, a file transcription, another retry)
    /// has started since this retry began. See `PendingRetryIdentity`.
    private func isPendingRetrySuperseded(_ identity: PendingRetryIdentity) -> Bool {
        guard let appState = appState else { return true }
        let superseded = identity.isSuperseded(
            currentSessionGeneration: appState.transcriptionSessionGeneration
        )
        if superseded {
            AppLogger.audio.info("Pending-file retry superseded by a newer flow; leaving shared state to it")
        }
        return superseded
    }

    /// The mode a pending-file retry transcribes with, or `nil` when the user
    /// must pick one first (#1617).
    ///
    /// Looks the mode up by id ONLY, and NEVER falls back to a mode found by
    /// name or to the default mode: on a fresh install the default is the
    /// Cloud mode "Hyper", so a deleted on-device mode would otherwise send
    /// the audio to HyperWhisper Cloud without asking. Same rule as History's
    /// Retry (`TranscriptionRetryController`, #1440). A session mode that
    /// still exists resolves by its id, as before.
    ///
    /// No name step, for the session mode or a PICKED one: if the mode was
    /// deleted, another mode with the same name (maybe a Cloud mode the user
    /// created or imported since) must not stand in for it. An empty session
    /// id (no app state, a cleared selection) has no mode to match either: a
    /// name then ("Default", or "") could only reach some other mode. `nil`
    /// opens the picker and nothing is sent.
    static func resolvePendingRetryMode(
        pickedMode: PendingRetryModeChoice?,
        sessionModeId: String,
        persistence: PersistenceController = .shared
    ) async -> Mode? {
        let id = pickedMode?.id ?? sessionModeId
        guard !id.isEmpty else { return nil }
        return await persistence.resolveTranscriptionModeInBackground(
            id: id,
            fallbackName: "",
            allowNameFallback: false,
            allowDefaultFallback: false
        )
    }

    private func retryTranscriptionFromPendingPath(
        identity: PendingRetryIdentity,
        pickedMode: PendingRetryModeChoice?
    ) async {
        guard !isPendingRetrySuperseded(identity) else { return }

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

        let resolvedMode = await Self.resolvePendingRetryMode(
            pickedMode: pickedMode,
            sessionModeId: activeSessionModeId
        )

        // The mode lookup suspends; a new dictation may have started meanwhile.
        guard !isPendingRetrySuperseded(identity) else { return }

        // #1617: the mode was deleted (or the picked one was deleted since).
        // Send nothing and ask: the dialog shows a mode picker and retries with
        // the user's pick. `pendingRetryAudioPath` is left alone, so the audio
        // is kept whether the user picks a mode or dismisses the picker.
        guard let transcriptionMode = resolvedMode else {
            AppLogger.audio.warning("Pending-file retry: its mode no longer exists; asking the user to pick one, no request sent")
            await MainActor.run {
                appState.pendingRetryNeedsModePick = true
                appState.showRecordingDialog = true
            }
            return
        }

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

            // A newer flow owns the dialog, the state and the session mode now.
            guard !isPendingRetrySuperseded(identity) else { return }

            // #1636: the History row saved for THIS file when it could not be
            // read. Read now, not before the request: its write may have
            // landed while the retry ran.
            let failedTranscriptID = appState.pendingRetryAudioPath == path
                ? appState.pendingRetryTranscriptID
                : nil

            // Deliver like a dictation (paste, clipboard fallback, the pill's
            // "Pasted!" only after a real paste), then save. Before #1636 the
            // retry only set the text: nothing was pasted, copied or saved.
            let pasteStart = Date()
            await MainActor.run {
                appState.lastTranscription = transcriptionResult.text
                appState.recordingState = .idle
                appState.pendingRetryAudioPath = nil
                // The recording's session ended at its stop: no onboarding
                // gate or Quick Capture context of its own is left, and a
                // newer session would have superseded this retry.
                deliverBatchTranscript(
                    transcriptionResult,
                    transcriptionMode: transcriptionMode,
                    sessionStartedSuppressed: false,
                    trigger: .unknown,
                    isQuickCaptureRouting: false,
                    pasteStart: pasteStart
                )
            }
            clearActiveSessionMode()

            let savedTranscriptID = await Self.savePendingRetryTranscript(
                transcriptionResult,
                failedTranscriptID: failedTranscriptID,
                audioURL: audioURL,
                modeName: transcriptionMode.name
            )

            // Same storage step as a dictation's success: the row now holds a
            // completed transcript, so its WAV may be compressed to M4A.
            if audioURL.pathExtension.lowercased() == "wav",
               settingsManager?.storeAsM4A == true,
               let savedTranscriptID {
                Task {
                    await recordingLifecycle.performBackgroundWAVToM4AConversion(
                        transcriptID: savedTranscriptID,
                        wavURL: audioURL
                    )
                }
            }
        } catch {
            // Superseded: the newer flow's own request cancelled this one (or it
            // failed while the newer flow ran). Its error is not the user's.
            guard !isPendingRetrySuperseded(identity) else { return }

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
        sessionStartedWithTextDeliverySuppressed = false
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

        // PRIVACY: the folder PATH is never sent. The default is
        // `~/Documents/hyperwhisper/recordings`, so the raw string carries the
        // account name, and `SentryService.beforeSend` only redacts extras whose
        // KEY matches transcript/text/prompt — it never looks at values. The two
        // booleans below carry the diagnostic value the path was carrying:
        // "did the user move this folder, and is it off the home volume".
        let recordingsFolder = settingsManager?.recordingsFolder ?? ""
        let defaultRecordingsFolder = FileManager.default
            .urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("hyperwhisper/recordings").path
        metadata["recordingsFolderIsDefault"] = !recordingsFolder.isEmpty && recordingsFolder == defaultRecordingsFolder
        metadata["recordingsFolderIsInHome"] = recordingsFolder.hasPrefix(NSHomeDirectory() + "/")
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
            case .audioSystemNotResponding:
                return "audio_system_not_responding"
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
    /// - Parameter modeIdentity: the provider axis the no-speech diagnostic
    ///   groups on, snapshotted off the Core Data `Mode` by the caller (which is
    ///   where the resolved mode is in scope). Value-typed and `Sendable` so it
    ///   can cross onto the detached capture task.
    func handleTranscriptionError(_ error: Error, processingTranscriptID: NSManagedObjectID?, modeIdentity: NoSpeechModeIdentity?, attemptDiagnostics: TranscriptionAttemptDiagnostics?, duration: TimeInterval, audioURL: URL) {
        // HYPERWHISPER-EX: `TranscriptionPipeline` deliberately excludes
        // `.noSpeechDetected` from Sentry capture as "user-recoverable", which
        // also hid every case where the audio DID contain speech and a provider
        // returned an empty transcript anyway. Windows measures that cohort and
        // it is real (57 backend-confirmed events / 90 days, median peak
        // -18.47 dBFS, across Deepgram and ElevenLabs); macOS reported nothing.
        //
        // `TranscriptionDiagnosticsService` is the narrow reporting path: it
        // measures the audio and skips genuine silence, so the pipeline's
        // blanket exclusion stays and the common case still produces no event.
        // It subsumes the previous mic-auto-boost-only capture — that signal
        // now rides along as the `mic_boost_failed` tag, so a quiet recording
        // caused by a failed boost stays distinguishable from a provider fault.
        if let te = error as? TranscriptionError, case .noSpeechDetected = te {
            let micBoostFailed = recordingLifecycle.lastMicBoostFailed
            let deviceName = recordingLifecycle.deviceManager.activeInputDeviceName
            Task.detached(priority: .utility) {
                await TranscriptionDiagnosticsService.captureNoSpeechDiagnostic(
                    audioURL: audioURL,
                    fallbackDurationSeconds: duration,
                    modeIdentity: modeIdentity,
                    attemptDiagnostics: attemptDiagnostics,
                    diagnosticStage: "live_recording",
                    diagnosticSource: "provider_no_speech",
                    error: te,
                    // Reaching here means the provider itself reported no-speech:
                    // `.noSpeechDetected` is what `RustRetry` maps the provider's
                    // `.NoSpeech` to, and what `LibWhisperProvider` throws for an
                    // empty local transcript. The literal used to sit inside the
                    // classifier; it belongs here, at the site that knows.
                    //
                    // `emptyTranscriptWithoutFlag` is left at its default: the
                    // error case carries no associated value, so macOS cannot
                    // distinguish the two producers and arm 3 of the shared
                    // classifier is unreachable here. See the parameter's doc
                    // comment on `captureNoSpeechDiagnostic`.
                    backendNoSpeechDetected: true,
                    inputDeviceName: deviceName,
                    micBoostFailed: micBoostFailed
                )
            }
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
