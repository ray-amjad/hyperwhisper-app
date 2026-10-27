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

        // A toggle that starts or stops a session is a newer flow: an in-flight
        // pending-file retry must not write its outcome over it (#1276).
        let takeOverGeneration: UInt64? = Self.toggleTakesOverSession(
            stopOnly: stopOnly,
            isRecording: recordingLifecycle.isRecording,
            isStreamingActive: isStreamingActive
        ) ? appState?.beginTranscriptionSession() : nil

        // Create a new task for this toggle operation
        // The task will check for cancellation at key points
        toggleTask = Task {
            // The one start/stop decision of this run. No suspension until the
            // branch below, so the cancel guard and the branch agree on it.
            let stopsASession = recordingLifecycle.isRecording || isStreamingActive
            let startsASession = !stopsASession && !stopOnly

            // CHECK FOR CANCELLATION: If this task was cancelled (due to a newer toggle),
            // exit immediately to avoid conflicting operations
            guard !Task.isCancelled else {
                AppLogger.audio.debug("Toggle task cancelled before execution")
                // Only a start can end having started nothing; a stop's own
                // flow owns the state it leaves (#1394).
                if startsASession {
                    releaseStateNoSessionTookOver(takeOverGeneration)
                }
                return
            }

            // Use appState.selectedModeName as the single source of truth
            let modeToUse = mode ?? appState?.currentSessionModeName ?? "Default"

            if stopsASession {
                // STOP RECORDING AND TRANSCRIBE (includes streaming sessions)
                // Check again for cancellation before stopping
                guard !Task.isCancelled else {
                    AppLogger.audio.debug("Toggle task cancelled before stop operation")
                    return
                }
                await handleStopRecordingWithTranscription(mode: modeToUse, cancelled: stopOnly)
            } else if startsASession {
                // START RECORDING
                // Check again for cancellation before starting
                guard !Task.isCancelled else {
                    AppLogger.audio.debug("Toggle task cancelled before start operation")
                    releaseStateNoSessionTookOver(takeOverGeneration)
                    return
                }
                await handleStartRecording(mode: modeToUse)
                // A start that returned early (microphone permission denied,
                // a newer toggle) recorded nothing, yet its bump above
                // superseded an in-flight retry, which now writes nothing.
                releaseStateNoSessionTookOver(takeOverGeneration)
            }
        }
    }

    /// Sets `recordingState` back to idle when this toggle began a session
    /// (`generation`), nothing newer began since, nothing records, no stop is
    /// in progress, and the state still reads `.transcribing` /
    /// `.postProcessing` (#1394).
    ///
    /// That state belongs to the flow this toggle superseded: a pending-file
    /// retry that now writes nothing, or a file import whose release no longer
    /// resets it. Without this, a start that returns early leaves the dialog
    /// and the menu bar on "transcribing" for good. A start that records sets
    /// `.recording` and is left alone; one that failed already set `.idle`.
    ///
    /// A start that DID record can also end before `handleStartRecording`
    /// returns: a lost capture or the max-duration auto-stop stops it during
    /// the start's later awaits, without a generation bump. That stop's
    /// dictation is `.transcribing` with nothing recording, and it is the
    /// user's own: `isStopInProgress` is held across the whole stop, so the
    /// release refuses while it is set.
    private func releaseStateNoSessionTookOver(_ generation: UInt64?) {
        guard let appState else { return }
        guard Self.shouldReleaseStateAfterToggle(
            takeOverGeneration: generation,
            currentSessionGeneration: appState.transcriptionSessionGeneration,
            isRecording: recordingLifecycle.isRecording,
            isStreamingActive: isStreamingActive,
            isStopInProgress: isStopInProgress,
            recordingState: appState.recordingState
        ) else { return }
        AppLogger.audio.info("Toggle took the session over but started nothing; releasing the superseded flow's transcribing state")
        // Same order as the cancel shortcut: an empty text first, so the
        // dialog's idle handler closes it instead of showing a stale error.
        appState.lastTranscription = ""
        appState.recordingState = .idle
        appState.showRecordingDialog = false
    }

    /// The decision behind `releaseStateNoSessionTookOver(_:)`. Pure, so it is
    /// tested by calling it.
    nonisolated static func shouldReleaseStateAfterToggle(
        takeOverGeneration: UInt64?,
        currentSessionGeneration: UInt64,
        isRecording: Bool,
        isStreamingActive: Bool,
        isStopInProgress: Bool,
        recordingState: RecordingState
    ) -> Bool {
        return false // MUTANT: path-2 release disabled

        guard let takeOverGeneration, takeOverGeneration == currentSessionGeneration else { return false }
        guard !isRecording, !isStreamingActive, !isStopInProgress else { return false }
        return recordingState == .transcribing || recordingState == .postProcessing
    }

    /// Whether a toggle starts or stops a session, and so supersedes an
    /// in-flight pending-file retry (#1276). A stop-only toggle with nothing
    /// recording does nothing, so it takes nothing over: bumping the generation
    /// for it would leave the retry's dialog stuck on "transcribing".
    nonisolated static func toggleTakesOverSession(
        stopOnly: Bool,
        isRecording: Bool,
        isStreamingActive: Bool
    ) -> Bool {
        !stopOnly || isRecording || isStreamingActive
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
            transcriptionPipeline?.cancelTranscription(.dictation)
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
