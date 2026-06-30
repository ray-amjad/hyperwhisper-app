//
//  ComponentProgressAggregatorTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

@MainActor
struct ComponentProgressAggregatorTests {

    /// `AsrModels.download` restarts its progress handler at 0 for each of the four
    /// V2/V3 components. Feeding that synthetic 4-component sweep, the collapsed
    /// fraction must climb monotonically, never overshoot, and finish at a full ring.
    @Test func collapsesFourComponentSweepIntoMonotonicFraction() {
        let aggregator = ComponentProgressAggregator(componentCount: 4)
        let perComponentSweep = [0.0, 0.25, 0.5, 0.75, 1.0]   // raw resets each component

        var outputs: [Double] = []
        for _ in 0..<4 {
            for raw in perComponentSweep {
                outputs.append(aggregator.aggregate(raw))
            }
        }

        // Non-decreasing: the ring never jumps backward across component boundaries.
        for (prev, next) in zip(outputs, outputs.dropFirst()) {
            #expect(next >= prev)
        }
        // Never overshoots the full ring.
        #expect(outputs.allSatisfy { $0 <= 1.0 })
        // Reaches a full ring on the final component's completion.
        #expect(outputs.last == 1.0)
    }

    /// Mirrors FluidAudio's real per-component emission: a download sweep up to 0.5,
    /// then two compile ticks at 0.5 and 1.0. The repeated 0.5 must NOT be misread as
    /// a component boundary, so exactly one boundary fires per component and each
    /// component completes at exactly k / componentCount.
    @Test func detectsExactlyOneBoundaryPerComponent() {
        let aggregator = ComponentProgressAggregator(componentCount: 4)
        let realSweep = [0.0, 0.2, 0.4, 0.5, 0.5, 1.0]   // listing → download → compile-start → done

        var completionValues: [Double] = []
        for _ in 0..<4 {
            var last = 0.0
            for raw in realSweep { last = aggregator.aggregate(raw) }
            completionValues.append(last)
        }

        #expect(completionValues == [0.25, 0.5, 0.75, 1.0])
    }

    /// A backward jump larger than the threshold still advances at most one component,
    /// and the published fraction stays clamped to the ring even past the last component.
    @Test func neverExceedsFullRingOnExtraResets() {
        let aggregator = ComponentProgressAggregator(componentCount: 3)
        // Six full sweeps through a 3-component aggregator — more resets than components.
        var maxOutput = 0.0
        for _ in 0..<6 {
            for raw in [0.0, 0.5, 1.0] {
                maxOutput = max(maxOutput, aggregator.aggregate(raw))
            }
        }
        #expect(maxOutput <= 1.0)
    }
}
