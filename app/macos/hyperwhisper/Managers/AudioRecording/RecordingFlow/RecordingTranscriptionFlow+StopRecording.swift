//
//  RecordingTranscriptionFlow+StopRecording.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import KeyboardShortcuts

extension RecordingTranscriptionFlow {

    // MARK: - Stop Recording

    /// Minimum recording duration in seconds before transcription is attempted.
    /// Recordings shorter than this are automatically discarded to prevent:
    /// 1. "Recording too short" errors from insufficient audio data
    /// 2. Wasted API calls for audio that can't be meaningfully transcribed
    /// 3. FileWatcher timeout errors when the audio file is too small to finalize
    private static let minimumRecordingDuration: TimeInterval = 1.0

    /// Handle stopping recording with transcription
    ///
    /// **What This Does:**
    /// Complete workflow from stop to transcription result:
    /// 1. Stop recording and get audio file
    /// 2. Check minimum duration (discard if too short)
    /// 3. Wait for file to be ready (FileWatcher)
    /// 4. If cancelled: clean up and return
    /// 5. Create processing transcript
    /// 6. Transcribe audio
    /// 7. Update Core Data with results
    /// 8. Auto-paste or keep dialog open
    /// 9. Handle errors gracefully
    ///
    /// **Parameters:**
    /// - `mode`: Transcription mode name
    /// - `cancelled`: If true, skip transcription (user cancelled)
    func handleStopRecordingWithTranscription(mode: String, cancelled: Bool) async {
        guard !isStopInProgress else {
            AppLogger.audio.debug("Stop recording ignored because another stop is already in progress")
            return
        }

        isStopInProgress = true
        defer { isStopInProgress = false }
        let flowStart = Date()
        let slowTranscribingUIThresholdMs = 8_000
        let slowTranscribingUIWithPostProcessingThresholdMs = 15_000
        let slowTranscribingUIWithLocalLLMThresholdMs = 45_000
        var transcribingUIStart: Date?

        let sessionModeName = activeSessionModeName
        let sessionModeId = activeSessionModeId

        let attemptId = currentRecordingAttemptId ?? "none"
        let trigger = currentRecordingTriggerSource.rawValue

        defer {
            currentRecordingAttemptId = nil
            currentRecordingTriggerSource = .unknown
            // Quick Capture context lives for one session — clear it now so the
            // next non-QC recording doesn't accidentally re-route to Notes.
            quickCaptureContext = nil
        }

        SentryService.addBreadcrumb(
            message: "Recording stop requested",
            category: "audio.recording",
            data: [
                "mode": mode,
                "cancelled": cancelled,
                "isStreamingActive": isStreamingActive,
                "trigger": trigger,
                "attemptId": attemptId
            ]
        )
        // ═══════════════════════════════════════════════════════════════════════════
        // STREAMING MODE CHECK: If streaming is active, use the streaming stop flow
        // Streaming transcription types directly as you speak, so "stop" just ends
        // the WebSocket session and saves the accumulated transcript.
        // ═══════════════════════════════════════════════════════════════════════════
        if isStreamingActive {
            if cancelled {
                // User cancelled - clean up without pasting/saving
                AppLogger.audio.info("❌ Streaming cancelled by user")
                recordingMaxDurationTimer?.invalidate()
                recordingMaxDurationTimer = nil
                streamingMaxDurationTimer?.invalidate()
                streamingMaxDurationTimer = nil
                streamingStartTime = nil
                isStreamingActive = false
                streamingAccumulatedText = ""
                streamingPreviewTextSnapshot = ""
                streamingDeliveryMode = .directInsert
                streamingTargetBundleId = nil
                recordingLifecycle.audioLevel = 0

                if let service = streamingService {
                    await service.cancel()
                }
                streamingService = nil

                await MainActor.run {
                    appState?.recordingState = .idle
                    appState?.showRecordingDialog = false
                    appState?.showStreamingPreview = false
                    appState?.streamingText = ""
                    appState?.lastTranscription = ""
                    appState?.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag
                    KeyboardShortcuts.disable(.cancelRecording)
                    StreamingPreviewWindowManager.shared.close()
                }
                clearActiveSessionMode()

                powerActivityManager.endPowerActivity()
                AccessibilityHelper.shared.endRecordingSession()
            } else {
                // Play stop sound effect before saving
                if let settings = settingsManager, settings.enableSoundEffects {
                    SoundEffectsManager.shared.playStopSound(volume: settings.soundEffectsVolume)
                }
                // Normal stop - save the transcript
                await stopStreamingTranscription(mode: mode)
            }
            return
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BATCH TRANSCRIPTION FLOW (Normal flow - non-streaming)
        // ═══════════════════════════════════════════════════════════════════════════

        // Stop recording and get audio file.
        // Pass `cancelled` to skip file finalization when the user cancels — avoids
        // wasteful file polling and prevents orphaned WAV files on disk.
        // (Extends the HYPERWHISPER-F1 fix to the user-cancel path.)
        recordingMaxDurationTimer?.invalidate()
        recordingMaxDurationTimer = nil
        guard let result = await recordingLifecycle.stopRecording(cancelled: cancelled) else {
            await MainActor.run {
                appState?.recordingState = .idle
            }
            clearActiveSessionMode()
            powerActivityManager.endPowerActivity()
            return
        }

        // USER CANCEL: Clean up fully and return immediately.
        // Previously, cancelled recordings skipped transcription but left the audio file
        // on disk and the Core Data session in place — causing orphaned files to accumulate.
        // Now we match the cleanup done by cancelRecordingWithError() and the streaming
        // cancel path: delete the file, delete the session, reset all UI state.
        if cancelled {
            if let url = result.url {
                try? FileManager.default.removeItem(at: url)
            }
            await recordingLifecycle.sessionManager.deleteCurrentSession()
            await MainActor.run {
                appState?.recordingState = .idle
                appState?.showRecordingDialog = false
                appState?.lastTranscription = ""
                appState?.isStreamingShortcutTriggered = false
                KeyboardShortcuts.disable(.cancelRecording)
            }
            clearActiveSessionMode()
            powerActivityManager.endPowerActivity()
            AccessibilityHelper.shared.endRecordingSession()
            return
        }

        let audioURL = result.url!
        let recordingDuration = result.duration
        let conversionWarning = result.conversionWarning

        // PLAY STOP SOUND EFFECT
        if let settings = settingsManager, settings.enableSoundEffects {
            SoundEffectsManager.shared.playStopSound(volume: settings.soundEffectsVolume)
        }

        // MINIMUM DURATION CHECK:
        // Discard recordings that are too short to be meaningful.
        // This prevents "recording too short" errors and wasted API calls.
        // The user likely pressed and released too quickly (accidental trigger).
        if recordingDuration < Self.minimumRecordingDuration {
            AppLogger.audio.info("⏱️ Recording too short (\(String(format: "%.2f", recordingDuration))s < \(Self.minimumRecordingDuration)s) - discarding")

            // Log to Sentry for analytics on how often this happens
            if AppLogger.isErrorLoggingEnabled {
                // Get file info before deletion for diagnostics
                var fileSize: Int64 = -1
                var fileExists = false
                if let url = result.url {
                    fileExists = FileManager.default.fileExists(atPath: url.path)
                    fileSize = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? -1
                }

                SentryService.addBreadcrumb(
                    message: "Recording discarded - too short",
                    category: "audio.recording",
                    level: .info,
                    data: [
                        "recordingDurationMs": Int(recordingDuration * 1000),
                        "minimumDurationMs": Int(Self.minimumRecordingDuration * 1000),
                        "fileExists": fileExists,
                        "fileSizeBytes": fileSize,
                        "mode": mode
                    ]
                )
            }

            // Clean up the audio file since we're not using it
            if let url = result.url {
                try? FileManager.default.removeItem(at: url)
            }
            if let session = result.recordingSession {
                recordingLifecycle.sessionManager.deleteSession(session, deleteAudioFile: false)
            }

            await MainActor.run {
                appState?.recordingState = .idle
                appState?.showRecordingDialog = false
                appState?.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag
                KeyboardShortcuts.disable(.cancelRecording)
            }

            clearActiveSessionMode()
            powerActivityManager.endPowerActivity()
            AccessibilityHelper.shared.endRecordingSession()
            return
        }

        // CONVERSION WARNING: If compression failed and we're using an oversized file,
        // show a warning toast to inform the user, but continue with transcription.
        // The warning explains why transcription may be slower or fail.
        if let warning = conversionWarning {
            AppLogger.audio.warning("Audio compression failed: \(warning, privacy: .public) - attempting transcription anyway")
            await MainActor.run {
                appState?.showWarning(warning)
            }
        }

        // Wait for file to be ready using DispatchSource
        do {
            AppLogger.audio.debug("Waiting for audio file to become ready...")
            try await fileWatcher.waitForFirstWrite(to: audioURL, timeout: 3.0)
            AppLogger.audio.debug("Initial write event received. Starting readability check...")

            // Poll briefly to ensure file is fully closed and readable
            var isReadable = false
            for attempt in 1...5 {
                if FileManager.default.isReadableFile(atPath: audioURL.path) {
                    isReadable = true
                    AppLogger.audio.debug("File is readable on attempt #\(attempt).")
                    break
                }
                AppLogger.audio.debug("Readability check attempt #\(attempt) failed. Retrying in 100ms...")
                try await Task.sleep(nanoseconds: 100_000_000) // 100ms
            }

            if !isReadable {
                AppLogger.audio.error("Audio file not readable after waiting.")
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Audio file unreadable after wait",
                        category: "audio.recording",
                        level: .error,
                        data: [
                            "audioPath": audioURL.path,
                            "mode": sessionModeName,
                            "attempts": 5,
                            "recordingsFolder": settingsManager?.recordingsFolder ?? "unknown"
                        ]
                    )
                }
                await MainActor.run {
                    appState?.recordingState = .idle
                    appState?.lastTranscription = "Error: Audio file could not be read"
                    appState?.pendingRetryAudioPath = audioURL.path
                    appState?.showRecordingDialog = true
                }
                // Persist failed attempt to history so user can retry later
                let failedTranscript = PersistenceController.shared.createProcessingTranscript(
                    duration: recordingDuration,
                    mode: sessionModeName,
                    audioFilePath: audioURL.path
                )
                failedTranscript.setValue("failed", forKey: "status")
                failedTranscript.setValue("Audio file could not be read", forKey: "failedReason")
                failedTranscript.text = "Error: Audio file could not be read"
                PersistenceController.shared.save()

                powerActivityManager.endPowerActivity()
                return
            }

        } catch {
            AppLogger.audio.error("Failed to wait for audio file: \(error.localizedDescription)")

            var fallbackReadable = false
            for attempt in 1...20 {
                let exists = FileManager.default.fileExists(atPath: audioURL.path)
                let readable = FileManager.default.isReadableFile(atPath: audioURL.path)
                if exists && readable {
                    fallbackReadable = true
                    AppLogger.audio.warning("File watcher failed but file is readable (attempt #\(attempt)) – continuing to transcription")
                    break
                }
                do {
                    try await Task.sleep(nanoseconds: 150_000_000) // ~150ms per attempt
                } catch {
                    break
                }
            }

            if !fallbackReadable {
                if AppLogger.isErrorLoggingEnabled {
                    let nsError = error as NSError
                    SentryService.addBreadcrumb(
                        message: "File watcher failed",
                        category: "audio.recording",
                        level: .error,
                        data: [
                            "audioPath": audioURL.path,
                            "mode": sessionModeName,
                            "errorDomain": nsError.domain,
                            "errorCode": nsError.code,
                            "recordingsFolder": settingsManager?.recordingsFolder ?? "unknown",
                            "fallbackAttempts": 20
                        ]
                    )
                }
                await MainActor.run {
                    appState?.recordingState = .idle
                    appState?.lastTranscription = "Error: \(error.localizedDescription)"
                    appState?.showRecordingDialog = true
                    appState?.pendingRetryAudioPath = audioURL.path

                    // Ensure the cancel shortcut is disabled when we bail out early
                    KeyboardShortcuts.disable(.cancelRecording)
                }
                // Persist failed attempt to history so user can retry later
                let failedTranscript = PersistenceController.shared.createProcessingTranscript(
                    duration: recordingDuration,
                    mode: sessionModeName,
                    audioFilePath: audioURL.path
                )
                failedTranscript.setValue("failed", forKey: "status")
                failedTranscript.setValue(error.localizedDescription, forKey: "failedReason")
                failedTranscript.text = "Error: \(error.localizedDescription)"
                PersistenceController.shared.save()

                powerActivityManager.endPowerActivity()
                return
            }
        }

