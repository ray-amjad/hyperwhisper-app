//
//  RecordingLiveMetrics.swift
//  hyperwhisper
//
//  DEDICATED HIGH-FREQUENCY METRICS OBJECT
//  This class isolates rapidly-updating audio metrics (audioLevel, recordingDuration)
//  from AudioRecordingManager to prevent main app view tree invalidation at 30 FPS.
//
//  **The Problem:**
//  When AudioRecordingManager had @Published audioLevel updating at ~30 FPS,
//  SwiftUI invalidated ALL views observing AudioRecordingManager (including MainAppView
//  and HistoryView) 30 times per second. Since HistoryView is heavy (List + Sections),
//  this starved the main thread and caused the RecordingDialog waveform to lag.
//
//  **The Solution:**
//  By moving high-frequency metrics to this separate ObservableObject, only views
//  that specifically need real-time audio data (RecordingDialog, OnboardingView)
//  observe this object. The rest of the app doesn't get invalidated during recording.
//
//  **Usage:**
//  - Inject via `.environmentObject(audioManager.liveMetrics)`
//  - Only RecordingDialog and OnboardingView should observe this
//  - Other views should continue to observe AudioRecordingManager for state like isRecording
//

import Foundation
import Combine

/// Dedicated object for high-frequency audio metrics.
/// Isolated from AudioRecordingManager so main app views don't invalidate at 30 FPS.
///
/// **Update Frequencies:**
/// - `audioLevel`: ~30 FPS (33ms interval from SimpleRecorder)
/// - `recordingDuration`: ~10 FPS (100ms interval from RecordingLifecycle)
///
/// **Observers:**
/// - RecordingDialog: Uses both audioLevel (waveform) and recordingDuration (timer display)
/// - OnboardingView: Uses audioLevel only (audio level indicator bar)
@MainActor
final class RecordingLiveMetrics: ObservableObject {

    /// Current audio input level (0.0 to 1.0) for waveform visualization.
    /// Updated at ~30 FPS during recording by SimpleRecorder's meter updates.
    /// Value is normalized from AVAudioRecorder's dB readings (-60 to 0 dB range).
    @Published var audioLevel: Float = 0

    /// Duration of current recording in seconds.
    /// Updated at ~10 FPS during recording by RecordingLifecycle's timer.
    /// Used for the MM:SS display in RecordingDialog.
    @Published var recordingDuration: TimeInterval = 0
}
