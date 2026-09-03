//
//  ModelDownloadProgressAggregatorTests.swift
//  hyperwhisperTests
//

import Testing
import FluidAudio
@testable import HyperWhisper

@MainActor
struct ModelDownloadProgressAggregatorTests {

    /// The share of the bar the aggregator gives to the transfer. Its own constant is
    /// private; this is a copy, and the boundary assertions below are what would catch
    /// the two drifting apart.
    private static let downloadSpan = 0.9

    /// Builds the exact value FluidAudio 0.15.2 hands to a `DownloadUtils.ProgressHandler`.
    private func update(
        _ fractionCompleted: Double,
        _ phase: DownloadUtils.DownloadPhase
    ) -> DownloadUtils.DownloadProgress {
        DownloadUtils.DownloadProgress(fractionCompleted: fractionCompleted, phase: phase)
    }

    /// The aggregator's own stage ordering, restated here so the tests assert against a
    /// stated invariant rather than against the implementation's private helper.
    private func rank(_ stage: ModelDownloadStage) -> Int {
        switch stage {
        case .preparing: return 0
        case .downloading: return 1
        case .processing: return 2
        }
    }

    /// The real emission order for a cold Parakeet V2 install on FluidAudio 0.15.2.
    /// `AsrModels.download` runs `loadModels` once per component, but `loadModels` checks
    /// the cache for the *whole* repository, so only the first component transfers bytes;
    /// components 2-4 find every file on disk and emit their ticks instantly.
    private func coldV2Sequence() -> [DownloadUtils.DownloadProgress] {
        var updates: [DownloadUtils.DownloadProgress] = [update(0.0, .listing)]
        // Component 1: the only one that downloads. FluidAudio emits a byte-weighted
        // fraction in 0...0.5 as each of the repository's 22 files completes.
        for file in 1...22 {
            updates.append(
                update(
                    0.5 * Double(file) / 22.0,
                    .downloading(completedFiles: file, totalFiles: 22)))
        }
        // `loadModelsOnce` ticks 0.5 *before* the slow `MLModel(contentsOf:)` call and
        // 1.0 after the loop, so every component contributes two `.compiling` updates.
        updates.append(update(0.5, .compiling(modelName: "Encoder.mlmodelc")))
        updates.append(update(1.0, .compiling(modelName: "")))
        // Components 2-4: cache hits, each announced by the `totalFiles: 0` sentinel.
        for _ in 0..<3 {
            updates.append(update(0.5, .downloading(completedFiles: 0, totalFiles: 0)))
            updates.append(update(0.5, .compiling(modelName: "Decoder.mlmodelc")))
            updates.append(update(1.0, .compiling(modelName: "")))
        }
        return updates
    }

    @Test func realV2SequenceClimbsToOneAndSpendsTheBarOnTheTransfer() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        var outputs: [Double] = []
        var transferEndFraction = 0.0
        var sawCompiling = false
        for next in coldV2Sequence() {
            let published = aggregator.aggregate(next)
            outputs.append(published.fraction)
            if case .compiling = next.phase { sawCompiling = true }
            if case .downloading = next.phase, !sawCompiling {
                transferEndFraction = published.fraction
            }
        }

