//
//  ModelDownloadProgressAggregatorTests.swift
//  hyperwhisperTests
//

import Testing
import FluidAudio
@testable import HyperWhisper

@MainActor
struct ModelDownloadProgressAggregatorTests {

    /// Builds the exact value FluidAudio 0.15.2 hands to a `DownloadUtils.ProgressHandler`.
    private func update(
        _ fractionCompleted: Double,
        _ phase: DownloadUtils.DownloadPhase
    ) -> DownloadUtils.DownloadProgress {
        DownloadUtils.DownloadProgress(fractionCompleted: fractionCompleted, phase: phase)
    }

    /// The real emission order for a cold Parakeet V2 install on FluidAudio 0.15.2.
    /// `AsrModels.download` runs `loadModels` once per component, but `loadModels` checks
    /// the cache for the *whole* repository, so only the first component transfers bytes;
    /// components 2-4 find every file on disk and emit their ticks instantly.
    @Test func realV2SequenceClimbsToOneAndSpendsTheBarOnTheTransfer() {
        let aggregator = ModelDownloadProgressAggregator(componentCount: 4)

        var updates: [DownloadUtils.DownloadProgress] = [update(0.0, .listing)]
        // Component 1: the only one that downloads. FluidAudio emits a byte-weighted
        // fraction in 0...0.5 as each of the repository's 22 files completes.
        for file in 1...22 {
            updates.append(
                update(
                    0.5 * Double(file) / 22.0,
                    .downloading(completedFiles: file, totalFiles: 22)))
        }
        updates.append(update(0.5, .compiling(modelName: "Encoder.mlmodelc")))
        updates.append(update(1.0, .compiling(modelName: "")))
        // Components 2-4: cache hits.
        for _ in 0..<3 {
            updates.append(update(0.5, .downloading(completedFiles: 0, totalFiles: 0)))
            updates.append(update(0.5, .compiling(modelName: "Decoder.mlmodelc")))
            updates.append(update(1.0, .compiling(modelName: "")))
        }

        var outputs: [Double] = []
        var transferEndFraction = 0.0
        var sawCompiling = false
        for next in updates {
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
    }

    /// FluidAudio documents the handler as arriving on an unspecified queue, and
    /// `runDownload` hops each callback onto the main actor, so updates can land out of
    /// order or repeat. The published fraction must never move backwards.
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
        for next in updates {
            outputs.append(aggregator.aggregate(next).fraction)
        }

        for (prev, next) in zip(outputs, outputs.dropFirst()) {
            #expect(next >= prev)
        }
        #expect(outputs.allSatisfy { $0 <= 1.0 })
    }
}
