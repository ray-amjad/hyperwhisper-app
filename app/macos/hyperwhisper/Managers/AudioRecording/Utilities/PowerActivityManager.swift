//
//  PowerActivityManager.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation

/// Manages power assertions to prevent App Nap during critical operations
///
/// **Purpose:**
/// Prevents macOS from throttling the app during recording and transcription.
/// Without this, timers may be coalesced and audio processing may be delayed.
///
/// **How it Works:**
/// 1. When recording starts, we call `beginPowerActivity()` with a reason
/// 2. ProcessInfo creates an activity token with specific options:
///    - `.userInitiated`: Indicates this is user-triggered work
///    - `.latencyCritical`: Prevents timer coalescing
///    - `.idleSystemSleepDisabled`: Keeps system awake
/// 3. When transcription completes, we call `endPowerActivity()` to release
///
/// **Benefits:**
/// - Ensures smooth audio recording without buffer underruns
/// - Maintains accurate timer firing for duration tracking
/// - Prevents system sleep during long recordings
///
/// **Thread Safety:**
/// This class should be used from the main actor since it manages UI-related state.
@MainActor
class PowerActivityManager {

    // MARK: - Properties

    /// The current power activity token, if one is active
    /// This token must be retained and later passed to `endActivity()` to release the assertion
    private var powerActivity: NSObjectProtocol?

    /// True while a caller still wants a power assertion. `beginActivity` can
    /// synchronously block in RunningBoard/App Nap, so the token may arrive after
    /// recording has already stopped.
    private var wantsPowerActivity = false

    /// Prevents launching duplicate background assertions while the first
    /// `beginActivity` call is still in flight.
    private var isPowerActivityRequestInFlight = false

    // MARK: - Public Methods

    /// Begins a power activity to prevent App Nap and timer throttling
    ///
    /// **What This Does:**
    /// Creates a power assertion that tells macOS this app is doing critical work
    /// that should not be throttled or deferred.
    ///
    /// **When to Call:**
    /// - At the start of audio recording
    /// - Before starting transcription
    /// - Any time you need guaranteed timer accuracy
    ///
    /// **Parameters:**
    /// - `reason`: Human-readable description of why we need power (for debugging)
    ///
    /// **Implementation Notes:**
    /// - Only creates a new activity if one doesn't already exist
    /// - Multiple calls with an active token are ignored (idempotent)
    /// - The token is stored in `powerActivity` for later cleanup
    func beginPowerActivity(_ reason: String) {
        // Only create a new power activity if we don't already have one
        // This prevents creating duplicate assertions
        wantsPowerActivity = true
        guard powerActivity == nil, !isPowerActivityRequestInFlight else { return }

        isPowerActivityRequestInFlight = true
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let token = ProcessInfo.processInfo.beginActivity(
                options: [
                    .userInitiated,              // User-triggered work
                    .latencyCritical,            // Don't coalesce timers
                    .idleSystemSleepDisabled     // Don't sleep during recording
                ],
                reason: reason
            )

            DispatchQueue.main.async { [weak self] in
                guard let self else {
                    ProcessInfo.processInfo.endActivity(token)
                    return
                }

                self.isPowerActivityRequestInFlight = false

                guard self.wantsPowerActivity, self.powerActivity == nil else {
                    ProcessInfo.processInfo.endActivity(token)
                    return
                }

                self.powerActivity = token
                AppLogger.audio.debug("🔋 Began power activity: \(reason)")
            }
        }
    }

    /// Ends the current power activity and allows normal power management
    ///
    /// **What This Does:**
    /// Releases the power assertion, allowing macOS to resume normal energy-saving
    /// behaviors like App Nap and timer coalescing.
    ///
    /// **When to Call:**
    /// - After recording stops
    /// - After transcription completes
    /// - Any time the critical operation is finished
    ///
    /// **Implementation Notes:**
    /// - Safe to call even if no activity is active (idempotent)
    /// - Clears the `powerActivity` token after releasing
    func endPowerActivity() {
        wantsPowerActivity = false

        if let token = powerActivity {
            ProcessInfo.processInfo.endActivity(token)
            powerActivity = nil
            AppLogger.audio.debug("🔋 Ended power activity")
        }
    }
}