        // Non-decreasing: the bar never jumps backward across component boundaries.
        for (prev, next) in zip(outputs, outputs.dropFirst()) {
            #expect(next >= prev)
        }
        // Never overshoots a full bar.
        #expect(outputs.allSatisfy { $0 <= 1.0 })
        // Reaches a full bar once the last component has compiled.
        #expect(outputs.last == 1.0)
        // Regression for #312: the four-minute transfer owns most of the bar. The old
        // aggregator divided it by the component count and ended it at ~0.125.
        #expect(transferEndFraction >= 0.8)
    }

    /// The boundary invariant. The compile tail must advance once per *completed
    /// component*, not once per `.compiling` callback: FluidAudio sends two of those per
    /// component plus a cache-hit sentinel, so an aggregator that counted ticks would be
    /// at 4/4 partway through component 2 and would publish a full bar with two
    /// components still to compile — while every fraction assertion above stayed green.
    @Test func theCompileTailAdvancesExactlyOncePerComponent() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        var outputs: [Double] = []
        for next in coldV2Sequence() {
            outputs.append(aggregator.aggregate(next).fraction)
        }

        // The bar is full only on the very last update, never before it.
        #expect(outputs.firstIndex(of: 1.0) == outputs.count - 1)

        // And it gets there in exactly four steps from the end of the transfer.
        var compileSteps = 0
        for index in 1..<outputs.count where outputs[index] > outputs[index - 1] {
            if outputs[index - 1] >= Self.downloadSpan {
                compileSteps += 1
            }
        }
        #expect(compileSteps == 4)
    }

    /// Issue #312, the stage half. `loadModels` re-checks the cache for the whole
    /// repository on every component, so components 2-4 each emit the cache-hit sentinel
    /// `.downloading(completedFiles: 0, totalFiles: 0)` *after* component 1 has already
    /// compiled. Read raw, that maps to `.preparing` and runs the published stage
    /// `.processing -> .preparing -> .processing` once per cached component — which
    /// flips the card back to "Preparing download..." after the transfer has finished.
    @Test func theStageNeverMovesBackwardsAcrossTheRealV2Sequence() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        var stages: [ModelDownloadStage] = []
        for next in coldV2Sequence() {
            stages.append(aggregator.aggregate(next).stage)
        }

        // preparing -> downloading -> processing, one way only.
        for (prev, next) in zip(stages, stages.dropFirst()) {
            #expect(rank(next) >= rank(prev))
        }
        // The transfer opens on `.preparing` and the run ends inside `.processing`.
        #expect(stages.first == ModelDownloadStage.preparing)
        #expect(stages.last == ModelDownloadStage.processing)
        // Nothing after the first `.compiling` reports anything but `.processing`, so
        // the compile tail is one stable state and not eleven alternating ones.
        guard let firstProcessing = stages.firstIndex(of: .processing) else {
            Issue.record("the sequence never reached .processing")
            return
        }
        #expect(stages[firstProcessing...].allSatisfy { $0 == .processing })
        // The transfer's real file counters still get through untouched.
        #expect(stages.contains(.downloading(completedFiles: 22, totalFiles: 22)))
    }

    /// A cache-hit component reports `.downloading(completedFiles: 0, totalFiles: 0)`,
    /// which is a sentinel and not a real file counter — it must not surface as
    /// "file 0 of 0". A genuine counter passes through untouched.
    @Test func cacheHitDownloadingUpdateMapsToPreparing() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        let cacheHit = aggregator.aggregate(
            update(0.5, .downloading(completedFiles: 0, totalFiles: 0)))
        #expect(cacheHit.stage == .preparing)

        let real = aggregator.aggregate(
            update(0.25, .downloading(completedFiles: 7, totalFiles: 22)))
        #expect(real.stage == .downloading(completedFiles: 7, totalFiles: 22))

        // Once anything has compiled the sentinel is discarded outright rather than
        // demoted to `.preparing`.
        _ = aggregator.aggregate(update(1.0, .compiling(modelName: "")))
        let sentinelAfterCompile = aggregator.aggregate(
            update(0.5, .downloading(completedFiles: 0, totalFiles: 0)))
        #expect(sentinelAfterCompile.stage == .processing)
    }

    /// FluidAudio documents the handler as arriving on an unspecified queue, and
    /// `runDownload` hops each callback onto the main actor, so updates can land out of
    /// order or repeat. Neither published value may move backwards.
    @Test func neverMovesBackwardsOnRepeatedOrOutOfOrderUpdates() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)
        let updates = [
            update(0.0, .listing),
            update(0.40, .downloading(completedFiles: 18, totalFiles: 22)),
            update(0.10, .downloading(completedFiles: 4, totalFiles: 22)),   // late straggler
            update(0.40, .downloading(completedFiles: 18, totalFiles: 22)),  // repeat
            update(1.0, .compiling(modelName: "")),                          // component 1 done
            update(0.5, .downloading(completedFiles: 0, totalFiles: 0)),     // component 2, cached
        ]

        var outputs: [Double] = []
        var stages: [ModelDownloadStage] = []
        for next in updates {
            let published = aggregator.aggregate(next)
            outputs.append(published.fraction)
            stages.append(published.stage)
        }

        for (prev, next) in zip(outputs, outputs.dropFirst()) {
            #expect(next >= prev)
        }
        #expect(outputs.allSatisfy { $0 <= 1.0 })

        for (prev, next) in zip(stages, stages.dropFirst()) {
            #expect(rank(next) >= rank(prev))
        }
        // The straggler must not walk the file counter back to "file 5 of 22".
        #expect(stages[2] == .downloading(completedFiles: 18, totalFiles: 22))
        // And the cached component's sentinel must not undo the compile.
        #expect(stages.last == ModelDownloadStage.processing)
    }

    /// `DownloadUtils.loadModels` catches any load failure, deletes the whole repository
    /// directory and re-runs with the *same* handler. That re-download is hundreds of
    /// megabytes of real network activity, and the monotonic guards were swallowing every
    /// callback of it — the bar sat still at 90% for minutes. A second `.listing` is the
    /// restart signal: `downloadRepo` emits exactly one, at its top, before it has
    /// transferred anything, and the cache-hit path emits none at all.
    @Test func aSecondListingRewindsTheRunSoTheRetryIsVisible() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        _ = aggregator.aggregate(update(0.0, .listing))
        _ = aggregator.aggregate(update(0.5, .downloading(completedFiles: 22, totalFiles: 22)))
        let atCompile = aggregator.aggregate(update(0.5, .compiling(modelName: "Encoder.mlmodelc")))
        #expect(atCompile.fraction >= Self.downloadSpan)
        #expect(atCompile.stage == .processing)

        // The compile threw; FluidAudio wiped the cache and started over.
        let restart = aggregator.aggregate(update(0.0, .listing))
        #expect(restart.fraction == 0.0)
        #expect(restart.stage == .preparing)

        // The re-download is now visible again instead of frozen behind the guards.
        let early = aggregator.aggregate(update(0.05, .downloading(completedFiles: 2, totalFiles: 22)))
        #expect(early.fraction < atCompile.fraction)
        #expect(early.stage == .downloading(completedFiles: 2, totalFiles: 22))
    }

    /// The retry must not cost the run its ending. `AsrModels.download`'s earlier
    /// per-component `loadModels` calls have already returned and will not compile again,
    /// so a restart that rewound `completedComponents` too would leave the bar short of
    /// full for the rest of the download.
    @Test func aRestartKeepsTheComponentsThatAlreadyCompiled() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        // Components 1 and 2 complete normally.
        _ = aggregator.aggregate(update(0.0, .listing))
        _ = aggregator.aggregate(update(0.5, .downloading(completedFiles: 22, totalFiles: 22)))
        _ = aggregator.aggregate(update(1.0, .compiling(modelName: "")))
        _ = aggregator.aggregate(update(1.0, .compiling(modelName: "")))

        // Component 3 fails to load; the repo is deleted and re-fetched.
        _ = aggregator.aggregate(update(0.0, .listing))
        _ = aggregator.aggregate(update(0.5, .downloading(completedFiles: 22, totalFiles: 22)))
        _ = aggregator.aggregate(update(1.0, .compiling(modelName: "")))
        // Component 4.
        let done = aggregator.aggregate(update(1.0, .compiling(modelName: "")))

        #expect(done.fraction == 1.0)
        #expect(done.stage == .processing)
    }
}
