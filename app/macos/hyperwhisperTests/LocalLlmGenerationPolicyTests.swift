//
//  LocalLlmGenerationPolicyTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct LocalLlmGenerationPolicyTests {

    @Test @MainActor func localGenerationReserves8192OutputTokens() {
        #expect(LocalLlmGenerationPolicy.maxOutputTokens == 8_192)
        #expect(LlamaServerController.Configuration.default.contextSize >= 16_384)
    }

    @Test func completeWrapperWithNaturalStopIsAccepted() {
        #expect(LocalLlmGenerationPolicy.isComplete(
            text: "<<CLEANED>>clean transcript<<END>>",
            finishReason: "stop"
        ))
    }

    @Test func outputLimitIsRejectedEvenWithClosingWrapper() {
        #expect(!LocalLlmGenerationPolicy.isComplete(
            text: "<<CLEANED>>partial transcript<<END>>",
            finishReason: "length"
        ))
    }

    @Test func missingClosingWrapperIsRejected() {
        #expect(!LocalLlmGenerationPolicy.isComplete(
            text: "<<CLEANED>>partial transcript",
            finishReason: "stop"
        ))
    }
}
