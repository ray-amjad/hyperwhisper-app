//
//  RecordingDialog.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  RECORDING DIALOG
//  A dialog window that appears during recording with animated waveform visualization.
//  Shows real-time audio levels, recording duration, and control buttons.
//

import SwiftUI
import AVFoundation
import AppKit
import KeyboardShortcuts
import os

/// Logger for RecordingDialog (static to work with SwiftUI structs)
private let recordingDialogLogger = Logger(subsystem: "com.hyperwhisper.app", category: "RecordingDialog")

enum RecordingDialogIdleCompletionAction: Equatable {
    case none
    case close
    case showError(String)
    case showTranscription(String)

    static func resolve(wasLoadingOrPostProcessing: Bool, lastTranscription: String) -> RecordingDialogIdleCompletionAction {
        guard wasLoadingOrPostProcessing else { return .none }

        if lastTranscription.isEmpty {
            return .close
        }

        if lastTranscription.hasPrefix("Error:") {
            return .showError(lastTranscription)
        }

        return .showTranscription(lastTranscription)
    }
}

// MARK: - Recording Dialog View

struct RecordingDialog: View {
    @EnvironmentObject var audioManager: AudioRecordingManager
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    /// High-frequency metrics (audioLevel, recordingDuration) isolated for performance.
    /// This prevents MainAppView from re-evaluating at 30 FPS during recording.
    @EnvironmentObject var liveMetrics: RecordingLiveMetrics
    /// High-frequency streaming preview text, isolated for the same reason (HYPERWHISPER-F7).
    @EnvironmentObject var liveStreamingText: StreamingLiveText
    @ObservedObject private var network = NetworkStatus.shared
    
    @Binding var isPresented: Bool
    
    // MARK: - Size Configuration
    // Compact horizontal bar design
    private let dialogHeight: CGFloat = 40
    private let baseDialogWidth: CGFloat = 200
    private let controlHeight: CGFloat = 24  // Stop button size
    
    // Mode selection
    @State private var modeCycleRotation: Double = 0
    @State private var modeJustCycled = false
    
    // Dialog display states
    @State private var isLoading = false
    @State private var isPostProcessing = false  // New state for AI post-processing
    @State private var transcribedText: String = ""
    @State private var showTranscription = false
    @State private var hasError = false
    @State private var showCopiedFeedback = false
    @State private var isRetrying = false
    @State private var transcriptActionHandler: TranscriptActionHandler?
    @State private var showSuccessState = false  // Brief success indicator

    // MARK: - Animation States
    @State private var stopButtonPressed = false  // Stop button press animation
    @State private var stopButtonHovered = false  // Stop button hover state
    @State private var successScale: CGFloat = 0.0  // Success state scale animation
    @State private var stateTransitionScale: CGFloat = 1.0  // Scale for state transitions
    @State private var pulseAnimation = false  // Pulse animation for connection status indicator
    
    // MARK: - Computed Properties
    
    /// Text to show in the loading overlay
    private var loadingStatusText: String {
        if appState.isStreamingShortcutTriggered {
            switch appState.streamingConnectionState {
            case .warmingUp:
                return "streaming.loading.warming".localized
            case .connecting:
                return "streaming.loading.connecting".localized
            case .ready:
                return "streaming.loading.ready".localized
            case .streaming:
                return "streaming.loading.streaming".localized
            case .reconnecting:
                return "streaming.state.reconnecting".localized
            default:
                return "recording.loading.starting".localized
            }
        }

        // Existing non-streaming logic
        if isRetrying {
            return "recording.loading.retrying".localized
        } else if isPostProcessing {
            return "recording.loading.postprocessing".localized
        } else {
            return "recording.loading.transcribing".localized
        }
    }

    private var dialogWidth: CGFloat {
        return baseDialogWidth
    }
    
