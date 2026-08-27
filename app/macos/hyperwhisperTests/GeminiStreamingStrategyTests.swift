//
//  GeminiStreamingStrategyTests.swift
//  hyperwhisperTests
//
//  Holds the macOS BYOK Gemini live strategy to the same wire contract as the
//  Windows and shared-.NET mirrors. Expectations come from
//  shared-conformance/live-frame-vectors.json, which is generated from the Rust
//  frame builders in hw-net — those are deliberately not exposed over UniFFI
//  (per-chunk audio marshalling would add a hot-path copy), so vectors plus
//  per-platform tests are how the three implementations are kept honest.
//

import Testing
import Foundation
@testable import HyperWhisper

@Suite("Gemini live streaming strategy")
struct GeminiStreamingStrategyTests {

    private func config(
        language: String? = nil,
        vocabulary: String? = nil,
        model: String? = nil
    ) -> StreamingSessionConfig {
        StreamingSessionConfig(
            licenseKey: nil,
            deviceId: nil,
            language: language,
            vocabulary: vocabulary,
            apiKey: "AIza-test",
            model: model,
            fastFormatting: false
        )
    }

    private func setupObject(_ config: StreamingSessionConfig) throws -> [String: Any] {
        let json = try #require(GeminiStreamingStrategy.setupFrame(config: config))
        let data = try #require(json.data(using: .utf8))
        let root = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        return try #require(root["setup"] as? [String: Any])
    }

    @Test("TRAP: the config lives at setup.input_audio_transcription")
    func setupFramePutsConfigAtTheLivePath() throws {
        // The PRE-RECORDED model takes this same object at
        // setup.generation_config.transcription_config, and sending that shape to
        // the LIVE socket closes it with 1007. One family, two models, two paths.
        let setup = try setupObject(config(language: "en-US", vocabulary: "HyperWhisper"))

        #expect(setup["model"] as? String == "models/gemini-3.5-transcribe-live")
        #expect(setup["generation_config"] == nil)

        let transcription = try #require(setup["input_audio_transcription"] as? [String: Any])
        #expect(transcription["language_codes"] as? [String] == ["en-US"])
        #expect(transcription["custom_vocabulary"] as? [String] == ["HyperWhisper"])
    }

    @Test("Auto-detect drops language_codes but keeps custom_vocabulary")
    func autoDetectStillSendsVocabulary() throws {
        // "No vocabulary without an explicit language" is a Deepgram Nova-3
        // constraint. Gemini accepts custom_vocabulary in auto-detect, and
        // vocabulary is the headline reason to pick this provider, so applying
        // Deepgram's rule here would silently delete the feature.
        let transcription = try #require(
            try setupObject(config(language: "auto", vocabulary: "Kalamazoo"))["input_audio_transcription"]
                as? [String: Any]
        )

        #expect(transcription["language_codes"] == nil)
        #expect(transcription["custom_vocabulary"] as? [String] == ["Kalamazoo"])
    }

    @Test("A region subtag survives instead of being flattened")
    func regionIsPreserved() throws {
        // Every other strategy here takes a bare subtag and flattens en-GB to en.
        // Gemini accepts the qualified form, so flattening would throw away a
        // region the user deliberately picked.
        let transcription = try #require(
            try setupObject(config(language: "en-GB"))["input_audio_transcription"] as? [String: Any]
        )
        #expect(transcription["language_codes"] as? [String] == ["en-GB"])
    }

    @Test("A bare model id is prefixed and an already-prefixed one is left alone")
    func modelIdIsNormalized() throws {
        #expect(try setupObject(config())["model"] as? String == "models/gemini-3.5-transcribe-live")
        #expect(
            try setupObject(config(model: "models/gemini-3.5-transcribe-live"))["model"] as? String
                == "models/gemini-3.5-transcribe-live"
        )
    }

    @Test("Audio frames are base64 JSON at the catalogued mime type")
    func audioFrameMatchesTheVector() throws {
        let strategy = GeminiStreamingStrategy()
        guard case let .string(json) = strategy.encodeAudioChunk(Data("ABC".utf8)) else {
            Issue.record("expected a text frame")
            return
        }
        #expect(json == #"{"realtime_input":{"audio":{"data":"QUJD","mime_type":"audio/pcm;rate=16000"}}}"#)
    }

    @Test("The stop sequence ends the audio stream and does not wait on Google")
    func stopSequenceSendsAudioStreamEnd() {
        // Google never closes the socket after audio_stream_end - measured 54 s of
        // silence - so the wait is our own budget, not a pause for a close that
        // is coming.
        let steps = GeminiStreamingStrategy().stopSequence()
        guard case let .sendText(text) = steps.first else {
            Issue.record("expected a sendText step first")
            return
        }
        #expect(text == #"{"realtime_input":{"audio_stream_end":true}}"#)
        if case let .waitForSessionComplete(timeout) = steps[1] {
            #expect(timeout <= 10.0)
        } else {
            Issue.record("expected a bounded waitForSessionComplete")
        }
        if case .closeWebSocket = steps[2] {} else {
            Issue.record("expected the socket to be closed by us")
        }
    }

    @Test("Interim maps to partial and inputTranscription to final, with no diffing")
    func transcriptEventsAreNotPrefixDiffed() {
        // interimInputTranscription is cumulative only WITHIN a turn and restarts
        // after each final; inputTranscription carries only that turn's committed
        // text. A repeated turn is the case that discriminates: xAI-style prefix
        // diffing would emit nothing for the second "again.".
        let strategy = GeminiStreamingStrategy()

        guard case .sessionStarted = strategy.parseMessage(#"{"setupComplete":{}}"#) else {
            Issue.record("setupComplete must start the session")
            return
        }
        guard case let .partialTranscript(text) =
            strategy.parseMessage(#"{"serverContent":{"interimInputTranscription":{"text":"hel"}}}"#) else {
            Issue.record("interim must be a partial")
            return
        }
        #expect(text == "hel")

        for _ in 0..<2 {
            guard case let .finalTranscript(final) =
                strategy.parseMessage(#"{"serverContent":{"inputTranscription":{"text":"again."}}}"#) else {
                Issue.record("a repeated turn must still be emitted whole")
                return
            }
            #expect(final == "again.")
        }

        guard case .sessionComplete = strategy.parseMessage(#"{"serverContent":{"generationComplete":true}}"#) else {
            Issue.record("generationComplete must end the session")
            return
        }
    }

    @Test("Unmodelled frames are ignored and error frames surface their message")
    func unknownFramesAreIgnored() {
        let strategy = GeminiStreamingStrategy()
        #expect(strategy.parseMessage(#"{"usageMetadata":{"totalTokenCount":3}}"#) == nil)
        #expect(strategy.parseMessage("not json at all") == nil)

        guard case let .error(message) =
            strategy.parseMessage(#"{"error":{"code":1007,"message":"invalid setup"}}"#) else {
            Issue.record("an error frame must surface as an error event")
            return
        }
        #expect(message == "invalid setup")
    }

    @Test("A missing API key refuses to build a URL")
    func missingKeyBuildsNoURL() {
        let strategy = GeminiStreamingStrategy()
        #expect(strategy.buildWebSocketURL(config: StreamingSessionConfig(
            licenseKey: nil, deviceId: nil, language: "en", vocabulary: nil,
            apiKey: nil, model: nil, fastFormatting: false)) == nil)

        let url = strategy.buildWebSocketURL(config: config())
        #expect(url?.host == "generativelanguage.googleapis.com")
        #expect(url?.path.hasSuffix("BidiGenerateContent") == true)
    }
}
