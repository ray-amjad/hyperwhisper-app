//
//  SimpleRecorder.swift
//  hyperwhisper
//
//  Simplified audio recorder using AVAudioRecorder instead of AVAudioEngine.
//  Records directly at Whisper-optimized format (16kHz mono) without real-time conversion.
//

import Foundation
import AVFoundation

/// How a `SimpleRecorder.startRecording(to:)` attempt ended.
///
/// A start can now finish in three ways, not two: it can succeed, it can fail (throw),
/// or it can be **superseded** — a newer start, or a stop, took ownership of the
/// recorder while this attempt was suspended inside CoreAudio.
///
/// Supersede is neither success nor failure and must not be reported as either. A
/// superseded attempt installed nothing and already discarded its own recorder and WAV,
/// so a caller that treats it as success shows a live recording UI over a dead recorder,
/// and a caller that treats it as failure runs its cleanup over the *winning* attempt's
/// files. Returning it explicitly is what stops both.
///
/// Declared at file scope rather than nested in `SimpleRecorder`, because that type is
/// `@MainActor` and nesting would make this enum main-actor isolated for no reason.
enum RecorderStartOutcome: Sendable {
    /// The recorder is live and installed; `isRecording` is true.
    case started
    /// A newer start (or a stop) owns the recorder. Nothing was installed, and this
    /// attempt has already stopped its instance and deleted its own WAV.
    case superseded
}

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
    /// (see the doc comment on `makeLiveRecorder(url:settings:)`).
    /// `startRecording(to:)`, `stopRecording()` and `cancelPendingStart()` all bump
    /// this counter, so an attempt whose ticket no longer matches knows it was
    /// superseded, must throw its instance away instead of installing it, and must
    /// report `.superseded` to its caller. Only ever touched on the main actor.
    ///
    /// A ticket only ever tells the *loser* it lost. It says nothing to the winner about
    /// what the winner is about to overwrite — which is why the install path in
    /// `startRecording(to:)` also has to retire whatever recorder is already there.
    ///
    /// This is the one place in the audio stack where a monotonic ticket is the
    /// right tool: the losing attempt has to be told it lost *after* the fact,
    /// while holding a live object it must dispose of itself. Where a superseded
    /// operation merely has to stop, use `Task` cancellation instead.
    private var startGeneration: Int = 0

    /// How many `startRecording(to:)` calls are currently suspended inside
    /// `makeLiveRecorder(url:settings:)`.
    ///
    /// A counter rather than a `Bool` because two starts can overlap: the first to
    /// resume must not clear a flag the second is still relying on. Only read by
    /// `cancelPendingStart()`, so that it can report truthfully whether it invalidated
    /// anything instead of bumping the ticket unconditionally. Only ever touched on the
    /// main actor, and only outside the `await`.
    private var startsInFlight: Int = 0

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
    ///
    /// **The `record()`-returned-false branch deletes its own file, and has to.**
    /// `AVAudioRecorder(url:settings:)` creates the file at `url` before `record()` is
    /// ever called, so a `record()` failure leaves a header-only `.incomplete_*.wav` on
    /// disk. Cleaning up here rather than at the call site is the only place that works
    /// for every exit: this function throws away the only reference to the instance, so
    /// no caller can call `deleteRecording()` on it afterwards. The caller's
    /// `cleanupFailedStartArtifacts()` does not cover it either — on a superseded
    /// attempt the caller deliberately runs no cleanup at all, and that exit is how a
    /// header-only WAV reached `synthesizeStubSessionsForUnclaimedWAVs`, which has no
    /// minimum-size filter, and became a phantom History entry on the next launch.
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
                    // The init above already created the file. This is the last point at
                    // which anything holds a reference to the instance, so it is the last
                    // point at which the header-only WAV can be removed — see the
                    // `record()`-returned-false note in this function's doc comment.
                    if !newRecorder.deleteRecording() {
                        AppLogger.audio.warning("Could not delete the header-only WAV of a failed AVAudioRecorder start: \(url.lastPathComponent, privacy: .public)")
                    }
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
    /// this method was synchronous, so a second `startRecording(to:)` — from a
    /// double-tapped hotkey, or from a push-to-talk release that re-enters
    /// `toggleRecordingWithTranscription` while `isRecording` is still false —
    /// can now land in the middle of a start. Without the `startGeneration`
    /// ticket that interleaving leaks a live, running, invisible recorder holding
    /// the user's microphone: the second start overwrites `recorder` while the
    /// first instance is still running, and nothing can ever stop it again.
    ///
    /// A superseded attempt stops and deletes its own instance and reports
    /// `.superseded`. It does not throw, because the winning attempt owns the
    /// state now: throwing would send the caller down
    /// `handleRecordingStartFailure` → `cleanupFailedStartArtifacts()`, whose
    /// business is deleting the `.incomplete_*.wav` of a start that failed — not the
    /// one a winner is actively recording into. It does not return `.started` either: the
    /// caller would show a live recording UI, run a duration timer and persist a
    /// Core Data session over a recorder that does not exist.
    ///
    /// The same reasoning covers a failure that arrives after the attempt was
    /// superseded — `makeLiveRecorder` throwing is the loser's problem, not the
    /// winner's, so it is logged and reported as `.superseded` rather than
    /// rethrown into the winner's cleanup.
    ///
    /// **The ticket runs both ways.** It stops a *stale* attempt installing over a
    /// newer one, and — because a ticket only ever tells the loser it lost — the
    /// install below also has to cope with a recorder that is already live. That
    /// happens whenever a start completes while an earlier start is still recording:
    /// the newer attempt holds the highest ticket, so it legitimately wins, but
    /// assigning straight over `recorder` would drop the last strong reference to a
    /// *running* `AVAudioRecorder` and deallocate it inline on the main actor,
    /// mid-record, with none of the 300 ms `recorderRetention` AudioQueue-drain
    /// discipline the rest of this file insists on — and would leave the displaced
    /// recorder's `meterUpdateTask` running against it. `retireActiveRecorder()` is
    /// therefore run first, so there is exactly one path by which a live recorder is
    /// ever torn down.
    ///
    /// **Parameters:**
    /// - `url`: Where to save the WAV file
    ///
    /// **Returns:**
    /// - `.started` when the recorder is live and installed
    /// - `.superseded` when a newer start took over; nothing was installed
    ///
    /// **Throws:**
    /// - AudioError.recordingFailed if recorder cannot start
    func startRecording(to url: URL) async throws -> RecorderStartOutcome {
        // Cancel any pending deferred release from a previous stop.
        recorderReleaseTask?.cancel()
        recorderReleaseTask = nil

        // Take a ticket for this attempt before suspending. Any stop, any
        // `cancelPendingStart()`, or any later start invalidates it.
        startGeneration &+= 1
        let generation = startGeneration

        // Advertise the suspension window to `cancelPendingStart()`. A counter, not a
        // flag, because two starts can be in flight at once.
        startsInFlight += 1
        defer { startsInFlight -= 1 }

        // Create recorder with Whisper-optimized settings and start it, off the
        // main actor. Throws exactly what the synchronous version threw.
        let startedRecorder: AVAudioRecorder
        do {
            startedRecorder = try await SimpleRecorder.makeLiveRecorder(url: url, settings: recordSettings)
        } catch {
            // A failure only belongs to the caller if the caller still owns the
            // recorder. Rethrowing a superseded attempt's error would run the
            // winner's start down the failure path and delete the file it is
            // recording into.
            guard generation == startGeneration else {
                AppLogger.audio.warning("Superseded SimpleRecorder start also failed - swallowing its error: \(error.localizedDescription, privacy: .public)")
                return .superseded
            }
            throw error
        }

        guard generation == startGeneration else {
            AppLogger.audio.warning("SimpleRecorder start was superseded while AVAudioRecorder was starting - discarding the orphaned recorder")
            SimpleRecorder.discardSupersededRecorder(startedRecorder)
            return .superseded
        }

        // WE WON — but winning does not mean the slot is empty. An earlier start may
        // already be live and recording. Tear it down with the same discipline
        // `stopRecording()` uses instead of dropping it on the floor.
        if recorder != nil {
            AppLogger.audio.warning("A new SimpleRecorder start completed while an earlier recorder was still live - retiring the earlier one")
            SentryService.addBreadcrumb(
                message: "Recorder start displaced a live recorder",
                category: "audio.recording",
                level: .warning
            )
            retireActiveRecorder()
        }

        recorder = startedRecorder
        isRecording = true
        startMeterUpdates()

        AppLogger.audio.info("SimpleRecorder started recording to: \(url.lastPathComponent, privacy: .public)")
        return .started
    }

    /// Invalidate a `startRecording(to:)` that is currently suspended inside CoreAudio,
    /// without otherwise touching recorder state.
    ///
    /// **Why this exists.** `RecordingLifecycle.stopRecording()` is gated on
    /// `guard isRecording else { return nil }`, and `isRecording` is only set *after*
    /// the start's suspension resolves. So a user-initiated stop that lands inside the
    /// start window — a push-to-talk key released while `AVAudioRecorder.record()` is
    /// still blocked on an unresponsive `coreaudiod`, which is exactly the
    /// HYPERWHISPER-F7 condition — never reaches `stopRecording()` here and never bumps
    /// the ticket. The start would then resume, install a live recorder nothing is
    /// showing and nothing will ever stop, and hold the user's microphone open
    /// indefinitely.
    ///
    /// Bumping the ticket makes that start discard its own instance and report
    /// `.superseded`, which its caller already knows how to unwind. Nothing else is
    /// touched: there is no installed recorder to stop, and `isRecording` /
    /// `audioLevel` are already false / 0.
    ///
    /// - Returns: `true` if a start was in flight and has been invalidated.
    @discardableResult
    func cancelPendingStart() -> Bool {
        guard startsInFlight > 0 else { return false }
        startGeneration &+= 1
        AppLogger.audio.notice("Invalidated \(self.startsInFlight, privacy: .public) in-flight SimpleRecorder start(s) - they will discard their recorders")
        return true
    }

    /// Stop, retire and schedule the release of whatever is installed in `recorder`,
    /// and cancel the meter loop reading from it.
    ///
    /// The single teardown path for a *live* recorder, shared by `stopRecording()` and
    /// by the install path of `startRecording(to:)`. Dropping the last strong reference
    /// to a running `AVAudioRecorder` deallocates it inline while AudioQueue callbacks
    /// may still be draining; the 300 ms retention below is what avoids that, and it
    /// only helps if every teardown goes through here.
    ///
    /// Deliberately does **not** touch `startGeneration`, `isRecording` or `audioLevel`
    /// — those belong to the caller. `stopRecording()` clears them; a start that is
    /// about to install its own recorder must not.
    ///
    /// Deliberately does **not** delete the retired recorder's file. Unlike a superseded
    /// start, which never captured anything and cleans up after itself in
    /// `discardSupersededRecorder(_:)`, a recorder that reached the installed state was
    /// live and may hold real audio. Leaving the WAV lets `CrashRecoveryManager` claim it
    /// as an unfinished recording, which is the non-destructive outcome.
    private func retireActiveRecorder() {
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
    }

    /// Stop recording
    ///
    /// **What This Does:**
    /// 1. Cancels meter update task
    /// 2. Stops the recorder
    /// 3. Releases recorder instance
    /// 4. Resets audio level to 0
    ///
    /// **Re-entrancy:** bumping `startGeneration` invalidates a `startRecording(to:)`
    /// that is currently suspended inside CoreAudio, so it discards its instance
    /// instead of installing one nobody can see or stop.
    ///
    /// Be precise about what that does and does not protect. This method's only caller —
    /// `RecordingLifecycle.stopRecording()` — is gated on
    /// `guard isRecording else { return nil }`, and `isRecording` is set only *after*
    /// the suspension, so a user-initiated stop landing inside the start window never
    /// reaches this method at all. That window is closed by `cancelPendingStart()`,
    /// which the same caller now invokes from its `guard`'s else branch; the bump here
    /// covers the ordinary stop-a-live-recording case and any future caller that is not
    /// gated on `isRecording`.
    ///
    /// The teardown itself lives in `retireActiveRecorder()` so that the install path of
    /// `startRecording(to:)` uses the identical sequence.
    func stopRecording() {
        // Invalidate any start that is still suspended in makeLiveRecorder().
        startGeneration &+= 1

        retireActiveRecorder()

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
