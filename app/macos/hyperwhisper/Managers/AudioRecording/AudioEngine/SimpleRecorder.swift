//
//  SimpleRecorder.swift
//  hyperwhisper
//
//  Simplified audio recorder using AVAudioRecorder instead of AVAudioEngine.
//  Records directly at Whisper-optimized format (16kHz mono) without real-time conversion.
//

import Foundation
import AVFoundation

/// Simple audio recorder using AVAudioRecorder
///
/// **Why This Exists:**
/// The previous AVAudioEngine-based approach required real-time format conversion
/// from hardware format (48kHz stereo) to Whisper format (16kHz mono) inside a tap callback.
/// This was fragile and caused static/silence issues.
///
/// **How It Works:**
/// AVAudioRecorder handles all buffer management internally and can record
/// directly at 16kHz mono, eliminating the need for real-time conversion.
///
/// **Audio Level Monitoring:**
/// Uses built-in metering (averagePower) instead of custom RMS calculation.
/// The dB values (-60 to 0) are normalized to 0.0-1.0 for UI binding.
@MainActor
class SimpleRecorder: NSObject, ObservableObject {

    // MARK: - Properties

    /// The AVAudioRecorder instance
    private var recorder: AVAudioRecorder?

    /// Task for periodic meter updates
    private var meterUpdateTask: Task<Void, Never>?

    /// Keep the last stopped recorder alive briefly to avoid AudioQueue callback/dealloc races.
    private var recorderRetention: AVAudioRecorder?

    /// Deferred release task for `recorderRetention`.
    private var recorderReleaseTask: Task<Void, Never>?

    /// Current audio level (0.0 to 1.0) for UI visualization
    @Published var audioLevel: Float = 0

    /// Whether recording is currently active
    @Published var isRecording: Bool = false

    // MARK: - Recording Settings

    /// Whisper-optimized recording format
    /// - 16kHz sample rate (Whisper's native format)
    /// - Mono (single channel)
    /// - 16-bit integer PCM
    /// - Little-endian, interleaved
    private let recordSettings: [String: Any] = [
        AVFormatIDKey: Int(kAudioFormatLinearPCM),
        AVSampleRateKey: 16000.0,
        AVNumberOfChannelsKey: 1,
        AVLinearPCMBitDepthKey: 16,
        AVLinearPCMIsFloatKey: false,
        AVLinearPCMIsBigEndianKey: false,
        AVLinearPCMIsNonInterleaved: false
    ]

    // MARK: - Audio Level Normalization

    /// Minimum dB value for normalization (silence threshold)
    private let minDb: Float = -60.0

    /// Maximum dB value for normalization (full scale)
    private let maxDb: Float = 0.0

    deinit {
        meterUpdateTask?.cancel()
        recorderReleaseTask?.cancel()
    }

    // MARK: - Recording Control

    /// Start recording to the specified URL
    ///
    /// **What This Does:**
    /// 1. Creates AVAudioRecorder with Whisper-optimized settings
    /// 2. Enables metering for audio level visualization
    /// 3. Starts recording
    /// 4. Begins periodic meter updates (30 FPS)
    ///
    /// **Parameters:**
    /// - `url`: Where to save the WAV file
    ///
    /// **Throws:**
    /// - AudioError.recordingFailed if recorder cannot start
    func startRecording(to url: URL) throws {
        // Cancel any pending deferred release from a previous stop.
        recorderReleaseTask?.cancel()
        recorderReleaseTask = nil

        // Create recorder with Whisper-optimized settings
        do {
            recorder = try AVAudioRecorder(url: url, settings: recordSettings)
        } catch {
            AppLogger.audio.error("Failed to create AVAudioRecorder: \(error.localizedDescription)")
            throw AudioError.recordingFailed(reason: error.localizedDescription)
        }

        recorder?.isMeteringEnabled = true

        guard recorder?.record() == true else {
            AppLogger.audio.error("AVAudioRecorder.record() returned false")
            recorder = nil
            throw AudioError.recordingFailed(reason: "Failed to start recording")
        }

        isRecording = true
        startMeterUpdates()

        AppLogger.audio.info("SimpleRecorder started recording to: \(url.lastPathComponent, privacy: .public)")
    }

    /// Stop recording
    ///
    /// **What This Does:**
    /// 1. Cancels meter update task
    /// 2. Stops the recorder
    /// 3. Releases recorder instance
    /// 4. Resets audio level to 0
    func stopRecording() {
        meterUpdateTask?.cancel()
        meterUpdateTask = nil

        let stoppedRecorder = recorder
        stoppedRecorder?.delegate = nil
        stoppedRecorder?.stop()
        recorder = nil
        recorderRetention = stoppedRecorder

        // Avoid immediate deallocation right after stop() while AQ callbacks may still be draining.
        recorderReleaseTask?.cancel()
        recorderReleaseTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 300_000_000) // 300ms
            guard !Task.isCancelled else { return }
            self?.recorderRetention = nil
            self?.recorderReleaseTask = nil
        }

        isRecording = false
        audioLevel = 0

        AppLogger.audio.info("SimpleRecorder stopped recording")
    }

    // MARK: - Audio Level Metering

    /// Start periodic meter updates for UI visualization
    ///
    /// **Update Rate:**
    /// 30 FPS (33ms interval)
    /// Provides smooth visualization without excessive CPU usage
    private func startMeterUpdates() {
        meterUpdateTask = Task {
            while !Task.isCancelled && recorder != nil {
                updateMeter()
                try? await Task.sleep(nanoseconds: 33_000_000) // ~30 FPS
            }
        }
    }

    /// Update audio level from recorder's built-in metering
    ///
    /// **What This Does:**
    /// 1. Calls updateMeters() to refresh internal meter state
    /// 2. Gets average power in dB (-160 to 0)
    /// 3. Normalizes to 0.0-1.0 range for UI binding
    ///
    /// **Normalization:**
    /// - Values below -60 dB map to 0.0 (silence)
    /// - Values at 0 dB map to 1.0 (full scale)
    /// - Linear interpolation between
    private func updateMeter() {
        guard let recorder = recorder else { return }

        recorder.updateMeters()
        let power = recorder.averagePower(forChannel: 0)

        // Normalize dB to 0.0-1.0 range
        let normalized: Float
        if power <= minDb {
            normalized = 0.0
        } else if power >= maxDb {
            normalized = 1.0
        } else {
            normalized = (power - minDb) / (maxDb - minDb)
        }

        audioLevel = normalized
    }
}
