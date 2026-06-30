//
//  FileTranscriptionProgress.swift
//  hyperwhisper
//
//  FILE TRANSCRIPTION PROGRESS MODEL
//  Observable state model for tracking file transcription progress in the popup.
//
//  STAGE-BASED PROGRESS:
//  Since transcription APIs don't provide real-time progress, we use discrete stages
//  with smooth animated transitions between them:
//  - Preparing (0-15%): File copy, video extraction, VAD processing
//  - Transcribing (15-85%): API call (slow animated progress)
//  - Finishing (85-100%): Post-processing, saving
//
//  ANIMATION STRATEGY:
//  Uses a 60fps timer for smooth progress bar animation with easeOutCubic easing.
//  When the actual stage completes, progress jumps to the stage's end value.
//

import Foundation
import SwiftUI

// MARK: - File Transcription Stage

/// Represents the stages of file transcription
///
/// **Stage Progression:**
/// 1. `preparing` - File validation, copying, video extraction, VAD
/// 2. `transcribing` - API call to transcription provider
/// 3. `finishing` - Post-processing, saving results
///
/// Each stage has a progress range that the animated progress bar fills.
enum FileTranscriptionStage: String, CaseIterable {
    case preparing = "preparing"
    case transcribing = "transcribing"
    case finishing = "finishing"

    /// Localized title for display in the popup
    var localizedTitle: String {
        switch self {
        case .preparing:
            return "file.transcription.preparing".localized
        case .transcribing:
            return "file.transcription.transcribing".localized
        case .finishing:
            return "file.transcription.finishing".localized
        }
    }

    /// Progress range for this stage (0.0 to 1.0)
    ///
    /// **Range Breakdown:**
    /// - Preparing: 0% - 15% (quick operations)
    /// - Transcribing: 15% - 85% (bulk of the time)
    /// - Finishing: 85% - 100% (quick wrap-up)
    var progressRange: ClosedRange<Float> {
        switch self {
        case .preparing:
            return 0.0...0.15
        case .transcribing:
            return 0.15...0.85
        case .finishing:
            return 0.85...1.0
        }
    }

    /// SF Symbol icon for this stage
    var icon: String {
        switch self {
        case .preparing:
            return "doc.badge.gearshape"
        case .transcribing:
            return "waveform"
        case .finishing:
            return "checkmark.circle"
        }
    }
}

// MARK: - File Transcription Progress

/// Observable state model for file transcription progress
///
/// **Purpose:**
/// Tracks the current stage and animated progress for the file transcription popup.
/// Provides smooth animations using a 60fps timer with easeOutCubic easing.
///
/// **Usage:**
/// 1. Create instance and call `beginTranscription(fileName:modeName:)`
/// 2. Update progress with `updateStage(_:)` and `animateProgress(to:duration:)`
/// 3. Call `complete()` when done or `cancel()` to abort
/// 4. Call `reset()` to prepare for next transcription
///
/// **Thread Safety:**
/// All properties and methods are MainActor-isolated for UI consistency.
@MainActor
final class FileTranscriptionProgress: ObservableObject {

    // MARK: - Published Properties

    /// Current transcription stage
    @Published var stage: FileTranscriptionStage = .preparing

    /// Current progress value (0.0 to 1.0)
    @Published var progress: Float = 0.0

    /// Whether a file transcription is currently active
    @Published var isActive: Bool = false

    /// Whether the user has cancelled the transcription
    @Published var isCancelled: Bool = false

    /// Name of the file being transcribed (for display)
    @Published var fileName: String = ""

    /// Name of the transcription mode (for display)
    @Published var modeName: String = ""

    // MARK: - Private Properties

    /// Timer for smooth progress animation
    private var animationTimer: Timer?

    /// Target progress for current animation
    private var targetProgress: Float = 0.0

    /// Start progress for current animation
    private var startProgress: Float = 0.0

    /// Start time for current animation
    private var animationStartTime: Date?

    /// Duration for current animation
    private var animationDuration: TimeInterval = 0.0

    // MARK: - Public Methods

    /// Begin tracking a new file transcription
    ///
    /// **What This Does:**
    /// - Sets the file and mode names for display
    /// - Resets progress to 0
    /// - Sets stage to preparing
    /// - Marks transcription as active
    ///
    /// - Parameters:
    ///   - fileName: Name of the file being transcribed
    ///   - modeName: Name of the transcription mode
    func beginTranscription(fileName: String, modeName: String) {
        self.fileName = fileName
        self.modeName = modeName
        self.progress = 0.0
        self.stage = .preparing
        self.isActive = true
        self.isCancelled = false

        // Start initial animation to show activity
        animateProgress(to: 0.05, duration: 0.3)
    }

