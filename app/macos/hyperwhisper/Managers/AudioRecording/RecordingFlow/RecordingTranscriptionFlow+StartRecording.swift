//
//  RecordingTranscriptionFlow+StartRecording.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import AppKit
import KeyboardShortcuts

extension RecordingTranscriptionFlow {

    // MARK: - Start Recording

    /// Handle starting recording with fast activation
    ///
    /// **TWO-PHASE APPROACH FOR FAST RECORDING:**
    /// This implementation keeps the recording start path short, then runs all
    /// non-critical validation checks in the background. If background validation
    /// fails, the recording is cancelled and an error is shown (~500ms later).
    ///
    /// **Phase A - Critical Path (blocking, normally ~30-60ms):**
    /// 1. ✓ Check transcription manager ready (race condition prevention)
    /// 2. ✓ Capture frontmost app context (for auto-paste)
    /// 3. ✓ Request microphone permission
    /// 4. ✓ Begin power activity (prevent App Nap)
    /// 5. ✓ Start audio recording
    /// 6. ✓ Show recording dialog and start sound after recorder is live
    /// 7. ✓ Update state to recording
    /// 8. ✓ Enable cancel shortcut
    ///
    /// **Phase B - Background Validation (non-blocking, parallel):**
    /// - License check → fail? cancel + error
    /// - API key validation → fail? cancel + error
    /// - Provider health check → fail? cancel + error
    /// - Storage preparation → fail? cancel + error
    /// - After validation passes: capture clipboard, selected text, app context
    ///
    /// **Why This Approach:**
    /// Users expect fast feedback when pressing the hotkey, but the UI and start sound
    /// should not imply that audio is being captured until the recorder is actually live.
    /// This matters when CoreAudio takes longer to publish input devices after wake or
    /// route changes.
    ///
    /// **Parameters:**
    /// - `mode`: Transcription mode name to use
    func handleStartRecording(mode: String) async {
        let attemptId = UUID().uuidString
        currentRecordingAttemptId = attemptId

        let resolvedTrigger: RecordingTriggerSource = (appState?.isStreamingShortcutTriggered == true) ? .streamingShortcut : currentRecordingTriggerSource
        currentRecordingTriggerSource = resolvedTrigger
        sessionStartedWithTextDeliverySuppressed = RecordingTextDeliveryPolicy.shouldSuppress(
            sessionStartedSuppressed: false,
            currentlySuppressed: TextDeliveryGate.isSuppressed,
            trigger: resolvedTrigger
        )

        let recordingStartTransaction = SentryService.startTransaction(name: "Recording Start", operation: "audio.recording.start")
        func finishStartTransaction(_ status: SpanStatus) {
            SentryService.finishSpan(recordingStartTransaction, status: status)
        }

        SentryService.addBreadcrumb(
            message: "Recording start requested",
            category: "audio.recording",
            data: [
                "mode": mode,
                "streamingShortcutTriggered": appState?.isStreamingShortcutTriggered ?? false,
                "isRecording": recordingLifecycle.isRecording,
                "isStreamingActive": isStreamingActive,
                "trigger": resolvedTrigger.rawValue,
                "attemptId": attemptId,
                "permissionStatus": permissionManager.currentAuthorizationStatusString()
            ]
        )

        let preflightStart = Date()
        var lastCheckpoint = preflightStart
        func logPreflightCheckpoint(_ name: String) {
            let now = Date()
            let step = now.timeIntervalSince(lastCheckpoint)
            let total = now.timeIntervalSince(preflightStart)
            AppLogger.audio.info("⏱️ Recording preflight checkpoint '\(name)' · step=\(String(format: "%.2f", step))s · total=\(String(format: "%.2f", total))s")
            let stepMs = Int((step * 1_000).rounded())
            let totalMs = Int((total * 1_000).rounded())
            SentryService.addBreadcrumb(
                message: "Recording preflight checkpoint",
                category: "audio.preflight",
                data: [
                    "checkpoint": name,
                    "stepMs": stepMs,
                    "totalMs": totalMs
                ]
            )
            lastCheckpoint = now
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE A: CRITICAL PATH - Start recording before showing recording cues
        // Target: normally completes within ~30-60ms
        // ═══════════════════════════════════════════════════════════════════════════

        // NOTE: We no longer block recording start during transcription.
        // TranscriptionPipeline.transcribeWithDetails() will auto-cancel the previous transcription.
        // This allows users to re-record immediately without waiting for the previous transcription.
        if let transcriptionPipeline = transcriptionPipeline, !transcriptionPipeline.state_isReadyForTranscription() {
            AppLogger.audio.info("Recording will cancel previous transcription in progress")
        }

        // CHECK FOR TASK CANCELLATION: Exit if a newer toggle request has arrived
        guard !(toggleTask?.isCancelled ?? false) else {
            AppLogger.audio.debug("Start recording cancelled due to newer toggle request")
            SentryService.addBreadcrumb(
                message: "Recording start cancelled by newer toggle",
                category: "audio.recording",
                level: .warning,
                data: [
                    "attemptId": attemptId,
                    "trigger": resolvedTrigger.rawValue
                ]
            )
            currentRecordingAttemptId = nil
            currentRecordingTriggerSource = .unknown
            sessionStartedWithTextDeliverySuppressed = false
            quickCaptureContext = nil
            finishStartTransaction(.internalError)
            return
        }

        // Reset state for new recording
        await MainActor.run {
            appState?.lastTranscription = ""
            appState?.pendingRetryAudioPath = nil
            appState?.transcriptionPasteFailed = false
            appState?.lastDeliveryWasQuickCapture = false
            appState?.showCancelConfirmation = false
        }

        // 1. CAPTURE FRONTMOST APP CONTEXT (instant, ~1ms)
        // Must capture BEFORE any recording dialog is shown. Even though the
        // recording panel is .nonactivatingPanel, opening it can race with
        // NSWorkspace's frontmostApplication update.
        // Critical for: AUTO-PASTE (need PID), STREAMING DELIVERY MODE (need bundle ID)
        let frontmostApp = NSWorkspace.shared.frontmostApplication
        previousFrontmostPID = frontmostApp?.processIdentifier
        previousFrontmostBundleID = frontmostApp?.bundleIdentifier
        autoPasteHandler.setPreviousFrontmostApp(pid: previousFrontmostPID,
                                                 bundleID: previousFrontmostBundleID)

        // ═══════════════════════════════════════════════════════════════════════════
        // STREAMING CHECK: Check if streaming shortcut was triggered
        // Streaming is now a standalone feature (not mode-based) and is triggered
        // via the dedicated streaming shortcut (Option+Shift+Space).
        // ═══════════════════════════════════════════════════════════════════════════
        if appState?.isStreamingShortcutTriggered == true {
            AppLogger.audio.info("📡 Streaming shortcut detected - using real-time transcription")
            logPreflightCheckpoint("streaming shortcut detected")
            clearActiveSessionMode()

            await MainActor.run {
                appState?.showRecordingDialog = true
            }
            logPreflightCheckpoint("streaming dialog shown")

            // Frontmost app context already captured above (before dialog show).

            // Request microphone permission (streaming needs it too)
            let hasPermission = await SentryService.measureAsync(
                operation: "audio.permission",
                description: "request microphone permission (streaming)"
            ) {
                await permissionManager.requestMicrophonePermission()
            }
            if !hasPermission {
                await MainActor.run {
                    appState?.showRecordingDialog = false
                    permissionManager.showPermissionDeniedAlert = true
                }
                finishStartTransaction(.internalError)
                return
            }

            // Begin power activity for streaming session
            powerActivityManager.beginPowerActivity("Streaming transcription")

            // Enable cancel shortcut
            if !Self.cancelShortcutHandlerRegistered {
                KeyboardShortcuts.onKeyUp(for: .cancelRecording) { [weak self] in
                    AppLogger.ui.debug("⌨️ Cancel recording shortcut pressed")
                    Task { @MainActor in
                        self?.handleCancelShortcut()
                    }
                }
                Self.cancelShortcutHandlerRegistered = true
            }
            KeyboardShortcuts.enable(.cancelRecording)

            // Get streaming settings from SettingsManager. The `model`
            // field is provider-specific: Deepgram model id for cloud,
            // Parakeet version id for on-device.
            let streamingLanguage = settingsManager?.streamingLanguageEffective ?? LanguageData.automaticCode
            let streamingProvider = settingsManager?.streamingProvider ?? "hyperwhisperCloud"
            let streamingModel: String?
            switch StreamingTranscriptionProvider(rawValue: streamingProvider) {
            case .parakeetLocal:
                streamingModel = settingsManager?.streamingLocalParakeetVersion
            case .nemotronLocal:
                streamingModel = settingsManager?.streamingLocalNemotronVariant
            case .gemini:
                // MUST NOT fall through to the `default:` arm below. Only two
                // strategies read `config.model` — Deepgram's and Gemini's — and
                // Gemini prefixes whatever arrives with `models/`, so the shared
                // Deepgram setting would reach Google as `models/nova-3-general`
                // and the socket would close on the setup frame. nil selects the
                // strategy's own `gemini-3.5-transcribe-live` default, which is
                // the only live model this provider has.
                streamingModel = nil
            default:
                streamingModel = settingsManager?.streamingDeepgramModel
            }
            let streamingFastFormatting = settingsManager?.streamingFastFormatting ?? true

            // Play start sound effect before streaming begins
            if let settings = settingsManager, settings.enableSoundEffects {
                SoundEffectsManager.shared.playStartSound(volume: settings.soundEffectsVolume)
            }

            // Start streaming transcription with the selected provider and settings
            await startStreamingTranscription(
                language: streamingLanguage,
                provider: streamingProvider,
                model: streamingModel,
                fastFormatting: streamingFastFormatting
            )
            logPreflightCheckpoint("streaming started")
            finishStartTransaction(isStreamingActive ? .ok : .internalError)
            return
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BATCH TRANSCRIPTION FLOW (Normal flow - non-streaming)
        // ═══════════════════════════════════════════════════════════════════════════

        // Get the selected mode ID for later use.
        // Quick Capture overrides AppState's selected mode when a specific Mode
        // was pinned in settings; nil/"current mode" falls back to AppState.
        let selectedModeId: String = {
            if let pinnedId = quickCaptureContext?.modeId, !pinnedId.isEmpty {
                return pinnedId
            }
            return appState?.selectedModeId ?? ""
        }()
        setActiveSessionMode(id: selectedModeId, name: mode)

        // Frontmost app context already captured above (before dialog show).

        // 3. REQUEST MICROPHONE PERMISSION
        // May show system dialog on first use.
        let hasPermission = await SentryService.measureAsync(
            operation: "audio.permission",
            description: "request microphone permission"
        ) {
            await permissionManager.requestMicrophonePermission()
        }
        logPreflightCheckpoint("microphone permission")

        guard hasPermission else {
            await MainActor.run {
                appState?.showRecordingDialog = false
                permissionManager.showPermissionDeniedAlert = true
            }
            quickCaptureContext = nil
            finishStartTransaction(.internalError)
            return
        }

        // 4. PREVENT APP NAP during recording/transcription
        powerActivityManager.beginPowerActivity("Recording and transcribing audio")

        // 5. START AUDIO RECORDING
        //
        // Three outcomes, not two. `startRecording()` can also come back `.superseded`,
        // meaning a newer start took ownership of the recorder while this one was
        // suspended inside CoreAudio. That is why the span is opened by hand instead of
        // through `measureAsync`: the helper only knows `.ok` and `.internalError`, and
        // a superseded start is neither.
        let startSpan = SentryService.startSpan(operation: "audio.recorder", description: "start recording")
        let startOutcome: RecorderStartOutcome
        do {
            startOutcome = try await recordingLifecycle.startRecording()
        } catch {
            SentryService.finishSpan(startSpan, status: .internalError)
            await handleRecordingStartFailure(error)
            finishStartTransaction(.internalError)
            return
        }

        if case .superseded = startOutcome {
            SentryService.finishSpan(startSpan, status: .cancelled)
            AppLogger.audio.notice("Recording start superseded by a newer start - abandoning attempt \(attemptId, privacy: .public)")
            SentryService.addBreadcrumb(
                message: "Recording start superseded by a newer start",
                category: "audio.recording",
                level: .warning,
                data: [
                    "attemptId": attemptId,
                    "trigger": resolvedTrigger.rawValue
                ]
            )

            // DO NOT run handleRecordingStartFailure() here. Its cleanup deletes
            // `rawURL`, which if a winning attempt is live is the `.incomplete_*.wav`
            // being actively recorded into. (`cleanupFailedStartArtifacts()` now refuses
            // to run while a recording is live, but that is a backstop, not a licence to
            // call the failure path for something that did not fail.) Nothing shows
            // recording UI, no session is persisted, no timer runs: the winner does all
            // of that for itself.
            //
            // Shared state is only unwound if this attempt still owns it. In the normal
            // case a newer `handleStartRecording` has already overwritten
            // `currentRecordingAttemptId` and taken the power assertion for itself, and
            // clearing either here would sabotage a recording that is genuinely live.
            if currentRecordingAttemptId == attemptId {
                currentRecordingAttemptId = nil
                currentRecordingTriggerSource = .unknown
                sessionStartedWithTextDeliverySuppressed = false
                quickCaptureContext = nil
                powerActivityManager.endPowerActivity()
            }

            finishStartTransaction(.cancelled)
            return
        }

        SentryService.finishSpan(startSpan, status: .ok)
        logPreflightCheckpoint("audio engine started")

        // 6. SHOW RECORDING UI NOW THAT THE RECORDER IS LIVE
        await MainActor.run {
            appState?.showRecordingDialog = true
            appState?.recordingState = .recording
        }
        logPreflightCheckpoint("recording dialog shown")

        // 6.5. PLAY START SOUND EFFECT & APPLY MEDIA CONTROL
        if let settings = settingsManager, settings.enableSoundEffects {
            SoundEffectsManager.shared.playStartSound(volume: settings.soundEffectsVolume)
            // Wait for chime to finish before muting other media, but don't block recording start
            Task {
                try? await Task.sleep(for: .milliseconds(350))
                recordingLifecycle.applyMediaControl()
            }
        } else {
            recordingLifecycle.applyMediaControl()
        }

        logPreflightCheckpoint("recording state updated")

        // 7. ENABLE CANCEL SHORTCUT
        // Register handler only once per app lifetime to prevent accumulation
        if !Self.cancelShortcutHandlerRegistered {
            KeyboardShortcuts.onKeyUp(for: .cancelRecording) { [weak self] in
                AppLogger.ui.debug("⌨️ Cancel recording shortcut pressed")
                Task { @MainActor in
                    self?.handleCancelShortcut()
                }
            }
            Self.cancelShortcutHandlerRegistered = true
        }
        KeyboardShortcuts.enable(.cancelRecording)

        AppLogger.audio.info("⏺️ Started recording with mode: \(mode)")
        logPreflightCheckpoint("critical path complete - recording active")
        finishStartTransaction(.ok)
        recordingLifecycle.persistSessionForActiveRecording()
        armRecordingMaxDurationTimer(mode: mode, attemptId: attemptId)

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE B: BACKGROUND VALIDATION - Run all checks in parallel
        // If any check fails, cancel recording and show error
        // ═══════════════════════════════════════════════════════════════════════════

        // Launch background validation task
        Task { [weak self] in
            guard let self else { return }

            // Fetch mode on a background context to avoid blocking the main thread.
            // Fixes Sentry HYPERWHISPER-KP (DB on Main Thread during Recording Start).
            guard let modeSnapshot = await PersistenceController.shared.fetchModeSnapshotInBackground(withId: selectedModeId) else {
                await self.cancelRecordingWithError("recording.error.modeMissing".localized)
                return
            }

            // RECORD-START PREWARM (WS2):
            // Close the residual cold-load window by preparing the EXACT resolved
            // model as recording starts — covers post-eviction cold state and Quick
            // Capture's pinned mode (selectedModeId already accounts for it). This is
            // fire-and-forget: it is never awaited by the start path and must not delay
            // recording. Cloud modes no-op in prepareModel, so skip them here.
            let prewarmModelId = modeSnapshot.model.lowercased()
            if !prewarmModelId.isEmpty, prewarmModelId != "cloud" {
                Task { [weak self] in
                    guard let self else { return }
                    // Guard against a stale prewarm from a cancelled/superseded start.
                    guard self.currentRecordingAttemptId == attemptId else { return }
                    guard let mode = await PersistenceController.shared.fetchModeInBackground(withId: selectedModeId) else { return }
                    guard self.currentRecordingAttemptId == attemptId else { return }
                    await self.transcriptionPipeline?.prepareModel(for: mode)
                    await self.transcriptionPipeline?.prepareLocalRuntime(for: mode)
                }
            }

            // Run critical checks in parallel for faster validation
            // NOTE: Provider health check removed - errors will surface during transcription instead.
            // This prevents network timeouts from blocking recording start.
            // NOTE: No local license/trial gate — local transcription is unlimited (open source).
            async let apiKeysResult = self.validateAPIKeys(for: modeSnapshot)
            async let storageResult = self.prepareStorage()

            // Wait for all critical checks to complete
            let results = await [apiKeysResult, storageResult]

            // If any check failed, cancel recording and show the first error
            // GUARD: Only cancel if still actively recording
            // For very short recordings, user may have already stopped before validation completes.
            // In that case, don't interrupt the transcription flow with a delayed error.
            if let error = results.compactMap({ $0 }).first {
                // Guard: Only cancel if this is still the same recording attempt.
                // A fast stop/start sequence could let Attempt A's validation cancel Attempt B.
                guard self.currentRecordingAttemptId == attemptId else {
                    AppLogger.audio.debug("Background validation failed but attempt \(attemptId) is stale - skipping cancel")
                    return
                }
                let currentState = await MainActor.run { self.appState?.recordingState }
                guard currentState == .recording else {
                    AppLogger.audio.debug("Background validation failed but state is \(String(describing: currentState)) - skipping cancel (recording already stopped)")
                    return
                }
                await self.cancelRecordingWithError(error)
                return
            }

            logPreflightCheckpoint("background validation passed")

            // ═══════════════════════════════════════════════════════════════════════
            // NON-CRITICAL CONTEXT CAPTURE - fire and forget after validation passes
            // These don't block recording but enhance transcription quality
            // ═══════════════════════════════════════════════════════════════════════

            // RESET: Clear stale context from previous recording session.
            // Without this, a short recording that stops before context capture
            // completes would use the previous session's context (privacy risk).
            self.capturedApplicationContext = nil

            // Guard: Only capture context if this is still the same recording attempt.
            // A fast stop/start sequence could let Attempt A overwrite Attempt B's context.
            guard self.currentRecordingAttemptId == attemptId else {
                AppLogger.audio.debug("Recording attempt \(attemptId) is stale - skipping context capture")
                return
            }

            // GUARD: Skip context capture if recording already stopped
            // For very short recordings, we may reach this point after the user has
            // already stopped. Context capture is wasted work in that case.
            guard self.recordingLifecycle.isRecording else {
                AppLogger.audio.debug("Recording already stopped - skipping context capture")
                return
            }

            // Clipboard snapshot for restoration after auto-paste
            AccessibilityHelper.shared.startRecordingSession()

            // Capture screen OCR text if enabled on this mode
            var screenOCRText: String? = nil
            if modeSnapshot.enableScreenOCR {
                screenOCRText = await ScreenOCRCapture.shared.captureAndOCR(
                    frontmostPID: self.previousFrontmostPID
                )
                if let text = screenOCRText {
                    AppLogger.audio.info("Screen OCR captured: \(text.count, privacy: .public) characters")
                    #if DEBUG
                    AppLogger.audio.debug("Screen OCR content: \(text, privacy: .public)")
                    #endif
                }
            }

            // Capture application context (browser URL, focused element, etc.)
            self.capturedApplicationContext = ApplicationContextGatherer.shared.gatherContext(
                screenOCRText: screenOCRText,
                frontmostPID: self.previousFrontmostPID
            )

            logPreflightCheckpoint("context capture complete")
        }
    }

    // MARK: - Background Validation Helpers

    /// Validate API keys are configured for the selected mode
    /// Returns error message if critical keys are missing, nil if OK
    /// For post-processing-only missing keys, sets flag but allows recording
    private func validateAPIKeys(for snapshot: ModeSnapshot) async -> String? {
        guard let settings = settingsManager else { return nil }
        let missingKeys = settings.getMissingAPIKeys(for: snapshot)

        // Special case: offline when cloud is needed
        if missingKeys.count == 1, case .offline = missingKeys[0].context {
            await MainActor.run {
                appState?.missingAPIKeys = missingKeys
                appState?.showAPIKeyAlert = true
            }
            return "Cannot use cloud transcription while offline"
        }

        // If transcription keys are missing, we must block
        if !missingKeys.isEmpty && !SettingsManager.onlyPostProcessingKeysMissing(missingKeys) {
            await MainActor.run {
                appState?.missingAPIKeys = missingKeys
                appState?.showAPIKeyAlert = true
            }
            return "Missing API keys"
        }

        // Handle post-processing only missing (non-blocking)
        if !missingKeys.isEmpty {
            await MainActor.run {
                appState?.postProcessingKeyMissing = true
                appState?.missingAPIKeys = missingKeys
            }
        } else {
            await MainActor.run {
                appState?.postProcessingKeyMissing = false
                appState?.missingAPIKeys = []
            }
        }

        return nil
    }

    /// Prepare storage folder for recording
    /// Returns error message if storage inaccessible, nil if OK
    private func prepareStorage() async -> String? {
        guard let settings = settingsManager else { return nil }
        let ready = await settings.prepareRecordingsFolderIfNeededAsync()
        if !ready {
            let chose = await MainActor.run { settings.presentStorageRecoveryPrompt() }
            if !chose {
                return "recording.error.documentsAccess".localized
            }
        }
        return nil
    }

    /// Cancel an in-progress recording due to background validation failure
    /// Stops recording, resets state, and shows error to user
    func cancelRecordingWithError(_ message: String) async {
        AppLogger.audio.warning("Recording cancelled due to validation failure: \(message)")

        recordingMaxDurationTimer?.invalidate()
        recordingMaxDurationTimer = nil
        await resetStreamingSessionState(cancelService: true)

        // Stop the recording and capture result for cleanup.
        // Pass cancelled: true to skip file finalization — the raw file has no
        // useful audio and will be deleted below (fixes HYPERWHISPER-F1).
        let result = await recordingLifecycle.stopRecording(cancelled: true)

        // Fallback: if deleteCurrentSession doesn't catch the file (session already
        // cleared by stopRecording's error path), delete it directly.
        if let url = result?.url, FileManager.default.fileExists(atPath: url.path) {
            try? FileManager.default.removeItem(at: url)
            AppLogger.audio.info("Deleted orphaned audio file from cancelled recording")
        }

        // Clean up orphaned Core Data session and its audio file.
        // stopRecording() finalizes the file and updates the session entity,
        // deleteCurrentSession() then removes both.
        await recordingLifecycle.sessionManager.deleteCurrentSession()

        await MainActor.run {
            appState?.recordingState = .idle
            appState?.showRecordingDialog = false
            appState?.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag
            appState?.showError(message)
        }

        // Cleanup
        powerActivityManager.endPowerActivity()
        KeyboardShortcuts.disable(.cancelRecording)
        AccessibilityHelper.shared.endRecordingSession()
        currentRecordingAttemptId = nil
        currentRecordingTriggerSource = .unknown
        quickCaptureContext = nil
    }

    private func armRecordingMaxDurationTimer(mode: String, attemptId: String) {
        recordingMaxDurationTimer?.invalidate()
        recordingMaxDurationTimer = nil

        // User setting: 0 = no limit, so no timer is armed.
        guard let maxDuration = settingsManager?.maxRecordingDurationInterval else {
            AppLogger.audio.info("⏱️ Recording max duration timer off (no limit set)")
            return
        }
        let limitMinutes = Int(maxDuration) / 60

        recordingMaxDurationTimer = Timer.scheduledTimer(withTimeInterval: maxDuration, repeats: false) { [weak self] _ in
            Task { @MainActor in
                guard let self,
                      self.recordingLifecycle.isRecording,
                      !self.isStreamingActive,
                      !self.isStopInProgress,
                      self.currentRecordingAttemptId == attemptId
                else { return }

                AppLogger.audio.warning("⏱️ Recording max duration (\(limitMinutes) minutes) reached — auto-stopping")
                SentryService.addBreadcrumb(
                    message: "Recording max duration reached",
                    category: "audio.recording",
                    level: .warning,
                    data: [
                        "attemptId": attemptId,
                        "maxDurationSeconds": Int(maxDuration)
                    ]
                )
                self.appState?.showWarning(String(format: "recording.maxDuration.reachedToast".localized, limitMinutes))
                self.currentRecordingTriggerSource = .autoStop
                await self.handleStopRecordingWithTranscription(mode: mode, cancelled: false)
            }
        }
        AppLogger.audio.info("⏱️ Recording max duration timer set (\(limitMinutes) minutes)")
    }
}