        // VAD SILENCE TRIMMING (Optional)
        // Uses VADProcessingService to analyze audio and trim leading/trailing silence.
        // Benefits: Reduced API costs, faster transcription, potentially better accuracy.
        // Only applies to recordings >= 30 seconds when VAD is enabled in settings.
        let vadResult = await vadProcessingService.processAudioForTranscription(
            audioURL: audioURL,
            duration: recordingDuration,
            vadEnabled: settingsManager?.enableVAD ?? false,
            context: "Recording"
        )
        let finalAudioURL = vadResult.finalAudioURL
        let trimResult = vadResult.trimResult

        // TRANSCRIPTION FLOW
        // Step 1: Create processing transcript
        let actualMode = sessionModeName
        let processingTranscript = await MainActor.run {
            PersistenceController.shared.createProcessingTranscript(
                duration: recordingDuration,
                mode: actualMode,
                audioFilePath: audioURL.path
            )
        }
        AppLogger.audio.info("💾 Created processing transcript: duration=\(recordingDuration)s, mode=\(actualMode)")

        // SAVE TRIMMED AUDIO PATH:
        // If VAD was used and created a valid trimmed file, store the path in Core Data.
        // This allows users to toggle between original and trimmed audio in the history view,
        // and ensures the trimmed file is properly cleaned up when the transcript is deleted.
        if vadResult.wasProcessed, let result = trimResult {
            await MainActor.run {
                PersistenceController.shared.setTrimmedAudioPath(processingTranscript, trimmedPath: result.outputURL.path)
            }
            AppLogger.audio.debug("📝 Saved trimmed audio path to transcript: \(result.outputURL.path, privacy: .public)")
        }

