//
//  RecordingTranscriptionFlow+StopRecording.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import AVFoundation
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
        // WS4: lightweight per-stage timings (ms). -1 = stage not reached.
        var wavReadyMs = -1
        var fileCheckMs = -1
        var vadTrimMs = -1
        var createRowMs = -1
        var transcribeMs = -1
        var coreDataUpdateMs = -1
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
        let wavReadyStart = Date()
        guard let result = await recordingLifecycle.stopRecording(cancelled: cancelled) else {
            await MainActor.run {
                appState?.recordingState = .idle
            }
            clearActiveSessionMode()
            powerActivityManager.endPowerActivity()
            return
        }
        wavReadyMs = Int(Date().timeIntervalSince(wavReadyStart) * 1000)

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
                await recordingLifecycle.sessionManager.deleteSession(session, deleteAudioFile: false)
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

        // FILE READINESS CHECK (WS3):
        // `recordingLifecycle.stopRecording()` already ran `waitForRawFileReady`,
        // which guarantees the file exists and is ≥5KB before this flow proceeds.
        // The old downstream `fileWatcher.waitForFirstWrite` + 5×100ms + 20×150ms
        // re-waited on that same validated file, adding up to ~1.2s. Collapse it to a
        // single open/readability test plus one short retry for the genuinely-async
        // edge (the file was validated moments ago, so this virtually always passes
        // on the first check).
        let fileCheckStart = Date()
        var isReadable = isAudioFileReadable(audioURL)
        if !isReadable {
            for _ in 1...2 {
                try? await Task.sleep(nanoseconds: 50_000_000) // 50ms
                if isAudioFileReadable(audioURL) {
                    isReadable = true
                    break
                }
            }
        }
        fileCheckMs = Int(Date().timeIntervalSince(fileCheckStart) * 1000)

        if !isReadable {
            AppLogger.audio.error("Audio file not readable after stop-flow check.")
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Audio file unreadable after wait",
                    category: "audio.recording",
                    level: .error,
                    data: [
                        "audioPath": audioURL.path,
                        "mode": sessionModeName,
                        "recordingsFolder": settingsManager?.recordingsFolder ?? "unknown"
                    ]
                )
            }
            await MainActor.run {
                appState?.recordingState = .idle
                appState?.lastTranscription = "Error: Audio file could not be read"
                appState?.pendingRetryAudioPath = audioURL.path
                appState?.showRecordingDialog = true
                KeyboardShortcuts.disable(.cancelRecording)
            }
            // Persist failed attempt to history so user can retry later — one write.
            _ = await PersistenceController.shared.createFailedTranscriptInBackground(
                duration: recordingDuration,
                mode: sessionModeName,
                audioFilePath: audioURL.path,
                failedReason: "Audio file could not be read",
                errorText: "Error: Audio file could not be read"
            )

            powerActivityManager.endPowerActivity()
            return
        }

        // VAD SILENCE TRIMMING (Optional)
        // Uses VADProcessingService to analyze audio and trim leading/trailing silence.
        // Benefits: Reduced API costs, faster transcription, potentially better accuracy.
        // Only applies to recordings >= 30 seconds when VAD is enabled in settings.
        let vadStart = Date()
        let vadResult = await vadProcessingService.processAudioForTranscription(
            audioURL: audioURL,
            duration: recordingDuration,
            vadEnabled: settingsManager?.enableVAD ?? false,
            context: "Recording"
        )
        vadTrimMs = Int(Date().timeIntervalSince(vadStart) * 1000)
        let finalAudioURL = vadResult.finalAudioURL
        let trimResult = vadResult.trimResult

        // TRANSCRIPTION FLOW
        // Step 1: Create processing transcript on the serial background writer.
        // The VAD trimmed path (if any) is folded into this single create, so the
        // separate setTrimmedAudioPath write no longer exists on the stop path.
        let actualMode = sessionModeName
        let createRowStart = Date()
        let processingTranscriptID = await PersistenceController.shared.createProcessingTranscriptInBackground(
            duration: recordingDuration,
            mode: actualMode,
            audioFilePath: audioURL.path,
            trimmedAudioPath: (vadResult.wasProcessed ? trimResult?.outputURL.path : nil)
        )
        createRowMs = Int(Date().timeIntervalSince(createRowStart) * 1000)
        if processingTranscriptID == nil {
            AppLogger.audio.warning("⚠️ Failed to create processing transcript row — transcription/paste proceed, persistence updates skipped")
        } else {
            AppLogger.audio.info("💾 Created processing transcript: duration=\(recordingDuration)s, mode=\(actualMode)")
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

            // Use finalAudioURL which may be VAD-trimmed if VAD was enabled.
            // `transcribe` folds provider selection + transcription + post-processing;
            // its wall time is surfaced as the transcribe stage.
            let transcribeStart = Date()
            let transcriptionResult = try await transcriptionMgr.transcribeWithDetails(
                audioURL: finalAudioURL,
                mode: transcriptionMode,
                recordingSession: nil, // Will be set by RecordingLifecycle
                applicationContext: capturedApplicationContext
            )
            transcribeMs = Int(Date().timeIntervalSince(transcribeStart) * 1000)

            // No local usage recording — local transcription is unlimited (open source).

            // Step 5: paste FIRST (latency independent of the DB write), then persist
            // the completed transcript on the serial background writer. The order swap
            // keeps the ~tens-of-ms Core Data update off the stop→paste path.
            let pasteStart = Date()
            await MainActor.run {
                appState?.lastTranscription = transcriptionResult.text
                appState?.recordingState = .idle
                appState?.pendingRetryAudioPath = nil

                // CRITICAL: Disable cancel shortcut when transcription completes
                // Without this, the shortcut stays active even when idle
                KeyboardShortcuts.disable(.cancelRecording)
                clearActiveSessionMode()

                powerActivityManager.endPowerActivity()

                // Quick Capture always routes to Notes, regardless of the
                // `pasteResultText` setting. The user opted in by binding a
                // dedicated shortcut and toggling the feature on.
                let isQuickCaptureRouting = (quickCaptureContext != nil)
                let shouldDeliverText = isQuickCaptureRouting
                    || (settingsManager?.pasteResultText ?? false)

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

                        // Paste runs concurrently with the Core Data write, so its
                        // latency is logged here rather than in the flow-completion line.
                        let pasteElapsedMs = Int(Date().timeIntervalSince(pasteStart) * 1000)
                        if delivered {
                            appState?.transcriptionPasteFailed = false
                            appState?.showRecordingDialog = false
                            appState?.isStreamingShortcutTriggered = false
                            if isQuickCaptureRouting {
                                AppLogger.audio.info("✅ Quick Capture: saved to Notes — closing dialog · pasteMs=\(pasteElapsedMs)")
                            } else {
                                AppLogger.audio.info("✅ Auto-paste succeeded - closing dialog · pasteMs=\(pasteElapsedMs)")
                            }
                        } else {
                            // Paste path: text is on the clipboard.
                            // Quick Capture path: NotesDestination has surfaced the banner.
                            appState?.transcriptionPasteFailed = true
                            appState?.showRecordingDialog = true
                            AppLogger.audio.info("📋 Text delivery failed - keeping dialog open · pasteMs=\(pasteElapsedMs)")
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

            // Persist the completed transcript on the serial background writer.
            // Runs AFTER paste was dispatched, so paste latency is independent of it.
            if let processingTranscriptID {
                let coreDataStart = Date()
                await PersistenceController.shared.updateTranscriptWithTranscriptionInBackground(
                    transcriptID: processingTranscriptID,
                    transcribedText: transcriptionResult.rawText,
                    postProcessedText: transcriptionResult.wasPostProcessed ? transcriptionResult.text : nil,
                    transcriptionProvider: transcriptionResult.provider,
                    postProcessingProvider: transcriptionResult.postProcessingProvider,
                    wordTimestampsJSON: transcriptionResult.timestamps?.wordTimestampsJSON()
                )
                coreDataUpdateMs = Int(Date().timeIntervalSince(coreDataStart) * 1000)
                AppLogger.audio.info("✅ Updated transcript with transcription")
            }

            // BACKGROUND M4A CONVERSION:
            // After successful transcription, convert WAV to M4A if the setting is enabled.
            // This runs in a detached task to avoid blocking - transcription is already complete.
            // Enqueues after the completed-status update on the same serial queue, so the
            // completed status lands before the path rewrite.
            //
            // Why after transcription:
            // 1. WAV files are more reliable for transcription (no codec issues)
            // 2. M4A compression is only for storage efficiency
            // 3. If conversion fails, we still have the successful transcription
            if audioURL.pathExtension.lowercased() == "wav",
               let settings = settingsManager,
               settings.storeAsM4A,
               let processingTranscriptID {
                Task {
                    await recordingLifecycle.performBackgroundWAVToM4AConversion(
                        transcriptID: processingTranscriptID,
                        wavURL: audioURL
                    )
                }
            }

            // FLOW COMPLETION LOG (WS4): all per-stage timings on one line.
            let flowElapsedMs = Int(Date().timeIntervalSince(flowStart) * 1000)
            let transcribingUIElapsedMs = transcribingUIStart.map { Int(Date().timeIntervalSince($0) * 1000) } ?? -1
            let trimmedSeconds = trimResult.map { String(format: "%.1f", $0.silenceRemoved) } ?? "0.0"
            let stageTimings = "wavReadyMs=\(wavReadyMs) · fileCheckMs=\(fileCheckMs) · vadTrimMs=\(vadTrimMs) · createRowMs=\(createRowMs) · transcribeMs=\(transcribeMs) · coreDataUpdateMs=\(coreDataUpdateMs)"
            let uiLogMessage =
                "Recording transcription flow succeeded · attemptId=\(attemptId) · trigger=\(trigger) · mode=\(actualMode) · provider=\(transcriptionResult.provider) · flowMs=\(flowElapsedMs) · transcribingUiMs=\(transcribingUIElapsedMs) · vadProcessed=\(vadResult.wasProcessed) · silenceRemovedSeconds=\(trimmedSeconds) · \(stageTimings)"

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
                // Slow path: attach per-stage timings as scope extras (they survive
                // beforeSend, unlike breadcrumbs) so any subsequent event carries them.
                SentryService.setExtras([
                    "stage_wav_ready_ms": wavReadyMs,
                    "stage_file_check_ms": fileCheckMs,
                    "stage_vad_trim_ms": vadTrimMs,
                    "stage_create_row_ms": createRowMs,
                    "stage_transcribe_ms": transcribeMs,
                    "stage_core_data_update_ms": coreDataUpdateMs,
                    "stage_flow_ms": flowElapsedMs
                ])
                AppLogger.audio.warning("\(uiLogMessage, privacy: .public)")
            } else {
                AppLogger.audio.info("\(uiLogMessage, privacy: .public)")
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
            let stageTimings = "wavReadyMs=\(wavReadyMs) · fileCheckMs=\(fileCheckMs) · vadTrimMs=\(vadTrimMs) · createRowMs=\(createRowMs) · transcribeMs=\(transcribeMs)"
            AppLogger.audio.error(
                "Recording transcription flow failed · attemptId=\(attemptId, privacy: .public) · trigger=\(trigger, privacy: .public) · mode=\(actualMode, privacy: .public) · flowMs=\(flowElapsedMs, privacy: .public) · transcribingUiMs=\(transcribingUIElapsedMs, privacy: .public) · \(stageTimings, privacy: .public) · error=\(error.localizedDescription, privacy: .public)"
            )
            // Attach per-stage timings as scope extras so the pipeline's error event carries them.
            SentryService.setExtras([
                "stage_wav_ready_ms": wavReadyMs,
                "stage_file_check_ms": fileCheckMs,
                "stage_vad_trim_ms": vadTrimMs,
                "stage_create_row_ms": createRowMs,
                "stage_transcribe_ms": transcribeMs,
                "stage_flow_ms": flowElapsedMs
            ])
            handleTranscriptionError(error, processingTranscriptID: processingTranscriptID, mode: actualMode, duration: recordingDuration, audioURL: audioURL)
        }
    }

    /// Definitive readability test for the finalized recording file (WS3).
    /// The lifecycle already validated existence + size; this confirms the file
    /// opens as a decodable audio file before handing it to transcription.
    private func isAudioFileReadable(_ url: URL) -> Bool {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path), fm.isReadableFile(atPath: url.path) else {
            return false
        }
        return (try? AVAudioFile(forReading: url)) != nil
    }
}
