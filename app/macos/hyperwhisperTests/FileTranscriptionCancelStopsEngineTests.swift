//
//  FileTranscriptionCancelStopsEngineTests.swift
//  hyperwhisperTests
//
//  Issue #1507: the Transcribe File HUD's Cancel cancels the flow's own task,
//  but the pipeline runs the provider in an unstructured task, which inherits
//  no cancellation. Qwen3 ran on for minutes and Nemotron finished the whole
//  file. `TranscriptionPipeline.value(of:)` now forwards the cancel into that
//  task, and `NemotronProvider` feeds the file in slices with a cancel check
//  between them, because FluidAudio's `process(samples:)` never checks.
//

import Foundation
import Testing
@testable import HyperWhisper

struct FileTranscriptionCancelStopsEngineTests {

    // MARK: - The pipeline forwards the caller's cancel

    @Test func cancellingTheAwaitingTaskCancelsTheInnerTask() async throws {
        // Stands in for the provider task: it runs until it is cancelled, the
        // way Qwen3's per-chunk `Task.checkCancellation()` behaves.
        let inner = Task<String, Error> {
            while true {
                try Task.checkCancellation()
                try await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        let outer = Task<String, Error> {
            try await TranscriptionPipeline.value(of: inner)
        }

        try await Task.sleep(nanoseconds: 50_000_000)
        outer.cancel()

        // Before the fix the inner task never ended, so this await would hang.
        let innerResult = await inner.result
        #expect(throws: CancellationError.self) { try innerResult.get() }
        let outerResult = await outer.result
        #expect(throws: CancellationError.self) { try outerResult.get() }
    }

    @Test func anAlreadyCancelledCallerCancelsTheInnerTaskAtOnce() async {
        let inner = Task<String, Error> {
            while true {
                try Task.checkCancellation()
                try await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        let outer = Task<String, Error> {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await TranscriptionPipeline.value(of: inner)
        }

        let innerResult = await inner.result
        #expect(throws: CancellationError.self) { try innerResult.get() }
        _ = await outer.result
    }

    @Test func anUncancelledCallerGetsTheInnerValue() async throws {
        let inner = Task<String, Error> { "text" }

        let value = try await TranscriptionPipeline.value(of: inner)

        #expect(value == "text")
        #expect(!inner.isCancelled)
    }

    // MARK: - Nemotron feeds a file in cancellable slices

    @Test func nemotronSlicesCoverEverySampleOnceInOrder() {
        let size = NemotronProvider.processSliceSamples
        for count in [0, 1, size - 1, size, size + 1, 3 * size, 97 * 60 * 16_000 + 123] {
            let slices = NemotronProvider.processSlices(sampleCount: count)

            #expect(slices.first?.lowerBound ?? 0 == 0)
            #expect(slices.last?.upperBound ?? 0 == count)
            #expect(slices.allSatisfy { !$0.isEmpty && $0.count <= size })
            #expect(zip(slices, slices.dropFirst()).allSatisfy { $0.upperBound == $1.lowerBound })
        }
    }

    @Test func aLongFileGetsManyCancelChecks() {
        // The issue's 97 min file: one cancel check per 30 s of audio, where
        // there used to be none at all inside the pass.
        let slices = NemotronProvider.processSlices(sampleCount: 97 * 60 * 16_000)

        #expect(slices.count == 97 * 2)
    }
}
