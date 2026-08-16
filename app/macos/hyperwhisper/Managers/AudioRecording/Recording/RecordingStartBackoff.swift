//
//  RecordingStartBackoff.swift
//  hyperwhisper
//
//  Extracted from RecordingLifecycle so the schedule is unit-testable.
//

import Foundation

/// Retry schedule for the input-device probe at the top of
/// `RecordingLifecycle.startRecording()`.
///
/// **Why this is a named constant and not an inline literal:**
/// CoreAudio can transiently report zero input devices during audio route changes
/// (Bluetooth disconnect, USB audio removal, AirPods reconnect, wake-from-sleep) even
/// on Macs with a built-in microphone. When that happens the recording start bails out
/// with `AudioError.noMicrophoneAvailable` and the user sees "no microphone" on a
/// machine that has one — Sentry HYPERWHISPER-NF.
///
/// The original fix used 2 × 250 ms = 500 ms of total backoff. That was too short, and
/// the issue regressed on v2.33.1. The schedule below totals 3200 ms, which is what
/// stopped it. `RecordingStartBackoffTests` asserts that total, so shortening it again
/// fails CI instead of shipping. How the total is split across attempts is deliberately
/// not asserted — that part is tunable.
///
/// **Do not shorten this.** A machine with genuinely no input device returns instantly
/// on the first probe and never reaches the retry loop, so the schedule costs nothing
/// in the common failure case — it is only spent on the transient case it exists for.
///
/// **Isolation:** deliberately `nonisolated` (no global actor). The probe loop that
/// reads it runs on the main actor today, but the whole point of HYPERWHISPER-F7 is to
/// move that work off it, and the tests are `async` (therefore off the main actor), so
/// annotating this type `@MainActor` would break the build.
enum RecordingStartBackoff {

    /// Per-attempt sleep, in milliseconds, between input-device probes.
    ///
    /// One entry per retry; the first probe happens before the loop, so the number of
    /// probes is `inputDeviceRetryDelaysMs.count + 1`.
    static let inputDeviceRetryDelaysMs: [UInt64] = [150, 250, 400, 600, 800, 1000]

    /// Total wall time the probe is willing to spend on retries, in milliseconds.
    ///
    /// Derived from `inputDeviceRetryDelaysMs` rather than written out separately, so
    /// the two can never disagree. Reported as `deviceRetryBudgetMs` on the
    /// "no audio input devices after retries" breadcrumb, where it says which schedule
    /// the build in front of you actually shipped — releases lag `main`, so a Sentry
    /// event showing a short budget is a stale build, not a new bug.
    static var totalInputDeviceRetryBudgetMs: UInt64 {
        inputDeviceRetryDelaysMs.reduce(0, +)
    }
}
