//
//  RecordingTranscriptionFlow+Toggle.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import KeyboardShortcuts

extension RecordingTranscriptionFlow {

    // MARK: - Toggle Recording

    /// Toggle recording on/off with transcription
    ///
    /// **What This Does:**
    /// - If currently recording: stops and transcribes
    /// - If not recording: starts new recording (unless stopOnly = true)
    /// - Cancels any previous toggle task to prevent race conditions
    ///
    /// **Race Condition Fix:**
    /// Rapid keyboard shortcuts could start multiple recordings simultaneously.
    /// We cancel the previous task before starting a new one, ensuring only
    /// one recording is active at a time.
    ///
    /// **Parameters:**
    /// - `mode`: Transcription mode name (uses selected mode if nil)
    /// - `stopOnly`: If true, only stops recording (doesn't start new one)
    /// - `trigger`: Source of the user/system action that initiated the toggle
    ///
    /// **When to Call:**
    /// - User presses toggle recording shortcut
    /// - User clicks record button in UI
    /// - Auto-stop after silence timeout
    func toggleRecordingWithTranscription(
        mode: String? = nil,
        stopOnly: Bool = false,
        trigger: RecordingTriggerSource = .unknown
    ) {
        // PROTECT STOP FLOW: If currently stopping/finalizing, ignore new toggles.
        // The stop flow must complete fully to save the audio file and transcribe.
        // Without this, a double-press cancels the stop task mid-flight, causing
        // CancellationError + "Audio file missing" errors.
        if isStopInProgress {
            AppLogger.audio.info("Ignoring toggle — stop already in progress")
            // NOTE: Do not clear `quickCaptureContext` here. A normal toggle pressed
            // during an in-flight Quick Capture stop must NOT wipe the context that
            // stop flow is still using to decide routing. The Quick Capture leak case
            // (QC toggle pressed during a stop) is prevented in `toggleQuickCapture`
            // at the call site — it skips setting context when `isStopInProgress`.
            return
        }

        // CRITICAL: Cancel any previous toggle task to ensure serial execution
        // This prevents race conditions from rapid keyboard shortcuts
        if toggleTask != nil {
            SentryService.addBreadcrumb(
                message: "Recording toggle cancelled previous request",
                category: "audio.toggle",
                data: [
                    "stopOnly": stopOnly,
                    "wasRecording": recordingLifecycle.isRecording,
                    "wasStreamingActive": isStreamingActive,
                    "trigger": trigger.rawValue,
                    "attemptId": currentRecordingAttemptId ?? "none"
                ]
            )
        }
        currentRecordingTriggerSource = trigger
        toggleTask?.cancel()

        // Create a new task for this toggle operation
        // The task will check for cancellation at key points
        toggleTask = Task {
            // CHECK FOR CANCELLATION: If this task was cancelled (due to a newer toggle),
            // exit immediately to avoid conflicting operations
            guard !Task.isCancelled else {
                AppLogger.audio.debug("Toggle task cancelled before execution")
                return
            }

            // Use appState.selectedModeName as the single source of truth
            let modeToUse = mode ?? appState?.currentSessionModeName ?? "Default"

            if recordingLifecycle.isRecording || isStreamingActive {
                // STOP RECORDING AND TRANSCRIBE (includes streaming sessions)
                // Check again for cancellation before stopping
                guard !Task.isCancelled else {
                    AppLogger.audio.debug("Toggle task cancelled before stop operation")
                    return
                }
                await handleStopRecordingWithTranscription(mode: modeToUse, cancelled: stopOnly)
            } else if !stopOnly {
                // START RECORDING
                // Check again for cancellation before starting
                guard !Task.isCancelled else {
                    AppLogger.audio.debug("Toggle task cancelled before start operation")
                    return
                }
                await handleStartRecording(mode: modeToUse)
            }
        }
    }

    // MARK: - Cancel Shortcut

    /// Handles user request to cancel recording
    ///
    /// **What This Does:**
    /// - If confirmation is showing: dismisses it (resumes recording)
    /// - If recording > 15 seconds: shows confirmation dialog
    /// - If recording ≤ 15 seconds: cancels immediately
    ///
    /// **Why 15 Second Threshold:**
    /// Short recordings are likely accidental starts - quick cancel is convenient.
    /// Long recordings represent significant work - confirmation prevents accidental loss.
    func handleCancelShortcut() {
        guard let appState = appState else { return }

        // If confirmation is visible, pressing cancel dismisses it (resume recording)
        if appState.showCancelConfirmation {
            appState.showCancelConfirmation = false
            AppLogger.ui.debug("↩️ Dismissed cancel confirmation, continuing recording")
            return
        }

        // STREAMING MODE: Cancel streaming session immediately
        // Streaming doesn't have a "duration" concept since text is typed live,
        // so we don't show confirmation - just cancel immediately.
        if isStreamingActive {
            AppLogger.ui.debug("❌ Cancelled streaming transcription via shortcut")
            toggleTask?.cancel()

            // Cancel streaming in background
            Task {
                await handleStopRecordingWithTranscription(mode: appState.currentSessionModeName, cancelled: true)
            }
            return
        }

        // If actively transcribing or post-processing, cancel immediately
        if appState.recordingState == .transcribing || appState.recordingState == .postProcessing {
            AppLogger.ui.debug("❌ Cancelled transcription/processing via shortcut")
            transcriptionPipeline?.cancelTranscription()
            toggleTask?.cancel() // Ensure the coordinator task is also cancelled

            // CRITICAL: Clear lastTranscription before setting state to idle
            // This prevents RecordingDialog's onChange observer from showing a stale
            // error message when it sees the state transition to .idle
            appState.lastTranscription = ""

            appState.recordingState = .idle
            appState.showRecordingDialog = false
            appState.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag

            // Clean up cancel shortcut since we're done
            KeyboardShortcuts.disable(.cancelRecording)
            clearActiveSessionMode()
            return
        }

        // Show confirmation after 15 seconds of recording
        if recordingLifecycle.recordingDuration > 15 {
            appState.showCancelConfirmation = true
        } else {
            toggleRecordingWithTranscription(stopOnly: true, trigger: .cancelShortcut)
            appState.showRecordingDialog = false
            appState.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag
            AppLogger.ui.debug("❌ Cancelled recording via shortcut")
        }
    }
}
