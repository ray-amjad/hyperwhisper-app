//
//  RustLiveStreamingStrategyTests.swift
//  hyperwhisperTests
//
//  Holds `RustLiveStreamingStrategy` — the one macOS live-streaming strategy,
//  over `hw_net::live` (issue #326) — to the wire contract the Rust suite pins,
//  ASSERTED ON THE MACOS SIDE OF THE FFI. The Rust tests prove the core builds
//  the right bytes; these prove the Swift adapter carries them, unchanged, into
//  the shapes `StreamingTranscriptionClient` consumes. A mapping bug lives
//  exactly in the gap between those two claims.
//
//  Four of these cases pin a deliberate CHANGE to what macOS puts on the wire,
//  not a no-op:
//
//  1. Deepgram now sends thirteen query parameters, not ten — `filler_words`,
//     `utterance_end_ms` and `vad_events` are new here.
//  2. Deepgram auto-detect now spells itself `detect_language=true` instead of
//     omitting the parameter (which left Deepgram on its account default).
//  3. One custom-vocabulary policy replaces four: sanitize, case-insensitive
//     de-duplicate, truncate at 80 characters, cap at 100 terms. Deepgram and
//     HyperWhisper Cloud had no policy at all before this.
//  4. HyperWhisper Cloud's `vocabulary=` is a `", "`-join of the parsed terms
//     rather than the user's raw stored string.
//
//  Nothing here opens a socket, reads the keychain or needs an API key.
//

import Testing
import Foundation
@testable import HyperWhisper

@Suite("Rust live streaming strategy")
struct RustLiveStreamingStrategyTests {

    // MARK: - Fixtures

    /// Every remote provider this strategy speaks. The two on-device engines
    /// are deliberately absent: they are not websocket protocols, have no
    /// `HwLiveProvider` arm, and route to their own clients.
    private static let remoteProviders: [StreamingTranscriptionProvider] = [
        .hyperwhisperCloud, .deepgram, .elevenLabs, .openAI, .xai, .gemini
    ]

    private func config(
        licenseKey: String? = nil,
        deviceId: String? = nil,
        language: String? = nil,
        vocabulary: String? = nil,
        apiKey: String? = "test-key",
        model: String? = nil,
        fastFormatting: Bool = false
    ) -> StreamingSessionConfig {
        StreamingSessionConfig(
            licenseKey: licenseKey,
            deviceId: deviceId,
            language: language,
            vocabulary: vocabulary,
            apiKey: apiKey,
            model: model,
            fastFormatting: fastFormatting
        )
    }

