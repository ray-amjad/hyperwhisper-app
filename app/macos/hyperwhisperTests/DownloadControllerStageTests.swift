//
//  DownloadControllerStageTests.swift
//  hyperwhisperTests
//

import Testing
import Combine
@testable import HyperWhisper

/// `DownloadController`'s first frame and its publishing discipline (issue #312).
///
/// Every assertion here reads the controller on the frame `start` / `report` returns,
/// which is exactly when the onboarding card renders: `downloading` publishes
/// synchronously, so the card is on screen well before the manager's first progress
/// callback has hopped to the main actor.
@MainActor
struct DownloadControllerStageTests {

    private static let parakeet = "parakeet-tdt-0.6b-v2"

    /// The reported symptom of #312, on the first frame of every download. Without a
    /// stage here a reader that picks "percentage or indeterminate" from the stage sees
    /// `nil`, treats the download as measurable, and prints the 0.01 seed as a hard
    /// "Downloading... 1%" over a determinate bar.
    @Test func startSeedsTheStageOnTheSameFrameAsTheProgressFloor() {
        let controller = DownloadController<String>()
        controller.start(Self.parakeet, initialStage: .preparing) { _ in }

        #expect(controller.isDownloading(Self.parakeet))
        #expect(controller.progress[Self.parakeet] == 0.01)
        #expect(controller.stage[Self.parakeet] == ModelDownloadStage.preparing)
    }

    /// The seed is a caller opt-in, so `stage[key] == nil` keeps meaning "this manager
    /// has no stage to give" — which is what `LiveOnboardingModelCatalog.stage(for:)`
    /// and the `OnboardingModelCatalog.stage(for:)` default both read it as. Qwen3 is
    /// the manager that passes nothing.
    @Test func aCallerWithNoStageToGiveLeavesTheKeyStageFree() {
        let controller = DownloadController<String>()
        controller.start("qwen3-asr") { _ in }

        #expect(controller.isDownloading("qwen3-asr"))
        #expect(controller.progress["qwen3-asr"] == 0.01)
        #expect(controller.stage["qwen3-asr"] == nil)
    }

    /// `@Published` fires `objectWillChange` on every assignment, identical value or
    /// not. FluidAudio holds `completedFiles` fixed for the whole of one file — the
    /// encoder weights are most of the 474 MB — so a large file used to fire a long run
    /// of redundant ticks into the onboarding subscription.
    @Test func reportSkipsWritesThatChangeNothing() {
        let controller = DownloadController<String>()
        var ticks = 0
        let subscription = controller.objectWillChange.sink { _ in ticks += 1 }

        controller.start(Self.parakeet, initialStage: .preparing) { _ in }
        ticks = 0

        // A real move: the fraction and the stage both change.
        controller.report(
            Self.parakeet, fraction: 0.4,
            stage: .downloading(completedFiles: 3, totalFiles: 22))
        #expect(ticks == 2)

        // The same values again, twice: nothing has changed, so nothing is published.
        ticks = 0
        controller.report(
            Self.parakeet, fraction: 0.4,
            stage: .downloading(completedFiles: 3, totalFiles: 22))
        controller.report(
            Self.parakeet, fraction: 0.4,
            stage: .downloading(completedFiles: 3, totalFiles: 22))
        #expect(ticks == 0)
        #expect(controller.progress[Self.parakeet] == 0.4)
        #expect(
            controller.stage[Self.parakeet]
                == ModelDownloadStage.downloading(completedFiles: 3, totalFiles: 22))

        // A changed stage at an unchanged fraction still publishes — the whole compile
        // tail sits at one fraction, so this is the tick the phase line lives on.
        ticks = 0
        controller.report(Self.parakeet, fraction: 0.4, stage: .processing)
        #expect(ticks == 1)
        #expect(controller.stage[Self.parakeet] == ModelDownloadStage.processing)

        subscription.cancel()
    }

    /// The 0.01 floor is why deduplicating matters at the start too: every fraction
    /// under it clamps onto the seed, so the opening callbacks are all no-ops.
    @Test func fractionsUnderTheFloorDoNotRepublishTheSeed() {
        let controller = DownloadController<String>()
        controller.start(Self.parakeet, initialStage: .preparing) { _ in }

        var ticks = 0
        let subscription = controller.objectWillChange.sink { _ in ticks += 1 }
        controller.report(Self.parakeet, fraction: 0.0, stage: .preparing)
        controller.report(Self.parakeet, fraction: 0.001, stage: .preparing)
        #expect(ticks == 0)
        #expect(controller.progress[Self.parakeet] == 0.01)

        subscription.cancel()
    }
}
