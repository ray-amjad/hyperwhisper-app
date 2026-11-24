//
//  SilentMicrophoneWatcher.swift
//  hyperwhisper
//
//  Issue #2411: warn while recording when the microphone hears nothing.
//
//  The same rule runs on every head (macOS, Windows, Linux):
//  - Each audio block's PEAK in dBFS is "heard" when it is at or above
//    `silenceThresholdDbfs` (-60). An all-zero block is -infinity.
//  - The warning shows once capture has run for `warnAfter` (2.0 s) with no block
//    heard yet. It is driven by a clock tick as well as by blocks, so it also
//    fires when the device delivers no blocks at all.
//  - The first heard block hides it and DISARMS the watcher for the rest of the
//    recording, so pauses in speech can never bring it back (noise-gated Bluetooth
//    headsets, AirPods and Voice Isolation output digital zero between words).
//
//  Pure value type with injected time, so the rule is unit-testable without a
//  microphone (`SilentMicrophoneWatcherTests`).
//

import Foundation

struct SilentMicrophoneWatcher: Equatable, Sendable {

    /// A block whose peak reaches this level is "heard". Speech peaks around
    /// -40...-6 dBFS and a live mic's room noise above -60; a muted, blocked,
    /// unplugged or idle virtual input reads exact zero or below -70. -60 is also
    /// the floor the waveform maps to "flat" (`SimpleRecorder.minDb`).
    static let silenceThresholdDbfs: Float = -60

    /// How long capture must run with nothing heard before the warning shows.
    static let warnAfter: TimeInterval = 2.0

    enum Phase: Equatable, Sendable {
        /// No recording is capturing. Blocks and ticks are ignored.
        case idle
        /// Capturing since `since`; nothing heard yet.
        case listening(since: TimeInterval)
        /// A block was heard in this recording. Disarmed until the next `start`.
        case heard
    }

    private(set) var phase: Phase = .idle

    /// Whether the warning should be on screen now.
    private(set) var isWarning = false

    /// True when a block with this peak counts as sound. NaN never does.
    static func isHeard(peakDbfs: Float) -> Bool {
        peakDbfs >= silenceThresholdDbfs
    }

    /// Capture has actually started (not the hotkey, not a streaming connect).
    /// Re-arms the watcher for this recording.
    mutating func start(at now: TimeInterval) {
        phase = .listening(since: now)
        isWarning = false
    }

    /// One audio block's peak arrived.
    mutating func observe(peakDbfs: Float, at now: TimeInterval) {
        guard case .listening = phase else { return }
        if Self.isHeard(peakDbfs: peakDbfs) {
            phase = .heard
            isWarning = false
            return
        }
        tick(at: now)
    }

    /// The clock moved on, with or without blocks.
    mutating func tick(at now: TimeInterval) {
        guard case .listening(let since) = phase else {
            isWarning = false
            return
        }
        isWarning = now - since >= Self.warnAfter
    }

    /// The recording stopped, was cancelled or failed.
    mutating func stop() {
        phase = .idle
        isWarning = false
    }

    // MARK: - Peak helpers

    /// 20·log10 of a block's largest absolute sample (full scale = 1.0).
    /// Zero, or anything not finite, is -infinity.
    static func peakDbfs(maxAbsoluteSample: Float) -> Float {
        guard maxAbsoluteSample.isFinite, maxAbsoluteSample > 0 else { return -Float.infinity }
        return 20 * log10(maxAbsoluteSample)
    }

    // MARK: - Display

    /// Longest microphone name shown before it is cut with an ellipsis.
    static let maxDisplayedNameLength = 32

    /// The microphone name for the warning line, cut with "…" when long.
    static func displayName(_ name: String, maxLength: Int = maxDisplayedNameLength) -> String {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard maxLength > 1, trimmed.count > maxLength else { return trimmed }
        return String(trimmed.prefix(maxLength - 1)) + "…"
    }
}