    /// A strategy that has completed one `connect()`, plus the URL it produced.
    private func connected(
        _ provider: StreamingTranscriptionProvider,
        _ config: StreamingSessionConfig,
        baseURL: String? = nil,
        cloudTier: String? = nil,
        nowMs: (() -> UInt64)? = nil
    ) throws -> (strategy: RustLiveStreamingStrategy, url: URL) {
        let strategy = RustLiveStreamingStrategy(
            provider: provider,
            baseURL: baseURL,
            cloudTier: cloudTier,
            nowMs: nowMs
        )
        let url = try #require(
            strategy.buildWebSocketURL(config: config),
            "\(provider) refused to build a URL from a complete config"
        )
        return (strategy, url)
    }

    /// Query parameters, percent-decoded, in wire order. Repeats are kept —
    /// Deepgram's `keyterm` is a repeated parameter.
    private func query(_ url: URL) -> [(name: String, value: String)] {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let items = components.queryItems else { return [] }
        return items.map { (name: $0.name, value: $0.value ?? "") }
    }

    private func values(_ url: URL, _ name: String) -> [String] {
        query(url).filter { $0.name == name }.map(\.value)
    }

    private func text(_ message: URLSessionWebSocketTask.Message?) throws -> String {
        guard case let .string(text) = message else {
            Issue.record("expected a text frame, got \(String(describing: message))")
            throw StreamingStrategyTestFailure.notATextFrame
        }
        return text
    }

    // MARK: - §0.3.1 / §0.3.2 — Deepgram's query string

    @Test("DELTA: Deepgram now sends thirteen parameters and spells auto-detect out loud")
    func deepgramSendsThirteenParametersAndDetectsTheLanguage() throws {
        // macOS sent TEN of these and omitted the auto-detect parameter
        // entirely, which leaves Deepgram on the account default rather than
        // detecting. `filler_words`, `utterance_end_ms` and `vad_events` are
        // new on this platform. Byte-for-byte with
        // hw-net/src/live/tests.rs `deepgram_connect_sends_the_thirteen_parameters…`.
        let (_, url) = try connected(.deepgram, config())

        #expect(url.absoluteString == "wss://api.deepgram.com/v1/listen"
            + "?model=nova-3-general&encoding=linear16&sample_rate=16000&channels=1"
            + "&smart_format=true&punctuate=true&filler_words=true&no_delay=false"
            + "&endpointing=300&utterance_end_ms=1500&interim_results=true&vad_events=true"
            + "&mip_opt_out=true&detect_language=true")
    }

    @Test("Deepgram's fast-formatting toggle reaches no_delay, and an explicit language replaces detect_language")
    func deepgramLanguageAndFastFormatting() throws {
        let (_, url) = try connected(.deepgram, config(language: "zh-TW", fastFormatting: true))

        // zh-TW survives whole. Deepgram treats zh and zh-TW as different
        // languages, so flattening to the primary subtag would ask for
        // Simplified Chinese when the user picked Traditional. This is the
        // regression PR #320 hit in its first review round.
        #expect(values(url, "language") == ["zh-TW"])
        #expect(values(url, "detect_language").isEmpty)
        #expect(values(url, "no_delay") == ["true"])
    }

    // MARK: - §0.3.4 — one vocabulary policy, four replaced

    @Test("DELTA: vocabulary de-duplicates case-insensitively, strips angle brackets and truncates at 80")
    func deepgramVocabularyRunsThroughTheCorePolicy() throws {
        // The shipped Deepgram strategy split on "," and sent whatever came
        // out: no cap, no de-dupe, no sanitize. All three now happen once, in
        // hw_net::helpers::keyword_boost_terms, for every provider.
        let long = String(repeating: "z", count: 100)
        let (_, url) = try connected(.deepgram, config(
            language: "en",
            vocabulary: "HyperWhisper, hyperwhisper , <Kalamazoo>,  , \(long)"
        ))

        #expect(values(url, "keyterm") == [
            "HyperWhisper",                          // first-seen casing wins
            "Kalamazoo",                             // < and > dropped
            String(repeating: "z", count: 80)        // truncated, not rejected
        ])
    }

    @Test("DELTA: Deepgram's vocabulary is capped at 100 terms")
    func deepgramVocabularyIsCappedAtOneHundred() throws {
        let terms = (1...150).map { "term\($0)" }
        let (_, url) = try connected(.deepgram, config(
            language: "en",
            vocabulary: terms.joined(separator: ",")
        ))

        let keyterms = values(url, "keyterm")
        #expect(keyterms.count == 100)
        #expect(keyterms.first == "term1")
        #expect(keyterms.last == "term100")
    }

    @Test("Deepgram still withholds vocabulary under auto-detect")
    func deepgramWithholdsVocabularyWithoutALanguage() throws {
        // Nova-3 silently ignores keyterm in multilingual mode, so the terms
        // are dropped rather than sent to be discarded. Unchanged behaviour —
        // pinned because it is the ONE gate the new single policy must not
        // flatten away.
        let (_, url) = try connected(.deepgram, config(vocabulary: "Kalamazoo"))
        #expect(values(url, "keyterm").isEmpty)
        #expect(values(url, "detect_language") == ["true"])
    }

    @Test("DELTA: HyperWhisper Cloud sends a \", \"-joined vocabulary, not the raw stored string")
    func hyperWhisperCloudJoinsVocabularyWithCommaSpace() throws {
        // The shipped strategy forwarded `config.vocabulary` verbatim, so the
        // user's own spacing went on the wire. It is now the parsed, sanitized,
        // de-duplicated list re-joined with ", ".
        let (_, url) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1", language: "en", vocabulary: "alpha,beta ,ALPHA"),
            baseURL: "https://transcribe-staging-v2.hyperwhisper.com",
            cloudTier: "deepgramNova3"
        )

        #expect(values(url, "vocabulary") == ["alpha, beta"])
    }

    // MARK: - §0.3.7 — the base URL a DEBUG build depends on

    @Test("HyperWhisper Cloud honours the injected base URL and maps https to wss")
    func hyperWhisperCloudHonoursTheInjectedBaseURL() throws {
        // THE TEST THAT STOPS A DEBUG BUILD BILLING PRODUCTION. The strategy
        // is constructed with NetworkConfig.hyperwhisperCloudURL, which is the
        // staging host under #if DEBUG. Forgetting to thread it through points
        // every developer session at the production relay.
        let (_, url) = try connected(
            .hyperwhisperCloud,
            config(deviceId: "device-1"),
            baseURL: NetworkConfig.hyperwhisperCloudURL,
            cloudTier: "deepgramNova3"
        )

        let expectedHost = try #require(URL(string: NetworkConfig.hyperwhisperCloudURL)?.host)
        #expect(url.scheme == "wss")
        #expect(url.host == expectedHost)
        #expect(url.path == "/ws/streaming-deepgram")
        #expect(values(url, "device_id") == ["device-1"])
    }

    @Test("http maps to ws, and a trailing slash does not double up")
    func baseURLSchemeAndSlashHandling() throws {
        let (_, url) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1"),
            baseURL: "http://localhost:8787/",
            cloudTier: "deepgramNova3"
        )
        #expect(url.scheme == "ws")
        #expect(url.host == "localhost")
        #expect(url.port == 8787)
        #expect(url.path == "/ws/streaming-deepgram")
    }

    @Test("A license key wins over a device id, and the route is derived from the tier")
    func hyperWhisperCloudCredentialPrecedenceAndTierRoute() throws {
        let (_, url) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1", deviceId: "device-1"),
            baseURL: "https://relay.test",
            cloudTier: "deepgramNova3"
        )
        #expect(values(url, "license_key") == ["HW-1"])
        #expect(values(url, "device_id").isEmpty)

        // An unrecognised tier falls back to Deepgram rather than deriving a
        // path the backend would 404 — the client half of the guard, because
        // the catalog has no `enabled` gate.
        let (_, fallback) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1"),
            baseURL: "https://relay.test",
            cloudTier: "not-a-tier"
        )
        #expect(fallback.path == "/ws/streaming-deepgram")
    }

    // MARK: - Stop sequences

    @Test("HyperWhisper Cloud stops with stop → waitForSessionComplete(10s) → close")
    func hyperWhisperCloudStopSequence() throws {
        // A no-regression pin, not a new delta: macOS already ships this shape.
        // It is here because the milliseconds→seconds conversion at the FFI
        // boundary is new, and a factor-of-1000 slip would either close the
        // socket instantly (losing the last utterance and the credit figure)
        // or hang the stop for nearly three hours.
        let (strategy, _) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1"),
            baseURL: "https://relay.test",
            cloudTier: "deepgramNova3"
        )
        let steps = strategy.stopSequence()

        #expect(steps.count == 3)
        guard case let .sendText(text) = steps.first else {
            Issue.record("expected a sendText step first")
            return
        }
        #expect(text == #"{"type":"stop"}"#)
        guard case let .waitForSessionComplete(timeout) = steps[1] else {
            Issue.record("expected a bounded waitForSessionComplete")
            return
        }
        #expect(timeout == 10.0)
        guard case .closeWebSocket = steps[2] else {
            Issue.record("expected the socket to be closed by us")
            return
        }
    }

    @Test("Deepgram's stop waits half a second BETWEEN Finalize and CloseStream")
    func deepgramStopSequenceKeepsTheGapBetweenTheTwoFrames() throws {
        // Sent together, the close can be processed before the flush completes
        // and the finalized tail is lost. This also pins the ms→seconds
        // conversion on the `.wait` arm.
        let (strategy, _) = try connected(.deepgram, config())
        let steps = strategy.stopSequence()

        #expect(steps.count == 4)
        guard case let .sendText(finalize) = steps[0],
              case let .wait(gap) = steps[1],
              case let .sendText(close) = steps[2],
              case .closeWebSocket = steps[3] else {
            Issue.record("unexpected Deepgram stop shape: \(steps)")
            return
        }
        #expect(finalize == #"{"type":"Finalize"}"#)
        #expect(gap == 0.5)
        #expect(close == #"{"type":"CloseStream"}"#)
    }

    // MARK: - Credentials

    @Test("A missing credential refuses to build a URL, for every provider")
    func missingCredentialBuildsNoURL() {
        for provider in Self.remoteProviders {
            let strategy = RustLiveStreamingStrategy(provider: provider)
            // No API key, no license key, no device id.
            #expect(
                strategy.buildWebSocketURL(config: config(apiKey: nil)) == nil,
                "\(provider) opened a session with no credential"
            )
            // A whitespace-only credential is a misconfiguration, not a
            // credential, and the core reads it as absent.
            #expect(
                strategy.buildWebSocketURL(
                    config: config(licenseKey: "  ", deviceId: " ", apiKey: "   ")
                ) == nil,
                "\(provider) accepted a whitespace-only credential"
            )
        }
    }

    @Test("A refused connect clears the previous session's descriptor")
    func aRefusedConnectDoesNotLeaveAStaleDescriptor() throws {
        // The reconnect hazard: `buildWebSocketURL` returning nil must not
        // leave `webSocketSubprotocols` still handing out the dead session's
        // API key.
        let (strategy, _) = try connected(.deepgram, config(apiKey: "secret-key"))
        #expect(strategy.webSocketSubprotocols(config: config()) == ["token", "secret-key"])

        #expect(strategy.buildWebSocketURL(config: config(apiKey: nil)) == nil)
        #expect(strategy.webSocketSubprotocols(config: config()) == nil)
        #expect(strategy.startMessages(config: config()).isEmpty)
    }

    // MARK: - Handshake: headers vs subprotocols

    @Test("TRAP: Deepgram gets subprotocols and NO URLRequest")
    func deepgramAuthRidesTheSubprotocolsAlone() throws {
        // `StreamingTranscriptionClient.makeWebSocketTask` prefers a URLRequest
        // over subprotocols and never applies both. Returning a request here —
        // which the Windows adapter's shape invites, because C# sets both on
        // one ClientWebSocket — would silently drop
        // `Sec-WebSocket-Protocol: token, <key>` and fail every handshake.
        let (strategy, url) = try connected(.deepgram, config(apiKey: "dg-key"))

        #expect(strategy.webSocketSubprotocols(config: config()) == ["token", "dg-key"])
        #expect(strategy.buildWebSocketRequest(url: url, config: config()) == nil)
    }

    @Test("Header-authenticated providers get a URLRequest and no subprotocols")
    func headerProvidersCarryTheirHeaders() throws {
        let (elevenLabs, elevenLabsURL) = try connected(.elevenLabs, config(apiKey: "el-key"))
        let elevenLabsRequest = try #require(
            elevenLabs.buildWebSocketRequest(url: elevenLabsURL, config: config())
        )
        #expect(elevenLabsRequest.value(forHTTPHeaderField: "xi-api-key") == "el-key")
        #expect(elevenLabs.webSocketSubprotocols(config: config()) == nil)

        let (openAI, openAIURL) = try connected(.openAI, config(apiKey: "sk-test"))
        let openAIRequest = try #require(
            openAI.buildWebSocketRequest(url: openAIURL, config: config())
        )
        #expect(openAIRequest.value(forHTTPHeaderField: "Authorization") == "Bearer sk-test")
    }

    @Test("HyperWhisper Cloud carries the macOS client-identity headers the core cannot know")
    func hyperWhisperCloudCarriesClientIdentity() throws {
        // A shared core does not know which platform it is linked into, so
        // these two are this head's to add — for the one provider that has ever
        // carried them.
        let (strategy, url) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1"),
            baseURL: "https://relay.test",
            cloudTier: "deepgramNova3"
        )
        let request = try #require(strategy.buildWebSocketRequest(url: url, config: config()))

        #expect(
            request.value(forHTTPHeaderField: HyperWhisperClientInfo.platformHeaderName)
                == HyperWhisperClientInfo.platform
        )
        #expect(
            request.value(forHTTPHeaderField: HyperWhisperClientInfo.versionHeaderName)
                == HyperWhisperClientInfo.version
        )
    }

    @Test("Gemini's handshake carries no headers at all")
    func geminiSendsNoHandshakeHeaders() throws {
        // Google rejects the upgrade outright if an Authorization header is
        // present; the key travels in the query string.
        let (strategy, url) = try connected(.gemini, config(apiKey: "AIza-test"))
        #expect(strategy.buildWebSocketRequest(url: url, config: config()) == nil)
        #expect(strategy.webSocketSubprotocols(config: config()) == nil)
        #expect(values(url, "key") == ["AIza-test"])
    }

    // MARK: - Framing and start frames (the live-frame-vectors proof, at the FFI)

    @Test("Gemini's setup frame puts the config at setup.input_audio_transcription")
    func geminiSetupFrameMatchesTheVector() throws {
        // Re-aimed from GeminiStreamingStrategyTests at connect().startFrames.
        // Expectations come from shared-conformance/live-frame-vectors.json.
        // The PRE-RECORDED model takes this same object at
        // setup.generation_config.transcription_config, and sending that shape
        // to the LIVE socket closes it with 1007.
        let (strategy, _) = try connected(
            .gemini,
            config(language: "en-US", vocabulary: "HyperWhisper", apiKey: "AIza-test")
        )
        let frames = strategy.startMessages(config: config())
        #expect(frames.count == 1)

        let json = try text(frames.first)
        let data = try #require(json.data(using: .utf8))
        let root = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let setup = try #require(root["setup"] as? [String: Any])

        #expect(setup["model"] as? String == "models/gemini-3.5-transcribe-live")
        #expect(setup["generation_config"] == nil)

        let transcription = try #require(setup["input_audio_transcription"] as? [String: Any])
        #expect(transcription["language_codes"] as? [String] == ["en-US"])
        #expect(transcription["custom_vocabulary"] as? [String] == ["HyperWhisper"])
    }

    @Test("Gemini auto-detect drops language_codes but keeps custom_vocabulary")
    func geminiAutoDetectStillSendsVocabulary() throws {
        // "No vocabulary without an explicit language" is a Deepgram Nova-3
        // constraint. Gemini accepts custom_vocabulary in auto-detect, and
        // vocabulary is the headline reason to pick this provider.
        let (strategy, _) = try connected(
            .gemini,
            config(language: "auto", vocabulary: "Kalamazoo", apiKey: "AIza-test")
        )
        let json = try text(strategy.startMessages(config: config()).first)
        let data = try #require(json.data(using: .utf8))
        let root = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let setup = try #require(root["setup"] as? [String: Any])
        let transcription = try #require(setup["input_audio_transcription"] as? [String: Any])

        #expect(transcription["language_codes"] == nil)
        #expect(transcription["custom_vocabulary"] as? [String] == ["Kalamazoo"])
    }

    @Test("Gemini audio frames are base64 JSON at the catalogued mime type")
    func geminiAudioFrameMatchesTheVector() throws {
        // The base64 and the concatenation happen HERE, on bytes this process
        // already holds — the core answers a prefix/suffix descriptor once and
        // never sees a sample.
        let (strategy, _) = try connected(.gemini, config(apiKey: "AIza-test"))
        let frame = try text(strategy.encodeAudioChunk(Data("ABC".utf8)))

        #expect(frame == #"{"realtime_input":{"audio":{"data":"QUJD","mime_type":"audio/pcm;rate=16000"}}}"#)
    }

    @Test("Gemini's stop sequence ends the audio stream and does not wait on Google")
    func geminiStopSequenceMatchesTheVector() throws {
        let (strategy, _) = try connected(.gemini, config(apiKey: "AIza-test"))
        let steps = strategy.stopSequence()

        guard case let .sendText(text) = steps.first else {
            Issue.record("expected audio_stream_end first")
            return
        }
        #expect(text == #"{"realtime_input":{"audio_stream_end":true}}"#)
        guard case let .waitForSessionComplete(timeout) = steps[1] else {
            Issue.record("the wait must stay bounded")
            return
        }
        #expect(timeout > 0 && timeout <= 10.0)
    }

    @Test("Binary-framed providers send the PCM bytes unchanged")
    func binaryProvidersSendRawPCM() throws {
        let pcm = Data([0x01, 0x02, 0x03, 0x04])
        let cases: [(StreamingTranscriptionProvider, StreamingSessionConfig)] = [
            (.deepgram, config()),
            (.xai, config()),
            (.hyperwhisperCloud, config(licenseKey: "HW-1"))
        ]
        for (provider, sessionConfig) in cases {
            let (strategy, _) = try connected(
                provider,
                sessionConfig,
                baseURL: "https://relay.test",
                cloudTier: "deepgramNova3"
            )
            guard case let .data(sent) = strategy.encodeAudioChunk(pcm) else {
                Issue.record("\(provider) must frame audio as raw binary")
                continue
            }
            #expect(sent == pcm)
        }
    }

    // MARK: - Capabilities

    @Test("OpenAI is the only 24 kHz provider")
    func onlyOpenAIRunsAtTwentyFourKilohertz() {
        // The capture graph is configured from this before a session opens, so
        // it is a hard requirement: 16 kHz audio on the 24 kHz endpoint
        // produces a transcript at the wrong speed, not an error.
        for provider in Self.remoteProviders {
            let expected: Double = provider == .openAI ? 24_000 : 16_000
            #expect(
                RustLiveStreamingStrategy(provider: provider).audioSampleRate == expected,
                "\(provider) reported the wrong capture rate"
            )
        }
    }

    @Test("Every provider's history label round-trips to the shipped string")
    func providerLabelsAreByteIdenticalToWhatShipped() {
        // These strings are PERSISTED on history rows. Changing one silently
        // splits a vendor in two in the history list.
        let shipped: [StreamingTranscriptionProvider: String] = [
            .hyperwhisperCloud: "HyperWhisper Cloud (Streaming)",
            .deepgram: "Deepgram (Streaming)",
            .elevenLabs: "ElevenLabs (Streaming)",
            .openAI: "OpenAI (Streaming)",
            .xai: "SpaceXAI (Streaming)",
            .gemini: "Gemini 3.5 Transcribe (Streaming)"
        ]
        for (provider, label) in shipped {
            #expect(
                RustLiveStreamingStrategy(provider: provider).transcriptionProviderLabel == label,
                "\(provider) re-labelled itself"
            )
        }
    }

    @Test("Vocabulary support answers with no credential and no session")
    func vocabularySupportNeedsNoSession() {
        // The settings page reads this with neither, which is why it comes off
        // the capability table and not off a connect descriptor.
        let withoutVocabulary: [StreamingTranscriptionProvider] = [.elevenLabs, .openAI]
        for provider in Self.remoteProviders {
            let expected = !withoutVocabulary.contains(provider)
            #expect(
                RustLiveStreamingStrategy(provider: provider).supportsVocabulary == expected,
                "\(provider) reported the wrong vocabulary support"
            )
        }
    }

    @Test("Deepgram alone treats the socket opening as the session starting")
    func onlyDeepgramStartsOnOpen() throws {
        // Deepgram's only session-shaped frame (Metadata) does not arrive until
        // after audio has been sent, so a client that waited for it would
        // deadlock.
        let (deepgram, _) = try connected(.deepgram, config())
        #expect(deepgram.sessionStartsOnWebSocketOpen)

        for provider in Self.remoteProviders where provider != .deepgram {
            let (strategy, _) = try connected(
                provider,
                config(licenseKey: "HW-1"),
                baseURL: "https://relay.test",
                cloudTier: "deepgramNova3"
            )
            #expect(
                strategy.sessionStartsOnWebSocketOpen == false,
                "\(provider) would skip waiting for its session-started frame"
            )
        }
    }

    // MARK: - The injected clock (no sleeping)

    @Test("Deepgram's KeepAlive fires only after three seconds of silence")
    func deepgramKeepAliveIsDrivenByTheInjectedClock() throws {
        // The core reads no clock of its own — `now_ms` is a parameter — which
        // is the property that makes this testable at all. Without the
        // KeepAlive, Deepgram closes the socket after ~10 s of silence.
        var now: UInt64 = 0
        let (strategy, _) = try connected(.deepgram, config(), nowMs: { now })

        var sent: [URLSessionWebSocketTask.Message] = []
        strategy.onAudioSendOpportunity { sent.append($0) }
        #expect(sent.isEmpty, "the first opportunity only seeds the idle mark")

        now = 2_999
        strategy.onAudioSendOpportunity { sent.append($0) }
        #expect(sent.isEmpty, "under the threshold, ordinary audio keeps the socket alive")

        now = 6_001
        strategy.onAudioSendOpportunity { sent.append($0) }
        #expect(sent.count == 1)
        let keepAlive = try text(sent.first)
        #expect(keepAlive == #"{"type":"KeepAlive"}"#)
    }

    @Test("OpenAI's periodic commit needs BOTH the interval and the 100 ms byte floor")
    func openAICommitGateIsDrivenByTheInjectedClockAndTheByteCount() throws {
        // The byte count is the only thing the core ever learns about audio,
        // and `encodeAudioChunk` is what reports it. A commit covering less
        // than 100 ms is rejected by OpenAI outright ("buffer too small"), and
        // with turn_detection null there is no server-side VAD to rescue a
        // dropped tail. 4800 bytes is exactly 100 ms of 24 kHz 16-bit mono.
        var now: UInt64 = 0
        let (strategy, _) = try connected(.openAI, config(apiKey: "sk-test"), nowMs: { now })

        var sent: [URLSessionWebSocketTask.Message] = []
        strategy.onAudioSendOpportunity { sent.append($0) }
        #expect(sent.isEmpty, "the first opportunity only seeds the commit clock")

        // One byte short of the floor, and well past the interval.
        _ = strategy.encodeAudioChunk(Data(count: 4_799))
        now = 5_000
        strategy.onAudioSendOpportunity { sent.append($0) }
        #expect(sent.isEmpty, "the interval alone must not commit a sub-threshold buffer")

        // The next chunk clears the floor, and the commit fires immediately
        // rather than waiting out another whole interval.
        _ = strategy.encodeAudioChunk(Data(count: 1))
        strategy.onAudioSendOpportunity { sent.append($0) }
        #expect(sent.count == 1)
        let commit = try text(sent.first)
        #expect(commit == #"{"type":"input_audio_buffer.commit"}"#)
    }

    // MARK: - Event mapping

    @Test("Provider frames map onto the client's normalized events")
    func parsedFramesMapOntoStreamingProviderEvents() throws {
        let (cloud, _) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1"),
            baseURL: "https://relay.test",
            cloudTier: "deepgramNova3"
        )

        guard case let .sessionStarted(sessionId) =
            cloud.parseMessage(#"{"type":"ready","sessionId":"s-1"}"#) else {
            Issue.record("ready must start the session")
            return
        }
        #expect(sessionId == "s-1")

        guard case let .partialTranscript(partial) =
            cloud.parseMessage(#"{"type":"transcript","text":"hel","is_final":false}"#) else {
            Issue.record("a non-final transcript must be a partial")
            return
        }
        #expect(partial == "hel")

        guard case let .finalTranscript(final) =
            cloud.parseMessage(#"{"type":"transcript","text":"hello","is_final":true}"#) else {
            Issue.record("a final transcript must be a final")
            return
        }
        #expect(final == "hello")

        guard case let .sessionComplete(duration, credits) =
            cloud.parseMessage(#"{"type":"session_complete","duration_seconds":4.5,"credits_used":2}"#) else {
            Issue.record("session_complete must end the session and carry the bill")
            return
        }
        #expect(duration == 4.5)
        #expect(credits == 2)

        guard case let .error(message) =
            cloud.parseMessage(#"{"type":"error","message":"quota exceeded"}"#) else {
            Issue.record("an error frame must surface as an error event")
            return
        }
        // The core's `kind` is dropped on the way in, matching Windows. macOS
        // classifies from the wording instead, and the wording carries the
        // marker the classifier reads.
        #expect(message == "quota exceeded")
        #expect(liveClassifyErrorMessage(message: message) != .transient)

        guard case let .warning(warning) =
            cloud.parseMessage(#"{"type":"warning","message":"session ending soon"}"#) else {
            Issue.record("a warning frame must surface as a warning event")
            return
        }
        #expect(warning == "session ending soon")
    }

    @Test("An unrecognised frame — including text that is not JSON — is ignored, never an error")
    func unrecognisedFramesAreIgnored() throws {
        // A provider adding a frame shape must never end a recording in
        // progress. `HwLiveEvent.ignore` maps to nil, which is the client's own
        // "nothing happened".
        let (cloud, _) = try connected(
            .hyperwhisperCloud,
            config(licenseKey: "HW-1"),
            baseURL: "https://relay.test",
            cloudTier: "deepgramNova3"
        )
        #expect(cloud.parseMessage(#"{"type":"something_new"}"#) == nil)
        #expect(cloud.parseMessage("not json at all") == nil)
        #expect(cloud.parseMessage("") == nil)
    }

    @Test("Deepgram's polymorphic channel field does not swallow the frame")
    func deepgramPolymorphicChannelIsANonEvent() throws {
        // On Results `channel` is an object; on SpeechStarted/UtteranceEnd it
        // is an array of channel indices. A strict decode of the object shape
        // throws on those frames and makes the metadata arm unreachable — the
        // bug the deleted Swift strategy documented at length.
        let (deepgram, _) = try connected(.deepgram, config())

        guard case let .finalTranscript(text) = deepgram.parseMessage(
            #"{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"hello"}]}}"#
        ) else {
            Issue.record("a Results frame must produce a transcript")
            return
        }
        #expect(text == "hello")

        guard case .metadata = deepgram.parseMessage(#"{"type":"UtteranceEnd","channel":[0,1]}"#) else {
            Issue.record("an index-array channel must not swallow the frame")
            return
        }
    }
}

/// Thrown by the frame helpers so a shape mismatch fails the case it happened
/// in rather than reading on against a placeholder.
private enum StreamingStrategyTestFailure: Error {
    case notATextFrame
}
