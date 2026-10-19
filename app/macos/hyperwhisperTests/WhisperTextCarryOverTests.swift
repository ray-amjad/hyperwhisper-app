//
//  WhisperTextCarryOverTests.swift
//  hyperwhisperTests
//
//  Issue #1559: on a long recording local Whisper could repeat an earlier
//  phrase once per second in place of the rest of the speech, because
//  whisper.cpp primes every 30 s window with the text decoded before it.
//  Ray's rule (2026-10-19): above 30 s, `n_max_text_ctx` is 0 (the vocabulary
//  prompt is dropped too); up to 30 s the default stays, so the prompt is kept.
//

import Foundation
import Testing
@testable import HyperWhisper

struct WhisperTextCarryOverTests {

    /// whisper.cpp's `whisper_full_default_params` value.
    private let whisperDefault: Int32 = 16384

    @Test func shortRecordingKeepsTheDefaultContext() {
        #expect(WhisperContext.maxTextContext(sampleCount: 5 * 16_000, default: whisperDefault) == whisperDefault)
    }

    @Test func exactlyThirtySecondsKeepsTheDefaultContext() {
        #expect(WhisperContext.maxTextContext(sampleCount: 30 * 16_000, default: whisperDefault) == whisperDefault)
    }

    @Test func overThirtySecondsTurnsCarryOverOff() {
        #expect(WhisperContext.maxTextContext(sampleCount: 30 * 16_000 + 1, default: whisperDefault) == 0)
        #expect(WhisperContext.maxTextContext(sampleCount: 150 * 16_000, default: whisperDefault) == 0)
    }
}
