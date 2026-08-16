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

    /// Monotonic ticket that invalidates an in-flight `startRecording(to:)`.
    ///
    /// `startRecording(to:)` now suspends while CoreAudio brings the recorder up
    /// (see the doc comment on `makeLiveRecorder(url:settings:)`). Both
    /// `startRecording(to:)` and `stopRecording()` bump this counter, so an
    /// attempt whose ticket no longer matches knows it was superseded and must
    /// throw its instance away instead of installing it. Only ever touched on
    /// the main actor.
    private var startGeneration: Int = 0

    /// Dedicated **serial** queue for the blocking CoreAudio calls in
    /// `makeLiveRecorder(url:settings:)`.
    ///
    /// Serial, not concurrent, and not `Task.detached`: two overlapping recording
    /// starts must never be inside `AVAudioRecorder.record()` on the HAL at the
    /// same time — that is the state the daemon handles worst.
    ///
    /// ACTOR ISOLATION: `nonisolated(unsafe)` so the `nonisolated static` helper
    /// can reach it without hopping to the main actor. `DispatchQueue` is itself
    /// thread-safe and the queue is assigned once and never mutated, so escaping
    /// isolation here is safe. Same rationale as `deviceListenerQueue` in
    /// `AudioDeviceManager`.
    nonisolated(unsafe) private static let recorderStartQueue = DispatchQueue(
        label: "com.hyperwhisper.audio.recorder-start",
        qos: .userInitiated
    )

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

    /// Build a live `AVAudioRecorder` off the main actor and hand it back.
    ///
    /// **Why this exists (Sentry HYPERWHISPER-F7, "App hanging for at least
    /// 10000 ms"):** `AVAudioRecorder.record()` — and the `AVAudioRecorder(url:
    /// settings:)` init that precedes it — are synchronous CoreAudio calls that
    /// block in `mach_msg` waiting on the `coreaudiod` daemon. During an audio
    /// route change or wake-from-sleep that wait ran to 10086 ms of a 10287 ms
    /// `Recording Start` transaction, and because `SimpleRecorder` is
    /// `@MainActor` the whole app was frozen for it.
    ///
    /// **Why `nonisolated` alone is not enough:** a `nonisolated` method called
    /// from `@MainActor` code still runs synchronously on the caller's thread.
    /// The `DispatchQueue.async` hop underneath the continuation is what actually
    /// gets this work off the main thread; `nonisolated` only makes that hop legal
    /// without an actor round-trip.
    ///
    /// **Why a continuation over a dedicated serial queue, not `Task.detached`:**
    /// `AVAudioRecorder` is not `Sendable`, so it cannot be returned from a
    /// detached task. A checked continuation can carry it because
    /// `resume(returning:)` takes its value as `sending`, not `Sendable` — a
    /// one-shot transfer, which is exactly what this is. The queue is serial so
    /// two overlapping starts cannot be inside `record()` on the HAL at once.
    /// Continuation-over-a-queue precedent: `SilenceTrimmer.writeAudioFile(samples:to:)`.
    ///
    /// **Continuation safety:** exactly one path resumes — one `queue.async`
    /// block with a single `resume` per branch and a `return` after each. The
    /// `ManagedAtomic<Bool>` guard the macOS guidelines mandate is for callbacks
    /// that can fire more than once (`AVAssetWriter`, `URLSession` delegates);
    /// it would be dead weight here, so its absence is deliberate, not an
    /// oversight.
    ///
    /// **Delegate note:** `SimpleRecorder` never assigns an
    /// `AVAudioRecorderDelegate` (it only clears it on stop), so the usual
    /// "a recorder created on a thread without a run loop never delivers its
    /// delegate callbacks" hazard does not apply. Do not add a run loop here.
    ///
    /// **What did NOT move off the main actor:** the `recorder` /
    /// `recorderRetention` / `recorderReleaseTask` state, the `@Published`
    /// `isRecording` and `audioLevel` writes, `startMeterUpdates()`,
    /// `updateMeter()` / `updateMeters()` / `averagePower(forChannel:)`, and
    /// `stopRecording()`. The instance crosses threads exactly once; resuming the
    /// continuation establishes the happens-before edge for that handoff, which
    /// is why no `@unchecked Sendable` conformance is needed anywhere.
    ///
    /// **Throws:** `AudioError.recordingFailed` if the recorder cannot be created
    /// or `record()` returns false. A failed instance is simply never handed back.
    nonisolated private static func makeLiveRecorder(
        url: URL,
        settings: [String: Any]
    ) async throws -> AVAudioRecorder {
        try await withCheckedThrowingContinuation { continuation in
            SimpleRecorder.recorderStartQueue.async {
                let newRecorder: AVAudioRecorder
                do {
                    newRecorder = try AVAudioRecorder(url: url, settings: settings)
                } catch {
                    AppLogger.audio.error("Failed to create AVAudioRecorder: \(error.localizedDescription)")
                    continuation.resume(throwing: AudioError.recordingFailed(reason: error.localizedDescription))
                    return
                }

                newRecorder.isMeteringEnabled = true

                guard newRecorder.record() else {
                    AppLogger.audio.error("AVAudioRecorder.record() returned false")
                    continuation.resume(throwing: AudioError.recordingFailed(reason: "Failed to start recording"))
                    return
                }

                continuation.resume(returning: newRecorder)
            }
        }
    }

    /// Stop and release a recorder that came back from `makeLiveRecorder` after
    /// its start was superseded.
    ///
    /// Runs on `recorderStartQueue` because `stop()` is another blocking HAL call,
    /// and the whole point of this change is that those do not happen on the main
    /// thread. The deferred release mirrors `stopRecording()`: dropping an
    /// `AVAudioRecorder` immediately after `stop()` can race with AudioQueue
    /// callbacks that are still draining.
    ///
    /// `deleteRecording()` removes this attempt's own `.incomplete_<uuid>.wav`.
    /// The URL is unique per attempt, so this can never touch the winning
    /// attempt's file; leaving it behind would hand `CrashRecoveryManager` an
    /// unclaimed near-empty WAV to synthesize a stub session for.
    nonisolated private static func discardSupersededRecorder(_ recorder: AVAudioRecorder) {
        SimpleRecorder.recorderStartQueue.async {
            recorder.delegate = nil
            recorder.stop()
            if !recorder.deleteRecording() {
                AppLogger.audio.warning("Failed to delete the WAV of a superseded AVAudioRecorder start")
            }
            SimpleRecorder.recorderStartQueue.asyncAfter(deadline: .now() + .milliseconds(300)) {
                withExtendedLifetime(recorder) {}
            }
        }
    }

    /// Start recording to the specified URL
    ///
    /// **What This Does:**
    /// 1. Creates AVAudioRecorder with Whisper-optimized settings
    /// 2. Enables metering for audio level visualization
    /// 3. Starts recording
    /// 4. Begins periodic meter updates (30 FPS)
    ///
    /// Steps 1-3 happen off the main actor (see `makeLiveRecorder(url:settings:)`
    /// for why — Sentry HYPERWHISPER-F7). Everything this method touches on
    /// `self`, including both `@Published` properties, still happens on the main
    /// actor: before the `await` for the teardown bookkeeping, after it for the
    /// install.
    ///
    /// **Re-entrancy:** the `await` is a suspension point that did not exist when
    /// this method was synchronous, so a `stopRecording()` — or a second
    /// `startRecording(to:)` from a double-tapped hotkey — can now land in the
    /// middle of a start. Without the `startGeneration` ticket that interleaving
    /// leaks a live, running, invisible recorder holding the user's microphone:
    /// the stop finds `recorder == nil` and no-ops, then the in-flight start
    /// installs its instance and sets `isRecording = true` (or the second start
    /// overwrites the first one's still-running instance). A superseded attempt
    /// therefore stops its own instance and returns without touching any state.
    ///
    /// It returns rather than throws because the winning attempt owns the state
    /// now: throwing would send the caller down `handleRecordingStartFailure` →
    /// `cleanupFailedStartArtifacts()`, which deletes `rawURL` — by then the
    /// *winner's* `.incomplete_*.wav`, i.e. the file being actively recorded.
    ///
    /// **Parameters:**
    /// - `url`: Where to save the WAV file
    ///
    /// **Throws:**
    /// - AudioError.recordingFailed if recorder cannot start
    func startRecording(to url: URL) async throws {
        // Cancel any pending deferred release from a previous stop.
        recorderReleaseTask?.cancel()
        recorderReleaseTask = nil

        // Take a ticket for this attempt before suspending. Any stop, or any
        // later start, invalidates it.
        startGeneration &+= 1
        let generation = startGeneration

        // Create recorder with Whisper-optimized settings and start it, off the
        // main actor. Throws exactly what the synchronous version threw.
        let startedRecorder = try await SimpleRecorder.makeLiveRecorder(url: url, settings: recordSettings)

        guard generation == startGeneration else {
            AppLogger.audio.warning("SimpleRecorder start was superseded while AVAudioRecorder was starting - discarding the orphaned recorder")
            SimpleRecorder.discardSupersededRecorder(startedRecorder)
            return
        }

        recorder = startedRecorder
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
    ///
    /// **Re-entrancy:** bumping `startGeneration` is what makes this safe against
    /// a `startRecording(to:)` that is currently suspended inside CoreAudio. That
    /// start would otherwise complete after this stop and install a live recorder
    /// nobody can see or stop. See `startRecording(to:)`.
    func stopRecording() {
        // Invalidate any start that is still suspended in makeLiveRecorder().
        startGeneration &+= 1

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