        // Step 2: Update state to show transcription in progress
        await MainActor.run {
            appState?.recordingState = .transcribing
        }
        transcribingUIStart = Date()

        // Step 3: Resolve mode off the main-context fetch path.
        let transcriptionMode = await PersistenceController.shared.resolveTranscriptionModeInBackground(
            id: sessionModeId,
            fallbackName: actualMode
        )

        // Step 4: Perform transcription
        do {
            guard let transcriptionMgr = transcriptionPipeline else {
                throw AudioError.noTranscriptionPipeline
            }

            // Add breadcrumb before transcription starts
            if AppLogger.isErrorLoggingEnabled {
                let fileSize = (try? FileManager.default.attributesOfItem(atPath: audioURL.path)[.size] as? Int64) ?? -1

                // Flag very short recordings that may cause issues
                let isVeryShortRecording = recordingDuration < 0.5
                let isShortRecording = recordingDuration < 1.0

                SentryService.addBreadcrumb(
                    message: isVeryShortRecording ? "Starting transcription (VERY SHORT)" : "Starting transcription",
                    category: "audio.transcription",
                    data: [
                        "audioPath": audioURL.path,
                        "fileExists": FileManager.default.fileExists(atPath: audioURL.path),
                        "fileReadable": FileManager.default.isReadableFile(atPath: audioURL.path),
                        "fileSizeBytes": fileSize,
                        "duration": recordingDuration,
                        "mode": actualMode,
                        "isVeryShortRecording": isVeryShortRecording,
                        "isShortRecording": isShortRecording
                    ]
                )
            }

            // Use finalAudioURL which may be VAD-trimmed if VAD was enabled
            let transcriptionResult = try await transcriptionMgr.transcribeWithDetails(
                audioURL: finalAudioURL,
                mode: transcriptionMode,
                recordingSession: nil, // Will be set by RecordingLifecycle
                applicationContext: capturedApplicationContext
            )
            let flowElapsedMs = Int(Date().timeIntervalSince(flowStart) * 1000)
            let transcribingUIElapsedMs = transcribingUIStart.map { Int(Date().timeIntervalSince($0) * 1000) } ?? -1
            let trimmedSeconds = trimResult.map { String(format: "%.1f", $0.silenceRemoved) } ?? "0.0"
            let uiLogMessage =
                "Recording transcription flow succeeded · attemptId=\(attemptId) · trigger=\(trigger) · mode=\(actualMode) · provider=\(transcriptionResult.provider) · flowMs=\(flowElapsedMs) · transcribingUiMs=\(transcribingUIElapsedMs) · vadProcessed=\(vadResult.wasProcessed) · silenceRemovedSeconds=\(trimmedSeconds)"

            let isLocalLLM = transcriptionResult.postProcessingProvider == PostProcessingProvider.localLLM.rawValue
            let effectiveUIThreshold: Int
            if isLocalLLM {
                effectiveUIThreshold = slowTranscribingUIWithLocalLLMThresholdMs
            } else if transcriptionResult.wasPostProcessed {
                effectiveUIThreshold = slowTranscribingUIWithPostProcessingThresholdMs
            } else {
                effectiveUIThreshold = slowTranscribingUIThresholdMs
            }
            if transcribingUIElapsedMs >= effectiveUIThreshold {
                AppLogger.audio.warning("\(uiLogMessage, privacy: .public)")
            } else {
                AppLogger.audio.info("\(uiLogMessage, privacy: .public)")
            }

            // No local usage recording — local transcription is unlimited (open source).

            // Step 5: Update transcript with results
            await MainActor.run {
                appState?.lastTranscription = transcriptionResult.text
                appState?.recordingState = .idle
                appState?.pendingRetryAudioPath = nil

                PersistenceController.shared.updateTranscriptWithTranscription(
                    processingTranscript,
                    transcribedText: transcriptionResult.rawText,
                    postProcessedText: transcriptionResult.wasPostProcessed ? transcriptionResult.text : nil,
                    transcriptionProvider: transcriptionResult.provider,
                    postProcessingProvider: transcriptionResult.postProcessingProvider,
                    wordTimestampsJSON: transcriptionResult.timestamps?.wordTimestampsJSON()
                )

                // CRITICAL: Disable cancel shortcut when transcription completes
                // Without this, the shortcut stays active even when idle
                KeyboardShortcuts.disable(.cancelRecording)
                clearActiveSessionMode()

                AppLogger.audio.info("✅ Updated transcript with transcription")
                powerActivityManager.endPowerActivity()

                // Quick Capture always routes to Notes, regardless of the
                // `pasteResultText` setting. The user opted in by binding a
                // dedicated shortcut and toggling the feature on.
                let isQuickCaptureRouting = (quickCaptureContext != nil)

                // ONBOARDING "GIVE IT A TRY": the transcript is surfaced inline in
                // the onboarding window only and must NEVER paste into another app,
                // regardless of the user's global `pasteResultText` setting. We do
                // not flip that setting — we just suppress delivery for this one
                // trigger. `lastTranscription` was already set above (line ~467),
                // which is what the onboarding view observes to render "You said …".
                let isOnboardingTry = (trigger == RecordingTriggerSource.onboarding.rawValue)
                let shouldDeliverText = !isOnboardingTry
                    && (isQuickCaptureRouting
                        || (settingsManager?.pasteResultText ?? false))

                if shouldDeliverText, let settings = settingsManager {
                    var processedText = transcriptionResult.text

                    // REMOVE TRAILING PERIOD:
                    // When enabled, strip the final period from transcriptions (but preserve ellipsis).
                    // Applied after post-processing but before smart spacing and auto-paste.
                    if transcriptionMode?.removeTrailingPeriod == true {
                        processedText = TranscriptionTextProcessing.removeTrailingPeriod(processedText)
                    }

                    // Snapshot for the Quick Capture path: Notes wants a fresh-note
                    // transcript before any paste-target adjustments below mutate it.
                    let notesText = processedText

                    // AUTOCAPITALIZE INSERT:
                    // Lowercase the first letter when the caret is mid-sentence
                    // in the focused text field. Sentence-start / unknown context
                    // pass through unchanged. Any AX failure returns .unknown so
                    // the text is left alone.
                    if settings.autocapitalizeInsert {
                        let context = AccessibilityHelper.shared.cursorContextOfFocusedElement()
                        processedText = AutocapitalizeInsert.apply(processedText, context: context)
                    }

                    // SMART SPACING FOR CONSECUTIVE TRANSCRIPTIONS:
                    // Adds trailing space based on language to enable seamless consecutive dictation.
                    // - Space-delimited languages (English, Danish, German, etc.): adds trailing space
                    // - CJK languages (Japanese, Chinese, Korean): no trailing space (words aren't separated by spaces)
                    // - Auto-detect mode: analyzes text content for CJK characters
                    //
                    // This solves the issue where consecutive recordings would paste without spacing:
                    // "Hello world.How are you?" → "Hello world. How are you?"
                    let modeLanguage = transcriptionMode?.language ?? "en"
                    let spacedText = SmartSpacing.appendTrailingSpace(processedText, modeLanguage: modeLanguage)

                    // Drives the success toast: "Saved to Notes!" vs "Pasted!".
                    // Set synchronously before the delivery await so RecordingDialog
                    // sees the correct value when `lastTranscription` changes — the
                    // Notes await can block 0.5–2s on cold launch, long enough for
                    // the dialog to render "Pasted!" first if we set this later.
                    appState?.lastDeliveryWasQuickCapture = isQuickCaptureRouting

                    // Quick Capture sessions go to Notes; everything else uses the
                    // accessibility-driven paste into the previously focused app.
                    Task { @MainActor in
                        let delivered: Bool
                        if isQuickCaptureRouting {
                            // Notes gets the un-paste-adjusted transcript:
                            // AutocapitalizeInsert reads the *previously focused*
                            // app's caret context (Slack/Safari/etc) and would
                            // demote a brand-new note's first letter; SmartSpacing's
                            // trailing space is for seamless paste, not a fresh note.
                            delivered = await NotesDestination.send(text: notesText)
                        } else {
                            delivered = await autoPasteHandler.handleAutoPaste(spacedText)
                        }

                        if delivered {
                            appState?.transcriptionPasteFailed = false
                            appState?.showRecordingDialog = false
                            appState?.isStreamingShortcutTriggered = false
                            if isQuickCaptureRouting {
                                AppLogger.audio.info("✅ Quick Capture: saved to Notes — closing dialog")
                            } else {
                                AppLogger.audio.info("✅ Auto-paste succeeded - closing dialog")
                            }
                        } else {
                            // Paste path: text is on the clipboard.
                            // Quick Capture path: NotesDestination has surfaced the banner.
                            appState?.transcriptionPasteFailed = true
                            appState?.showRecordingDialog = true
                            AppLogger.audio.info("📋 Text delivery failed - keeping dialog open")
                        }
                    }
                } else {
                    // AUTO-PASTE DISABLED: Keep dialog open
                    AppLogger.audio.info("📋 Auto-paste disabled - transcription in dialog only")
                    appState?.transcriptionPasteFailed = true
                    appState?.showRecordingDialog = true
                }

                // PRIVACY: Don't log actual transcription text - users export diagnostic logs
                let wordCount = transcriptionResult.text.split(separator: " ").count
                AppLogger.audio.info("✅ Transcription complete: \(transcriptionResult.text.count) chars, \(wordCount) words")
            }

            // BACKGROUND M4A CONVERSION:
            // After successful transcription, convert WAV to M4A if the setting is enabled.
            // This runs in a detached task to avoid blocking - transcription is already complete.
            //
            // Why after transcription:
            // 1. WAV files are more reliable for transcription (no codec issues)
            // 2. M4A compression is only for storage efficiency
            // 3. If conversion fails, we still have the successful transcription
            if audioURL.pathExtension.lowercased() == "wav",
               let settings = settingsManager,
               settings.storeAsM4A {
                Task {
                    await recordingLifecycle.performBackgroundWAVToM4AConversion(
                        transcript: processingTranscript,
                        wavURL: audioURL
                    )
                }
            }

        } catch is CancellationError {
            let flowElapsedMs = Int(Date().timeIntervalSince(flowStart) * 1000)
            let transcribingUIElapsedMs = transcribingUIStart.map { Int(Date().timeIntervalSince($0) * 1000) } ?? -1
            AppLogger.audio.info(
                "Recording transcription flow cancelled · attemptId=\(attemptId, privacy: .public) · trigger=\(trigger, privacy: .public) · mode=\(actualMode, privacy: .public) · flowMs=\(flowElapsedMs, privacy: .public) · transcribingUiMs=\(transcribingUIElapsedMs, privacy: .public)"
            )
            // User cancelled - treat as benign (no error toast shown)
            await MainActor.run {
                // CRITICAL: Clear lastTranscription to prevent error toast from stale values
                // The RecordingDialog checks lastTranscription on state changes
                appState?.lastTranscription = ""
                appState?.recordingState = .idle

                // CRITICAL: Disable cancel shortcut when cancelled
                KeyboardShortcuts.disable(.cancelRecording)
            }
            AppLogger.audio.info("❌ Transcription cancelled (no error shown)")
            clearActiveSessionMode()
            powerActivityManager.endPowerActivity()

        } catch {
            let flowElapsedMs = Int(Date().timeIntervalSince(flowStart) * 1000)
            let transcribingUIElapsedMs = transcribingUIStart.map { Int(Date().timeIntervalSince($0) * 1000) } ?? -1
            AppLogger.audio.error(
                "Recording transcription flow failed · attemptId=\(attemptId, privacy: .public) · trigger=\(trigger, privacy: .public) · mode=\(actualMode, privacy: .public) · flowMs=\(flowElapsedMs, privacy: .public) · transcribingUiMs=\(transcribingUIElapsedMs, privacy: .public) · error=\(error.localizedDescription, privacy: .public)"
            )
            handleTranscriptionError(error, processingTranscript: processingTranscript, mode: actualMode, duration: recordingDuration, audioURL: audioURL)
        }
    }
}
