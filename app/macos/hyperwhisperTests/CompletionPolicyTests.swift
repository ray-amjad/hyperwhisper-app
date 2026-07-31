//
//  CompletionPolicyTests.swift
//  hyperwhisperTests
//
//  Spot checks against the shared LLM completion policy exposed by the Rust
//  core (`hw_text::completion`, wired via `TranscriptionTextProcessing`).
//  Source of truth for the full case set is
//  `shared-conformance/completion-vectors.json`, consumed by the Rust vector
//  tests (`shared-core-rs/crates/hw-text/tests/completion_vectors.rs`) and the
//  TS vector tests (`hyperwhisper-cloud/src/lib/llm-completion.test.ts`) — this
//  file only exercises a representative sample of those vectors through the
//  Swift facade to confirm the FFI wiring behaves the same way.
//

import Testing
@testable import HyperWhisper

struct CompletionPolicyTests {

    @Test func openaiStopWrappedIsAccepted() {
        // Vector: openai-stop-wrapped-accepted
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .openAiChat, reason: "stop")
        #expect(state == .complete)

        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "hello world",
            content: "<<CLEANED>>Hello world.<<END>>",
            state: state
        )
        #expect(evaluation.accepted)
        #expect(evaluation.text == "Hello world.")
        #expect(evaluation.failure == .none)
    }

    @Test func openaiLengthIsRejectedAndKeepsOriginal() {
        // Vector: openai-length-rejected
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .openAiChat, reason: "length")
        #expect(state == .outputLimit)

        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "raw transcript",
            content: "<<CLEANED>>partial text that was cut",
            state: state
        )
        #expect(!evaluation.accepted)
        #expect(evaluation.text == "raw transcript")
        #expect(evaluation.failure == .outputLimit)
    }

    @Test func missingFinishReasonProceeds() {
        // Vector: openai-missing-reason-proceeds — custom/self-hosted servers
        // that omit finish_reason must not be treated as truncated.
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .openAiChat, reason: nil)
        #expect(state == .unspecified)

        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "raw transcript",
            content: "<<CLEANED>>Cleaned text from a server that omits finish_reason.<<END>>",
            state: state
        )
        #expect(evaluation.accepted)
        #expect(evaluation.text == "Cleaned text from a server that omits finish_reason.")
    }

    @Test func markerlessCompleteContentIsAcceptedAsIs() {
        // Vector: markerless-complete-accepted-stripped — a model that skips the
        // <<CLEANED>> wrapper entirely still gets its output through.
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .openAiChat, reason: "stop")
        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "here is the cleaned sentence",
            content: "Here is the cleaned sentence.",
            state: state
        )
        #expect(evaluation.accepted)
        #expect(evaluation.text == "Here is the cleaned sentence.")
    }

    @Test func promptLeakageIsRejected() {
        // Vector: leakage-application-context-rejected
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .openAiChat, reason: "stop")
        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "raw transcript",
            content: "<APPLICATION_CONTEXT>\n<APP>Mail</APP>\n</APPLICATION_CONTEXT>",
            state: state
        )
        #expect(!evaluation.accepted)
        #expect(evaluation.text == "raw transcript")
        #expect(evaluation.failure == .promptLeakage)
    }

    @Test func promptLeakageInsideWrapperIsRejected() {
        // Vector: leakage-inside-wrapper-rejected — scaffolding leaked inside
        // the <<CLEANED>>…<<END>> wrapper must not reach the user either.
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .openAiChat, reason: "stop")
        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "raw transcript",
            content: "<<CLEANED>><APPLICATION_CONTEXT>\nApp: Mail\n</APPLICATION_CONTEXT><<END>>",
            state: state
        )
        #expect(!evaluation.accepted)
        #expect(evaluation.text == "raw transcript")
        #expect(evaluation.failure == .promptLeakage)
    }

    @Test func anthropicMaxTokensIsRejected() {
        // Vector: anthropic-max-tokens-rejected — Anthropic's stop_reason is
        // parsed inside the core, so BYO Anthropic gets the same enforcement.
        let state = TranscriptionTextProcessing.normalizeTermination(wireProtocol: .anthropicMessages, reason: "max_tokens")
        #expect(state == .outputLimit)

        let evaluation = TranscriptionTextProcessing.evaluateCompletion(
            original: "raw transcript",
            content: "<<CLEANED>>truncated mid",
            state: state
        )
        #expect(!evaluation.accepted)
        #expect(evaluation.text == "raw transcript")
        #expect(evaluation.failure == .outputLimit)
    }

    @Test func openaiJsonResponseConvenienceMatchesTwoStepEvaluation() {
        // Vector: openai-stop-wrapped-accepted, via the JSON convenience used by
        // the non-streaming/custom-endpoint call sites in AIPostProcessor.
        let responseJson = """
        {"choices":[{"message":{"content":"<<CLEANED>>Hello world.<<END>>"},"finish_reason":"stop"}]}
        """
        let evaluation = TranscriptionTextProcessing.evaluateLlmResponseJson(
            wireProtocol: .openAiChat,
            responseJson: responseJson,
            original: "hello world"
        )
        #expect(evaluation.accepted)
        #expect(evaluation.text == "Hello world.")
    }

    @Test func malformedJsonResponseIsRejectedAsMalformed() {
        let evaluation = TranscriptionTextProcessing.evaluateLlmResponseJson(
            wireProtocol: .openAiChat,
            responseJson: "not json",
            original: "raw transcript"
        )
        #expect(!evaluation.accepted)
        #expect(evaluation.text == "raw transcript")
        #expect(evaluation.failure == .malformedResponse)
    }
}
