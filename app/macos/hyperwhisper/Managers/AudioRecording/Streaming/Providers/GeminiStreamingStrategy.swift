//
//  GeminiStreamingStrategy.swift
//  hyperwhisper
//
//  Direct WebSocket streaming to Gemini 3.5 Transcribe Live over Google's
//  BidiGenerateContent endpoint, using the user's own Gemini API key.
//
//  Wire shape is pinned by shared-conformance/live-frame-vectors.json and built
//  in shared-core-rs/crates/hw-net/src/providers/gemini_transcribe.rs. The Rust
//  frame builders are deliberately NOT exposed over UniFFI (per-chunk audio
//  marshalling would add a hot-path copy), so those vectors are how this Swift
//  strategy, the Windows one and the shared .NET one are held to one wire shape.
//
//  TRAP — the live transcription config lives at `setup.input_audio_transcription`.
//  The pre-recorded position (`setup.generation_config.transcription_config`,
//  which IS correct for POST /v1beta/interactions) makes the server close the
//  socket with 1007. One config object, two models, two different paths.
//
//  TRAP 2 — `generationComplete` is a TURN boundary, not the end of the session.
//  Google emits one after EVERY utterance, so mapping it straight to
//  `.sessionComplete` latches the shared client's `didReceiveSessionComplete`
//  on the first pause, `waitForSessionComplete` then returns instantly at stop,
//  and the socket closes behind `audio_stream_end` before the last utterance's
//  final arrives. `clientRequestedStop` below is the gate — the same rule the
//  backend applies in `hyperwhisper-cloud/src/routes/ws-streaming-shared.ts`.
//  Deepgram, ElevenLabs, OpenAI and xAI genuinely do send a terminal completion
//  and are deliberately left alone.
//
//  NOT xAI-SHAPED, despite the JSON framing looking like it. Gemini's
//  `interimInputTranscription` is cumulative only WITHIN a turn and restarts
//  after each final, and `inputTranscription` carries only that turn's committed
//  text. That is Deepgram's contract: interim is a replacement preview, and
//  inputTranscription is an append-me delta. Do NOT copy
//  `XAIStreamingStrategy.committedDelta(from:)` here — xAI's transcript is
//  cumulative across the whole SESSION, and diffing Gemini would chop the head
//  off every utterance after the first.
//

import Foundation
import OSLog