    /// Update to a new stage
    ///
    /// **What This Does:**
    /// - Updates the stage property
    /// - Triggers SwiftUI animation for stage indicator
    ///
    /// - Parameter newStage: The new stage to transition to
    func updateStage(_ newStage: FileTranscriptionStage) {
        withAnimation(.spring(response: 0.3, dampingFraction: 0.7)) {
            self.stage = newStage
        }
    }

    /// Animate progress to a target value over a duration
    ///
    /// **How It Works:**
    /// 1. Stores animation parameters (start, target, duration)
    /// 2. Starts a 60fps timer
    /// 3. Each tick calculates progress using easeOutCubic easing
    /// 4. Timer stops when target is reached
    ///
    /// **Use Cases:**
    /// - Known-duration ops: Use actual expected time
    /// - API call (unknown): Use long duration (60s), animation will be cut short when result arrives
    ///
    /// - Parameters:
    ///   - target: Target progress value (0.0 to 1.0)
    ///   - duration: Animation duration in seconds
    func animateProgress(to target: Float, duration: TimeInterval) {
        // Stop any existing animation
        animationTimer?.invalidate()

        // Store animation parameters
        startProgress = progress
        targetProgress = min(max(target, 0.0), 1.0)
        animationDuration = duration
        animationStartTime = Date()

        // Start animation timer at 60fps
        animationTimer = Timer.scheduledTimer(withTimeInterval: 1.0/60.0, repeats: true) { [weak self] timer in
            Task { @MainActor in
                self?.updateAnimatedProgress(timer: timer)
            }
        }
    }

    /// Immediately set progress to a value (no animation)
    ///
    /// **Use Case:**
    /// When you need to jump to a specific progress without animation,
    /// such as when completing a stage early.
    ///
    /// - Parameter value: Progress value to set (0.0 to 1.0)
    func setProgress(_ value: Float) {
        animationTimer?.invalidate()
        animationTimer = nil
        progress = min(max(value, 0.0), 1.0)
    }

    /// Mark transcription as complete
    ///
    /// **What This Does:**
    /// - Animates progress to 100%
    /// - Updates stage to finishing (if not already)
    func complete() {
        updateStage(.finishing)
        animateProgress(to: 1.0, duration: 0.3)
    }

    /// Cancel the transcription
    ///
    /// **What This Does:**
    /// - Sets isCancelled flag
    /// - Stops any running animation
    func cancel() {
        isCancelled = true
        animationTimer?.invalidate()
        animationTimer = nil
    }

    /// Reset state for next transcription
    ///
    /// **What This Does:**
    /// - Resets all state to initial values
    /// - Stops any running animation
    /// - Ready for next `beginTranscription()` call
    func reset() {
        animationTimer?.invalidate()
        animationTimer = nil

        stage = .preparing
        progress = 0.0
        isActive = false
        isCancelled = false
        fileName = ""
        modeName = ""
        targetProgress = 0.0
        startProgress = 0.0
        animationStartTime = nil
        animationDuration = 0.0
    }

    // MARK: - Private Methods

    /// Update progress based on animation timer
    ///
    /// **Animation Math:**
    /// Uses easeOutCubic easing for natural deceleration:
    /// `1.0 - pow(1.0 - t, 3)` where t is normalized time (0 to 1)
    ///
    /// - Parameter timer: The timer that triggered this update
    private func updateAnimatedProgress(timer: Timer) {
        guard let startTime = animationStartTime else {
            timer.invalidate()
            return
        }

        let elapsed = Date().timeIntervalSince(startTime)
        let normalizedTime = min(elapsed / animationDuration, 1.0)
        let easedTime = easeOutCubic(normalizedTime)

        // Calculate new progress
        let newProgress = startProgress + (targetProgress - startProgress) * Float(easedTime)
        progress = newProgress

        // Stop timer when animation complete
        if normalizedTime >= 1.0 {
            timer.invalidate()
            animationTimer = nil
            progress = targetProgress
        }
    }

    /// EaseOutCubic easing function
    ///
    /// **Behavior:**
    /// Starts fast, decelerates toward the end.
    /// Creates a natural "settling" feel for progress animations.
    ///
    /// - Parameter t: Normalized time (0.0 to 1.0)
    /// - Returns: Eased value (0.0 to 1.0)
    private func easeOutCubic(_ t: Double) -> Double {
        return 1.0 - pow(1.0 - t, 3)
    }
}