    /// Streaming status indicator with fixed footprint so the waveform width stays stable.
    @ViewBuilder
    private var connectionStatusIndicator: some View {
        if appState.isStreamingShortcutTriggered {
            Group {
                switch appState.streamingConnectionState {
                case .warmingUp, .connecting, .ready:
                    Circle()
                        .fill(Color.orange)
                        .opacity(pulseAnimation ? 0.3 : 1.0)
                case .streaming:
                    Circle()
                        .fill(Color.green)
                case .reconnecting:
                    Circle()
                        .fill(Color.yellow)
                        .opacity(pulseAnimation ? 0.3 : 1.0)
                default:
                    Color.clear
                }
            }
            .frame(width: 6, height: 6)
            .animation(.easeInOut(duration: 0.8).repeatForever(), value: pulseAnimation)
            .onAppear { pulseAnimation = true }
        }
    }

    var body: some View {
        // CRITICAL: Multiple clipping layers to ensure perfect rounded corners
        // Using cornerRadius with explicit value instead of Capsule for more control
        Group {
            if appState.showCancelConfirmation {
                // Cancel confirmation state
                cancelConfirmationView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            } else if hasError {
                // API/transcription error - just show close button
                apiErrorStateView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            } else if showTranscription && appState.transcriptionPasteFailed {
                // Paste failed (or auto-paste disabled) - show copy button for transcription
                pasteFailureStateView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            } else if showTranscription {
                // Success state after transcription
                successStateView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            } else if shouldShowPendingRetry {
                pendingRetryStateView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            } else if isLoading || isPostProcessing || isRetrying {
                // Loading state - spinner + text
                loadingStateView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            } else {
                // Recording state - horizontal bar
                recordingStateView
                    .transition(.opacity.combined(with: .scale(scale: 0.95)))
            }
        }
        .scaleEffect(stateTransitionScale)
        .animation(.spring(response: 0.25, dampingFraction: 0.75), value: showSuccessState)
        .animation(.spring(response: 0.25, dampingFraction: 0.75), value: isLoading)
        .animation(.spring(response: 0.25, dampingFraction: 0.75), value: hasError)
        .animation(.spring(response: 0.25, dampingFraction: 0.75), value: appState.showCancelConfirmation)
        .frame(width: dialogWidth, height: dialogHeight)
        .background(
            RoundedRectangle(cornerRadius: dialogHeight / 2)
                .fill(Color.black.opacity(0.85))
        )
        .cornerRadius(dialogHeight / 2)
        .overlay(
            RoundedRectangle(cornerRadius: dialogHeight / 2)
                .stroke(Color.white.opacity(0.1), lineWidth: 1)
        )
        .clipped()
        .shadow(color: .black.opacity(0.3), radius: 20, y: 10)
        .focusable()
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { _ in
            // Ensure the dialog window has focus for keyboard shortcuts
        }
        .onChange(of: appState.showCancelConfirmation) { isShowing in
            // CRITICAL: Start/stop global key interception for cancel confirmation
            // When cancel confirmation is shown, we need to capture Enter/Escape
            // without activating the app (which would show main window)
            if isShowing {
                RecordingWindowManager.shared.beginOverlayKeyInterception(
                    onReturn: {
                        // Enter pressed - confirm cancellation
                        // Already on MainActor from the handler
                        DispatchQueue.main.async {
                            cancelRecording()
                        }
                    },
                    onEscape: {
                        // Escape pressed - dismiss confirmation
                        // Already on MainActor from the handler
                        DispatchQueue.main.async {
                            appState.showCancelConfirmation = false
                        }
                    }
                )
            } else {
                RecordingWindowManager.shared.endOverlayKeyInterception()
            }
        }
        .onAppear {
            // CRITICAL: Reset ALL states when dialog appears for new recording
            // This ensures old transcription doesn't persist when starting a new recording
            
            // Reset local dialog states
            isLoading = false
            isPostProcessing = false
            showTranscription = false
            hasError = false
            transcribedText = ""
            showCopiedFeedback = false

            // Reset animation states
            successScale = 0.0
            stopButtonPressed = false
            stopButtonHovered = false
            stateTransitionScale = 1.0
            
            // CRITICAL: Clear AppState transcription data to prevent showing old results
            // When user starts a new recording, these must be cleared or the dialog
            // will show the previous transcription instead of the waveform
            appState.lastTranscription = ""
            liveStreamingText.text = ""
            appState.isStreaming = false
            appState.transcriptionPasteFailed = false

            if transcriptActionHandler == nil {
                transcriptActionHandler = TranscriptActionHandler(transcriptionPipeline: transcriptionPipeline)
            }
        }
        .onDisappear {
            // CRITICAL: Ensure any active event tap is cleaned up when dialog closes
            // This prevents crashes from stale taps trying to access deallocated views
            RecordingWindowManager.shared.endOverlayKeyInterception()
        }
        .onChange(of: appState.recordingState) { newState in
            // Monitor recording state changes
            switch newState {
            case .recording:
                // CRITICAL: New recording started - reset dialog to show waveform
                // This handles the case where user starts recording again while dialog shows old transcript
                isLoading = false
                isPostProcessing = false
                showTranscription = false
                hasError = false
                transcribedText = ""
                showCopiedFeedback = false
                isRetrying = false
                
                // Clear any lingering transcription data
                appState.lastTranscription = ""
                liveStreamingText.text = ""
                appState.isStreaming = false
                appState.transcriptionPasteFailed = false
                break
            case .transcribing:
                // Transcription has started - ensure we're in loading state
                if !showTranscription {
                    isLoading = true
                    isPostProcessing = false
                }
                break
            case .postProcessing:
                // AI post-processing has started
                if !showTranscription {
                    isLoading = false
                    isPostProcessing = true
                }
                break
            case .idle:
                // Transcription completed or cancelled
                // Only process if we were actually loading or post-processing (not during initial recording)
                switch RecordingDialogIdleCompletionAction.resolve(
                    wasLoadingOrPostProcessing: isLoading || isPostProcessing,
                    lastTranscription: appState.lastTranscription
                ) {
                case .none:
                    break
                case .close:
                    recordingDialogLogger.info("Empty transcription completed while loading; closing recording dialog")
                    closeDialog()
                case .showError(let errorText):
                    // Show inline error toast and close the dialog
                    showTranscriptionErrorAlert(errorText: errorText)
                    closeDialog()
                case .showTranscription(let text):
                    // Show successful transcription result
                    transcribedText = text
                    isLoading = false
                    isPostProcessing = false
                    hasError = false
                    showTranscription = true
                }
            default:
                break
            }
        }
        .onChange(of: appState.lastTranscription) { newText in
            // Monitor for transcription updates
            if (isLoading || isPostProcessing) && !newText.isEmpty {
                // Check if it's an error
                if newText.hasPrefix("Error:") {
                    // Show inline error toast and close the dialog
                    showTranscriptionErrorAlert(errorText: newText)
                    closeDialog()
                } else {
                    // Successful transcription - update view
                    transcribedText = newText
                    isLoading = false
                    isPostProcessing = false
                    hasError = false
                    handleTranscriptionUpdate(text: newText)
                }
            }
        }
        .onChange(of: appState.currentSessionModeName) { newModeName in
            // Animate the rotation and scale when mode changes via keyboard shortcut
            withAnimation(.easeInOut(duration: 0.3)) {
                modeCycleRotation += 360 // Full rotation on mode change
                modeJustCycled = true
            }
            
            // Reset scale after animation
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                withAnimation(.easeOut(duration: 0.2)) {
                    modeJustCycled = false
                }
            }
        }
        .onChange(of: appState.isStreaming) { isNowStreaming in
            // When streaming ends, there is a brief moment before final text is set.
            // Defer classification slightly to avoid flashing error state on success.
            if !isNowStreaming {
                let partial = liveStreamingText.text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !partial.isEmpty else { return }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                    // Only treat as error if we still have no final text and streaming hasn't resumed
                    if !appState.isStreaming && appState.lastTranscription.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        transcribedText = partial
                        isLoading = false
                        isPostProcessing = false
                        hasError = true
                        showTranscription = true
                    }
                }
            }
        }
    }

    // MARK: - View Builders
    
    // MARK: - State Views

    /// Recording state - horizontal layout with stop, mode, waveform
    @ViewBuilder
    private var recordingStateView: some View {
        HStack(spacing: 6) {
            // Stop button with hover and press animations
            Button {
                // Trigger press animation
                withAnimation(.spring(response: 0.2, dampingFraction: 0.6)) {
                    stopButtonPressed = true
                }
                // Reset after animation
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                    stopButtonPressed = false
                }
                stopRecording()
            } label: {
                Circle()
                    .fill(Color.red.opacity(stopButtonHovered ? 0.95 : 0.9))
                    .frame(width: controlHeight, height: controlHeight)
                    .overlay(
                        Image(systemName: "stop.fill")
                            .font(.system(size: 10))
                            .foregroundColor(.white)
                    )
                    .scaleEffect(stopButtonPressed ? 0.85 : (stopButtonHovered ? 1.05 : 1.0))
                    .animation(.spring(response: 0.3, dampingFraction: 0.6), value: stopButtonPressed)
                    .animation(.spring(response: 0.3, dampingFraction: 0.7), value: stopButtonHovered)
            }
            .buttonStyle(.plain)
            .onHover { hovering in
                stopButtonHovered = hovering
            }
            .help("recording.stop.help".localized)

            // Mode badge - only show for non-streaming recordings
            if !appState.isStreamingShortcutTriggered {
                Text(appState.currentSessionModeName)
                    .font(.system(size: 9, weight: .medium))
                    .foregroundColor(.white.opacity(0.9))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 3)
                    .background(
                        Capsule()
                            .fill(Color.white.opacity(0.15))
                    )
                    .lineLimit(1)
            }

            if appState.isStreamingShortcutTriggered {
                HStack {
                    Spacer(minLength: 0)
                    connectionStatusIndicator
                    Spacer(minLength: 0)
                }
                .frame(width: 20)
            }

            // Waveform - Core Animation based for lag-free rendering
            WaveformCARepresentable(level: CGFloat(liveMetrics.audioLevel))
                .frame(maxWidth: .infinity)
                .frame(height: 24)
                .padding(.horizontal, 4)  // Extra padding to keep bars away from curved edges
                .clipped()
        }
        .padding(.horizontal, 8)
    }

    /// Loading state - spinner with text
    @ViewBuilder
    private var loadingStateView: some View {
        HStack(spacing: 10) {
            ProgressView()
                .progressViewStyle(.circular)
                .controlSize(.small)
                .tint(.white)

            Text(loadingStatusText)
                .font(.system(size: 12, weight: .medium))
                .foregroundColor(.white.opacity(0.9))
        }
    }

    /// Success state - animated checkmark with text
    @ViewBuilder
    private var successStateView: some View {
        let wasQuickCapture = appState.lastDeliveryWasQuickCapture
        let iconName = wasQuickCapture ? "note.text.badge.plus" : "checkmark.circle.fill"
        let labelKey = wasQuickCapture
            ? "recording.success.savedToNotes"
            : "recording.success.pasted"

        HStack(spacing: 10) {
            // Animated checkmark with spring bounce
            ZStack {
                // Circle background
                Circle()
                    .fill(Color.green.opacity(0.2))
                    .frame(width: 16, height: 16)
                    .scaleEffect(successScale)

                // Checkmark / destination icon with scale animation
                Image(systemName: iconName)
                    .font(.system(size: 16))
                    .foregroundColor(.green)
                    .scaleEffect(successScale)
            }

            // Text with slide-in animation
            Text(labelKey.localized)
                .font(.system(size: 12, weight: .medium))
                .foregroundColor(.white.opacity(0.9))
                .opacity(successScale > 0.5 ? 1.0 : 0.0)
                .offset(x: successScale > 0.5 ? 0 : 10)
        }
        .onAppear {
            // Trigger checkmark pop animation with overshoot
            // Fast animation (0.3s) to complete before 1s dialog close
            withAnimation(.spring(response: 0.3, dampingFraction: 0.6)) {
                successScale = 1.0
            }
        }
    }

    /// Paste failure state - show copy button with capsule design
    @ViewBuilder
    private var pasteFailureStateView: some View {
        HStack(spacing: 6) {
            // Copy button with rounded capsule design
            Button {
                AccessibilityHelper.shared.cancelPendingClipboardRestoration()
                AccessibilityHelper.shared.copyToClipboard(transcribedText)
                showCopiedFeedback = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                    showCopiedFeedback = false
                }
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: showCopiedFeedback ? "checkmark" : "doc.on.doc")
                        .font(.system(size: 10))
                    Text(showCopiedFeedback ? "Copied!" : "Copy")
                        .font(.system(size: 9, weight: .medium))
                }
                .foregroundColor(.white)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(
                    Capsule()
                        .fill(showCopiedFeedback ? Color.green.opacity(0.8) : Color.blue.opacity(0.8))
                )
            }
            .buttonStyle(.plain)

            Spacer()

            // Close button
            Button {
                closeDialog()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 18))
                    .foregroundColor(.white.opacity(0.6))
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 12)
    }

    /// API error state - only show close button (error message goes to main app)
    @ViewBuilder
    private var apiErrorStateView: some View {
        HStack(spacing: 6) {
            // Error icon
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 14))
                .foregroundColor(.red.opacity(0.9))

            Text("Error - Check main app")
                .font(.system(size: 9, weight: .medium))
                .foregroundColor(.white.opacity(0.9))

            Spacer()

            // Close button
            Button {
                closeDialog()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 18))
                    .foregroundColor(.white.opacity(0.6))
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 12)
    }

    /// Pending retry state - offer a retry button for a saved audio file
    @ViewBuilder
    private var pendingRetryStateView: some View {
        HStack(spacing: 8) {
            Image(systemName: "arrow.clockwise.circle.fill")
                .font(.system(size: 14))
                .foregroundColor(.yellow.opacity(0.9))

            Text("recording.retry.pending".localized)
                .font(.system(size: 10, weight: .medium))
                .foregroundColor(.white.opacity(0.9))

            Spacer()

            Button {
                retryPendingAudio()
            } label: {
                Text("recording.retry".localized)
                    .font(.system(size: 10, weight: .semibold))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(Capsule().fill(Color.blue.opacity(0.8)))
                    .foregroundColor(.white)
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 12)
    }

    /// Cancel confirmation - inline view instead of overlay
    @ViewBuilder
    private var cancelConfirmationView: some View {
        HStack(spacing: 6) {
            // Cancel text
            Text("Cancel?")
                .font(.system(size: 9, weight: .medium))
                .foregroundColor(.white.opacity(0.9))

            Spacer()

            // No button - more rounded with capsule shape
            Button {
                appState.showCancelConfirmation = false
            } label: {
                Text("No")
                    .font(.system(size: 9, weight: .medium))
                    .foregroundColor(.white)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(
                        Capsule()
                            .fill(Color.white.opacity(0.2))
                    )
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.escape, modifiers: [])

            // Yes button - with Enter key indicator, more rounded with capsule shape
            Button {
                cancelRecording()
            } label: {
                HStack(spacing: 3) {
                    Text("Yes")
                        .font(.system(size: 9, weight: .medium))
                    Image(systemName: "return")
                        .font(.system(size: 7))
                }
                .foregroundColor(.white)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(
                    Capsule()
                        .fill(Color.red.opacity(0.8))
                )
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.return, modifiers: [])
        }
        .padding(.horizontal, 12)
    }
    
    // MARK: - Helper Methods
    
    private var shouldShowPendingRetry: Bool {
        appState.pendingRetryAudioPath != nil && !isLoading && !isPostProcessing && !isRetrying && !showTranscription
    }
    
    private func stopRecording() {
        // Transition to loading state
        isLoading = true

        // Use the unified method with the selected mode from AppState
        audioManager.toggleRecordingWithTranscription(
            mode: appState.currentSessionModeName,
            trigger: .uiButton
        )

        // Don't close the dialog - keep it open to show loading and results
    }

    /// Handle successful transcription completion
    private func handleTranscriptionUpdate(text: String) {
        // Update the text and show the transcription view
        // We do NOT handle pasting here anymore - that is handled by RecordingTranscriptionFlow
        // This prevents race conditions where both the view and coordinator try to paste,
        // which can cause the "Pasted!" state to get stuck
        transcribedText = text
        isLoading = false
        isPostProcessing = false
        isRetrying = false
        hasError = false
        appState.transcriptionPasteFailed = false // Reset this, coordinator will handle failure state if needed
        showTranscription = true
    }

    /// Show inline error toast above the recording dialog
    ///
    /// **What This Does:**
    /// - Shows a compact, auto-dismissing error pill ABOVE the recording dialog
    /// - Recording dialog stays OPEN (does not close)
    /// - Toast auto-dismisses after 8 second countdown
    /// - "Open Settings" button shown for actionable errors (API key, auth, credits)
    ///
    /// **Why Inline Toast:**
    /// Unlike the large modal ErrorToastManager (400x220px), the inline toast (280x40px):
    /// 1. Doesn't interrupt the user's flow
    /// 2. Auto-dismisses without requiring user action
    /// 3. Keeps the recording dialog visible for context
    private func showTranscriptionErrorAlert(errorText: String) {
        // Remove "Error: " prefix
        let cleanError = errorText.replacingOccurrences(of: "Error: ", with: "")

        // Determine if we should show Settings button
        // Show for errors that user can fix in settings (API keys, auth, credits)
        // Hide for transient errors (network, rate limits, no speech)
        // "account key" matches `TranscriptionError.cloudAccountRequired`, the
        // client-side HyperWhisper Cloud entitlement refusal (HYPERWHISPER-T2).
        // Without it that error shows no button here at all — the `.unauthorized`
        // it replaced matched "unauthorized"/"API key" and got one.
        //
        // NOT in sync with `AppState.showError`'s `settingsActionableMarkers`,
        // and do not read it as a mirror of that list: this one holds 6 markers,
        // that one ~19, and this one is a strict subset. The asymmetry is
        // load-bearing rather than cosmetic, because this matcher runs LAST and
        // therefore wins. On the transcription-failure path
        // `RecordingTranscriptionFlow+ErrorHandling` calls `showError` first and
        // only then sets `lastTranscription` to "Error: …", which is what wakes
        // this dialog's `onChange` and lands here second; and
        // `InlineErrorToastManager.show` dismisses the existing toast before
        // presenting the new one. So while the recording dialog is on screen,
        // an error whose message matches only a marker unique to
        // `settingsActionableMarkers` (e.g. billing, forbidden, "exceeded your
        // current quota") is given a Settings button by `showError` and then has
        // it stripped again here. Reconciling the two lists is deliberately out
        // of scope for this change.
        //
        // English-only, like the list it shadows: these are English literals but
        // `cleanError` arrives already localized, so none of them match in the
        // other 39 locales. See the note in `AppState.showError` for the typed
        // alternative that exists and why this path bypasses it.
        let showSettings = cleanError.localizedCaseInsensitiveContains("API key") ||
                           cleanError.localizedCaseInsensitiveContains("unauthorized") ||
                           cleanError.localizedCaseInsensitiveContains("invalid api key") ||
                           cleanError.localizedCaseInsensitiveContains("insufficient credits") ||
                           cleanError.localizedCaseInsensitiveContains("quota exceeded") ||
                           cleanError.localizedCaseInsensitiveContains("account key")

        // Show the inline error toast ABOVE the recording dialog
        // This does NOT close the recording dialog
        appState.showInlineError(message: cleanError, showSettingsButton: showSettings)
    }

    private func cancelRecording() {
        RecordingWindowManager.shared.endOverlayKeyInterception(restoreGlobalCancelShortcut: false)
        appState.showCancelConfirmation = false

        // CRITICAL: Clear lastTranscription before closing to prevent error toast
        // from stale values when the dialog's onChange observer fires
        appState.lastTranscription = ""
        appState.transcriptionPasteFailed = false

        if isLoading {
            // Cancel during transcription - actively cancel the task to avoid late updates
            audioManager.transcriptionPipeline?.cancelTranscription()
            closeDialog()
        } else {
            // Cancel during recording
            audioManager.toggleRecordingWithTranscription(stopOnly: true, trigger: .uiButton)

            // Use closeDialog() for consistent cleanup - ensures window properly closes
            closeDialog()
        }

        recordingDialogLogger.info("❌ Recording/transcription cancelled from dialog")
    }

    private func retryPendingAudio() {
        guard appState.pendingRetryAudioPath != nil else { return }
        isRetrying = true
        isLoading = true
        hasError = false
        showTranscription = false
        transcribedText = ""

        audioManager.retryTranscriptionFromPendingFile()
    }

    private func closeDialog() {
        recordingDialogLogger.debug("🔍 closeDialog() called - setting isPresented = false")
        // CRITICAL: End the recording session for clipboard management
        // This signals that the user is done with this recording session.
        // 
        // WHY THIS MATTERS:
        // When clipboard restoration is enabled (e.g., 20 seconds), we need to handle
        // multiple recordings properly. Without this:
        // 1. User records and gets text A → clipboard restoration scheduled for 20s
        // 2. User starts recording B after 7s → should cancel previous restoration
        // 3. At 20s mark, original clipboard would incorrectly restore, losing text B
        //
        // By tracking recording sessions, we ensure:
        // - Only the ORIGINAL clipboard (before any recordings) is restored
        // - New recordings cancel pending restorations from previous recordings
        // - The clipboard state remains predictable for the user
        AccessibilityHelper.shared.endRecordingSession()
        
        // Reset states and close
        isLoading = false
        isPostProcessing = false
        showTranscription = false
        transcribedText = ""
        hasError = false
        showCopiedFeedback = false
        isRetrying = false
        appState.transcriptionPasteFailed = false
        
        // First set the binding to false
        recordingDialogLogger.debug("🔍 Setting isPresented binding to false (which is bound to appState.showRecordingDialog)")
        isPresented = false
        
        // Also directly close any Recording Dialog windows to ensure cleanup
        // This handles cases where the binding might not trigger the close properly
        DispatchQueue.main.async {
            let windowTitle = "recording.dialog.window.title".localized
            for window in NSApplication.shared.windows {
                if window.title == windowTitle {
                    window.close()
                }
            }
        }
    }
    
    private func retryTranscription(_ transcript: Transcript) {
        guard let handler = transcriptActionHandler else { return }
        
        guard handler.canRetry(transcript) else {
            let message = "recording.retry.failed.missing".localized
            hasError = true
            showTranscription = true
            transcribedText = message
            appState.lastTranscription = message
            return
        }
        
        showTranscription = false
        isRetrying = true
        hasError = false
        transcribedText = ""
        appState.lastTranscription = ""

        Task { @MainActor in
            let success = await handler.retryTranscription(transcript)
            
            if !success {
                isRetrying = false
                showTranscription = true
            }
            
            if success {
                hasError = false
                    let postProcessed = transcript.value(forKey: "postProcessedText") as? String
                    let final = transcript.text
                    let raw = transcript.value(forKey: "transcribedText") as? String
                    let resolvedText: String
                    if let postProcessed, !postProcessed.isEmpty {
                        resolvedText = postProcessed
                    } else if let final, !final.isEmpty {
                        resolvedText = final
                    } else if let raw, !raw.isEmpty {
                        resolvedText = raw
                    } else {
                        resolvedText = ""
                    }
                    transcribedText = resolvedText
                    appState.lastTranscription = resolvedText
            } else {
                hasError = true
                let errorMessage = handler.lastError ?? "app.unknown.error".localized
                let message = String(format: "recording.retry.failed.error".localized, errorMessage)
                transcribedText = message
                appState.lastTranscription = message
            }
        }
    }
}

// MARK: - Visual Effect Background

/// macOS-style translucent background
struct VisualEffectBackground: NSViewRepresentable {
    enum Style {
        case standard
        case recordingDialog
    }

    var style: Style = .standard

    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        configure(view)
        return view
    }
    
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {
        configure(nsView)
    }

    private func configure(_ view: NSVisualEffectView) {
        view.blendingMode = .behindWindow
        view.state = .active

        switch style {
        case .standard:
            view.material = .sidebar  // Keep default translucency elsewhere in the app
            view.appearance = nil
        case .recordingDialog:
            view.material = .fullScreenUI  // Lowest-transparency dark blur for recording dialog
            view.appearance = NSAppearance(named: .vibrantDark)
            view.isEmphasized = true
        }
    }
}

// MARK: - Preview

#Preview {
    let previewAppState = AppState()
    RecordingDialog(isPresented: .constant(true))
        .environmentObject(AudioRecordingManager())
        .environmentObject(previewAppState)
        .environmentObject(SettingsManager())
        .environmentObject(RecordingLiveMetrics())
        .environmentObject(previewAppState.liveStreamingText)
        .environmentObject(TranscriptionPipeline())
}
