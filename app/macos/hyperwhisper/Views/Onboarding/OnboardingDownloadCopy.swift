//
//  OnboardingDownloadCopy.swift
//  hyperwhisper
//
//  The decisions the onboarding "Download your model" card makes about what to
//  put on screen while a local model downloads (issue #312).
//

import Foundation

/// What the onboarding download card says, and whether it says a number at all.
///
/// These four functions carry the whole user-visible half of the #312 fix: they
/// decide between a percentage and an indeterminate bar, and they produce the
/// three strings the card renders. They used to be `private static` members of
/// `OnboardingSetupView`, where nothing outside a running SwiftUI view could
/// reach them — so the negative-progress path, the "23 of 22" clamp and the
/// no-number pill were asserted only by reading the source.
///
/// They live here instead, on a caseless `enum` used purely as a namespace, so
/// the view and `hyperwhisperTests` can both call them. Nothing here touches
/// SwiftUI, a `View`, or the main actor; it is a pure function of a
/// `ModelDownloadStage?` and a `Double`. Layout constants stay in the view,
/// where they belong.
///
/// Kept as plain functions rather than inline `switch`es in the ViewBuilder on
/// purpose: a `switch` reads very differently inside a result builder, and
/// keeping the decision out here makes the card body a simple if/else over a
/// `Bool` and two `String`s.
enum OnboardingDownloadCopy {

    /// True while the published fraction is not worth printing as a percentage.
    ///
    /// For an engine that reports a stage that is the whole download. Every part
    /// of it publishes a number that stands still for minutes: `DownloadController`
    /// clamps to a 0.01 floor and FluidAudio emits nothing until a whole file
    /// lands, then the compile tail steps 0.9 → 1.0 once per component and so
    /// holds 90% for the entire ANE encoder compile. Both are #312 — relocating
    /// the frozen number from 1% to 90% would not have fixed it.
    ///
    /// `nil` — an engine with no stage — keeps today's percentage, but only when
    /// the fraction really is one. `WhisperModelManager` publishes
    /// `-Double(totalBytesWritten)` with the comment "Negative indicates
    /// indeterminate" when the server sends no `Content-Length`
    /// (`WhisperModelManager.swift:518`), and `LiveOnboardingModelCatalog` passes
    /// it through verbatim, so the determinate branch rendered "-48213504%" over
    /// an empty bar. An unusable fraction gets the indeterminate bar too — the
    /// same answer, for the same reason, on the one engine whose download really
    /// is unmeasurable. Nothing in `WhisperModelManager` is touched.
    static func isIndeterminate(stage: ModelDownloadStage?, progress: Double) -> Bool {
        guard let stage else {
            return !(progress.isFinite && progress >= 0.0 && progress <= 1.0)
        }
        // Listed case by case rather than a bare `return true` so that adding a
        // `ModelDownloadStage` forces a decision here instead of inheriting one.
        switch stage {
        case .preparing, .downloading, .processing:
            return true
        }
    }

    /// The heading that replaces the percentage while the bar is indeterminate.
    ///
    /// FluidAudio counts files it has *finished*, so the one in flight is the
    /// next one; the `min` stops the very last callback reading "23 of 22".
    ///
    /// The values differ a lot in length — "Downloading file 22 of 22" against
    /// "Optimizing the model for this Mac. Almost done." — and how many lines
    /// each takes is a property of the locale, not of English. The card
    /// therefore reserves two lines for this heading in both of its branches
    /// (`OnboardingSetupView.headingHeight`) so the stage transitions cannot
    /// move it. Nothing here may be allowed to need a third line: the card
    /// clamps with `lineLimit(2)` and a minimum scale factor.
    static func phaseText(_ stage: ModelDownloadStage?) -> String {
        // No stage and yet indeterminate: the only route here is an engine whose
        // fraction is unusable — Whisper against a server that sent no
        // `Content-Length`. Bytes are moving and nothing else is known, so
        // "Downloading..." is the whole of the honest answer.
        guard let stage else { return "onboarding.setup.onDevice.downloadingPill".localized }
        switch stage {
        case .preparing:
            return "onboarding.setup.onDevice.preparing".localized
        case .downloading(let completedFiles, let totalFiles):
            guard totalFiles > 0 else { return "onboarding.setup.onDevice.preparing".localized }
            let current = min(completedFiles + 1, totalFiles)
            return "onboarding.setup.onDevice.downloadingFile".localized(arguments: current, totalFiles)
        case .processing:
            return "onboarding.setup.onDevice.optimizing".localized
        }
    }

    /// The status pill next to the model name. Same rule as the big figure: no
    /// percentage while there is no percentage to be honest about.
    static func pillText(stage: ModelDownloadStage?, progress: Double) -> String {
        guard let stage else {
            // Whisper. A usable fraction keeps today's "Downloading... 42%"; the
            // negative "size unknown" sentinel drops the number rather than
            // printing it.
            if isIndeterminate(stage: nil, progress: progress) {
                return "onboarding.setup.onDevice.downloadingPill".localized
            }
            return "onboarding.setup.onDevice.downloading".localized(arguments: Int(progress * 100))
        }
        switch stage {
        case .preparing, .downloading:
            return "onboarding.setup.onDevice.downloadingPill".localized
        case .processing:
            // The bytes are down; saying "Downloading…" through the compile would
            // be the same kind of small lie the rest of this change removes.
            return "onboarding.setup.onDevice.optimizingPill".localized
        }
    }

    /// The reassurance note under the size, or `nil` for an engine that reports
    /// no stage — which is what keeps Whisper's card untouched.
    ///
    /// Non-`nil` for **every** stage, and the *same* string for every stage,
    /// deliberately. The note is the last thing in the card, so anything that
    /// changes it at a stage transition moves the card's bottom edge and
    /// everything under it. `.processing` used to return `nil`, which deleted the
    /// note outright at exactly the transition the reserved heading height was
    /// added to make stable; giving `.processing` a *different* sentence would
    /// only trade that for a note that is two lines in one stage and one in the
    /// next, which is the same defect in a locale I cannot measure from here.
    /// One string for all three stages is stable by construction, in every
    /// locale, with no height to reserve and no metric to guess.
    ///
    /// The explanation of the compile tail is not lost: at `.processing` it is
    /// the heading, which has two reserved lines to wrap into.
    static func hintText(_ stage: ModelDownloadStage?) -> String? {
        guard let stage else { return nil }
        switch stage {
        case .preparing, .downloading, .processing:
            return "onboarding.setup.onDevice.firstRunHint".localized
        }
    }
}
