//
//  MicrophoneKeepWarmManager.swift
//  hyperwhisper
//
//  Keeps the microphone capture path warm between recordings to reduce
//  first-sample latency for push-to-talk and other short recordings.
//
//  SOURCE REFERENCE:
//  Adapted from the open-source approach in:
//  https://github.com/drewburchfield/macos-mic-keepwarm
//
//  HyperWhisper-specific adaptations:
//  - Runs in-process instead of as a separate background executable
//  - Reuses HyperWhisper's existing audio-device selection flow
//  - Suspends itself while an active recording is running
//

import Foundation
import AVFoundation

/// Holds an idle AVCaptureSession open so the microphone hardware stays awake.
///
/// The session discards all audio buffers. Nothing is recorded, stored, or sent.
/// This manager is controlled by AudioRecordingManager, which decides when the
/// keep-warm session should run based on user settings, permissions, and recording state.
final class MicrophoneKeepWarmManager: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {

    // MARK: - Queue / Session State

    private let sessionQueue = DispatchQueue(label: "com.hyperwhisper.audio.keepwarm")
    private var captureSession: AVCaptureSession?
    private var currentDeviceUniqueID: String?
    private var pendingRefreshWorkItem: DispatchWorkItem?

    // MARK: - Main-Thread Configuration State

    private var keepWarmEnabled = false
    private var hasMicrophonePermission = false
    private var isSuspendedForRecording = false
    private var activeInputUID: String?
    private var activeInputName = "audio.device.default".localized

    deinit {
        pendingRefreshWorkItem?.cancel()
        if DispatchQueue.getSpecific(key: Self.sessionQueueKey) != Self.sessionQueueValue {
            sessionQueue.sync {
                self.teardownSessionOnQueue(reason: "manager deinitialized")
            }
        } else {
            teardownSessionOnQueue(reason: "manager deinitialized")
        }
    }

    private static let sessionQueueKey = DispatchSpecificKey<String>()
    private static let sessionQueueValue = "com.hyperwhisper.audio.keepwarm"

    override init() {
        super.init()
        sessionQueue.setSpecific(key: Self.sessionQueueKey, value: Self.sessionQueueValue)
    }

    // MARK: - Public API

    /// Enables or disables the keep-warm session.
    func setEnabled(_ enabled: Bool) {
        assert(Thread.isMainThread)
        guard keepWarmEnabled != enabled else { return }

        keepWarmEnabled = enabled
        reconcileState(reason: enabled ? "setting enabled" : "setting disabled", delay: 0)
    }

    /// Updates whether microphone permission is currently available.
    func setPermissionGranted(_ granted: Bool) {
        assert(Thread.isMainThread)
        guard hasMicrophonePermission != granted else { return }

        hasMicrophonePermission = granted
        reconcileState(
            reason: granted ? "microphone permission granted" : "microphone permission unavailable",
            delay: 0
        )
    }

    /// Updates the current active input device metadata used for refresh decisions and logging.
    func updateActiveInputDevice(uid: String?, name: String) {
        assert(Thread.isMainThread)

        let normalizedName = name.isEmpty ? "audio.device.default".localized : name
        let didChange = activeInputUID != uid || activeInputName != normalizedName
        activeInputUID = uid
        activeInputName = normalizedName

        guard didChange else { return }
        reconcileState(reason: "input device changed to \(normalizedName)", delay: 1.5)
    }

    /// Temporarily disables keep-warm while a real recording is active.
    func suspendForActiveRecording() {
        assert(Thread.isMainThread)
        guard !isSuspendedForRecording else { return }

        isSuspendedForRecording = true
        reconcileState(reason: "recording started", delay: 0)
    }

    /// Re-enables keep-warm after recording stops.
    func resumeAfterRecording() {
        assert(Thread.isMainThread)
        guard isSuspendedForRecording else { return }

        isSuspendedForRecording = false
        reconcileState(reason: "recording stopped", delay: 0.75)
    }

    // MARK: - AVCaptureAudioDataOutputSampleBufferDelegate

    func captureOutput(
        _ output: AVCaptureOutput,
        didOutput sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        // Intentionally discard all audio to keep the input stream warm.
    }

    // MARK: - Session Coordination

    private var shouldKeepWarm: Bool {
        keepWarmEnabled && hasMicrophonePermission && !isSuspendedForRecording
    }

    private func reconcileState(reason: String, delay: TimeInterval) {
        pendingRefreshWorkItem?.cancel()
        pendingRefreshWorkItem = nil

        guard shouldKeepWarm else {
            stopSession(reason: reason)
            return
        }

        let expectedUID = activeInputUID
        let expectedName = activeInputName
        let workItem = DispatchWorkItem { [weak self] in
            self?.startOrRefreshSession(
                reason: reason,
                expectedDeviceUID: expectedUID,
                expectedDeviceName: expectedName
            )
        }

        pendingRefreshWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: workItem)
    }

    private func startOrRefreshSession(
        reason: String,
        expectedDeviceUID: String?,
        expectedDeviceName: String
    ) {
        sessionQueue.async { [weak self] in
            guard let self else { return }

            guard let device = AVCaptureDevice.default(for: .audio) else {
                AppLogger.audio.warning("Mic keep-warm could not find an audio input device")
                self.teardownSessionOnQueue(reason: "no audio input device")
                return
            }

            if self.captureSession?.isRunning == true && self.currentDeviceUniqueID == device.uniqueID {
                AppLogger.audio.debug("Mic keep-warm already active on \(device.localizedName, privacy: .public)")
                return
            }

            self.teardownSessionOnQueue(reason: "refresh for \(reason)")

            if let expectedDeviceUID, expectedDeviceUID != device.uniqueID {
                AppLogger.audio.warning(
                    "Mic keep-warm route mismatch. Expected \(expectedDeviceName, privacy: .public) but AVFoundation resolved \(device.localizedName, privacy: .public)"
                )
            }

            let session = AVCaptureSession()

            do {
                let input = try AVCaptureDeviceInput(device: device)
                guard session.canAddInput(input) else {
                    AppLogger.audio.error("Mic keep-warm could not add AVCaptureDeviceInput")
                    return
                }
                session.addInput(input)
            } catch {
                AppLogger.audio.error("Mic keep-warm failed to open microphone: \(error.localizedDescription, privacy: .public)")
                return
            }

            let output = AVCaptureAudioDataOutput()
            guard session.canAddOutput(output) else {
                AppLogger.audio.error("Mic keep-warm could not add AVCaptureAudioDataOutput")
                return
            }

            output.setSampleBufferDelegate(self, queue: self.sessionQueue)
            session.addOutput(output)
            session.startRunning()

            self.captureSession = session
            self.currentDeviceUniqueID = device.uniqueID

            AppLogger.audio.info(
                "Mic keep-warm active on \(device.localizedName, privacy: .public) [reason: \(reason, privacy: .public)]"
            )
        }
    }

    private func stopSession(reason: String) {
        sessionQueue.async { [weak self] in
            self?.teardownSessionOnQueue(reason: reason)
        }
    }

    private func teardownSessionOnQueue(reason: String) {
        guard let session = captureSession else { return }

        for output in session.outputs {
            if let audioOutput = output as? AVCaptureAudioDataOutput {
                audioOutput.setSampleBufferDelegate(nil, queue: nil)
            }
            session.removeOutput(output)
        }

        for input in session.inputs {
            session.removeInput(input)
        }

        if session.isRunning {
            session.stopRunning()
        }

        captureSession = nil
        currentDeviceUniqueID = nil

        AppLogger.audio.info("Mic keep-warm stopped [reason: \(reason, privacy: .public)]")
    }
}