final class GeminiStreamingStrategy: StreamingProviderStrategy {
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "GeminiStreaming")

    /// Google's live vocabulary caps, mirroring `gemini_transcribe::custom_vocabulary`
    /// in the Rust core, which applies the same two limits on the batch path.
    private static let maxVocabularyTerms = 100
    private static let maxVocabularyChars = 80

    private static let liveModel = "gemini-3.5-transcribe-live"
    private static let modelPrefix = "models/"
    private static let audioMimeType = "audio/pcm;rate=16000"

    /// True once the client has begun its stop sequence for THIS session.
    ///
    /// `generationComplete` is a TURN boundary, not the end of the session:
    /// Google emits one after every utterance. The shared client latches
    /// `didReceiveSessionComplete` permanently on a `.sessionComplete` event and
    /// only clears it in `startSession`, so reporting the first turn boundary as
    /// a completion makes `waitForSessionComplete` return instantly at stop —
    /// the socket closes right behind `audio_stream_end` and the last
    /// utterance's final never arrives.
    ///
    /// So the same rule the backend applies in
    /// `hyperwhisper-cloud/src/routes/ws-streaming-shared.ts` ("only terminal
    /// once the client asked to stop") is applied here, at the only layer that
    /// knows Gemini's turn semantics. Every other vendor is untouched: their
    /// completion frame really is terminal, and their strategies keep mapping it
    /// straight to `.sessionComplete`.
    ///
    /// Raised in `stopSequence()` — the client calls it exactly once, at the top
    /// of `stopSession()`, before any stop step runs — and cleared in
    /// `startMessages(config:)`, which runs on every fresh socket, so a reused
    /// strategy instance cannot inherit the previous session's flag. Same
    /// mutable-strategy-state idiom as `OpenAIStreamingStrategy`'s commit gate.
    private var clientRequestedStop = false

    var transcriptionProviderLabel: String { "Gemini 3.5 Transcribe (Streaming)" }
    var supportsVocabulary: Bool { true }
    var audioSampleRate: Double { 16000 }

    func buildWebSocketURL(config: StreamingSessionConfig) -> URL? {
        guard let apiKey = config.apiKey, !apiKey.isEmpty else {
            logger.error("Cannot build Gemini URL: API key is missing")
            return nil
        }

        var components = URLComponents()
        components.scheme = "wss"
        components.host = "generativelanguage.googleapis.com"
        components.path = "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"
        // Google authenticates this socket by query parameter; it rejects the
        // Authorization header form the REST endpoints accept, so there is no
        // buildWebSocketRequest override here.
        components.queryItems = [URLQueryItem(name: "key", value: apiKey)]
        return components.url
    }

    func startMessages(config: StreamingSessionConfig) -> [URLSessionWebSocketTask.Message] {
        // Fresh socket — including a mid-session reconnect — so no stop has been
        // asked for yet and `generationComplete` is a turn boundary again.
        clientRequestedStop = false
        guard let json = Self.setupFrame(config: config) else {
            logger.error("Failed to encode Gemini setup frame")
            return []
        }
        return [.string(json)]
    }

    /// The setup frame, exactly as `shared-conformance/live-frame-vectors.json`
    /// specifies it. `static` and non-private so `GeminiStreamingStrategyTests`
    /// can assert the JSON without standing up a socket.
    static func setupFrame(config: StreamingSessionConfig) -> String? {
        var model = config.model?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if model.isEmpty { model = liveModel }
        if !model.hasPrefix(modelPrefix) { model = modelPrefix + model }

        var transcription: [String: Any] = [:]

        // Region-PRESERVING, unlike every other strategy here. Deepgram and xAI
        // take a bare subtag so they flatten "en-GB" to "en"; Gemini accepts the
        // qualified form (verified live) and flattening would silently throw away
        // a region the user deliberately picked.
        if let raw = config.language?.trimmingCharacters(in: .whitespacesAndNewlines),
           !raw.isEmpty,
           raw.lowercased() != "auto" {
            transcription["language_codes"] = [raw]
        }

        // NOT gated on an explicit language. "No vocabulary without a language" is
        // a Deepgram Nova-3 constraint; Gemini accepts custom_vocabulary in
        // auto-detect mode, and vocabulary is the headline reason to pick this
        // provider. Never send diarization_mode or timestamp_granularities
        // alongside it — Google rejects the pair outright.
        let terms = vocabularyTerms(from: config.vocabulary)
        if !terms.isEmpty {
            transcription["custom_vocabulary"] = terms
        }

        let payload: [String: Any] = [
            "setup": [
                "model": model,
                "input_audio_transcription": transcription
            ]
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: data, encoding: .utf8) else {
            return nil
        }
        return json
    }

    func encodeAudioChunk(_ pcmData: Data) -> URLSessionWebSocketTask.Message {
        let base64 = pcmData.base64EncodedString()
        let json = #"{"realtime_input":{"audio":{"data":"\#(base64)","mime_type":"\#(Self.audioMimeType)"}}}"#
        return .string(json)
    }

    func parseMessage(_ text: String) -> StreamingProviderEvent? {
        guard let data = text.data(using: .utf8) else {
            logger.error("Gemini parseMessage: failed to convert text to UTF-8 data")
            return nil
        }

        let message: GeminiLiveMessage
        do {
            message = try JSONDecoder().decode(GeminiLiveMessage.self, from: data)
        } catch {
            logger.warning("Gemini parseMessage: failed to decode JSON: \(error.localizedDescription, privacy: .public)")
            return nil
        }

        if message.setupComplete != nil {
            logger.info("Gemini live session setup complete")
            return .sessionStarted(sessionId: nil)
        }

        if let error = message.error {
            // A rejected setup frame and a rejected key both arrive here. The
            // message text is what StreamingProviderErrorPolicy classifies, and
            // both wordings are in its terminalMarkers, so neither is retried.
            return .error(message: error.message ?? "Gemini streaming transcription failed")
        }

        guard let content = message.serverContent else {
            return nil
        }

        // `generationComplete` closes a TURN. It is the end of the SESSION only
        // when we already asked to stop — see `clientRequestedStop`.
        let sessionEnded = content.generationComplete == true && clientRequestedStop

        // Final first: a frame could in principle carry both, and the committed
        // text is the one that must reach the document. When the terminal
        // complete rides along on that same frame, both are reported, so the
        // stop sequence does not sit out its whole wait for a completion that
        // has already been and gone.
        if let final = content.inputTranscription?.text, !final.isEmpty {
            // Google reports no duration or credit figures on this route, and a
            // BYOK session is not metered by HyperWhisper either way.
            return sessionEnded
                ? .finalTranscriptAndSessionComplete(text: final, durationSeconds: 0, creditsUsed: 0)
                : .finalTranscript(text: final)
        }
        if let partial = content.interimInputTranscription?.text, !partial.isEmpty {
            return .partialTranscript(text: partial)
        }
        if content.generationComplete == true {
            guard sessionEnded else {
                // Mid-dictation turn boundary. The turn's text was already
                // committed by the `inputTranscription` frame(s) above; reporting
                // nothing here keeps the socket open for the next utterance.
                logger.debug("Gemini turn boundary — session stays open")
                return nil
            }
            return .sessionComplete(durationSeconds: 0, creditsUsed: 0)
        }
        return nil
    }

    func stopSequence() -> [StreamingStopStep] {
        // The client calls this once, at the top of `stopSession()`, before any
        // step runs — so from here on a `generationComplete` really does end the
        // session. If Google never sends one, `waitForSessionComplete`'s own
        // timeout below bounds the wait and the socket is closed regardless.
        clientRequestedStop = true
        return [
            .sendText(#"{"realtime_input":{"audio_stream_end":true}}"#),
            // Google does NOT close the socket after audio_stream_end — measured
            // 54 s of silence — so this is the whole stop budget, not a courtesy
            // pause on an upstream close that is coming. Shorter than the 10 s the
            // other providers use precisely because waiting longer buys nothing.
            .waitForSessionComplete(timeout: 5.0),
            .closeWebSocket
        ]
    }
}

private extension GeminiStreamingStrategy {
    static func vocabularyTerms(from vocabulary: String?) -> [String] {
        guard let vocabulary, !vocabulary.isEmpty else { return [] }
        var seen = Set<String>()
        var terms: [String] = []
        for raw in vocabulary.split(separator: ",") {
            let term = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !term.isEmpty, term.count <= maxVocabularyChars else { continue }
            guard seen.insert(term.lowercased()).inserted else { continue }
            terms.append(term)
            if terms.count == maxVocabularyTerms { break }
        }
        return terms
    }
}

private struct GeminiLiveMessage: Decodable {
    let setupComplete: GeminiSetupComplete?
    let serverContent: GeminiServerContent?
    let error: GeminiLiveError?
}

private struct GeminiSetupComplete: Decodable {}

private struct GeminiServerContent: Decodable {
    let interimInputTranscription: GeminiTranscription?
    let inputTranscription: GeminiTranscription?
    let generationComplete: Bool?
}

private struct GeminiTranscription: Decodable {
    let text: String?
}

private struct GeminiLiveError: Decodable {
    let code: Int?
    let message: String?
}
