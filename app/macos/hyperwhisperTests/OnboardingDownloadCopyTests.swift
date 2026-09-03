//
//  OnboardingDownloadCopyTests.swift
//  hyperwhisperTests
//
//  The onboarding download card's four decisions (issue #312). These used to be
//  `private static` members of `OnboardingSetupView`, so the whole user-visible
//  half of the fix — including the negative-progress path — was asserted only by
//  reading the source. A refactor reinstating `guard let stage else { return
//  false }` in `isIndeterminate` would have restored the "-48213504%" render
//  with every test in the suite still green. These are the tests that fail.
//

import Foundation
import Testing
@testable import HyperWhisper

/// Assertions compare against `"key".localized` rather than against English
/// literals on purpose. The unit tests run in the app's own bundle, so
/// `.localized` resolves the shipped table for whatever locale the machine
/// running CI happens to be in; pinning "Downloading…" would make these tests a
/// report on the CI runner's language settings. What is being asserted is
/// *which key the card picks*, which is the decision under test, and that holds
/// in all 40 locales.
///
/// The digit assertions are safe alongside that: `String(format:)` is called
/// with no locale, so `%d` always renders ASCII digits.
struct OnboardingDownloadCopyTests {

    // MARK: isIndeterminate

    /// Every stage-reporting engine is indeterminate for the whole download.
    /// There is no honest number at any point of it: the transfer sits on
    /// `DownloadController`'s 0.01 floor until a whole file lands, and the
    /// compile tail is a four-step staircase that holds 90% for the entire ANE
    /// encoder compile.
    @Test func everyStageIsIndeterminate() {
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: .preparing, progress: 0.01))
        #expect(OnboardingDownloadCopy.isIndeterminate(
            stage: .downloading(completedFiles: 11, totalFiles: 22), progress: 0.44))
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: .processing, progress: 0.9))
    }

    /// A stage-reporting engine stays indeterminate even at a fraction that
    /// would render perfectly well. The stage decides, not the number — this is
    /// what stops the card swapping bar shapes mid-download.
    @Test func aStageWinsOverAPerfectlyUsableFraction() {
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: .processing, progress: 1.0))
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: .preparing, progress: 0.5))
    }

    /// No stage and a real fraction: Whisper's card, exactly as it was before
    /// this change. Determinate, with today's percentage.
    @Test func noStageWithAUsableFractionKeepsThePercentage() {
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: 0.0) == false)
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: 0.42) == false)
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: 1.0) == false)
    }

    /// The regression `680ac45` was written for. `WhisperModelManager.swift:518`
    /// publishes `-Double(totalBytesWritten)` when the server sends no
    /// `Content-Length`, and `LiveOnboardingModelCatalog` passes it through
    /// verbatim. Treating that as a fraction rendered a 30 pt "-48213504%".
    ///
    /// The value is the real one from the field report, not a token negative.
    @Test func whispersNegativeSizeUnknownSentinelIsIndeterminate() {
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: -48213504))
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: -0.000001))
    }

    /// The other two ways a `Double` stops being a fraction. Neither has been
    /// seen in the field; both would render as garbage in the determinate
    /// branch, and the guard costs nothing.
    @Test func aFractionOutsideZeroToOneOrNotANumberIsIndeterminate() {
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: 1.5))
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: Double.nan))
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: Double.infinity))
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: -Double.infinity))
    }

    // MARK: phaseText

    @Test func preparingReadsAsPreparing() {
        #expect(OnboardingDownloadCopy.phaseText(.preparing)
                == "onboarding.setup.onDevice.preparing".localized)
    }

    /// FluidAudio's `.downloading(completedFiles:totalFiles:)` counts files it
    /// has *finished* (`DownloadUtils.swift:572, 616`), so the file in flight is
    /// the next one and the card shows `completed + 1`.
    @Test func downloadingCountsTheFileInFlightNotTheOnesFinished() {
        #expect(OnboardingDownloadCopy.phaseText(.downloading(completedFiles: 0, totalFiles: 22))
                == "onboarding.setup.onDevice.downloadingFile".localized(arguments: 1, 22))
        #expect(OnboardingDownloadCopy.phaseText(.downloading(completedFiles: 11, totalFiles: 22))
                == "onboarding.setup.onDevice.downloadingFile".localized(arguments: 12, 22))
    }

    /// The last callback of a 22 file repo reports 22 completed of 22. Without
    /// the `min` the card's final frame reads "Downloading file 23 of 22".
    @Test func theLastCallbackDoesNotReadTwentyThreeOfTwentyTwo() {
        let text = OnboardingDownloadCopy.phaseText(.downloading(completedFiles: 22, totalFiles: 22))

        #expect(text == "onboarding.setup.onDevice.downloadingFile".localized(arguments: 22, 22))
        #expect(text != "onboarding.setup.onDevice.downloadingFile".localized(arguments: 23, 22))
        #expect(text.contains("23") == false)
    }

    /// The cache-hit sentinel. `DownloadUtils` emits `.downloading(0, 0)` for a
    /// component already on disk; "Downloading file 1 of 0" would be nonsense,
    /// so it falls back to the preparing line.
    @Test func aZeroFileCountFallsBackToPreparingRatherThanFileOneOfZero() {
        let text = OnboardingDownloadCopy.phaseText(.downloading(completedFiles: 0, totalFiles: 0))

        #expect(text == "onboarding.setup.onDevice.preparing".localized)
    }

    @Test func processingReadsAsTheOptimizingSentence() {
        #expect(OnboardingDownloadCopy.phaseText(.processing)
                == "onboarding.setup.onDevice.optimizing".localized)
    }

    /// The only route to a `nil` stage in the indeterminate branch is an engine
    /// whose fraction is unusable — Whisper against a server that sent no
    /// `Content-Length`. Bytes are moving and nothing else is known, so the
    /// heading is the bare "Downloading…" with no number and no file count.
    /// It must not read "Preparing download…", which was the round-1 defect:
    /// the transfer is already in flight.
    @Test func noStageReadsAsPlainDownloadingNotPreparing() {
        let text = OnboardingDownloadCopy.phaseText(nil)

        #expect(text == "onboarding.setup.onDevice.downloadingPill".localized)
        #expect(text != "onboarding.setup.onDevice.preparing".localized)
    }

    // MARK: pillText

    /// Whisper with a real fraction keeps today's pill verbatim.
    @Test func noStageWithAUsableFractionKeepsTheNumberInThePill() {
        #expect(OnboardingDownloadCopy.pillText(stage: nil, progress: 0.42)
                == "onboarding.setup.onDevice.downloading".localized(arguments: 42))
    }

    /// The pill is the second place the `-48213504%` render could reach the
    /// screen. It drops the number rather than printing it.
    @Test func theNegativeSentinelDropsTheNumberFromThePill() {
        let text = OnboardingDownloadCopy.pillText(stage: nil, progress: -48213504)

        #expect(text == "onboarding.setup.onDevice.downloadingPill".localized)
        #expect(text.contains("48213504") == false)
    }

    /// Through the transfer the pill carries no percentage, because there is no
    /// percentage worth carrying.
    @Test func theTransferPillCarriesNoNumber() {
        let transferStages: [ModelDownloadStage] = [
            .preparing,
            .downloading(completedFiles: 11, totalFiles: 22)
        ]
        for stage in transferStages {
            #expect(OnboardingDownloadCopy.pillText(stage: stage, progress: 0.01)
                    == "onboarding.setup.onDevice.downloadingPill".localized)
        }
    }

    /// Once the bytes are down the pill stops saying "Downloading…". Leaving it
    /// there through a four minute compile is the same small lie this change
    /// exists to remove.
    @Test func theCompileTailSaysOptimizingNotDownloading() {
        #expect(OnboardingDownloadCopy.pillText(stage: .processing, progress: 0.9)
                == "onboarding.setup.onDevice.optimizingPill".localized)
        #expect(OnboardingDownloadCopy.pillText(stage: .processing, progress: 0.9)
                != "onboarding.setup.onDevice.downloadingPill".localized)
    }

    // MARK: hintText

    /// An engine that reports no stage gets no note at all, which is what keeps
    /// Whisper's card byte-identical to what shipped.
    @Test func noStageGetsNoNote() {
        #expect(OnboardingDownloadCopy.hintText(nil) == nil)
    }

    /// The layout invariant this note carries. It is the last thing in the card,
    /// so if it appears, disappears, or changes length between stages, the card's
    /// bottom edge and everything under it move at that transition — which is
    /// the jump the reserved heading height was added to stop, arriving by
    /// another door. `.processing` returning `nil` was exactly that.
    ///
    /// Same string, present, for every stage.
    @Test func theNoteIsPresentAndIdenticalInEveryStage() {
        let stages: [ModelDownloadStage] = [
            .preparing,
            .downloading(completedFiles: 0, totalFiles: 22),
            .downloading(completedFiles: 22, totalFiles: 22),
            .processing
        ]
        let hints = stages.map { OnboardingDownloadCopy.hintText($0) }

        #expect(hints.allSatisfy { $0 != nil })
        #expect(Set(hints.compactMap { $0 }).count == 1)
        #expect(OnboardingDownloadCopy.hintText(.processing)
                == "onboarding.setup.onDevice.firstRunHint".localized)
    }

    // MARK: The card's two halves agree

    /// The heading and the pill are produced by two different functions and are
    /// on screen at the same time. They must never disagree about which half of
    /// the download this is.
    @Test func theHeadingAndThePillAgreeAboutTheCompileTail() {
        #expect(OnboardingDownloadCopy.phaseText(.processing)
                != "onboarding.setup.onDevice.preparing".localized)
        #expect(OnboardingDownloadCopy.pillText(stage: .processing, progress: 0.95)
                == "onboarding.setup.onDevice.optimizingPill".localized)
    }

    /// Whichever branch the card takes, the strings it needs for that branch
    /// exist. A number is printed only when `isIndeterminate` says it may be.
    @Test func aNumberIsPrintedOnlyWhereTheFractionIsUsable() {
        let usable = 0.42
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: usable) == false)
        #expect(OnboardingDownloadCopy.pillText(stage: nil, progress: usable).contains("42"))

        let garbage = -48213504.0
        #expect(OnboardingDownloadCopy.isIndeterminate(stage: nil, progress: garbage))
        #expect(OnboardingDownloadCopy.pillText(stage: nil, progress: garbage).contains("48213504") == false)
    }
}
